using System.Collections.Immutable;
using IxIFlow.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "W-03")]
public sealed class SwitchMutationAnalyzerTests
{
    [Theory]
    [InlineData("var alias = new List<int>(); void Rebind() { alias = items; throw new Exception(); } try { Rebind(); } catch { alias.Clear(); }", true)]
    [InlineData("var alias = new List<int>(); void Rebind() { alias = items; } Rebind(); alias.Clear();", true)]
    [InlineData("var alias = items; void Clear() { alias.Clear(); } Clear();", true)]
    [InlineData("var alias = new List<int>(); void Clear() { alias.Clear(); } Clear();", false)]
    [InlineData("var alias = new List<int>(); void Inner() { alias = items; } void Outer() { Inner(); } Outer(); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); void Recur() { if (flag) Recur(); alias.Clear(); alias = items; } Recur();", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias = new(); void Run() { rebind(); } Run(); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); void Clear() { alias.Clear(); } Action clear = Clear; alias = items; clear();", true)]
    [InlineData("Action clear = () => items.Clear(); clear();", true)]
    [InlineData("Action clear = delegate { items.Clear(); }; clear();", true)]
    [InlineData("Action clear = () => { var local = new List<int>(); local.Clear(); }; clear();", false)]
    [InlineData("var alias = new List<int>(); Action clear = () => alias.Clear(); clear();", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias.Clear();", true)]
    [InlineData("Action outer = () => { Action inner = () => items.Clear(); inner(); }; outer();", true)]
    [InlineData("var alias = new List<int>(); ref var slot = ref alias; slot = items; alias.Clear();", true)]
    [InlineData("ref var slot = ref data.Number; slot = 1;", true)]
    [InlineData("ref var slot = ref data.Numbers[0]; slot++;", true)]
    [InlineData("var alias = new List<int>(); ref var slot = ref alias; Replace(ref slot, items); alias.Clear();", true)]
    [InlineData("object replacement = new List<int>(); if (replacement is List<int> alias) alias.Clear();", false)]
    [InlineData("object replacement = items; if (replacement is List<int> alias) alias.Clear();", true)]
    [InlineData("object replacement = new List<int>(); if (replacement is List<int> { } alias) alias.Clear();", false)]
    [InlineData("var replacement = new List<int>(); if (replacement is var alias) alias.Clear();", false)]
    [InlineData("var replacement = new Holder(); if (replacement is { Items: var alias }) alias.Clear();", true)]
    [InlineData("object replacement = new List<int>(); if (replacement is not List<int> alias) return 0; alias.Clear();", false)]
    public async Task SwitchHelpersTrackClosuresReferencesAndPatterns(string body, bool mutation)
    {
        await Check("Pick(ctx.WorkflowData.Items, ctx.WorkflowData, ctx.WorkflowData.Number > 0)", body, mutation);
    }

    [Theory]
    [InlineData("new Holder { Values = { 1 } }.Values.Count", false)]
    [InlineData("new Holder { Items = { 1 } }.Items.Count", true)]
    [InlineData("new Holder { Getter = { 1 } }.Getter.Count", true)]
    public Task SwitchCollectionInitializerMatchesRuntime(string expression, bool mutation) => Check(expression, "", mutation);

    [Theory]
    [InlineData("var alias = new List<int>(); ref var slot = ref alias; slot = items; alias.Clear();")]
    [InlineData("var alias = new List<int>(); ref var slot = ref alias; Replace(ref slot, items); alias.Clear();")]
    public Task SwitchRefWritesAlsoInvalidateTheOriginalAlias(string body) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", body, true, "List.Clear (state mutation)");

    [Theory]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias = new(); Run(rebind); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => { alias = items; throw new Exception(); }; alias = new(); try { Run(rebind); } catch { alias.Clear(); }", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias = new(); _ = new Runner(rebind); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => { alias = items; throw new Exception(); }; alias = new(); try { _ = new Runner(rebind); } catch { alias.Clear(); }", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias = new(); void Local(Action action) => action(); Local(rebind); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias = new(); rebind(); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); void Rebind() => alias = items; Action rebind = Rebind; alias = new(); Run(rebind); alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => alias = items; alias = new(); void Outer() => Run(rebind); Outer(); alias.Clear();", true)]
    [InlineData("List<int> alias = new Box(items); alias.Clear();", true)]
    [InlineData("List<int> alias = flag ? new Box(items) : new Box(items); alias.Clear();", true)]
    [InlineData("new View(data).Value = 1;", true)]
    [InlineData("new View(data)[0] = 1;", true)]
    [InlineData("new View(data).Get() = 1;", true)]
    [InlineData("_ = new View(data).MutatingGetter;", true)]
    [InlineData("var counter = new OperatorCounter(items); counter++;", true)]
    [InlineData("var counter = new OperatorCounter(items); counter--;", true)]
    [InlineData("var counter = new OperatorCounter(items); counter += 1;", true)]
    [InlineData("var alias = new List<int>(); Action rebind = () => { alias = items; throw new Exception(); }; alias = new(); var counter = new CallbackCounter(rebind); alias = new(); try { counter++; } catch { alias.Clear(); }", true)]
    [InlineData("List<int> replacement = [1, 2]; replacement.Add(3);", false)]
    [InlineData("var alias = items; alias = new(); alias.Clear();", false)]
    // Lowered assignment-target captures remain unknown without a storage alias graph.
    [InlineData("var alias = items; alias = flag ? new List<int>() : new List<int>(); alias.Clear();", true)]
    [InlineData("int[] replacement = [1, 2]; replacement[0] = 3;", false)]
    [InlineData("BuiltCollection replacement = [1, 2]; replacement.Clear();", true)]
    [InlineData("var (left, right) = (1, 2); _ = left + right;", false)]
    [InlineData("var left = 0; var right = 0; (left, right) = (1, 2);", false)]
    [InlineData("var (left, (middle, right)) = (1, (2, 3)); _ = left + middle + right;", false)]
    [InlineData("var alias = new List<int>(); var other = new List<int>(); (alias, other) = (items, new List<int>()); alias.Clear();", true)]
    [InlineData("(data.Number, _) = (1, 2);", true)]
    [InlineData("_ = data.Immutable.Add(1).Count;", false)]
    [InlineData("_ = data.Immutable.Remove(1).Clear();", false)]
    [InlineData("_ = ((System.Collections.Immutable.IImmutableList<int>)data.Immutable).Add(1);", false)]
    [InlineData("_ = data.Persistent.Add(1).Count;", false)]
    [InlineData("data.Builder.Add(1);", true)]
    [InlineData("_ = data.Text.Append(1);", true)]
    public Task SwitchRound4MutationRules(string body, bool mutation) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", body, mutation);

    [Theory]
    [InlineData("var alias = items; if ((Flag)true) alias = new List<int>(); alias.Clear();")]
    [InlineData("new Accessors(items).Getter += 1;")]
    [InlineData("new Accessors(items).Setter++;")]
    [InlineData("new Accessors(items).Setter--;")]
    [InlineData("new Accessors(items)[0] += 1;")]
    [InlineData("var (left, right) = new Pair(items);")]
    [InlineData("if (new Pair(items) is (var left, var right)) { }")]
    [InlineData("var (left, (middle, right)) = new NestedPair(items);")]
    [InlineData("var (left, right) = new ExtensionPair(items);")]
    [InlineData("int left, right; (left, right) = (new Element(items), new Element(items));")]
    [InlineData("int left; (left, _) = new ElementPair(items);")]
    [InlineData("BuiltLists replacement = [items];")]
    [InlineData("_ = (int)new Element(items);")]
    [InlineData("_ = new Truth(items) + new Truth(items);")]
    [InlineData("if (new Truth(items)) { }")]
    [InlineData("_ = new Truth(items) && new Truth(items);")]
    [InlineData("_ = new Truth(items) || new Truth(items);")]
    public Task SwitchImplicitCallsHaveExplicitCallEffects(string body) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", body, true, helpers: ImplicitHelpers);

    [Theory]
    [InlineData("enumerator")] [InlineData("move")] [InlineData("current")] [InlineData("dispose")]
    public Task SwitchForeachAnalyzesEachImplicitMethod(string stage) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", "foreach (var value in new Sequence(items)) { }", true,
        helpers: ImplicitHelpers.Replace("/*" + stage + "*/", "items.Clear();"));

    [Theory]
    [InlineData("enumerator")] [InlineData("move")] [InlineData("current")] [InlineData("dispose")]
    public Task SwitchImplicitCallsHaveTheSamePurityChecks(string stage) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", "foreach (var value in new Sequence(items)) { }", false,
        member: "DateTime.Now", helpers: ImplicitHelpers.Replace("/*" + stage + "*/", "_ = DateTime.Now;"));

