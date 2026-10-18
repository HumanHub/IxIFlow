using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-03")]
public sealed class LambdaCodeTokenTests
{
    [Theory]
    [InlineData(OptimizationLevel.Debug)]
    [InlineData(OptimizationLevel.Release)]
    public void UnrelatedEditsPreserveWorkflowFingerprintsButLambdaEditsDoNot(OptimizationLevel optimization)
    {
        var edited = AddUnrelatedCode(WorkflowSource);
        using var first = new CompiledSource(WorkflowSource, optimization);
        using var second = new CompiledSource(edited, optimization);
        using var constant = new CompiledSource(edited.Replace("+ limit + 3", "+ limit + 4"), optimization);
        using var call = new CompiledSource(edited.Replace("=> Adjust(", "=> Other("), optimization);
        var original = first.Build();
        var unrelated = second.Build();
        var originalMapping = original.Steps[0].InputMappings.Single().SourceFunction!;
        var unrelatedMapping = unrelated.Steps[0].InputMappings.Single().SourceFunction!;
        Assert.NotEqual(originalMapping.Method.DeclaringType!.FullName, unrelatedMapping.Method.DeclaringType!.FullName);
        Assert.False(originalMapping.Method.GetMethodBody()!.GetILAsByteArray()!
            .SequenceEqual(unrelatedMapping.Method.GetMethodBody()!.GetILAsByteArray()!));

        var fingerprint = new WorkflowScopeCatalog(original).Fingerprint;
        Assert.Equal(fingerprint, new WorkflowScopeCatalog(unrelated).Fingerprint);
        Assert.NotEqual(fingerprint, new WorkflowScopeCatalog(constant.Build()).Fingerprint);
        Assert.NotEqual(fingerprint, new WorkflowScopeCatalog(call.Build()).Fingerprint);
    }

    [Theory]
    [InlineData("value > 3", "value > 4")]
    [InlineData("value + limit", "value - limit")]
    [InlineData("Keep", "Retain")]
    public void NestedLambdasAndLocalFunctionsSurviveRenumberingAndStillDetectCodeChanges(string before, string after)
    {
        using var first = new CompiledSource(CodeSource);
        using var unrelated = new CompiledSource(AddUnrelatedCode(CodeSource));
        using var changed = new CompiledSource(CodeSource.Replace(before, after));
        var original = first.Code();
        Assert.Equal(original, unrelated.Code());
        Assert.NotEqual(original, changed.Code());
        Assert.Contains("+", original);
    }

    [Theory]
    [InlineData("\"kept\"", "\"changed\"")]
    [InlineData("typeof(int)", "typeof(long)")]
    [InlineData("Echo<int>", "Echo<long>")]
    [InlineData("holder.First", "holder.Second")]
    public void ResolvedOperandsStillDistinguishAuthoredChanges(string before, string after)
    {
        using var first = new CompiledSource(CodeSource);
        using var changed = new CompiledSource(CodeSource.Replace(before, after));
        Assert.NotEqual(first.Code(), changed.Code());
    }

    [Fact]
    public void ClosedGenericTypeAndMethodContextsAreResolved()
    {
        using var first = new CompiledSource(CodeSource);
        using var unrelated = new CompiledSource(AddUnrelatedCode(CodeSource));
        Assert.Equal(first.GenericCode(typeof(int), typeof(string)), unrelated.GenericCode(typeof(int), typeof(string)));
        Assert.NotEqual(first.GenericCode(typeof(int), typeof(string)), first.GenericCode(typeof(long), typeof(string)));
        Assert.NotEqual(first.GenericCode(typeof(int), typeof(string)), first.GenericCode(typeof(int), typeof(object)));
    }

    [Fact]
    public void StandaloneSignaturesResolveEmbeddedTypeTokens()
    {
        using var first = new CompiledSource(CodeSource);
        using var unrelated = new CompiledSource(AddUnrelatedCode(CodeSource));
        using var changed = new CompiledSource(CodeSource.Replace("delegate*<Holder, int>", "delegate*<OtherHolder, int>"));
        Assert.Equal(first.Code("Indirect"), unrelated.Code("Indirect"));
        Assert.NotEqual(first.Code("Indirect"), changed.Code("Indirect"));
    }

    [Fact]
    public void ResolvedOperandsIgnoreAssemblyVersions()
    {
        using var first = new CompiledSource(CodeSource.Replace("namespace Sample;",
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]\nnamespace Sample;"));
        using var second = new CompiledSource(CodeSource.Replace("namespace Sample;",
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")]\nnamespace Sample;"));
        Assert.Equal(first.Code(), second.Code());
    }

    [Fact]
    public void LocalVariableTypesArePartOfTheCode()
    {
        Assert.NotEqual(LocalCode(typeof(int)), LocalCode(typeof(long)));

        static string LocalCode(Type local)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LocalTypes"), AssemblyBuilderAccess.RunAndCollect);
            var type = assembly.DefineDynamicModule("Code").DefineType("Sample", TypeAttributes.Public);
            var method = type.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.DeclareLocal(local);
            il.Emit(OpCodes.Ret);
            return LambdaCode.Of(type.CreateType()!.GetMethod("Read")!);
        }
    }

