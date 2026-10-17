using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IxIFlow.Templates.Razor;

/// <summary>
/// Compiles a Razor template into a class deriving from <see cref="RazorTemplate{TModel}"/>: Razor generates the C#,
/// and Roslyn compiles it against the model's assembly and the assemblies it references. The compiled assembly is
/// loaded into its own collectible load context, which resolves its references through the model's load context, so
/// a model loaded with a published bundle (F-02) compiles and runs against that bundle's assemblies.
/// </summary>
internal static class RazorCompiler
{
    private const string Namespace = "IxIFlow.Templates.Razor.Compiled";
    private const string TemplatePath = "/Template.cshtml";

    // What a template can use without its own @using.
    private static readonly string[] DefaultImports =
    [
        "@using System",
        "@using System.Collections.Generic",
        "@using System.Linq",
        "@using System.Threading.Tasks"
    ];

    private static readonly ConcurrentDictionary<string, MetadataReference> References =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compiles <paramref name="template"/> against <paramref name="model"/>. Only a compilation that is to be
    /// rendered is loaded, so validating drafts loads no assemblies.
    /// </summary>
    public static RazorCompilation Compile(TemplateSource template, Type model, bool load)
    {
        AssemblyLoadContext modelContext;
        try
        {
            modelContext = ContextOf(model);
        }
        catch (ArgumentException error)
        {
            return RazorCompilation.Failed([new TemplateDiagnostic(error.Message)]);
        }
        var className = "Template_" + template.Digest[..16];
        var baseType = $"global::{typeof(RazorTemplate<>).Namespace}.RazorTemplate<{CSharpName(model)}>";
        var engine = RazorProjectEngine.Create(RazorConfiguration.Default, EmptyFileSystem.Instance, builder =>
        {
            builder.SetNamespace(Namespace);
            builder.AddDefaultImports(DefaultImports);
            // The ASP.NET Core attributes that mark compiled views are not available, and nothing here reads them.
            builder.Features.Add(new WithoutMetadataAttributes());
            builder.SetBaseType(baseType);
            builder.ConfigureClass((_, node) =>
            {
                node.ClassName = className;
                node.Modifiers.Clear();
                node.Modifiers.Add("public");
                node.Modifiers.Add("sealed");
            });
        });
        var document = engine.Process(new SourceItem(template.Content));
        var csharp = document.GetCSharpDocument();
        var razorErrors = csharp.Diagnostics
            .Where(diagnostic => diagnostic.Severity == RazorDiagnosticSeverity.Error)
            .Select(diagnostic => new TemplateDiagnostic(diagnostic.GetMessage(),
                diagnostic.Span.LineIndex + 1, diagnostic.Span.CharacterIndex + 1))
            .ToList();
        if (razorErrors.Count > 0)
            return RazorCompilation.Failed(razorErrors);

        var tree = CSharpSyntaxTree.ParseText(csharp.GeneratedCode,
            new CSharpParseOptions(LanguageVersion.Latest), path: TemplatePath, encoding: Encoding.UTF8);
        var compilation = CSharpCompilation.Create(
            $"{Namespace}.{className}.{Guid.NewGuid():N}",
            [tree],
            ReferencesFor(model, modelContext),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            return RazorCompilation.Failed(emitted.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(Diagnostic)
                .ToList());
        }
        if (!DerivesFromModelBase(compilation, tree, className, baseType))
        {
            return RazorCompilation.Failed([new TemplateDiagnostic(
                "A template derives from the base the engine gives it for its model; @inherits is not available")]);
        }
        if (!load)
            return RazorCompilation.Succeeded(null);
        image.Position = 0;
        var context = new TemplateLoadContext(className, modelContext);
        var assembly = context.LoadFromStream(image);
        return RazorCompilation.Succeeded(assembly.GetType($"{Namespace}.{className}", throwOnError: true)!);
    }

    // @inherits would compile against another base than the model's, and fail only when rendered. The base the class
    // has is compared, as a type, with the one the engine names, bound where the class is declared.
    private static bool DerivesFromModelBase(CSharpCompilation compilation, SyntaxTree tree, string className,
        string baseType)
    {
        var declaration = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .First(node => node.Identifier.ValueText == className);
        var model = compilation.GetSemanticModel(tree);
        var expected = model.GetSpeculativeTypeInfo(declaration.SpanStart, SyntaxFactory.ParseTypeName(baseType),
            SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
        var actual = model.GetDeclaredSymbol(declaration)?.BaseType;
        return expected != null && SymbolEqualityComparer.Default.Equals(actual, expected);
    }

    /// <summary>
    /// The load context the model's types come from: a bundle's when any of them, a generic argument included, was
    /// loaded with one (F-02), and the default context otherwise.
    /// </summary>
    private static AssemblyLoadContext ContextOf(Type model)
    {
        var contexts = TypesOf(model)
            .Select(type => AssemblyLoadContext.GetLoadContext(type.Assembly) ?? AssemblyLoadContext.Default)
            .Where(context => context != AssemblyLoadContext.Default)
            .Distinct()
            .ToList();
        return contexts.Count switch
        {
            0 => AssemblyLoadContext.Default,
            1 => contexts[0],
            _ => throw new ArgumentException(
                $"The model '{TemplateVersion.ModelTypeName(model)}' mixes types from {contexts.Count} load contexts")
        };
    }

    // Razor maps the generated code back to the template with #line directives, so errors in the template's own
    // code report its lines; errors outside them are the engine's and carry no position.
    private static TemplateDiagnostic Diagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetMappedLineSpan();
        return span.HasMappedPath
            ? new TemplateDiagnostic($"{diagnostic.Id}: {diagnostic.GetMessage()}",
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1)
            : new TemplateDiagnostic($"{diagnostic.Id}: {diagnostic.GetMessage()}");
    }