    [Theory]
    [InlineData("&&")] [InlineData("||")]
    public Task SwitchConditionalOperatorsRunTheirImplicitPurityChecks(string op) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", "_ = new Truth(items) " + op + " new Truth(items);", false,
        member: "DateTime.Now", helpers: ImplicitHelpers
            .Replace("value.Items.Clear(); return true;", "_ = DateTime.Now; return true;")
            .Replace("value.Items.Clear(); return false;", "_ = DateTime.Now; return false;"));

    [Theory]
    [InlineData("var (left, right) = new SafePair(); _ = left + right;", false)]
    [InlineData("if (new SafePair() is (var left, var right)) { }", false)]
    [InlineData("var alias = items; var view = new CallbackGetter(() => alias = items); view.Value += (alias = new List<int>()).Count; alias.Clear();", false)]
    [InlineData("var alias = items; var view = new CallbackView(() => alias = items); view.Value += (alias = new List<int>()).Count; alias.Clear();", true)]
    [InlineData("var alias = items; var counter = new ConvertibleCounter(() => alias = items); counter += (alias = new List<int>()).Count; alias.Clear();", false)]
    [InlineData("var replacement = new List<int>(); new ValueSink().Value = replacement; replacement.Clear();", false)]
    [InlineData("new ValueSink().Value = items;", true)]
    [InlineData("var alias = new List<int>(); var view = new CallbackView(() => { alias = items; throw new Exception(); }); alias = new(); try { view.Value += 1; } catch { alias.Clear(); }", true)]
    [InlineData("var alias = new List<int>(); var view = new CallbackView(() => { alias = items; throw new Exception(); }); alias = new(); try { view.Value++; } catch { alias.Clear(); }", true)]
    [InlineData("var alias = new List<int>(); var pair = new CallbackPair(() => { alias = items; throw new Exception(); }); alias = new(); try { var (left, right) = pair; } catch { alias.Clear(); }", true)]
    [InlineData("foreach (var value in new[] { 1, 2 }) { _ = value; }", false)]
    public Task SwitchImplicitBindingsAndExceptionalEffects(string body, bool mutation) => Check(
        "Pick(ctx.WorkflowData.Items, ctx.WorkflowData, false)", body, mutation, helpers: ImplicitHelpers);

    private const string ImplicitHelpers = """
        public class Flag
        {
            public static explicit operator Flag(bool value) => new();
            public static implicit operator bool(Flag value) => false;
        }
        public class Accessors(List<int> items)
        {
            public int Getter { get { items.Clear(); return 0; } set { } }
            public int Setter { get => 0; set { items.Clear(); } }
            public int this[int index] { get { items.Clear(); return 0; } set { } }
        }
        public class Pair(List<int> items)
        {
            public void Deconstruct(out int left, out int right) { items.Clear(); left = right = 0; }
        }
        public class NestedPair(List<int> items)
        {
            public void Deconstruct(out int left, out Pair right) { left = 0; right = new Pair(items); }
        }
        public class ExtensionPair(List<int> items) { public List<int> Items = items; }
        public static class PairExtensions
        {
            public static void Deconstruct(this ExtensionPair pair, out int left, out int right) { pair.Items.Clear(); left = right = 0; }
        }
        public class SafePair
        {
            public void Deconstruct(out int left, out int right) { left = right = 0; }
        }
        public class Element(List<int> items)
        {
            public static implicit operator int(Element value) { value.Items.Clear(); return 0; }
            private List<int> Items = items;
        }
        public class ElementPair(List<int> items)
        {
            public void Deconstruct(out Element left, out int right) { left = new Element(items); right = 0; }
        }
        public class Truth(List<int> items)
        {
            private List<int> Items = items;
            public static bool operator true(Truth value) { value.Items.Clear(); return true; }
            public static bool operator false(Truth value) { value.Items.Clear(); return false; }
            public static Truth operator &(Truth left, Truth right) => left;
            public static Truth operator |(Truth left, Truth right) => left;
            public static Truth operator +(Truth left, Truth right) { left.Items.Clear(); return left; }
        }
        [System.Runtime.CompilerServices.CollectionBuilder(typeof(ListFactory), nameof(ListFactory.Create))]
        public class BuiltLists : IEnumerable<List<int>>
        {
            public IEnumerator<List<int>> GetEnumerator() => ((IEnumerable<List<int>>)Array.Empty<List<int>>()).GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        public static class ListFactory
        {
            public static BuiltLists Create(ReadOnlySpan<List<int>> values) { values[0].Clear(); return new(); }
        }
        public class ValueSink { public List<int> Value { set { value.Clear(); } } }
        public class CallbackGetter(Action callback) { public int Value { get { callback(); return 0; } set { } } }
        public class ConvertibleCounter(Action callback)
        {
            private Action Callback = callback;
            public static implicit operator int(ConvertibleCounter value) { value.Callback(); return 0; }
            public static implicit operator ConvertibleCounter(int value) => null!;
        }
        public class CallbackView(Action callback) { public int Value { get => 0; set { callback(); } } }
        public class CallbackPair(Action callback)
        {
            public void Deconstruct(out int left, out int right) { callback(); left = right = 0; }
        }
        public class Sequence(List<int> items)
        {
            public Cursor GetEnumerator() { /*enumerator*/ return new Cursor(items); }
        }
        public class Cursor(List<int> items) : IDisposable
        {
            public bool MoveNext() { /*move*/ return false; }
            public int Current { get { /*current*/ return 0; } }
            public void Dispose() { /*dispose*/ }
        }
        """;

    private static async Task Check(string expression, string body, bool mutation, string? member = null, string helpers = "")
    {
        var source = Verify.Usings + $$"""
            {{helpers}}
            public class Data
            {
                public List<int> Items = new(); public int Number; public int[] Numbers = new int[1];
                public System.Collections.Immutable.ImmutableList<int> Immutable = System.Collections.Immutable.ImmutableList<int>.Empty;
                public System.Collections.Immutable.ImmutableList<int>.Builder Builder = System.Collections.Immutable.ImmutableList.CreateBuilder<int>();
                public Persistent Persistent = new(); public System.Text.StringBuilder Text = new();
            }
            public class Runner { public Runner(Action action) => action(); }
            public class Box(List<int> items) { public static implicit operator List<int>(Box box) => box.Items; private List<int> Items = items; }
            public class View(Data data)
            {
                public ref int Value => ref data.Number;
                public ref int this[int index] => ref data.Numbers[index];
                public ref int Get() => ref data.Number;
                public ref int MutatingGetter { get { data.Items.Clear(); return ref data.Number; } }
            }
            public class OperatorCounter(List<int> items)
            {
                public List<int> Items = items;
                public static OperatorCounter operator ++(OperatorCounter value) { value.Items.Clear(); return value; }
                public static OperatorCounter operator --(OperatorCounter value) { value.Items.Clear(); return value; }
                public static OperatorCounter operator +(OperatorCounter value, int amount) { value.Items.Clear(); return value; }
            }
            public class CallbackCounter(Action callback)
            {
                public static CallbackCounter operator ++(CallbackCounter value) { value.Run(); return value; }
                private void Run() => callback();
            }
            public class Persistent : System.Collections.IEnumerable
            {
                public int Count => 0;
                public Persistent Add(int value) => new();
                public System.Collections.IEnumerator GetEnumerator() => Array.Empty<int>().GetEnumerator();
            }
            [System.Runtime.CompilerServices.CollectionBuilder(typeof(CollectionFactory), nameof(CollectionFactory.Create))]
            public class BuiltCollection : System.Collections.Generic.IEnumerable<int>
            {
                public void Clear() { }
                public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)Array.Empty<int>()).GetEnumerator();
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class CollectionFactory { public static BuiltCollection Create(ReadOnlySpan<int> values) => new(); }
            public class Holder { public Counter Values; public List<int> Items = new(); public List<int> Getter => new(); }
            public struct Counter : System.Collections.IEnumerable
            {
                public int Count;
                public void Add(int value) { Count += value; }
                public System.Collections.IEnumerator GetEnumerator() => Array.Empty<int>().GetEnumerator();
            }
            public class Author
            {
                public void Build() => Workflow.Create<Data>("Mutation").Switch(ctx => {{expression}}, c => {});
                private static void Replace(ref List<int> slot, List<int> value) { slot = value; }
                private static void Run(Action action) => action();
                private static int Pick(List<int> items, Data data, bool flag) { {{body}} return 1; }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "IxIFlow*.dll"))
            .Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("SwitchMutation", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.Equal(mutation, diagnostics.Any(d => d.Id == "IXIF0101" && d.GetMessage().Contains("state mutation")));
        if (member != null) Assert.Contains(diagnostics, d => d.GetMessage().Contains(member));
    }
}