    [Fact]
    public void UnresolvedTokensDoNotContributeTheirRawValue()
    {
        Assert.Equal(UnresolvedCode(0x0600FFFE), UnresolvedCode(0x0600FFFF));

        static string UnresolvedCode(int token)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Unresolved"), AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("Code");
            var type = module.DefineType("Sample", TypeAttributes.Public);
            var method = type.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Call, token);
            il.Emit(OpCodes.Ret);
            return LambdaCode.Of(type.CreateType()!.GetMethod("Read")!);
        }
    }

    private static string AddUnrelatedCode(string source) => source
        .Replace("// before types", "public class Noise { public string Text() => string.Concat(\"unrelated\", 42); }")
        .Replace("// before members", "public static Func<int, int> Before() { int offset = 2; return value => value + offset; }")
        .Replace("// before workflow", "Func<int, int> unused = value => value + 100; GC.KeepAlive(unused);")
        .Replace("// after members", "public static string After() => NewMethod(); private static string NewMethod() => \"extra literal\";");

    private sealed class CompiledSource : IDisposable
    {
        private readonly AssemblyLoadContext context = new(null, isCollectible: true);
        private readonly Assembly assembly;

        public CompiledSource(string source, OptimizationLevel optimization = OptimizationLevel.Debug)
        {
            try
            {
                var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                    .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location])
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var compilation = CSharpCompilation.Create("IxIFlow.FingerprintFixture",
                    [CSharpSyntaxTree.ParseText(source)], paths.Select(path => MetadataReference.CreateFromFile(path)),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization, allowUnsafe: true));
                using var stream = new MemoryStream();
                var emitted = compilation.Emit(stream);
                Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
                stream.Position = 0;
                assembly = context.LoadFromStream(stream);
            }
            catch { context.Unload(); throw; }
        }

        public WorkflowDefinition Build() => (WorkflowDefinition)assembly.GetType("Sample.Definition")!
            .GetMethod("Build")!.Invoke(null, null)!;

        public string Code(string name = "Read") => LambdaCode.Of(assembly.GetType("Sample.Definition")!.GetMethod(name)!);

        public string GenericCode(Type owner, Type argument) => LambdaCode.Of(assembly.GetType("Sample.GenericDefinition`1")!
            .MakeGenericType(owner).GetMethod("Read")!.MakeGenericMethod(argument));

        public void Dispose() => context.Unload();
    }

    private const string WorkflowSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using IxIFlow;
        using IxIFlow.Builders;
        using IxIFlow.Core;
        namespace Sample;
        // before types
        public sealed class Data { public int Total { get; set; } }
        public sealed class Activity : IAsyncActivity
        {
            public int Value { get; set; }
            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
        public static class Definition
        {
            // before members
            public static WorkflowDefinition Build()
            {
                // before workflow
                int limit = 7;
                var definition = Workflow.Create<Data>("Stable tokens").Step<Activity>()
                    .WhileDo(ctx => Adjust(ctx.WorkflowData.Total + limit + 3) > 0, body => body.Step<Activity>()).Build();
                definition.Steps[0].InputMappings.Add(new PropertyMapping
                {
                    TargetProperty = "Value", SourceType = typeof(int), TargetType = typeof(int),
                    Direction = PropertyMappingDirection.Input,
                    SourceFunction = ctx => Adjust(((WorkflowContext<Data>)ctx).WorkflowData.Total + limit + 3)
                });
                return definition;
            }
            private static int Adjust(int value) => value + 1;
            private static int Other(int value) => value + 1;
            // after members
        }
        """;

    private const string CodeSource = """
        using System;
        using System.Linq;
        namespace Sample;
        // before types
        public class Holder { public int First; public int Second; }
        public class OtherHolder { }
        public static class Definition
        {
            // before members
            public static int Read()
            {
                int limit = 2;
                int Keep(int value) => value + limit;
                var holder = new Holder();
                var label = "kept";
                var type = typeof(int);
                return new[] { 1, 2, 3 }.Where(value => value > 3).Select(Keep).Sum() +
                    Echo<int>(holder.First) + label.Length + type.Name.Length;
            }
            private static int Echo<T>(int value) => value;
            public static unsafe int Indirect(void* address) => ((delegate*<Holder, int>)address)(null);
            // after members
        }
        public class GenericDefinition<T>
        {
            public T Value = default;
            public U Read<U>()
            {
                var copy = Value;
                return Identity<U>(copy);
            }
            private static U Identity<U>(T value) => default;
        }
        """;
}
