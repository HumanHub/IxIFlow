using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Generators.Tests;

public sealed partial class ActivityCompilationTests
{
    [Trait("Feature", "authoring")]
    [Trait("Spec", "F-19")]
    public sealed class BoundDefaults
    {
        [Theory]
        [InlineData("@Int32", "Ready", false)]
        [InlineData("@Int32", "100", false)]
        [InlineData("@Int32?", "Ready", false)]
        [InlineData("Nullable<@Int32>", "Ready", false)]
        [InlineData("global::System.Nullable<@Int32>", "Ready", false)]
        [InlineData("Nullable<int>", "Ready", true)]
        [InlineData("Nullable<global::System.Int32>", "100.1", true)]
        [InlineData("Nullable<int>", "1e400", true)]
        public void NestedNumericNamesKeepTheAuthoredConversionType(string type, string literal, bool customNullable)
        {
            var declarations = customNullable ? CustomNullableDeclaration : "public enum Int32 { Ready = 100 }";

            var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: '" + type + "', default: " + literal +
                "}}\nbody: |\n  return;\ndeclarations: |\n  " + declarations.Replace("\n", "\n  ") + "\n";

            foreach (var document in ReadBoth(yaml))
            {
                Assert.Empty(ActivityDocumentValidator.Validate(document).Diagnostics);
                var written = WorkflowYamlSerializer.Write(document);
                var result = ActivityCompilation.Compile(written);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                Assert.Empty(result.Diagnostics);
                var context = new AssemblyLoadContext("nested-default-types", isCollectible: true);

                try
                {
                    using var reflection = context.EnterContextualReflection();
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                    AssertAuthoredDefault(activity.GetProperty("Value")!, literal, customNullable);
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        private const string CustomNullableDeclaration = """
                                                         [global::System.ComponentModel.TypeConverter(typeof(AuthoredConverter))]
                                                         public sealed class Nullable<T>
                                                         {
                                                             public string Text { get; }
                                                             public Nullable(string text) => Text = text;
                                                         }
                                                         public sealed class AuthoredConverter : global::System.ComponentModel.TypeConverter
                                                         {
                                                             public override object? ConvertFrom(global::System.ComponentModel.ITypeDescriptorContext? context,
                                                                 global::System.Globalization.CultureInfo? culture, object value) => new Nullable<int>((string)value);
                                                         }
                                                         """;

        [Fact]
        public void ReviewExampleKeepsTheNestedEnumDefault()
        {
            const string yaml = """
                                activity: {key: acme.example, version: 1}
                                input:
                                  Mode: {type: '@Int32', default: Ready}
                                body: |
                                  return;
                                declarations: |
                                  public enum Int32 { Ready }
                                """;

            foreach (var document in ReadBoth(yaml))
            {
                Assert.Empty(ActivityDocumentValidator.Validate(document).Diagnostics);
                var result = ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document));
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                Assert.Empty(result.Diagnostics);
                var context = new AssemblyLoadContext("review-enum", isCollectible: true);

                try
                {
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Example")!;
                    var property = activity.GetProperty("Mode")!;
                    AssertAuthoredDefault(property, "Ready", customNullable: false);

                    Assert.Equal(0,
                        Convert.ToInt32(property.GetCustomAttribute<DefaultValueAttribute>()!.Value, CultureInfo.InvariantCulture));
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        [Theory]
        [InlineData("System.Int32", "Ready", "", false)]
        [InlineData("@System.@Int32", "100", "", false)]
        [InlineData("System.Nullable<int>", "Ready", "", true)]
        [InlineData("System.Nullable<int>", "100.1", "", true)]
        [InlineData("Int32", "Ready", "", false)]
        [InlineData("@Int32", "100", "", false)]
        [InlineData("NumericNames.Int32", "Ready", "usings: [ActivityDefaultImports]\n", false)]
        [InlineData("NumericNames.@Int32", "100", "usings: [ActivityDefaultImports]\n", false)]
        public void PreparedReferencesAndUsingsParticipateInDefaultTypeBinding(string type, string literal, string usings,
            bool customNullable)
        {
            var directory = Path.Combine(Path.GetTempPath(), "actdoc-bound-defaults-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var referencePath = Path.Combine(directory, "AuthoredDefaultTypes.dll");

                var referenceSource = "#nullable enable\nnamespace IxIFlow.Activities.Acme.System { public enum Int32 { Ready = 100 }\n" +
                    CustomNullableDeclaration + "\n}\nnamespace IxIFlow.Activities.Acme { public enum Int32 { Ready = 100 } }\n" +
                    "namespace ActivityDefaultImports { public static class NumericNames { public enum Int32 { Ready = 100 } } }";

                var reference = CSharpCompilation.Create("AuthoredDefaultTypes", [CSharpSyntaxTree.ParseText(referenceSource)],
                    ActivityCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                using (var output = File.Create(referencePath))
                {
                    var emitted = reference.Emit(output);
                    Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
                    Assert.Empty(emitted.Diagnostics);
                }

                var yaml = "activity: {key: acme.defaults, version: 1}\n" + usings +
                    "input:\n  Value: {type: '" + type + "', default: " + literal + "}\n" +
                    "  Framework: {type: 'global::System.Nullable<global::System.Int32>', default: 1e2}\n" +
                    "  Keyword: {type: int, default: 100.0}\nbody: |\n  return;\n";

                foreach (var document in ReadBoth(yaml))
                {
                    Assert.Empty(ActivityDocumentValidator.Validate(document).Diagnostics);
                    var options = new ActivityCompilationOptions { PackageReferencePaths = [referencePath] };
                    var result = ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document), options: options);
                    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                    Assert.Empty(result.Diagnostics);
                    var context = new AssemblyLoadContext("referenced-default-types", isCollectible: true);

                    try
                    {
                        using var reflection = context.EnterContextualReflection();
                        context.LoadFromStream(new MemoryStream(File.ReadAllBytes(referencePath)));

                        var activity =
                            context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;

                        AssertAuthoredDefault(activity.GetProperty("Value")!, literal, customNullable);

                        foreach (var name in new[] { "Framework", "Keyword" })
                            Assert.Equal(100,
                                Assert.IsType<int>(activity.GetProperty(name)!.GetCustomAttribute<DefaultValueAttribute>()!.Value));
                    }
                    finally
                    {
                        context.Unload();
                    }

                    // Global framework names retain their numeric rules even with a nearer System namespace.
                    document.Input["Framework"] = document.Input["Framework"] with { Default = new DocumentNumber("100.1") };

                    var refused = Assert.Single(ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document), options: options)
                        .Diagnostics);

                    Assert.Equal("ACTD010", refused.Code);
                }
            }
            finally
            {
                File.Delete(Path.Combine(directory, "AuthoredDefaultTypes.dll"));
                Directory.Delete(directory);
            }
        }

        [Theory]
        [InlineData("int", true)]
        [InlineData("int /* keyword */ ?", true)]
        [InlineData("global::System.Int32", true)]
        [InlineData("global::System.Nullable<global::System.Int32>", true)]
        [InlineData("Int32", false)]
        [InlineData("@Int32", false)]
        [InlineData("System.Int32", false)]
        [InlineData("Nullable<int>", false)]
        [InlineData("System.Nullable<int>", false)]
        [InlineData("global::System.Nullable<System.Int32>", false)]
        public void DocumentNumericRulesDeferNamesThatRequireBinding(string type, bool refused)
        {
            var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: '" + type +
                "', default: 100.1}}\nbody: |\n  return;\n";

            foreach (var document in ReadBoth(yaml))
            {
                var diagnostics = ActivityDocumentValidator.Validate(document).Diagnostics;

                if (refused)
                    Assert.Equal("ACTD010", Assert.Single(diagnostics).Code);
                else
                    Assert.Empty(diagnostics);

                // Without shadowing declarations/references all of these names bind to int.
                var compiled = Assert.Single(ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document)).Diagnostics);
                Assert.Equal("ACTD010", compiled.Code);
            }
        }

