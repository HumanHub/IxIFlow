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

[Trait("Feature", "authoring")]
[Trait("Spec", "F-19")]
public sealed class ActivityCompilationTests
{
    private const string Source = """
        # Kept verbatim in both the assembly and symbols.
        activity: {key: acme.transform, version: 2, name: Transform, category: Test, icon: edit, designer: transform, description: Transform data}
        usings: [System.Text]
        input:
          Value: {type: int, default: 3, required: true, label: Value, help: Input value, control: text}
          Optional: {type: 'int?', default: null}
          Words: {type: 'List<string?>[]'}
          Text: {type: string, default: hello}
        output:
          Result: {type: int}
        inOut:
          Attempts: {type: int}
        body: |2-
          await Task.Yield();
          cancellationToken.ThrowIfCancellationRequested();
          Attempts++;
          Result = Twice(Value);
        declarations: |+
          private static int Twice(int value) => value * 2;
        """;

    [Fact]
    public async Task GeneratedActivityHasIdentityDirectionsMetadataAndExecutes()
    {
        var result = ActivityCompilation.Compile(Source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var context = new AssemblyLoadContext("activity-test", isCollectible: true);
        try
        {
            using var dll = new MemoryStream(result.Assembly);
            using var pdb = new MemoryStream(result.Pdb);
            var assembly = context.LoadFromStream(dll, pdb);
            Assert.Equal("acme.transform", assembly.GetName().Name);
            Assert.Equal(new Version(2, 0, 0, 0), assembly.GetName().Version);
            Assert.Equal("2.0.0", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            var type = assembly.GetType("IxIFlow.Activities.Acme.Transform", throwOnError: true)!;
            Assert.True(type.IsPublic && type.IsSealed && !type.IsGenericType && typeof(IAsyncActivity).IsAssignableFrom(type));
            var attribute = type.GetCustomAttribute<WorkflowActivityAttribute>()!;
            Assert.Equal("acme.transform", attribute.Key);
            Assert.Equal("2", attribute.Version);
            Assert.Equal("Transform", attribute.Name);
            Assert.Equal("Test", attribute.Category);
            Assert.Equal("edit", attribute.Icon);
            Assert.Equal("transform", attribute.Designer);
            Assert.Equal("Transform data", type.GetCustomAttribute<DescriptionAttribute>()!.Description);
            var input = type.GetProperty("Value")!;
            Assert.NotNull(input.GetCustomAttribute<ActivityInputAttribute>());
            Assert.NotNull(input.GetCustomAttribute<RequiredAttribute>());
            Assert.Equal(3, input.GetCustomAttribute<DefaultValueAttribute>()!.Value);
            Assert.Equal("Value", input.GetCustomAttribute<DisplayAttribute>()!.Name);
            Assert.Equal("Input value", input.GetCustomAttribute<DisplayAttribute>()!.Description);
            Assert.Equal("text", input.GetCustomAttribute<UIHintAttribute>()!.UIHint);
            Assert.Null(type.GetProperty("Optional")!.GetCustomAttribute<DefaultValueAttribute>()!.Value);
            Assert.Null(type.GetProperty("Words")!.GetCustomAttribute<DefaultValueAttribute>());
            Assert.Equal(typeof(List<string>[]), type.GetProperty("Words")!.PropertyType);
            Assert.NotNull(type.GetProperty("Result")!.GetCustomAttribute<ActivityOutputAttribute>());
            Assert.NotNull(type.GetProperty("Attempts")!.GetCustomAttribute<ActivityInputOutputAttribute>());
            var instance = (IAsyncActivity)Activator.CreateInstance(type)!;
            input.SetValue(instance, 5);
            await instance.ExecuteAsync(null!, CancellationToken.None);
            Assert.Equal(10, type.GetProperty("Result")!.GetValue(instance));
            Assert.Equal(1, type.GetProperty("Attempts")!.GetValue(instance));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => instance.ExecuteAsync(null!, cancellation.Token));
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData("usings: [Does.Not.Exist]", "body: |\n  await Task.Yield();", "Does.Not.Exist", "CS0246")]
    [InlineData("input: {Value: {type: 'Missing<int?>[]'}}", "body: |\n  await Task.Yield();", "Missing", "CS0246")]
    [InlineData("", "body: |2-\n  await Task.Yield();\n  MissingBody();", "MissingBody", "CS0103")]
    [InlineData("", "body: |\n  await Task.Yield();\ndeclarations: |2+\n  private MissingType Method() => null!;", "MissingType", "CS0246")]
    public void CompilerErrorsMapToAuthoredLinesAndColumns(string members, string body, string anchor, string code)
    {
        var yaml = "activity: {key: acme.test, version: 1}\n" + members + "\n" + body;
        var result = ActivityCompilation.Compile(yaml, "draft.activity.yaml");
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics, item => item.Code == code);
        var lines = yaml.Split('\n');
        var expectedLine = Array.FindIndex(lines, line => line.Contains(anchor, StringComparison.Ordinal));
        Assert.Equal("draft.activity.yaml", error.Path);
        Assert.Equal(expectedLine + 1, error.Line);
        Assert.Equal(lines[expectedLine].IndexOf(anchor, StringComparison.Ordinal) + 1, error.Column);
    }

    [Fact]
    public void SourceResourceAndEmbeddedPdbSourcesMatchExactlyAndEmitDeterministically()
    {
        var yaml = Source.Replace("\n", "\r\n") + "\r\n# trailing comment\r\n";
        var first = ActivityCompilation.Compile(yaml);
        var second = ActivityCompilation.Compile(yaml);
        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        Assert.Equal(first.Assembly, second.Assembly);
        Assert.Equal(first.Pdb, second.Pdb);
        var context = new AssemblyLoadContext("activity-source", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(first.Assembly));
            using var resource = assembly.GetManifestResourceStream(ActivityCompilation.SourceResourceName)!;
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            Assert.Equal(Encoding.UTF8.GetBytes(yaml), memory.ToArray());
            // The retrieved source remains a valid authoring document after a version edit.
            Assert.True(ActivityCompilation.Compile(Encoding.UTF8.GetString(memory.ToArray()).Replace("version: 2", "version: 3")).Success);
        }
        finally { context.Unload(); }
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(first.Pdb));
        var reader = provider.GetMetadataReader();
        var sources = reader.Documents.ToDictionary(handle => reader.GetString(reader.GetDocument(handle).Name), handle => EmbeddedSource(reader, handle));
        Assert.Equal(Encoding.UTF8.GetBytes(yaml), sources[ActivityCompilation.SourceResourceName]);
        var generated = ActivitySourceTranslator.Translate(Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml), yaml, ActivityCompilation.SourceResourceName);
        Assert.Equal(Encoding.UTF8.GetBytes(generated), sources["activity.g.cs"]);
    }

    [Fact]
    public async Task ExceptionStackUsesLiteralBodyLine()
    {
        const string yaml = "activity: {key: acme.fail, version: 1}\nbody: |4+\n    await Task.Yield();\n    throw new InvalidOperationException(\"expected\");\n";
        var result = ActivityCompilation.Compile(yaml, "throw.activity.yaml");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var context = new AssemblyLoadContext("activity-throw", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(result.Assembly), new MemoryStream(result.Pdb));
            var activity = (IAsyncActivity)Activator.CreateInstance(assembly.GetType("IxIFlow.Activities.Acme.Fail")!)!;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => activity.ExecuteAsync(null!));
            var frame = Assert.Single(new StackTrace(error, true).GetFrames(), item => item.GetFileName() == "throw.activity.yaml");
            Assert.Equal(4, frame.GetFileLineNumber());
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void ReferenceProfileExcludesApplicationImplementationAssemblies()
    {
        Assert.DoesNotContain(typeof(ActivityCompilationTests).Assembly.Location, ActivityCompilation.ReferencePaths());
        Assert.Contains(typeof(IAsyncActivity).Assembly.Location, ActivityCompilation.ReferencePaths());
        var result = ActivityCompilation.Compile("activity: {key: acme.test, version: 1}\ninput: {Value: {type: IxIFlow.Generators.Tests.ActivityCompilationTests}}\nbody: |\n  await Task.Yield();");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Stage == WorkflowPublishStage.Compile && item.Line == 2);
    }

    [Fact]
    public void IdentityMappingIsInjectiveAcrossNamespaceAndHyphenBoundaries()
    {
        var words = new[] { "a", "ab", "a1", "a-b", "ab-c", "a-bc", "a-b-c" };
        var keys = words.Concat(words.SelectMany(left => words.Select(right => left + "." + right))).ToArray();
        Assert.Equal(keys.Length, keys.Select(key => new ActivityPackageIdentity(key, 1).MetadataTypeName).Distinct(StringComparer.Ordinal).Count());
        var identity = new ActivityPackageIdentity("acme.send-email", 2);
        Assert.Equal("IxIFlow.Activities.Acme.SendEmail", identity.MetadataTypeName);
        Assert.Equal("acme.send-email", identity.PackageId);
        Assert.Equal("2", identity.ActivityVersion);
        Assert.Equal("2.0.0", identity.PackageVersion);
        Assert.Equal("2.0.0.0", identity.AssemblyVersion);
        Assert.Throws<ArgumentException>(() => new ActivityPackageIdentity("acme.2bad", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityPackageIdentity("acme.test", 65535));
    }

    [Theory]
    [InlineData("bool", "true", typeof(bool))]
    [InlineData("long", "42", typeof(long))]
    [InlineData("decimal", "12.5", typeof(decimal))]
    [InlineData("double", "12.5", typeof(double))]
    [InlineData("DayOfWeek", "Friday", typeof(DayOfWeek))]
    [InlineData("TimeSpan", "'00:00:05'", typeof(TimeSpan))]
    public void ScalarDefaultsEmitTypedMetadata(string typeName, string literal, Type expectedType)
    {
        var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: " + typeName + ", default: " + literal + "}}\nbody: |\n  await Task.Yield();";
        var result = ActivityCompilation.Compile(yaml);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var context = new AssemblyLoadContext("activity-default", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(result.Assembly));
            var property = assembly.GetType("IxIFlow.Activities.Acme.Defaults")!.GetProperty("Value")!;
            Assert.IsType(expectedType, property.GetCustomAttribute<DefaultValueAttribute>()!.Value);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void ExplicitPreparedReferencesAreUsedAndBodyCannotDivergeFromSource()
    {
        const string yaml = "activity: {key: acme.test, version: 1}\ninput: {Value: {type: IxIFlow.Generators.Tests.ActivityCompilationTests}}\nbody: |\n  await Task.Yield();";
        foreach (var options in new[]
                 {
                     new ActivityCompilationOptions { PackageReferencePaths = [typeof(ActivityCompilationTests).Assembly.Location] },
                     new ActivityCompilationOptions { SharedContractAssemblies = [typeof(ActivityCompilationTests).Assembly] }
                 })
            Assert.True(ActivityCompilation.Compile(yaml, options: options).Success);
        var document = Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml);
        Assert.Throws<WorkflowSourceException>(() => ActivitySourceTranslator.Translate(document with { Body = "throw new Exception();" }, yaml, "source.activity.yaml"));
    }

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
    [InlineData("decimal", "0.1234567890123456789012345678")]
    [InlineData("decimal", "79228162514264337593543950335")]
    [InlineData("long", "9223372036854775807")]
    [InlineData("double", "1.234567890123456789e100")]
    [InlineData("float", "1.23456789e20")]
    public void NumericDefaultsKeepExactLiteralAndTypedValueThroughBothReaders(string type, string literal)
    {
        var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: " + type +
            ", default: " + literal + "}}\nbody: |\n  return;\n";
        var json = "{\"activity\":{\"key\":\"acme.defaults\",\"version\":1},\"input\":{\"Value\":{\"type\":\"" + type +
            "\",\"default\":" + literal + "}},\"body\":\"return;\\n\"}";
        foreach (var document in new[] { Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml), Dsl.Documents.WorkflowJsonSerializer.ReadActivity(json) })
        {
            var writtenJson = Dsl.Documents.WorkflowJsonSerializer.Write(document);
            using var parsed = System.Text.Json.JsonDocument.Parse(writtenJson);
            Assert.Equal(literal, parsed.RootElement.GetProperty("input").GetProperty("Value").GetProperty("default").GetRawText());
            var writtenYaml = Dsl.Documents.WorkflowYamlSerializer.Write(Dsl.Documents.WorkflowJsonSerializer.ReadActivity(writtenJson));
            Assert.Contains(literal, writtenYaml);
            var result = ActivityCompilation.Compile(writtenYaml);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var context = new AssemblyLoadContext("exact-default", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(result.Assembly));
                var actual = assembly.GetType("IxIFlow.Activities.Acme.Defaults")!.GetProperty("Value")!.GetCustomAttribute<DefaultValueAttribute>()!.Value;
                object expected = type switch
                {
                    "decimal" => decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture),
                    "long" => long.Parse(literal, System.Globalization.CultureInfo.InvariantCulture),
                    "double" => double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture),
                    _ => float.Parse(literal, System.Globalization.CultureInfo.InvariantCulture)
                };
                Assert.Equal(expected, actual);
            }
            finally { context.Unload(); }
        }
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

    public static IEnumerable<object[]> IntegralTypes()
    {
        foreach (var (type, runtime) in new[] { ("sbyte", typeof(sbyte)), ("byte", typeof(byte)), ("short", typeof(short)),
                     ("ushort", typeof(ushort)), ("int", typeof(int)), ("uint", typeof(uint)), ("long", typeof(long)), ("ulong", typeof(ulong)) })
            foreach (var declaredType in NumericTypeSpellings(type, runtime)) yield return [declaredType, runtime];
    }

    private static IEnumerable<string> NumericTypeSpellings(string alias, Type runtime) =>
    [
        alias, alias + "?", "System." + runtime.Name, "global::System." + runtime.Name,
        runtime.Name, "@" + runtime.Name + "?",
        "global::System.Nullable<global::System." + runtime.Name + ">",
        alias + " /* nullable keyword */ ?",
        "System . Nullable /* spaced */ < " + alias + " >",
        "Nullable /* unqualified */ < System /* qualified */ . @" + runtime.Name + " >",
        "global /* alias */ :: @System . @Nullable < global::@System . @" + runtime.Name + " >",
        "@System /* escaped */ . @" + runtime.Name
    ];

    [Theory]
    [MemberData(nameof(IntegralTypes))]
    public void IntegralDefaultsConvertExactlyWithoutChangingAuthoredNumbers(string type, Type runtime)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var minimum = (IFormattable)runtime.GetField("MinValue")!.GetValue(null)!;
            var maximum = (IFormattable)runtime.GetField("MaxValue")!.GetValue(null)!;
            var literals = new[] { "1e2", "100.0", minimum.ToString(null, CultureInfo.InvariantCulture) + ".0",
                maximum.ToString(null, CultureInfo.InvariantCulture) + "e0", "100000000000000000000000000000000000000000000000000e-48" };
            var yaml = "activity: {key: acme.defaults, version: 1}\ninput:\n" + string.Join("\n", literals.Select((literal, index) =>
                "  Value" + index + ": {type: '" + type + "', default: " + literal + "}")) + "\nbody: |\n  return;\n";
            var json = WorkflowJsonSerializer.Write(WorkflowYamlSerializer.ReadActivity(yaml));
            foreach (var document in new[] { WorkflowYamlSerializer.ReadActivity(yaml), WorkflowJsonSerializer.ReadActivity(json) })
            {
                for (var index = 0; index < literals.Length; index++)
                    Assert.Equal(literals[index], Assert.IsType<DocumentNumber>(document.Input["Value" + index].Default).Literal);
                var written = WorkflowYamlSerializer.Write(document);
                Assert.All(literals, literal => Assert.Contains(literal, written));
                using var serialized = System.Text.Json.JsonDocument.Parse(WorkflowJsonSerializer.Write(document));
                for (var index = 0; index < literals.Length; index++)
                    Assert.Equal(literals[index], serialized.RootElement.GetProperty("input").GetProperty("Value" + index).GetProperty("default").GetRawText());
                var result = ActivityCompilation.Compile(written);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                Assert.Empty(result.Diagnostics);
                var context = new AssemblyLoadContext("integral-defaults", isCollectible: true);
                try
                {
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                    var expected = new[] { Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture), Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture),
                        minimum, maximum, Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture) };
                    for (var index = 0; index < literals.Length; index++)
                    {
                        var actual = activity.GetProperty("Value" + index)!.GetCustomAttribute<DefaultValueAttribute>()!.Value;
                        Assert.IsType(runtime, actual);
                        Assert.Equal(expected[index], actual);
                    }
                }
                finally { context.Unload(); }
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [MemberData(nameof(IntegralTypes))]
    public void FractionalAndOutOfRangeIntegralDefaultsAreRefused(string type, Type runtime)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var minimum = BigInteger.Parse(Convert.ToString(runtime.GetField("MinValue")!.GetValue(null), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            var maximum = BigInteger.Parse(Convert.ToString(runtime.GetField("MaxValue")!.GetValue(null), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            foreach (var literal in new[] { "100.1", "1001e-1", (minimum - 1).ToString(CultureInfo.InvariantCulture) + ".0",
                         (maximum + 1).ToString(CultureInfo.InvariantCulture) + "e0", "1e400", "1e-400",
                         "\" " + (minimum - 1).ToString(CultureInfo.InvariantCulture) + " \"",
                         "\" " + (maximum + 1).ToString(CultureInfo.InvariantCulture) + " \"",
                         "\"0x1" + new string('0', System.Runtime.InteropServices.Marshal.SizeOf(runtime) * 2) + "\"",
                         "\" 100.1 \"", "\"1e2\"", "\"100.0\"" })
                AssertInvalidNumericDefault(type, literal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("double", "1e400")]
    [InlineData("double", "-1e400")]
    [InlineData("double?", "1e400")]
    [InlineData("double?", "-1e400")]
    [InlineData("float", "1e400")]
    [InlineData("float", "-1e400")]
    [InlineData("float?", "1e400")]
    [InlineData("float?", "-1e400")]
    [InlineData("float", "3.402824e38")]
    [InlineData("double", "\"NaN\"")]
    [InlineData("float", "\"Infinity\"")]
    [InlineData("double?", "\"-Infinity\"")]
    public void NonFiniteFloatingDefaultsAreRefusedThroughBothReaders(string type, string literal) => AssertInvalidNumericDefault(type, literal);

    [Theory]
    [InlineData("double", "1.7976931348623157e308")]
    [InlineData("double?", "-1.7976931348623157e308")]
    [InlineData("double", "4.9406564584124654e-324")]
    [InlineData("double?", "-4.9406564584124654e-324")]
    [InlineData("float", "3.4028234663852886e38")]
    [InlineData("float?", "-3.4028234663852886e38")]
    [InlineData("float", "1.401298464324817e-45")]
    [InlineData("float?", "-1.401298464324817e-45")]
    public void FiniteFloatingBoundariesKeepTheirTypedValues(string type, string literal)
    {
        var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: '" + type + "', default: " + literal + "}}\nbody: |\n  return;\n";
        var json = WorkflowJsonSerializer.Write(WorkflowYamlSerializer.ReadActivity(yaml));
        foreach (var document in new[] { WorkflowYamlSerializer.ReadActivity(yaml), WorkflowJsonSerializer.ReadActivity(json) })
        {
            var result = ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            var context = new AssemblyLoadContext("floating-boundary", isCollectible: true);
            try
            {
                var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                var actual = activity.GetProperty("Value")!.GetCustomAttribute<DefaultValueAttribute>()!.Value;
                if (type.StartsWith("double", StringComparison.Ordinal)) Assert.Equal(double.Parse(literal, CultureInfo.InvariantCulture), Assert.IsType<double>(actual));
                else Assert.Equal(float.Parse(literal, CultureInfo.InvariantCulture), Assert.IsType<float>(actual));
            }
            finally { context.Unload(); }
        }
    }

    [Theory]
    [InlineData("sbyte", typeof(sbyte))]
    [InlineData("byte", typeof(byte))]
    [InlineData("short", typeof(short))]
    [InlineData("ushort", typeof(ushort))]
    [InlineData("int", typeof(int))]
    [InlineData("uint", typeof(uint))]
    [InlineData("long", typeof(long))]
    [InlineData("ulong", typeof(ulong))]
    public void IntegralConversionStringsKeepWhitespaceRadixAndExactBoundaries(string alias, Type runtime)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var minimum = runtime.GetField("MinValue")!.GetValue(null)!;
            var maximum = runtime.GetField("MaxValue")!.GetValue(null)!;
            var hundred = Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture);
            foreach (var type in new[] { alias, "System . Nullable /* conversion string */ < @System . @" + runtime.Name + " >" })
                AssertCompiledDefaults(type,
                [
                    ("\" 100 \"", hundred), ("\"0x64\"", hundred), ("\" 0X64 \"", hundred),
                    ("\"#64\"", hundred), ("\"&h64\"", hundred),
                    ("\" " + Convert.ToString(minimum, CultureInfo.InvariantCulture) + " \"", minimum),
                    ("\" " + Convert.ToString(maximum, CultureInfo.InvariantCulture) + " \"", maximum),
                    ("1e2", hundred), ("100.0", hundred)
                ]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void SignedHexadecimalStringsKeepTheConvertersTwosComplementValue() => AssertCompiledDefaults("int?",
        [("\"0xFFFFFFFF\"", -1), ("\"0x80000000\"", int.MinValue), ("\"0x7fffffff\"", int.MaxValue)]);

    public static IEnumerable<object[]> DecimalTypes() => NumericTypeSpellings("decimal", typeof(decimal)).Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(DecimalTypes))]
    public void DecimalDefaultsRequireExactCoefficientAndScale(string type)
    {
        (string Literal, object Expected)[] controls =
        [
            ("79228162514264337593543950335", decimal.MaxValue), ("-79228162514264337593543950335", decimal.MinValue),
            ("0.0000000000000000000000000001", 0.0000000000000000000000000001m),
            ("-1e-28", -0.0000000000000000000000000001m),
            ("7.9228162514264337593543950335", 7.9228162514264337593543950335m),
            ("1.234567890123456789012345678", 1.234567890123456789012345678m),
            ("0.10000000000000000000000000000", 0.1m), ("10e-29", 0.0000000000000000000000000001m),
            ("792281625142643375935439503350e-1", decimal.MaxValue), ("0e-99999999999999999999", 0m)
        ];
        AssertCompiledDefaults(type, controls.Concat(controls.Select(item => ("\" " + item.Literal + " \"", item.Expected))).ToArray());
        foreach (var literal in new[]
                 {
                     "79228162514264337593543950335.1", "-79228162514264337593543950335.1",
                     "79228162514264337593543950336", "-79228162514264337593543950336",
                     "7.9228162514264337593543950336", "-7.9228162514264337593543950336",
                     "0.12345678901234567890123456789", "1.23456789012345678901234567891",
                     "1e-29", "-1e-29", "1e400", "1e-400"
                 })
        {
            AssertInvalidNumericDefault(type, literal);
            AssertInvalidNumericDefault(type, "\"" + literal + "\"");
        }
    }

    public static IEnumerable<object[]> FloatingTypes()
    {
        foreach (var (alias, runtime) in new[] { ("float", typeof(float)), ("double", typeof(double)) })
            foreach (var type in NumericTypeSpellings(alias, runtime)) yield return [type, runtime];
    }

    [Theory]
    [MemberData(nameof(FloatingTypes))]
    public void FloatingTypeSyntaxCannotBypassFiniteConversion(string type, Type runtime)
    {
        AssertCompiledDefaults(type, [("1e2", Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture))]);
        foreach (var literal in new[] { "1e400", "-1e400", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"" })
            AssertInvalidNumericDefault(type, literal);
        if (runtime == typeof(float)) AssertInvalidNumericDefault(type, "1e39");
    }

    [Theory]
    [InlineData("System.Nullable // line comment\n <int>", typeof(int), "100.1")]
    [InlineData("global::System.Nullable<System.\\u0049nt32>", typeof(int), "2147483648")]
    [InlineData("global::System.Nullable // line comment\n <@System.@Single>", typeof(float), "1e39")]
    [InlineData("@System.@Nullable < global::System.\\u0044ecimal >", typeof(decimal), "1e-29")]
    public void LineCommentsAndUnicodeIdentifierEscapesKeepNumericRules(string type, Type runtime, string invalid)
    {
        AssertCompiledDefaults(type, [("1e2", Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture))]);
        AssertInvalidNumericDefault(type, invalid);
    }

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
            finally { context.Unload(); }
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
                Assert.Equal(0, Convert.ToInt32(property.GetCustomAttribute<DefaultValueAttribute>()!.Value, CultureInfo.InvariantCulture));
            }
            finally { context.Unload(); }
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
    public void PreparedReferencesAndUsingsParticipateInDefaultTypeBinding(string type, string literal, string usings, bool customNullable)
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
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                    AssertAuthoredDefault(activity.GetProperty("Value")!, literal, customNullable);
                    foreach (var name in new[] { "Framework", "Keyword" })
                        Assert.Equal(100, Assert.IsType<int>(activity.GetProperty(name)!.GetCustomAttribute<DefaultValueAttribute>()!.Value));
                }
                finally { context.Unload(); }

                // Global framework names retain their numeric rules even with a nearer System namespace.
                document.Input["Framework"] = document.Input["Framework"] with { Default = new DocumentNumber("100.1") };
                var refused = Assert.Single(ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document), options: options).Diagnostics);
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
            if (refused) Assert.Equal("ACTD010", Assert.Single(diagnostics).Code);
            else Assert.Empty(diagnostics);
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
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                    var property = Assert.Single(activity.GetProperties());
                    Assert.Equal(name, property.Name);
                    Assert.Equal(typeof(int), property.PropertyType);
                    var metadata = Assert.Single(property.GetCustomAttributesData(), item => item.AttributeType == typeof(DefaultValueAttribute));
                    Assert.Equal(typeof(int), metadata.ConstructorArguments[0].Value);
                    Assert.Equal("100", metadata.ConstructorArguments[1].Value);
                    Assert.Equal(100, Assert.IsType<int>(property.GetCustomAttribute<DefaultValueAttribute>()!.Value));
                }
                finally { context.Unload(); }
            }
            foreach (var document in ReadBoth(yaml.Replace("1e2", "100.1")))
            {
                var written = WorkflowYamlSerializer.Write(document);
                var refused = Assert.Single(ActivityCompilation.Compile(written, "defaults.yaml").Diagnostics);
                Assert.Equal("ACTD010", refused.Code);
                Assert.Equal(WorkflowPublishStage.Validate, refused.Stage);
                Assert.Equal("defaults.yaml", refused.Path);
                var expected = DocumentModel.GetLocation(WorkflowYamlSerializer.ReadActivity(written), direction + "." + name + ".default");
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

    private static IEnumerable<ActivityDocument> ReadBoth(string yaml)
    {
        yield return WorkflowYamlSerializer.ReadActivity(yaml);
        yield return WorkflowJsonSerializer.ReadActivity(WorkflowJsonSerializer.Write(WorkflowYamlSerializer.ReadActivity(yaml)));
    }

    private static void AssertAuthoredDefault(PropertyInfo property, string literal, bool customNullable)
    {
        var conversionType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var metadata = Assert.Single(property.GetCustomAttributesData(), item => item.AttributeType == typeof(DefaultValueAttribute));
        Assert.Equal(conversionType, metadata.ConstructorArguments[0].Value);
        Assert.Equal(literal, metadata.ConstructorArguments[1].Value);
        var value = property.GetCustomAttribute<DefaultValueAttribute>()!.Value;
        Assert.IsType(conversionType, value);
        if (customNullable) Assert.Equal(literal, conversionType.GetProperty("Text")!.GetValue(value));
        else Assert.Equal(Enum.Parse(conversionType, literal), value);
    }

    private static void AssertCompiledDefaults(string type, (string Literal, object Expected)[] defaults)
    {
        var typeScalar = System.Text.Json.JsonSerializer.Serialize(type);
        var yaml = "activity: {key: acme.defaults, version: 1}\ninput:\n" + string.Join("\n", defaults.Select((item, index) =>
            "  Value" + index + ": {type: " + typeScalar + ", default: " + item.Literal + "}")) + "\nbody: |\n  return;\n";
        var json = WorkflowJsonSerializer.Write(WorkflowYamlSerializer.ReadActivity(yaml));
        foreach (var document in new[] { WorkflowYamlSerializer.ReadActivity(yaml), WorkflowJsonSerializer.ReadActivity(json) })
        {
            Assert.Empty(ActivityDocumentValidator.Validate(document).Diagnostics);
            var result = ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            var context = new AssemblyLoadContext("numeric-defaults", isCollectible: true);
            try
            {
                var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                for (var index = 0; index < defaults.Length; index++)
                {
                    var actual = activity.GetProperty("Value" + index)!.GetCustomAttribute<DefaultValueAttribute>()!.Value;
                    Assert.IsType(defaults[index].Expected.GetType(), actual);
                    Assert.Equal(defaults[index].Expected, actual);
                }
            }
            finally { context.Unload(); }
        }
    }

    private static void AssertInvalidNumericDefault(string type, string literal)
    {
        var typeScalar = System.Text.Json.JsonSerializer.Serialize(type);
        var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: " + typeScalar + ", default: " + literal + "}}\nbody: |\n  return;\n";
        var json = "{\"activity\":{\"key\":\"acme.defaults\",\"version\":1},\"input\":{\"Value\":{\"type\":" + typeScalar + ",\"default\":" + literal + "}},\"body\":\"return;\\n\"}";
        foreach (var (document, source) in new[] { (WorkflowYamlSerializer.ReadActivity(yaml), yaml), (WorkflowJsonSerializer.ReadActivity(json), json) })
        {
            // Named types require compilation; document rules can refuse only unambiguous framework syntax.
            var errors = ActivityDocumentValidator.Validate(document).Diagnostics;
            Assert.All(errors, error =>
            {
                Assert.Equal("ACTD010", error.Code);
                Assert.Equal("input.Value.default", error.Path);
                var location = DocumentModel.GetLocation(document, error.Path);
                Assert.StartsWith(literal, source.Split('\n')[location.Line - 1].Substring(location.Column - 1));
                var exception = Assert.Throws<WorkflowSourceException>(() => ActivitySourceTranslator.Translate(document, source, "default.yaml"));
                Assert.StartsWith("ACTD010:", exception.Detail);
                Assert.Equal(location.Line, exception.Line);
                Assert.Equal(location.Column, exception.Column);
            });
            var written = WorkflowYamlSerializer.Write(document);
            var diagnostic = Assert.Single(ActivityCompilation.Compile(written, "default.yaml").Diagnostics);
            Assert.Equal("ACTD010", diagnostic.Code);
            Assert.Equal(WorkflowPublishStage.Validate, diagnostic.Stage);
            Assert.Equal("default.yaml", diagnostic.Path);
            var expected = DocumentModel.GetLocation(WorkflowYamlSerializer.ReadActivity(written), "input.Value.default");
            Assert.Equal(expected.Line, diagnostic.Line);
            Assert.Equal(expected.Column, diagnostic.Column);
        }
    }

    private static byte[] EmbeddedSource(MetadataReader reader, DocumentHandle document)
    {
        var item = Assert.Single(reader.GetCustomDebugInformation(document).Select(reader.GetCustomDebugInformation),
            item => reader.GetGuid(item.Kind) == new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe"));
        var bytes = reader.GetBlobBytes(item.Value);
        var size = BitConverter.ToInt32(bytes, 0);
        if (size == 0) return bytes[4..];
        using var compressed = new DeflateStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
        using var output = new MemoryStream();
        compressed.CopyTo(output);
        Assert.Equal(size, output.Length);
        return output.ToArray();
    }
}
