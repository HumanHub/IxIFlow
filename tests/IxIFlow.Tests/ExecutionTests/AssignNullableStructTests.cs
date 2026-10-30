using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignNullableStructTests
{
    [Theory]
    [InlineData("direct", "Child.Items")]
    [InlineData("object", "Value.Child.Items")]
    [InlineData("object-list", "[].Value.Child.Items")]
    [InlineData("object-element", "Value.Child.Items")]
    [InlineData("list", "[].Child.Items")]
    [InlineData("array", "[].Child.Items")]
    [InlineData("dictionary", "[].Child.Items")]
    [InlineData("list-element", "Child.Items")]
    [InlineData("array-element", "Child.Items")]
    [InlineData("dictionary-value", "Child.Items")]
    public void NullableReplacementSubtreesThatRetainInitializerItemsAreRefused(string shape, string path)
    {
        var changed = new Data
        {
            Value = Replacement(),
            Nested = new Nested { Value = Replacement() },
            Objects = [new Nested { Value = Replacement() }],
            Values = [Replacement()],
            Array = [Replacement()],
            Keys = new() { ["key"] = Replacement() }
        };

        var restored = Assert.IsType<Data>(BranchData.Copy(changed));

        var value = shape switch
        {
            "direct" => restored.Value,
            "object" => restored.Nested.Value,
            "object-list" or "object-element" => restored.Objects[0].Value,
            "list" or "list-element" => restored.Values[0],
            "array" or "array-element" => restored.Array[0],
            _ => restored.Keys["key"]
        };

        Assert.Equal(new[] { 1, 9 }, value!.Value.Child.Items);

        var builder = new WorkflowBuilder<Data, object>([], "NullableReplacementRecovery");

        var error = Assert.Throws<WorkflowEngineException>(() =>
        {
            switch (shape)
            {
                case "direct":
                    builder.Assign(ctx => ctx.WorkflowData.Value, ctx => (Wrapper?)Replacement());

                    break;
                case "object":
                    builder.Assign(ctx => ctx.WorkflowData.Nested, ctx => new Nested { Value = Replacement() });

                    break;
                case "object-list":
                    builder.Assign(ctx => ctx.WorkflowData.Objects, ctx => new List<Nested> { new Nested { Value = Replacement() } });

                    break;
                case "object-element":
                    builder.Assign(ctx => ctx.WorkflowData.Objects[0], ctx => new Nested { Value = Replacement() });

                    break;
                case "list":
                    builder.Assign(ctx => ctx.WorkflowData.Values, ctx => new List<Wrapper?> { Replacement() });

                    break;
                case "array":
                    builder.Assign(ctx => ctx.WorkflowData.Array, ctx => new Wrapper?[] { Replacement() });

                    break;
                case "dictionary":
                    builder.Assign(ctx => ctx.WorkflowData.Keys, ctx => new Dictionary<string, Wrapper?> { { "key", Replacement() } });

                    break;
                case "list-element":
                    builder.Assign(ctx => ctx.WorkflowData.Values[0], ctx => (Wrapper?)Replacement());

                    break;
                case "array-element":
                    builder.Assign(ctx => ctx.WorkflowData.Array[0], ctx => (Wrapper?)Replacement());

                    break;
                default:
                    builder.Assign(ctx => ctx.WorkflowData.Keys["key"], ctx => (Wrapper?)Replacement());

                    break;
            }

            builder.WaitFor<Go>("saved").Build();
        });

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("ASSIGN002", error.Message);
        Assert.Contains($"'{path}'", error.Message);
        Assert.Contains("cannot be restored whole", error.Message);
    }

    [Fact]
    public void RestorableNullableStructsAndNullRemainAccepted()
    {
        Workflow.Create<SafeData>("SafeNullableReplacement")
            .Assign(ctx => ctx.WorkflowData.Value, ctx => (SafeWrapper?)new SafeWrapper(new SafeChild { Items = new List<int> { 9 } }))
            .Assign(ctx => ctx.WorkflowData.Values,
                ctx => new List<SafeWrapper?> { new SafeWrapper(new SafeChild { Items = new List<int> { 9 } }), null })
            .Assign(ctx => ctx.WorkflowData.Value, ctx => (SafeWrapper?)null).Build();

        var changed = new SafeData
        {
            Value = new SafeWrapper(new SafeChild { Items = [9] }),
            Values = [new SafeWrapper(new SafeChild { Items = [9] }), null]
        };

        var restored = Assert.IsType<SafeData>(BranchData.Copy(changed));
        Assert.Equal(new[] { 9 }, restored.Value!.Value.Child.Items);
        Assert.Equal(new[] { 9 }, restored.Values[0]!.Value.Child.Items);
        Assert.Null(restored.Values[1]);
    }

    [Fact]
    public void NullableStructPropertyConvertersRemainOpaqueInsideReplacementObjects()
    {
        Workflow.Create<ConvertedData>("NullablePropertyConverter")
            .Assign(ctx => ctx.WorkflowData.Nested, ctx => new ConvertedNested { Value = Replacement() }).Build();

        var changed = new ConvertedData { Nested = new ConvertedNested { Value = Replacement() } };
        var restored = Assert.IsType<ConvertedData>(BranchData.Copy(changed));
        Assert.Equal(new[] { 9 }, restored.Nested.Value!.Value.Child.Items);
    }

    [Fact]
    public void NullableStructTypeConvertersRemainOpaqueInCollectionElements()
    {
        Workflow.Create<ConvertedTypeData>("NullableTypeConverter")
            .Assign(ctx => ctx.WorkflowData.Value,
                ctx => (ConvertedWrapper?)new ConvertedWrapper(new UnsafeChild { Items = new List<int> { 9 } }))
            .Assign(ctx => ctx.WorkflowData.Values,
                ctx => new List<ConvertedWrapper?> { new ConvertedWrapper(new UnsafeChild { Items = new List<int> { 9 } }) }).Build();

        var changed = new ConvertedTypeData
        {
            Value = new ConvertedWrapper(new UnsafeChild { Items = [9] }),
            Values = [new ConvertedWrapper(new UnsafeChild { Items = [9] })]
        };

        var restored = Assert.IsType<ConvertedTypeData>(BranchData.Copy(changed));
        Assert.Equal(new[] { 9 }, restored.Value!.Value.Child.Items);
        Assert.Equal(new[] { 9 }, restored.Values[0]!.Value.Child.Items);
    }

    private static Wrapper Replacement() => new(new UnsafeChild { Items = [9] });

    public readonly record struct Wrapper(UnsafeChild Child);

    public sealed class UnsafeChild
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public List<int> Items { get; set; } = [1];
    }

    public sealed class Nested
    {
        public Wrapper? Value { get; set; }
    }

    public sealed class Data
    {
        public Wrapper? Value { get; set; }
        public Nested Nested { get; set; } = new();
        public List<Nested> Objects { get; set; } = [];
        public List<Wrapper?> Values { get; set; } = [];
        public Wrapper?[] Array { get; set; } = [];
        public Dictionary<string, Wrapper?> Keys { get; set; } = [];
    }

    public readonly record struct SafeWrapper(SafeChild Child);

    public sealed class SafeChild
    {
        public List<int> Items { get; set; } = [1];
    }

    public sealed class SafeData
    {
        public SafeWrapper? Value { get; set; }
        public List<SafeWrapper?> Values { get; set; } = [];
    }

    public sealed class ConvertedData
    {
        public ConvertedNested Nested { get; set; } = new();
    }

    public sealed class ConvertedNested
    {
        [JsonConverter(typeof(NullableWrapperConverter))]
        public Wrapper? Value { get; set; }
    }

    public sealed class NullableWrapperConverter : JsonConverter<Wrapper?>
    {
        public override Wrapper? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new Wrapper(new UnsafeChild { Items = JsonSerializer.Deserialize<List<int>>(ref reader, options)! });

        public override void Write(Utf8JsonWriter writer, Wrapper? value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value?.Child.Items, options);
    }

    [JsonConverter(typeof(WrapperConverter))]
    public readonly record struct ConvertedWrapper(UnsafeChild Child);

    public sealed class ConvertedTypeData
    {
        public ConvertedWrapper? Value { get; set; }
        public List<ConvertedWrapper?> Values { get; set; } = [];
    }

    public sealed class WrapperConverter : JsonConverter<ConvertedWrapper>
    {
        public override ConvertedWrapper Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(new UnsafeChild { Items = JsonSerializer.Deserialize<List<int>>(ref reader, options)! });

        public override void Write(Utf8JsonWriter writer, ConvertedWrapper value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Child.Items, options);
    }

    public sealed class Go
    {
    }
}