        [Theory]
        [InlineData("Value")]
        [InlineData("Café")]
        [InlineData("Δείγμα")]
        [InlineData("変数")]
        public void ExactMemberNamesRetainBoundIntegerDefaultsInBothReaders(string name)
        {
            foreach (var direction in new[] { "input", "inOut" })
            {
                var yaml = "activity: {key: acme.defaults, version: 1}\n" + direction + ":\n  " +
                    System.Text.Json.JsonSerializer.Serialize(name) + ": {type: System.Int32, default: 1e2}\nbody: |\n  return;\n";

                foreach (var document in ReadBoth(yaml))
                {
                    Assert.Empty(ActivityDocumentValidator.Validate(document).Diagnostics);
                    var result = ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document));
                    Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                    Assert.Empty(result.Diagnostics);
                    var context = new AssemblyLoadContext("exact-member-name", isCollectible: true);

                    try
                    {
                        var activity =
                            context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;

                        var property = Assert.Single(activity.GetProperties());
                        Assert.Equal(name, property.Name);
                        Assert.Equal(typeof(int), property.PropertyType);

                        var metadata = Assert.Single(property.GetCustomAttributesData(),
                            item => item.AttributeType == typeof(DefaultValueAttribute));

                        Assert.Equal(typeof(int), metadata.ConstructorArguments[0].Value);
                        Assert.Equal("100", metadata.ConstructorArguments[1].Value);
                        Assert.Equal(100, Assert.IsType<int>(property.GetCustomAttribute<DefaultValueAttribute>()!.Value));
                    }
                    finally
                    {
                        context.Unload();
                    }
                }

                foreach (var document in ReadBoth(yaml.Replace("1e2", "100.1")))
                {
                    var written = WorkflowYamlSerializer.Write(document);
                    var refused = Assert.Single(ActivityCompilation.Compile(written, "defaults.yaml").Diagnostics);
                    Assert.Equal("ACTD010", refused.Code);
                    Assert.Equal(WorkflowPublishStage.Validate, refused.Stage);
                    Assert.Equal("defaults.yaml", refused.Path);

                    var expected = DocumentModel.GetLocation(WorkflowYamlSerializer.ReadActivity(written),
                        direction + "." + name + ".default");

                    Assert.Equal(expected.Line, refused.Line);
                    Assert.Equal(expected.Column, refused.Column);
                }
            }
        }

        [Theory]
        [InlineData("input", false)]
        [InlineData("input", true)]
        [InlineData("output", false)]
        [InlineData("inOut", false)]
        [InlineData("inOut", true)]
        public void MissingBoundMemberIsAnInternalError(string direction, bool hasDefault)
        {
            var yaml = "activity: {key: acme.defaults, version: 1}\n" + direction + ":\n  Value: {type: System.Int32" +
                (hasDefault ? ", default: 1e2" : "") + "}\nbody: |\n  return;\n";

            var options = new ActivitySourceOptions();

            typeof(ActivitySourceOptions).GetProperty("MemberTypes", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(options, new Dictionary<string, ITypeSymbol>());

            var exception = Assert.Throws<InvalidOperationException>(() => ActivitySourceTranslator.Translate(
                WorkflowYamlSerializer.ReadActivity(yaml), yaml, "defaults.yaml", options));

            Assert.Contains(direction + ".Value", exception.Message);
        }
    }
}