    /// <summary>
    /// The platform's assemblies, this engine's, and the model type's assemblies with everything they reference, as
    /// the model's load context resolves them.
    /// </summary>
    private static List<MetadataReference> ReferencesFor(Type model, AssemblyLoadContext context)
    {
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>(TypesOf(model).Select(type => type.Assembly)
            .Append(typeof(RazorTemplate<>).Assembly)
            .Append(typeof(TemplateFormat).Assembly));
        var seen = new HashSet<Assembly>();
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            if (!seen.Add(assembly) || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;
            assemblies.TryAdd(assembly.GetName().Name!, assembly.Location);
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                if (assemblies.ContainsKey(name.Name!))
                    continue;
                try
                {
                    pending.Enqueue(context.LoadFromAssemblyName(name));
                }
                catch (Exception error) when (error is FileNotFoundException or FileLoadException
                                                  or BadImageFormatException)
                {
                    // A reference the model never loads is not needed to compile against it.
                }
            }
        }
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in platform)
            assemblies.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        return assemblies.Values
            .Select(path => References.GetOrAdd(path, location => MetadataReference.CreateFromFile(location)))
            .ToList();
    }

    private static IEnumerable<Type> TypesOf(Type type)
    {
        yield return type;
        if (type.IsArray)
        {
            foreach (var element in TypesOf(type.GetElementType()!))
                yield return element;
        }
        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            foreach (var nested in TypesOf(argument))
                yield return nested;
        }
    }

    /// <summary>The model type as C# names it from anywhere: <c>global::</c>, nested types and generic arguments.</summary>
    internal static string CSharpName(Type type)
    {
        if (type.IsArray)
            return $"{CSharpName(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        if (type.IsGenericParameter || type.IsPointer || type.IsByRef)
            throw new ArgumentException($"'{type}' cannot be a template model", nameof(type));
        var arguments = type.IsGenericType ? type.GetGenericArguments() : [];
        var used = 0;
        var parts = new List<string>();
        foreach (var level in NestingOf(type))
        {
            var name = level.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
            {
                var count = int.Parse(name[(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                name = name[..tick] + "<" +
                       string.Join(", ", arguments.Skip(used).Take(count).Select(CSharpName)) + ">";
                used += count;
            }
            parts.Add(name);
        }
        var prefix = string.IsNullOrEmpty(type.Namespace) ? "global::" : $"global::{type.Namespace}.";
        return prefix + string.Join(".", parts);
    }

    private static IEnumerable<Type> NestingOf(Type type)
    {
        var chain = new Stack<Type>();
        for (var current = type; current != null; current = current.DeclaringType)
            chain.Push(current);
        return chain;
    }

    private sealed class TemplateLoadContext(string name, AssemblyLoadContext model)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        private readonly AssemblyLoadContext _model = model;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The base class must be the one the engine renders with, whichever context the model came from.
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(RazorTemplate<>).Assembly.GetName()))
                return typeof(RazorTemplate<>).Assembly;
            return _model.LoadFromAssemblyName(assemblyName);
        }
    }

    private sealed class SourceItem(string content) : RazorProjectItem
    {
        public override string BasePath => "/";
        public override string FilePath => TemplatePath;
        public override string PhysicalPath => TemplatePath;
        public override string RelativePhysicalPath => TemplatePath[1..];
        public override bool Exists => true;
        public override string FileKind => FileKinds.Legacy;
        public override Stream Read() => new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private sealed class WithoutMetadataAttributes : RazorEngineFeatureBase, IConfigureRazorCodeGenerationOptionsFeature
    {
        public int Order => 0;

        public void Configure(RazorCodeGenerationOptionsBuilder options)
        {
            options.SuppressMetadataAttributes = true;
            options.SuppressChecksum = true;
        }
    }

    // A template is self-contained: Razor finds no imports and no other files next to it.
    private sealed class MissingItem(string path, string? fileKind) : RazorProjectItem
    {
        public override string BasePath => "/";
        public override string FilePath => path;
        public override string PhysicalPath => path;
        public override string RelativePhysicalPath => path.TrimStart('/');
        public override bool Exists => false;
        public override string FileKind => fileKind ?? FileKinds.Legacy;
        public override Stream Read() => throw new FileNotFoundException(path);
    }

    private sealed class EmptyFileSystem : RazorProjectFileSystem
    {
        public static readonly EmptyFileSystem Instance = new();

        public override IEnumerable<RazorProjectItem> EnumerateItems(string basePath) => [];

#pragma warning disable CS0672 // Razor still calls the single-argument overload.
        public override RazorProjectItem GetItem(string path) => GetItem(path, null);
#pragma warning restore CS0672

        public override RazorProjectItem GetItem(string path, string? fileKind) => new MissingItem(path, fileKind);
    }
}

/// <summary>A compiled template's class, or why it did not compile.</summary>
internal sealed class RazorCompilation
{
    private RazorCompilation(Type? template, IReadOnlyList<TemplateDiagnostic> errors)
    {
        Template = template;
        Errors = errors;
    }

    /// <summary>The compiled class, when the compilation was loaded.</summary>
    public Type? Template { get; }

    public IReadOnlyList<TemplateDiagnostic> Errors { get; }

    public static RazorCompilation Succeeded(Type? template) => new(template, []);

    public static RazorCompilation Failed(IReadOnlyList<TemplateDiagnostic> errors) => new(null, errors);
}
