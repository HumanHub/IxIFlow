using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Debugging;

/// <summary>Compiles expressions only after every operation and resolved member passes the allowlist.</summary>
public sealed class BreakpointConditionCompiler : IBreakpointConditionCompiler, IDisposable
{
    private const int Capacity = 128;
    private readonly Dictionary<BreakpointConditionRequest, LinkedListNode<(BreakpointConditionRequest Key, CacheEntry Entry)>> _cache = [];
    private readonly LinkedList<(BreakpointConditionRequest Key, CacheEntry Entry)> _recency = [];
    private bool _disposed;

    public BreakpointConditionCompilation Compile(BreakpointConditionRequest request)
    {
        CacheEntry compiled;
        CacheEntry? evicted = null;
        lock (_cache)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(request, out var hit))
            {
                _recency.Remove(hit); _recency.AddFirst(hit);
                return hit.Value.Entry.Compilation;
            }
            compiled = CompileCore(request);
            _cache.Add(request, _recency.AddFirst((request, compiled)));
            if (_cache.Count > Capacity)
            {
                var oldest = _recency.Last!;
                _recency.RemoveLast(); _cache.Remove(oldest.Value.Key);
                evicted = oldest.Value.Entry;
            }
        }
        // Unload is deferred by the runtime while an in-flight evaluation still holds its delegate.
        evicted?.LoadContext?.Unload();
        return compiled.Compilation;
    }

    public void Dispose()
    {
        CacheEntry[] entries;
        lock (_cache)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _recency.Select(item => item.Entry).ToArray();
            _cache.Clear(); _recency.Clear();
        }
        foreach (var entry in entries) entry.LoadContext?.Unload();
    }

    private sealed class CacheEntry(Func<object, object?, object?, int?, DateTimeOffset, bool>? evaluate,
        IReadOnlyList<BreakpointConditionDiagnostic> diagnostics, AssemblyLoadContext? loadContext = null)
    {
        public BreakpointConditionCompilation Compilation { get; } = new(evaluate, diagnostics);
        public AssemblyLoadContext? LoadContext { get; } = loadContext;
    }

    private static CacheEntry CompileCore(BreakpointConditionRequest request)
    {
        // Parse the user's text as one expression before embedding it. In particular, directives and
        // trailing tokens cannot escape the expression into the generated method or assembly.
        var expression = SyntaxFactory.ParseExpression(request.Text, options: new CSharpParseOptions(LanguageVersion.CSharp14),
            consumeFullText: true);
        var parseErrors = expression.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (parseErrors.Length != 0) return new(null, parseErrors.Select(d => Positioned(d.Id, d.GetMessage(),
            d.Location.SourceSpan.Start, d.Location.SourceSpan.Length, request.Text)).ToArray());
        if (expression.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsDirective))
            return new(null, [Positioned("BPC001", "Directives are not allowed", 0, request.Text.Length, request.Text)]);

        var data = TypeName(request.DataType);
        var previous = TypeName(request.PreviousType ?? typeof(object));
        var item = TypeName(request.ItemType ?? typeof(object));
        var prefix = $$"""
            #nullable enable
            using System;
            using System.Linq;
            public static class Condition
            {
                public static bool Evaluate(object data, object? previous, object? item, int? index, DateTimeOffset now)
                {
                    var context = new global::IxIFlow.Core.BreakpointConditionContext<{{data}}, {{previous}}, {{item}}>(
                        ({{data}})data, ({{previous}})previous!, ({{item}})item!, index, now);
                    return (
            """ + "\n";
        var source = prefix + request.Text + "\n);\n}\n}";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        var references = References(request).ToArray();
        var compilation = CSharpCompilation.Create("IxIFlowCondition_" + Guid.NewGuid().ToString("N"), [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false, checkOverflow: true));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) return new(null, errors.Select(d => Positioned(d.Id, d.GetMessage(),
            d.Location.SourceSpan.Start - prefix.Length, d.Location.SourceSpan.Length, request.Text)).ToArray());
        var model = compilation.GetSemanticModel(tree);
        var returned = tree.GetRoot().DescendantNodes().OfType<ReturnStatementSyntax>().First();
        var operation = (model.GetOperation(returned) as IReturnOperation)?.ReturnedValue
            ?? throw new InvalidOperationException("Roslyn did not bind the condition's return value");
        var allowlist = new ConditionAllowlist(request.Text, prefix.Length);
        allowlist.Visit(operation);
        if (allowlist.Diagnostics.Count != 0) return new(null, allowlist.Diagnostics.Distinct().ToArray());
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success) return new(null, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => Positioned(d.Id, d.GetMessage(), d.Location.SourceSpan.Start - prefix.Length,
                d.Location.SourceSpan.Length, request.Text)).ToArray());
        stream.Position = 0;
        // The cache owns the collectible context until eviction or compiler disposal.
        var loadContext = new AssemblyLoadContext(compilation.AssemblyName!, isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromStream(stream);
            var predicate = assembly.GetType("Condition")!.GetMethod("Evaluate")!
                .CreateDelegate<Func<object, object?, object?, int?, DateTimeOffset, bool>>();
            return new(predicate, [], loadContext);
        }
        catch { loadContext.Unload(); throw; }
    }

    private static IEnumerable<MetadataReference> References(BreakpointConditionRequest request)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string platform)
            foreach (var path in platform.Split(Path.PathSeparator)) paths.Add(path);
        var seen = new HashSet<Assembly>();
        void Add(Assembly assembly)
        {
            if (!seen.Add(assembly) || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) return;
            paths.Add(assembly.Location);
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { Add(Assembly.Load(reference)); }
                catch (FileNotFoundException) { /* Roslyn diagnoses unavailable metadata at the expression. */ }
            }
        }
        Add(typeof(BreakpointConditionContext<,,>).Assembly);
        Add(request.DataType.Assembly);
        if (request.PreviousType != null) Add(request.PreviousType.Assembly);
        if (request.ItemType != null) Add(request.ItemType.Assembly);
        foreach (var path in paths) yield return MetadataReference.CreateFromFile(path);
    }

    private static string TypeName(Type type)
    {
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        // Nested generic types distribute their arguments over their declaring types.
        var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        var index = 0;
        var parts = (type.FullName ?? type.Name).Split('[')[0].Split('+').Select(part =>
        {
            var tick = part.IndexOf('`');
            if (tick < 0) return part;
            var count = int.Parse(part[(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var name = part[..tick] + "<" + string.Join(",", arguments.Skip(index).Take(count).Select(TypeName)) + ">";
            index += count;
            return name;
        });
        return "global::" + string.Join(".", parts);
    }

    internal static BreakpointConditionDiagnostic Positioned(string code, string message, int start, int length, string text)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        var preceding = text[..start];
        var line = 1 + preceding.Count(c => c == '\n');
        var column = start - preceding.LastIndexOf('\n');
        return new(code, message, start, length, line, column);
    }
}

internal sealed class ConditionAllowlist(string text, int offset) : OperationWalker
{
    public List<BreakpointConditionDiagnostic> Diagnostics { get; } = [];

    private static readonly ImmutableHashSet<string> MathMembers = ["Abs", "Acos", "Acosh", "Asin", "Asinh", "Atan", "Atan2", "Atanh",
        "BitDecrement", "BitIncrement", "Cbrt", "Ceiling", "Clamp", "CopySign", "Cos", "Cosh", "Exp", "Floor", "FusedMultiplyAdd",
        "IEEERemainder", "ILogB", "Log", "Log10", "Log2", "Max", "MaxMagnitude", "Min", "MinMagnitude", "Pow", "ReciprocalEstimate",
        "ReciprocalSqrtEstimate", "Round", "ScaleB", "Sign", "Sin", "SinCos", "Sinh", "Sqrt", "Tan", "Tanh", "Truncate"];
    private static readonly ImmutableHashSet<string> LinqMembers = ["All", "Any", "Append", "Average", "Cast", "Chunk", "Concat",
        "Count", "DefaultIfEmpty", "Distinct", "ElementAt", "ElementAtOrDefault", "Empty", "First", "FirstOrDefault", "Last", "LastOrDefault",
        "LongCount", "Max", "MaxBy", "Min", "MinBy", "OfType", "Order", "OrderBy", "OrderByDescending", "OrderDescending", "Prepend",
        "Range", "Repeat", "Reverse", "Select", "SelectMany", "SequenceEqual", "Single", "SingleOrDefault", "Skip", "SkipLast", "SkipWhile",
        "Sum", "Take", "TakeLast", "TakeWhile", "ThenBy", "ThenByDescending", "ToArray", "ToList", "Where", "Zip"];
    private static readonly ImmutableHashSet<string> DateMembers = ["Year", "Month", "Day", "DayOfWeek", "DayOfYear", "Hour", "Minute",
        "Second", "Millisecond", "Microsecond", "Nanosecond", "Ticks", "UtcTicks", "Offset", "Date", "TimeOfDay", "UtcDateTime", "Kind",
        "Add", "AddDays", "AddHours", "AddMilliseconds", "AddMinutes", "AddMonths", "AddSeconds", "AddTicks", "AddYears", "Subtract",
        "ToUnixTimeSeconds", "ToUnixTimeMilliseconds", "MinValue", "MaxValue", "UnixEpoch"];

    public override void Visit(IOperation? operation)
    {
        if (operation == null) return;
        if (operation.Type?.TypeKind == TypeKind.Dynamic) Refuse(operation, "dynamic is not allowed");
        var allowed = operation switch
        {
            ILiteralOperation or IDefaultValueOperation or IParenthesizedOperation or IConditionalOperation or
                ICoalesceOperation or IConditionalAccessOperation or IConditionalAccessInstanceOperation or
                IArrayElementReferenceOperation or IArgumentOperation or IDelegateCreationOperation => true,
            IParameterReferenceOperation p => p.Parameter.Name == "context" || p.Parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction },
            ILocalReferenceOperation l => l.Local.Name == "context" && l.Local.Type.OriginalDefinition.ToDisplayString() ==
                "IxIFlow.Core.BreakpointConditionContext<TData, TPrevious, TItem>",
            IBinaryOperation b => b.OperatorMethod == null || PureOperator(b.OperatorMethod),
            IUnaryOperation u => u.OperatorMethod == null || PureOperator(u.OperatorMethod),
            IConversionOperation c => c.OperatorMethod == null || PureOperator(c.OperatorMethod),
            IPropertyReferenceOperation p => Property(p),
            IFieldReferenceOperation f => Field(f),
            IInvocationOperation call => Method(call),
            IAnonymousFunctionOperation lambda => lambda.Symbol.Parameters.All(p => p.RefKind == RefKind.None),
            IBlockOperation block => block.IsImplicit,
            IReturnOperation ret => ret.IsImplicit,
            IIsPatternOperation or IConstantPatternOperation or INegatedPatternOperation or IBinaryPatternOperation or IRelationalPatternOperation => true,
            _ => false
        };
        if (!allowed) Refuse(operation, $"'{operation.Syntax}' is outside the breakpoint condition allowlist");
        base.Visit(operation);
    }

    private void Refuse(IOperation operation, string message) => Diagnostics.Add(
        BreakpointConditionCompiler.Positioned("BPC001", message, operation.Syntax.SpanStart - offset, operation.Syntax.Span.Length, text));

    private static bool Bcl(ITypeSymbol type) => type.ContainingAssembly?.Identity.Name is
        "System.Private.CoreLib" or "System.Runtime" or "System.Collections" or "System.Linq" or "System.Runtime.Extensions";
    private static bool Application(ITypeSymbol type)
    {
        var assembly = type.ContainingAssembly?.Identity.Name ?? "";
        return assembly is not ("mscorlib" or "netstandard") &&
            !assembly.StartsWith("System", StringComparison.Ordinal) &&
            !assembly.StartsWith("Microsoft.", StringComparison.Ordinal);
    }
    private static string Name(ITypeSymbol type) => type.OriginalDefinition.ToDisplayString();
    private static bool Numeric(ITypeSymbol type) => Bcl(type) && type.SpecialType is >= SpecialType.System_SByte and <= SpecialType.System_Decimal;
    private static bool PureOperator(IMethodSymbol method) => Bcl(method.ContainingType) &&
        (Numeric(method.ContainingType) || Name(method.ContainingType) is "string" or "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan");

    private static bool Property(IPropertyReferenceOperation property)
    {
        var symbol = property.Property;
        var type = symbol.ContainingType;
        var name = Name(type);
        if (name == "IxIFlow.Core.BreakpointConditionContext<TData, TPrevious, TItem>") return !symbol.IsStatic;
        if (!Bcl(type)) return Application(type) && !symbol.IsStatic && property.Instance != null && Rooted(property.Instance);
        if (name == "string") return symbol.Name == "Length" || symbol.IsIndexer;
        if (name == "System.Array") return symbol.Name is "Length" or "LongLength" or "Rank";
        if (name == "T?") return symbol.Name is "Value" or "HasValue";
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return symbol.Name is "Value" or "HasValue";
        if (name is "System.DateTime" or "System.DateTimeOffset") return DateMembers.Contains(symbol.Name);
        if (name == "System.TimeSpan") return symbol.Name is "Ticks" or "Days" or "Hours" or "Minutes" or "Seconds" or "Milliseconds" or
            "TotalDays" or "TotalHours" or "TotalMinutes" or "TotalSeconds" or "TotalMilliseconds";
        return InMemory(type) && (symbol.IsIndexer || symbol.Name is "Count" or "Length" or "Keys" or "Values" or "Key" or "Value");
    }

    private static bool Field(IFieldReferenceOperation field)
    {
        var symbol = field.Field;
        var type = symbol.ContainingType;
        if (!Bcl(type)) return Application(type) && !symbol.IsStatic && field.Instance != null && Rooted(field.Instance);
        if (Name(type) == "System.StringComparison") return symbol.Name is "Ordinal" or "OrdinalIgnoreCase";
        if (type.TypeKind == TypeKind.Enum) return Name(type) is "System.DayOfWeek" or "System.DateTimeKind" or "System.MidpointRounding";
        return symbol.IsConst && (Numeric(type) || Name(type) is "System.Math" or "System.MathF") ||
            Name(type) == "string" && symbol.Name == "Empty" ||
            Name(type) is "System.DateTime" or "System.DateTimeOffset" && DateMembers.Contains(symbol.Name);
    }

    private static bool Method(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        var type = method.ContainingType;
        if (!Bcl(type)) return false;
        var name = Name(type);
        if (name is "System.Math" or "System.MathF") return MathMembers.Contains(method.Name);
        if (name == "string")
        {
            if (method.Parameters.Any(p => Name(p.Type) == "System.StringComparison"))
                return method.Name is "Equals" or "Compare" or "Contains" or "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf" &&
                    invocation.Arguments.Where(a => Name(a.Parameter!.Type) == "System.StringComparison")
                        .All(a => a.Value is IFieldReferenceOperation f && Field(f));
            return method.Name is "IsNullOrEmpty" or "IsNullOrWhiteSpace" or "Substring" or "ToUpperInvariant" or "ToLowerInvariant" or
                "Trim" or "TrimStart" or "TrimEnd" or "Replace" or "Concat" or "Equals" or "Contains" &&
                method.Parameters.All(p => p.Type.SpecialType is SpecialType.System_String or SpecialType.System_Char);
        }
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return method.Name == "GetValueOrDefault";
        if (name is "System.DateTime" or "System.DateTimeOffset") return DateMembers.Contains(method.Name);
        if (name == "System.Linq.Enumerable" && LinqMembers.Contains(method.Name))
        {
            if (method.Parameters.Any(p => Name(p.Type).Contains("Comparer", StringComparison.Ordinal))) return false;
            // Default comparisons on application elements can dispatch application Equals/CompareTo.
            if (method.Name is "Distinct" or "SequenceEqual" or "Max" or "Min" or "MaxBy" or "MinBy" or
                "Order" or "OrderDescending" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                var compared = method.TypeArguments.LastOrDefault();
                if (compared != null && (!Numeric(compared) || compared.SpecialType == SpecialType.System_String)) return false;
            }
            if (method.Name == "SelectMany" && invocation.Arguments.Any(a =>
                a.Value is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } &&
                Sequence(lambda.Symbol.ReturnType) && !lambda.Body.Operations.OfType<IReturnOperation>()
                    .All(r => r.ReturnedValue != null && MemorySource(r.ReturnedValue)))) return false;
            return invocation.Arguments.All(a => a.Parameter!.Type.TypeKind == TypeKind.Delegate
                ? a.Value is IDelegateCreationOperation { Target: IAnonymousFunctionOperation }
                : !Sequence(a.Parameter.Type) || MemorySource(a.Value));
        }
        return false;
    }

    private static bool Sequence(ITypeSymbol type) => type is IArrayTypeSymbol || type.AllInterfaces.Any(i =>
        i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) ||
        type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;

    private static bool InMemory(ITypeSymbol type) => type is IArrayTypeSymbol || Bcl(type) && Name(type) is
        "System.Collections.Generic.List<T>" or "System.Collections.Generic.Dictionary<TKey, TValue>" or
        "System.Collections.Generic.Dictionary<TKey, TValue>.KeyCollection" or "System.Collections.Generic.Dictionary<TKey, TValue>.ValueCollection" or
        "System.Collections.Generic.HashSet<T>" or "System.Collections.Generic.KeyValuePair<TKey, TValue>";

    private static bool MemorySource(IOperation operation) => operation is IConversionOperation c ? MemorySource(c.Operand) :
        operation is IInvocationOperation call && Name(call.TargetMethod.ContainingType) == "System.Linq.Enumerable" ||
        operation.Type != null && InMemory(operation.Type);

    private static bool Rooted(IOperation operation) => operation switch
    {
        ILocalReferenceOperation l => l.Local.Name == "context",
        IParameterReferenceOperation p => p.Parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction },
        IPropertyReferenceOperation p => p.Instance != null && Rooted(p.Instance),
        IFieldReferenceOperation f => f.Instance != null && Rooted(f.Instance),
        IArrayElementReferenceOperation a => Rooted(a.ArrayReference),
        IConversionOperation c => Rooted(c.Operand),
        IConditionalAccessInstanceOperation => true,
        _ => false
    };
}
