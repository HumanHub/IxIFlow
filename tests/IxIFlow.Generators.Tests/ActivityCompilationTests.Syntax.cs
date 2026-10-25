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
    public sealed class Syntax
    {
        [Theory]
        [InlineData("declarations", "}\n[global::IxIFlow.ActivitySdk.WorkflowActivity(\"acme.extra\")]\npublic sealed class Extra : IAsyncActivity {\npublic Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;", "}")]
        [InlineData("body", "}\npublic int Escaped { get; set; }\npublic void Other() {", "}")]
        [InlineData("body", "#region injected\nreturn;\n#endregion", "#region")]
        [InlineData("declarations", "#region injected\nprivate int Number() => 1;\n#endregion", "#region")]
        [InlineData("declarations", "#if false\n}\n#endif", "#if")]
        [InlineData("body", "#if false\n}\n#endif", "#if")]
        [InlineData("body", "#line 500 \"other.cs\"\nreturn;", "#line")]
        [InlineData("declarations", "#line 500 \"other.cs\"\nprivate int Number() => 1;", "#line")]
        [InlineData("declarations", "private void Incomplete() {", "{")]
        [InlineData("body", "/* swallowed closing brace", "/*")]
        [InlineData("body", "await Task.Yield();\n/* swallowed closing brace", "/*")]
        [InlineData("declarations", "private int Number() => 1;\n/* swallowed closing brace", "/*")]
        public void AuthoredCodeMustStayInsideItsContainer(string member, string code, string anchor)
        {
            var yaml = "activity: {key: acme.test, version: 1}\n" +
                (member == "declarations" ? "body: |\n  return;\n" : "") +
                member + ": |4-\n    " + code.Replace("\n", "\n    ");
            var result = ActivityCompilation.Compile(yaml, "contained.activity.yaml");
            Assert.False(result.Success);
            Assert.Empty(result.Assembly);
            var error = Assert.Single(result.Diagnostics);
            Assert.Equal("IXIYAML001", error.Code);
            Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
            Assert.Equal("contained.activity.yaml", error.Path);
            var lines = yaml.Split('\n');
            var line = Array.FindIndex(lines, member == "body" ? 2 : 4, text => text.Contains(anchor, StringComparison.Ordinal));
            Assert.Equal(line + 1, error.Line);
            Assert.Equal(lines[line].IndexOf(anchor, StringComparison.Ordinal) + 1, error.Column);
            var document = Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml);
            var exception = Assert.Throws<WorkflowSourceException>(() =>
                ActivitySourceTranslator.Translate(document, yaml, "contained.activity.yaml"));
            Assert.Equal(error.Path, exception.Path);
            Assert.Equal(error.Line, exception.Line);
            Assert.Equal(error.Column, exception.Column);
        }

        [Theory]
        [InlineData("|", 4)]
        [InlineData("|-", 4)]
        [InlineData("|+", 4)]
        [InlineData("|2-", 2)]
        [InlineData("|4-", 4)]
        [InlineData("|4+", 4)]
        public void CodeBlocksEndBeforeTrailingYamlComments(string indicator, int indent)
        {
            var padding = new string(' ', indent);
            var comment = new string(' ', indent - 1) + "#error YAML-only comment\n";
            var yaml = "activity: {key: acme.test, version: 1}\nbody: " + indicator + "\n" +
                padding + "return;\n\n" + comment + "declarations: " + indicator + "\n" +
                padding + "private static int Number() => 1;\n\n" + comment;
            var result = ActivityCompilation.Compile(yaml, "bounds.activity.yaml");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            var document = Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml);
            var generated = ActivitySourceTranslator.Translate(document, yaml, "bounds.activity.yaml");
            Assert.DoesNotContain("YAML-only", generated);
            var invalid = yaml.Replace("return;", "MissingBody();");
            var error = Assert.Single(ActivityCompilation.Compile(invalid).Diagnostics, item => item.Code == "CS0103");
            Assert.Equal(3, error.Line);
            Assert.Equal(indent + 1, error.Column);
        }

        [Theory]
        [InlineData("public", false)]
        [InlineData("private", false)]
        [InlineData("public", true)]
        public void BoundNestedActivitiesAreRefusedAtTheirAttribute(string visibility, bool deeper)
        {
            var code = "[global::IxIFlow.ActivitySdk.WorkflowActivity(\"acme.extra\")]\n" + visibility +
                " sealed class Extra : IAsyncActivity\n{\npublic Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;\n}";
            if (deeper) code = "public class Helper\n{\n" + code + "\n}";
            var yaml = "activity: {key: acme.test, version: 1}\nbody: |\n  return;\ndeclarations: |\n  " + code.Replace("\n", "\n  ");
            var result = ActivityCompilation.Compile(yaml, "one.activity.yaml");
            Assert.False(result.Success);
            Assert.Empty(result.Assembly);
            var error = Assert.Single(result.Diagnostics);
            Assert.Equal("IXIYAML001", error.Code);
            Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
            Assert.Equal("one.activity.yaml", error.Path);
            var line = Array.FindIndex(yaml.Split('\n'), text => text.Contains("[global::", StringComparison.Ordinal));
            Assert.Equal(line + 1, error.Line);
            Assert.Equal(4, error.Column); // Attribute syntax begins after '['.
        }

        [Fact]
        public void ValidAuthoredSyntaxAndNestedHelpersCompileToOneDiscoverableActivity()
        {
            const string yaml = """"
                activity: {key: acme.syntax, version: 1}
                body: |
                  [global::System.ComponentModel.Description("{ local }")]
                  static string Local()
                  {
                      // } { These braces are comments.
                      /* { } */
                      var raw = """{ raw }""";
                      var verbatim = @"{ verbatim ""quoted"" }";
                      return raw + verbatim;
                  }
                  _ = new Helper().Echo(Local());
                  _ = new GenericHelper<int>().Echo(42);
                  _ = new PartialHelper().Echo(Local());
                declarations: |
                  [global::System.ComponentModel.Description("{ helper }")]
                  private sealed class Helper
                  {
                      public string Echo(string value) => value;
                      public sealed class Nested { }
                  }
                  private sealed class GenericHelper<T>
                  {
                      public T Echo(T value) => value;
                  }
                  private sealed partial class PartialHelper { public string Echo(string value) => Finish(value); }
                  private sealed partial class PartialHelper { private static string Finish(string value) => value; }
                """";
            var result = ActivityCompilation.Compile(yaml);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            var context = new AssemblyLoadContext("syntax-control", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(result.Assembly));
                Assert.Single(ActivityDescriptor.Scan(assembly, "acme.syntax", "1.0.0"));
                Assert.NotNull(assembly.GetType("IxIFlow.Activities.Acme.Syntax+Helper+Nested"));
                Assert.NotNull(assembly.GetType("IxIFlow.Activities.Acme.Syntax+GenericHelper`1"));
                Assert.NotNull(assembly.GetType("IxIFlow.Activities.Acme.Syntax+PartialHelper"));
            }
            finally { context.Unload(); }
        }

    }
}
