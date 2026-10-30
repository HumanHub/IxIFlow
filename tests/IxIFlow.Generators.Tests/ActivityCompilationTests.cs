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

// Each nested test class uses its own default xUnit collection. Shared helpers are stateless.
public sealed partial class ActivityCompilationTests
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
        finally
        {
            context.Unload();
        }
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
        finally
        {
            context.Unload();
        }

        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(first.Pdb));
        var reader = provider.GetMetadataReader();

        var sources = reader.Documents.ToDictionary(handle => reader.GetString(reader.GetDocument(handle).Name),
            handle => EmbeddedSource(reader, handle));

        Assert.Equal(Encoding.UTF8.GetBytes(yaml), sources[ActivityCompilation.SourceResourceName]);

        var generated = ActivitySourceTranslator.Translate(Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml), yaml,
            ActivityCompilation.SourceResourceName);

        Assert.Equal(Encoding.UTF8.GetBytes(generated), sources["activity.g.cs"]);
    }

    [Fact]
    public async Task ExceptionStackUsesLiteralBodyLine()
    {
        const string yaml =
            "activity: {key: acme.fail, version: 1}\nbody: |4+\n    await Task.Yield();\n    throw new InvalidOperationException(\"expected\");\n";

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
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void ReferenceProfileExcludesApplicationImplementationAssemblies()
    {
        Assert.DoesNotContain(typeof(ActivityCompilationTests).Assembly.Location, ActivityCompilation.ReferencePaths());
        Assert.Contains(typeof(IAsyncActivity).Assembly.Location, ActivityCompilation.ReferencePaths());

        var result = ActivityCompilation.Compile(
            "activity: {key: acme.test, version: 1}\ninput: {Value: {type: IxIFlow.Generators.Tests.ActivityCompilationTests}}\nbody: |\n  await Task.Yield();");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Stage == WorkflowPublishStage.Compile && item.Line == 2);
    }

    [Fact]
    public void IdentityMappingIsInjectiveAcrossNamespaceAndHyphenBoundaries()
    {
        var words = new[] { "a", "ab", "a1", "a-b", "ab-c", "a-bc", "a-b-c" };
        var keys = words.Concat(words.SelectMany(left => words.Select(right => left + "." + right))).ToArray();

        Assert.Equal(keys.Length,
            keys.Select(key => new ActivityPackageIdentity(key, 1).MetadataTypeName).Distinct(StringComparer.Ordinal).Count());

        var identity = new ActivityPackageIdentity("acme.send-email", 2);
        Assert.Equal("IxIFlow.Activities.Acme.SendEmail", identity.MetadataTypeName);
        Assert.Equal("acme.send-email", identity.PackageId);
        Assert.Equal("2", identity.ActivityVersion);
        Assert.Equal("2.0.0", identity.PackageVersion);
        Assert.Equal("2.0.0.0", identity.AssemblyVersion);
        Assert.Throws<ArgumentException>(() => new ActivityPackageIdentity("acme.2bad", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityPackageIdentity("acme.test", 65535));
    }

    [Fact]
    public void ExplicitPreparedReferencesAreUsedAndBodyCannotDivergeFromSource()
    {
        const string yaml =
            "activity: {key: acme.test, version: 1}\ninput: {Value: {type: IxIFlow.Generators.Tests.ActivityCompilationTests}}\nbody: |\n  await Task.Yield();";

        foreach (var options in new[]
                 {
                     new ActivityCompilationOptions { PackageReferencePaths = [typeof(ActivityCompilationTests).Assembly.Location] },
                     new ActivityCompilationOptions { SharedContractAssemblies = [typeof(ActivityCompilationTests).Assembly] }
                 })
            Assert.True(ActivityCompilation.Compile(yaml, options: options).Success);

        var document = Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml);

        Assert.Throws<WorkflowSourceException>(() =>
            ActivitySourceTranslator.Translate(document with { Body = "throw new Exception();" }, yaml, "source.activity.yaml"));
    }

    private static void IntegralDefaultsConvertExactlyWithoutChangingAuthoredNumbers(string type, Type runtime)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            var minimum = (IFormattable)runtime.GetField("MinValue")!.GetValue(null)!;
            var maximum = (IFormattable)runtime.GetField("MaxValue")!.GetValue(null)!;

            var literals = new[]
            {
                "1e2", "100.0", minimum.ToString(null, CultureInfo.InvariantCulture) + ".0",
                maximum.ToString(null, CultureInfo.InvariantCulture) + "e0", "100000000000000000000000000000000000000000000000000e-48"
            };

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
                    Assert.Equal(literals[index],
                        serialized.RootElement.GetProperty("input").GetProperty("Value" + index).GetProperty("default").GetRawText());

                var result = ActivityCompilation.Compile(written);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                Assert.Empty(result.Diagnostics);
                var context = new AssemblyLoadContext("integral-defaults", isCollectible: true);

                try
                {
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;

                    var expected = new[]
                    {
                        Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture),
                        Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture),
                        minimum, maximum, Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture)
                    };

                    for (var index = 0; index < literals.Length; index++)
                    {
                        var actual = activity.GetProperty("Value" + index)!.GetCustomAttribute<DefaultValueAttribute>()!.Value;
                        Assert.IsType(runtime, actual);
                        Assert.Equal(expected[index], actual);
                    }
                }
                finally
                {
                    context.Unload();
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void FractionalAndOutOfRangeIntegralDefaultsAreRefused(string type, Type runtime)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            var minimum = BigInteger.Parse(Convert.ToString(runtime.GetField("MinValue")!.GetValue(null), CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture);

            var maximum = BigInteger.Parse(Convert.ToString(runtime.GetField("MaxValue")!.GetValue(null), CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture);

            foreach (var literal in new[]
                     {
                         "100.1", "1001e-1", (minimum - 1).ToString(CultureInfo.InvariantCulture) + ".0",
                         (maximum + 1).ToString(CultureInfo.InvariantCulture) + "e0", "1e400", "1e-400",
                         "\" " + (minimum - 1).ToString(CultureInfo.InvariantCulture) + " \"",
                         "\" " + (maximum + 1).ToString(CultureInfo.InvariantCulture) + " \"",
                         "\"0x1" + new string('0', System.Runtime.InteropServices.Marshal.SizeOf(runtime) * 2) + "\"",
                         "\" 100.1 \"", "\"1e2\"", "\"100.0\""
                     })
                AssertInvalidNumericDefault(type, literal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Keep the original data catalog; disjoint widths expose the two large theories to xUnit parallelism.
    public static IEnumerable<object[]> IntegralTypesOfWidth(int bytes) =>
        IntegralTypes().Where(row => System.Runtime.InteropServices.Marshal.SizeOf((Type)row[1]) == bytes);

    public static IEnumerable<object[]> IntegralTypes()
    {
        foreach (var (type, runtime) in new[]
                 {
                     ("sbyte", typeof(sbyte)), ("byte", typeof(byte)), ("short", typeof(short)),
                     ("ushort", typeof(ushort)), ("int", typeof(int)), ("uint", typeof(uint)), ("long", typeof(long)),
                     ("ulong", typeof(ulong))
                 })
        foreach (var declaredType in NumericTypeSpellings(type, runtime))
            yield return [declaredType, runtime];
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

    public static IEnumerable<object[]> DecimalTypes() =>
        NumericTypeSpellings("decimal", typeof(decimal)).Select(type => new object[] { type });

    public static IEnumerable<object[]> FloatingTypes()
    {
        foreach (var (alias, runtime) in new[] { ("float", typeof(float)), ("double", typeof(double)) })
        foreach (var type in NumericTypeSpellings(alias, runtime))
            yield return [type, runtime];
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

        if (customNullable)
            Assert.Equal(literal, conversionType.GetProperty("Text")!.GetValue(value));
        else
            Assert.Equal(Enum.Parse(conversionType, literal), value);
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
            finally
            {
                context.Unload();
            }
        }
    }

    private static void AssertInvalidNumericDefault(string type, string literal)
    {
        var typeScalar = System.Text.Json.JsonSerializer.Serialize(type);

        var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: " + typeScalar + ", default: " + literal +
            "}}\nbody: |\n  return;\n";

        var json = "{\"activity\":{\"key\":\"acme.defaults\",\"version\":1},\"input\":{\"Value\":{\"type\":" + typeScalar +
            ",\"default\":" + literal + "}},\"body\":\"return;\\n\"}";

        foreach (var (document, source) in new[]
                     { (WorkflowYamlSerializer.ReadActivity(yaml), yaml), (WorkflowJsonSerializer.ReadActivity(json), json) })
        {
            // Named types require compilation; document rules can refuse only unambiguous framework syntax.
            var errors = ActivityDocumentValidator.Validate(document).Diagnostics;

            Assert.All(errors, error =>
            {
                Assert.Equal("ACTD010", error.Code);
                Assert.Equal("input.Value.default", error.Path);
                var location = DocumentModel.GetLocation(document, error.Path);
                Assert.StartsWith(literal, source.Split('\n')[location.Line - 1].Substring(location.Column - 1));

                var exception =
                    Assert.Throws<WorkflowSourceException>(() => ActivitySourceTranslator.Translate(document, source, "default.yaml"));

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

        if (size == 0)
            return bytes[4..];

        using var compressed = new DeflateStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
        using var output = new MemoryStream();
        compressed.CopyTo(output);
        Assert.Equal(size, output.Length);

        return output.ToArray();
    }
}