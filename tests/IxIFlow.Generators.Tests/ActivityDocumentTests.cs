using System.Collections;
using System.Text.Json;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using Json.Schema;
using YamlDotNet.Core;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "documents")]
[Trait("Spec", "F-19")]
public sealed class ActivityDocumentTests
{
    internal const string Complete = """
        $schema: activity.schema.json
        schemaVersion: '1.0'
        activity:
          key: acme.send-email
          version: 2
          name: Send email
          category: Messaging
          icon: mail
          designer: send-email
          description: Sends one email.
        packages: [MailKit@4.8.0]
        usings: [System.Text]
        input:
          To: {type: string, required: true, label: Recipient, help: Destination, control: text}
          Subject: {type: string, default: '(no subject)'}
          ReplyTo: {type: 'string?', default: null}
          Retries: {type: int, default: 3}
          Optional: {type: 'int?'}
        output:
          MessageId: {type: string, label: Message ID}
        inOut:
          Attempts: {type: int}
        body: |2-
          Attempts++;
          MessageId = await SendAsync(cancellationToken);
        declarations: |+
          private Task<string> SendAsync(CancellationToken token) => Task.FromResult("sent");
        """;

    [Fact]
    public void ActivityRoundTripsThroughBothReadersWithDefaultPresence()
    {
        var document = WorkflowYamlSerializer.ReadActivity(Complete);
        Assert.True(ActivityDocumentValidator.Validate(document, Complete).IsValid);
        foreach (var copy in new[] { document, WorkflowYamlSerializer.ReadActivity(WorkflowYamlSerializer.Write(document)),
                     WorkflowJsonSerializer.ReadActivity(WorkflowJsonSerializer.Write(document)) })
        {
            Assert.Equal("acme.send-email", copy.Activity.Key);
            Assert.Equal(2, copy.Activity.Version);
            Assert.Equal(document.Body, copy.Body);
            Assert.Equal(document.Declarations, copy.Declarations);
            Assert.True(copy.Input["ReplyTo"].HasDefault());
            Assert.Null(copy.Input["ReplyTo"].Default);
            Assert.False(copy.Input["Optional"].HasDefault());
            Assert.Equal("3", Assert.IsType<DocumentNumber>(copy.Input["Retries"].Default).Literal);
        }
        var built = new ActivityMemberDocument { Type = "string?", Default = null };
        Assert.True(built.HasDefault());
        Assert.False(new ActivityMemberDocument { Type = "string?" }.HasDefault());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("")]
    [InlineData("NULL")]
    public void EveryExplicitYamlNullRetainsPresence(string scalar)
    {
        var yaml = "activity: {key: acme.test, version: 1}\ninput:\n  Value:\n    type: string?\n    required: true\n    default: " + scalar + "\nbody: |\n  await Task.Yield();";
        var document = WorkflowYamlSerializer.ReadActivity(yaml);
        Assert.True(document.Input["Value"].HasDefault());
        Assert.Null(document.Input["Value"].Default);
        Assert.Contains(ActivityDocumentValidator.Validate(document, yaml).Diagnostics, item => item.Code == "ACTD016" && item.Path == "input.Value.default");
    }

    [Theory]
    [InlineData("workflow: {}", "workflow")]
    [InlineData("activity: {}", "activity")]
    [InlineData("template: {}", "template")]
    [InlineData("$schema: x\nactivity: {}", "activity")]
    public void RootKindIsShared(string source, string kind) => Assert.Equal(kind, WorkflowYamlSerializer.ReadRootKind(source));

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("body: x")]
    [InlineData("workflow: {}\nactivity: {}")]
    [InlineData("activity: {}\ntemplate: {}")]
    [InlineData("activity: {}\n---\nactivity: {}")]
    [InlineData("activity: {}\nactivity: {}")]
    public void InvalidRootKindHasAPosition(string source)
    {
        var error = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.ReadRootKind(source));
        Assert.True(error.Start.Line >= 1);
        Assert.True(error.Start.Column >= 1);
    }

    [Fact]
    public void LiteralWriterAndScalarCoordinatesAreShared()
    {
        var activity = WorkflowYamlSerializer.ReadActivity(Complete);
        var written = WorkflowYamlSerializer.Write(activity);
        Assert.Contains("body: |", written);
        Assert.Contains("declarations: |", written);
        var location = DocumentModel.GetLocation(activity, "input.ReplyTo.default");
        Assert.Equal(16, location.Line);
        Assert.Equal("null", Complete.Split('\n')[location.Line - 1].Substring(location.Column - 1, 4));
        Assert.Equal(new DocumentLocation(12, 10), DocumentModel.GetLocation(activity, "usings[0]"));
        var json = WorkflowJsonSerializer.Write(activity);
        var jsonCopy = WorkflowJsonSerializer.ReadActivity(json);
        var jsonLocation = DocumentModel.GetLocation(jsonCopy, "input.ReplyTo.default");
        Assert.Equal("null", json.Split('\n')[jsonLocation.Line - 1].Substring(jsonLocation.Column - 1, 4));
        var workflow = WorkflowYamlSerializer.Read("workflow: {name: Test, dataType: Data}\ncode:\n  test:\n    language: csharp-expression\n    source: |\n      1 +\n      2\n");
        Assert.Contains("source: |", WorkflowYamlSerializer.Write(workflow));
    }

    [Theory]
    [InlineData("body: >\n  await Task.Yield();", "body")]
    [InlineData("body: 'await Task.Yield();'", "body")]
    [InlineData("body: |\n  await Task.Yield();\ndeclarations: 'private int x;'", "declarations")]
    public void CodeRequiresLiteralStyleOnlyWhenRawYamlIsSupplied(string tail, string path)
    {
        var yaml = "activity: {key: acme.test, version: 1}\n" + tail;
        var document = WorkflowYamlSerializer.ReadActivity(yaml);
        Assert.Contains(ActivityDocumentValidator.Validate(document, yaml).Diagnostics, item => item.Code == "ACTD012" && item.Path == path);
        Assert.DoesNotContain(ActivityDocumentValidator.Validate(document).Diagnostics, item => item.Code == "ACTD012");
    }

    public static IEnumerable<object[]> DiagnosticCases()
    {
        yield return ["activity: {version: 1}\nbody: |\n  return;", "MEMBER001", "activity.key"];
        yield return ["activity: {key: acme.test}\nbody: |\n  return;", "MEMBER001", "activity.version"];
        yield return ["activity: {key: acme.test, version: 1}", "MEMBER001", "body"];
        yield return ["activity: {key: acme.test, version: 1}\ninput: {Value: {}}\nbody: |\n  return;", "MEMBER001", "input.Value.type"];
        foreach (var entry in new[]
        {
            ("activity: {key: Acme, version: 1}", "ACTD001", "activity.key"),
            ("activity: {key: acme.test, version: 65535}", "ACTD002", "activity.version"),
            ("input: {ExecuteAsync: {type: int}}", "ACTD003", "input.ExecuteAsync"),
            ("input: {Test: {type: int}}", "ACTD003", "input.Test"),
            ("input: {Value: {type: int}}\noutput: {Value: {type: int}}", "ACTD004", "output.Value"),
            ("output: {Value: {type: int, required: true}}", "ACTD005", "output.Value.required"),
            ("output: {Value: {type: int, default: null}}", "ACTD006", "output.Value.default"),
            ("activity: {key: acme.test, version: 1, icon: Mail}", "ACTD008", "activity.icon"),
            ("activity: {key: acme.test, version: 1, designer: BAD}", "ACTD008", "activity.designer"),
            ("input: {Value: {type: 'List<'}}", "ACTD009", "input.Value.type"),
            ("input: {Value: {type: int, default: [1]}}", "ACTD010", "input.Value.default"),
            ("input: {Value: {type: int, default: {a: 1}}}", "ACTD010", "input.Value.default"),
            ("input: {Value: {type: double, default: .nan}}", "ACTD010", "input.Value.default"),
            ("usings: ['System; class Inject {}']", "ACTD013", "usings[0]"),
            ("packages: [MailKit@4.8.0, mailkit@4.9.0]", "ACTD014", "packages[1]"),
            ("packages: [MailKit]", "ACTD014", "packages[0]"),
            ("input: {Value: {type: string, label: ' '}}", "ACTD015", "input.Value.label"),
            ("input: {Value: {type: string, help: ''}}", "ACTD015", "input.Value.help"),
            ("input: {Value: {type: 'string?', required: true, default: null}}", "ACTD016", "input.Value.default")
        })
            yield return [(entry.Item1.StartsWith("activity:", StringComparison.Ordinal) ? "" : "activity: {key: acme.test, version: 1}\n") + entry.Item1 + "\nbody: |\n  return;", entry.Item2, entry.Item3];
        yield return ["activity: {key: acme.test, version: 1}\nbody: |\n", "ACTD011", "body"];
    }

    [Theory]
    [MemberData(nameof(DiagnosticCases))]
    public void DiagnosticsNameTheirMembers(string yaml, string code, string path)
    {
        var document = WorkflowYamlSerializer.ReadActivity(yaml);
        Assert.Contains(ActivityDocumentValidator.Validate(document, yaml).Diagnostics, item => item.Code == code && item.Path == path);
        Assert.True(DocumentModel.GetLocation(document, path).Line >= 1);
    }

    [Fact]
    public void ClosedValuesAndUnknownMembersUseStrictReadersAndPureRules()
    {
        Assert.Throws<YamlException>(() => WorkflowYamlSerializer.ReadActivity(Complete + "\nunknown: x"));
        Assert.Throws<JsonException>(() => WorkflowJsonSerializer.ReadActivity("{\"activity\":{},\"unknown\":1}"));
        Assert.Throws<YamlException>(() => WorkflowYamlSerializer.ReadActivity(Complete.Replace("control: text", "control: unknown")));
        var document = WorkflowYamlSerializer.ReadActivity(Complete);
        document.Input["To"] = document.Input["To"] with { Control = "unknown" };
        Assert.Contains(ActivityDocumentValidator.Validate(document).Diagnostics, item => item.Code == "ACTD007" && item.Path == "input.To.control");
        Assert.Contains(DocumentRules.Validate(document with { SchemaVersion = "2.0" }).Diagnostics, item => item.Code == "CHOICE001" && item.Path == "schemaVersion");
    }

    [Fact]
    public void JsonScalarPositionsCountCharactersAfterUnicodeText()
    {
        const string json = "{\"activity\":{\"key\":\"acme.test\",\"version\":1},\"input\":{\"Value\":{\"type\":\"string?\",\"label\":\"é😀\",\"default\":null}},\"body\":\"return;\"}";
        var document = WorkflowJsonSerializer.ReadActivity(json);
        Assert.Equal(new DocumentLocation(1, json.IndexOf("null", StringComparison.Ordinal) + 1), DocumentModel.GetLocation(document, "input.Value.default"));
    }

    [Fact]
    public void RootAwareSchemaGoldenAndExhaustiveActivityMembers()
    {
        var schemaText = DocumentSchema.Write(typeof(ActivityDocument));
        var path = Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "src/IxIFlow/schemas/activity.schema.json");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), schemaText);
        Assert.Equal(DocumentSchema.Id(typeof(ActivityDocument)), JsonDocument.Parse(schemaText).RootElement.GetProperty("$id").GetString());
        var schema = JsonSchema.FromText(schemaText, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        Assert.True(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(Complete)).IsValid);
        var used = new HashSet<(Type, string)>();
        Collect(WorkflowYamlSerializer.ReadActivity(Complete));
        Collect(WorkflowYamlSerializer.ReadActivity(File.ReadAllText(Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "tests/IxIFlow.Generators.Tests/Documents/scope-activity.yaml"))));
        foreach (var type in new[] { typeof(ActivityDocument), typeof(ActivityDefinitionDocument), typeof(ActivityMemberDocument) })
            foreach (var member in DocumentModel.GetMembers(type)) Assert.Contains((type, DocumentModel.GetName(member)), used);
        foreach (var value in DocumentModel.GetValues(typeof(ActivityMemberDocument), nameof(ActivityMemberDocument.Control)))
            Assert.True(ActivityDocumentValidator.Validate(WorkflowYamlSerializer.ReadActivity(Complete.Replace("control: text", "control: " + value))).IsValid);
        foreach (var invalid in new[] { Complete.Replace("version: 2", "version: 65535"), Complete.Replace("type: string, required", "type: 42, required"),
                     Complete.Replace("schemaVersion: '1.0'", "schemaVersion: '2.0'"), Complete.Replace("control: text", "control: unknown") })
            Assert.False(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(invalid)).IsValid);

        void Collect(object? value)
        {
            if (value is DocumentNode node)
                foreach (var member in DocumentModel.GetMembers(node.GetType()))
                {
                    var name = DocumentModel.GetName(member);
                    if (node.Members.ContainsKey(name)) used.Add((node.GetType(), name));
                    Collect(member.GetValue(node));
                }
            else if (value is IDictionary dictionary) foreach (var item in dictionary.Values) Collect(item);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitGeneratorSelectionRefusesActivityRoot(bool invalidMembers)
    {
        var yaml = invalidMembers ? Complete + "\nunknown: rejected-by-activity-reader" : Complete;
        var result = WorkflowGeneratorHarness.Generate(yaml, "mail.activity.yaml");
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("IXIYAML002", error.Id);
        Assert.Equal("mail.activity.yaml", error.Location.GetLineSpan().Path);
        Assert.Empty(result.Results.SelectMany(item => item.GeneratedSources));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("inOut")]
    [InlineData("usings")]
    [InlineData("packages")]
    public void PresentNonNullableMembersRejectNull(string member)
    {
        var yaml = "activity: {key: acme.test, version: 1}\nbody: |\n  return;\n" + member + ": null\n";
        var error = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.ReadActivity(yaml));
        Assert.Equal(4, error.Start.Line);
        Assert.Equal(member.Length + 3, error.Start.Column);
        Assert.Throws<JsonException>(() => WorkflowJsonSerializer.ReadActivity(
            "{\"activity\":{\"key\":\"acme.test\",\"version\":1},\"body\":\"return;\",\"" + member + "\":null}"));
        Assert.False(DocumentRules.Validate(new ActivityDocument { Activity = new() { Key = "acme.test", Version = 1 }, Body = "return;", SchemaVersion = null! }).IsValid);
        Assert.False(Authoring.ActivityCompilation.Compile(yaml).Success);
    }

    [Fact]
    public void ExplicitNullDefaultsAndNullableMetadataRemainValidInBothReaders()
    {
        const string yaml = "activity: {key: acme.test, version: 1, name: null, category: null, icon: null, designer: null, description: null}\ninput: {Value: {type: 'string?', default: null, label: null, help: null, control: null}}\nbody: |\n  return;\n";
        var document = WorkflowYamlSerializer.ReadActivity(yaml);
        foreach (var copy in new[] { document, WorkflowJsonSerializer.ReadActivity(WorkflowJsonSerializer.Write(document)) })
        {
            Assert.True(ActivityDocumentValidator.Validate(copy).IsValid);
            Assert.Null(copy.Activity.Name);
            Assert.Null(copy.Activity.Category);
            Assert.Null(copy.Activity.Icon);
            Assert.Null(copy.Activity.Designer);
            Assert.Null(copy.Activity.Description);
            Assert.Null(copy.Input["Value"].Label);
            Assert.Null(copy.Input["Value"].Help);
            Assert.Null(copy.Input["Value"].Control);
            Assert.Null(copy.Input["Value"].Default);
            Assert.True(copy.Input["Value"].HasDefault());
        }
    }

    [Fact]
    public void RequiredNullsRetainMissingMemberDiagnosticsInBothReaders()
    {
        const string yaml = "activity: {key: null, version: 1}\ninput: {Value: {type: null}}\nbody: |\n  return;\n";
        const string json = "{\"activity\":{\"key\":null,\"version\":1},\"input\":{\"Value\":{\"type\":null}},\"body\":\"return;\"}";
        foreach (var document in new[] { WorkflowYamlSerializer.ReadActivity(yaml), WorkflowJsonSerializer.ReadActivity(json) })
        {
            var result = ActivityDocumentValidator.Validate(document);
            Assert.False(result.IsValid);
            Assert.Contains(result.Diagnostics, item => item.Code == "MEMBER001" && item.Path == "activity.key");
            Assert.Contains(result.Diagnostics, item => item.Code == "MEMBER001" && item.Path == "input.Value.type");
        }
        Assert.False(Authoring.ActivityCompilation.Compile(yaml).Success);
    }

    [Theory]
    [InlineData("input", "input")]
    [InlineData("output", "output")]
    [InlineData("inOut", "inOut")]
    [InlineData("input", "output")]
    [InlineData("input", "inOut")]
    [InlineData("output", "inOut")]
    public void CaseDistinctMemberNamesAreRefusedWithoutMergingKeys(string first, string second)
    {
        var yaml = "activity: {key: acme.test, version: 1}\n" + first + ":\n  Value: {type: int}\n" +
            (first == second ? "" : second + ":\n") + "  value: {type: int}\nbody: |\n  return;\n";
        var parsed = WorkflowYamlSerializer.ReadActivity(yaml);
        var json = WorkflowJsonSerializer.Write(parsed);
        foreach (var (document, source) in new[] { (parsed, yaml), (WorkflowJsonSerializer.ReadActivity(json), json) })
        {
            var firstMembers = first switch { "input" => document.Input, "output" => document.Output, _ => document.InOut };
            var secondMembers = second switch { "input" => document.Input, "output" => document.Output, _ => document.InOut };
            Assert.Contains("Value", firstMembers.Keys);
            Assert.Contains("value", secondMembers.Keys);
            var error = Assert.Single(ActivityDocumentValidator.Validate(document).Diagnostics);
            Assert.Equal("ACTD004", error.Code);
            Assert.Equal(second + ".value", error.Path);
            var location = DocumentModel.GetLocation(document, error.Path);
            Assert.Equal('{', source.Split('\n')[location.Line - 1][location.Column - 1]);
            var exception = Assert.Throws<WorkflowSourceException>(() => ActivitySourceTranslator.Translate(document, source, "names.yaml"));
            Assert.Equal(location.Line, exception.Line);
            Assert.Equal(location.Column, exception.Column);
        }
        var refused = Assert.Single(Authoring.ActivityCompilation.Compile(yaml, "names.yaml").Diagnostics);
        Assert.Equal("ACTD004", refused.Code);
        Assert.Equal(DocumentModel.GetLocation(parsed, second + ".value").Line, refused.Line);
        Assert.Equal(DocumentModel.GetLocation(parsed, second + ".value").Column, refused.Column);
    }

    [Theory]
    [InlineData("Va\u200Clue", false)]
    [InlineData("Va\u200Clue", true)]
    [InlineData(@"Va\u006Cue", false)]
    [InlineData(@"Va\u006Cue", true)]
    [InlineData("@Value", false)]
    [InlineData("@Value", true)]
    public void NormalizedMemberNamesAreRefusedInBothReaders(string name, bool collides)
    {
        var key = JsonSerializer.Serialize(name);
        foreach (var direction in new[] { "input", "output", "inOut" })
        {
            var yaml = "activity: {key: acme.defaults, version: 1}\n" + direction + ":\n" +
                (collides ? "  Value: {type: System.Int32}\n" : "") + "  " + key + ": {type: System.Int32" +
                (direction == "output" ? "" : ", default: 1e2") + "}\nbody: |\n  return;\n";
            var json = "{\"activity\":{\"key\":\"acme.defaults\",\"version\":1},\"" + direction + "\":{" +
                (collides ? "\"Value\":{\"type\":\"System.Int32\"}," : "") + key + ":{\"type\":\"System.Int32\"" +
                (direction == "output" ? "" : ",\"default\":1e2") + "}},\"body\":\"return;\\n\"}";
            foreach (var (document, source) in new[] { (WorkflowYamlSerializer.ReadActivity(yaml), yaml), (WorkflowJsonSerializer.ReadActivity(json), json) })
            {
                var error = Assert.Single(ActivityDocumentValidator.Validate(document).Diagnostics);
                Assert.Equal("ACTD003", error.Code);
                Assert.Equal(direction + "." + name, error.Path);
                var location = DocumentModel.GetLocation(document, error.Path);
                var lines = source.Split('\n');
                var expectedLine = Array.FindIndex(lines, line => line.Contains(key + ":", StringComparison.Ordinal));
                Assert.Equal(expectedLine + 1, location.Line);
                Assert.Equal(lines[expectedLine].IndexOf('{', lines[expectedLine].IndexOf(key + ":", StringComparison.Ordinal)) + 1, location.Column);
                var exception = Assert.Throws<WorkflowSourceException>(() => ActivitySourceTranslator.Translate(document, source, "names.yaml"));
                Assert.StartsWith("ACTD003:", exception.Detail);
                Assert.Equal("names.yaml", exception.Path);
                Assert.Equal(location.Line, exception.Line);
                Assert.Equal(location.Column, exception.Column);

                var written = WorkflowYamlSerializer.Write(document);
                var refused = Assert.Single(Authoring.ActivityCompilation.Compile(written, "names.yaml").Diagnostics);
                Assert.Equal("ACTD003", refused.Code);
                Assert.Equal(Authoring.WorkflowPublishStage.Validate, refused.Stage);
                Assert.Equal("names.yaml", refused.Path);
                var expected = DocumentModel.GetLocation(WorkflowYamlSerializer.ReadActivity(written), error.Path);
                Assert.Equal(expected.Line, refused.Line);
                Assert.Equal(expected.Column, refused.Column);
            }
        }
    }

    [Theory]
    [InlineData("Input", "input")]
    [InlineData("Output", "output")]
    [InlineData("InOut", "inOut")]
    [InlineData("Usings", "usings")]
    [InlineData("Packages", "packages")]
    public void ConstructedNullCollectionsReturnTheirStructuralDiagnostic(string property, string path)
    {
        var document = new ActivityDocument { Activity = new() { Key = "acme.test", Version = 1 }, Body = "return;" };
        typeof(ActivityDocument).GetProperty(property)!.SetValue(document, null);
        var diagnostic = Assert.Single(ActivityDocumentValidator.Validate(document).Diagnostics);
        Assert.Equal("MEMBER001", diagnostic.Code);
        Assert.Equal(path, diagnostic.Path);
        var exception = Assert.Throws<WorkflowSourceException>(() => ActivitySourceTranslator.Translate(document, "", "null.yaml"));
        Assert.StartsWith("MEMBER001:", exception.Detail);
    }

    [Fact]
    public void ExplicitNullDeclarationsSurviveBothReadersAndCompilation()
    {
        const string json = "{\"activity\":{\"key\":\"acme.test\",\"version\":1},\"body\":\"return;\\n\",\"declarations\":null}";
        const string yaml = "activity: {key: acme.test, version: 1}\nbody: |\n  return;\ndeclarations: null\n";
        foreach (var original in new[] { WorkflowJsonSerializer.ReadActivity(json), WorkflowYamlSerializer.ReadActivity(yaml) })
        {
            Assert.Null(original.Declarations);
            Assert.Contains("declarations", original.Members.Keys);
            var written = WorkflowYamlSerializer.Write(original);
            var copy = WorkflowYamlSerializer.ReadActivity(written);
            Assert.Null(copy.Declarations);
            Assert.Contains("declarations", copy.Members.Keys);
            Assert.True(ActivityDocumentValidator.Validate(copy, written).IsValid);
            var compiled = Authoring.ActivityCompilation.Compile(written);
            Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
            Assert.Empty(compiled.Diagnostics);
        }
        Assert.True(Authoring.ActivityCompilation.Compile(yaml).Success);
    }

    [Fact]
    public async Task NullableWorkflowComplexMembersAndCollectionsAgreeInBothReaders()
    {
        const string yaml = "workflow: {name: Nulls, dataType: Data, input: null, output: null}\ndefinitions:\n- kind: suspend\n  event: Reply\n  resumeCondition: null\n  onTimeout: null\n- kind: try\n  steps: []\n  finally: null\ndemands: [{key: test, from: null}]\n";
        const string json = "{\"workflow\":{\"name\":\"Nulls\",\"dataType\":\"Data\",\"input\":null,\"output\":null},\"definitions\":[{\"kind\":\"suspend\",\"event\":\"Reply\",\"resumeCondition\":null,\"onTimeout\":null},{\"kind\":\"try\",\"steps\":[],\"finally\":null}],\"demands\":[{\"key\":\"test\",\"from\":null}]}";
        foreach (var original in new[] { WorkflowYamlSerializer.Read(yaml), WorkflowJsonSerializer.Read(json) })
            foreach (var document in new[] { original, WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(original)), WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(original)) })
            {
                Assert.True(DocumentRules.Validate(document).IsValid);
                var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowContracts());
                var error = Assert.Single(validation.Diagnostics);
                Assert.Equal("TRY001", error.Code);
                Assert.Equal("definitions[1]", error.Path);
                var withFinally = document with
                {
                    Definitions = [document.Definitions[0], Assert.IsType<TryStepDocument>(document.Definitions[1]) with { Finally = [] }]
                };
                Assert.Empty((await new WorkflowDocumentValidator().ValidateAsync(withFinally, new WorkflowContracts())).Diagnostics);
                Assert.Null(document.Workflow.Input);
                Assert.Null(document.Workflow.Output);
                var wait = Assert.IsType<SuspendStepDocument>(document.Definitions[0]);
                Assert.Null(wait.ResumeCondition);
                Assert.Null(wait.OnTimeout);
                Assert.Null(Assert.IsType<TryStepDocument>(document.Definitions[1]).Finally);
                Assert.Null(Assert.Single(document.Demands).From);
                foreach (var path in new[] { "workflow.input", "workflow.output", "definitions[0].resumeCondition", "definitions[0].onTimeout", "definitions[1].finally", "demands[0].from" })
                {
                    var location = DocumentModel.GetLocation(document, path);
                    Assert.True(location.Line > 0 && location.Column > 0);
                }
                using var written = JsonDocument.Parse(WorkflowJsonSerializer.Write(document));
                Assert.Equal(JsonValueKind.Null, written.RootElement.GetProperty("workflow").GetProperty("input").ValueKind);
                Assert.Equal(JsonValueKind.Null, written.RootElement.GetProperty("definitions")[0].GetProperty("resumeCondition").ValueKind);
            }
    }

    [Theory]
    [InlineData("workflow: {name: Nulls, dataType: Data, input: VALUE}", "{}")]
    [InlineData("workflow: {name: Nulls, dataType: Data, output: VALUE}", "42")]
    [InlineData("definitions: [{kind: suspend, event: Reply, resumeCondition: VALUE}]", "[]")]
    [InlineData("definitions: [{kind: suspend, event: Reply, resumeCondition: VALUE}]", "{kind: unknown}")]
    [InlineData("definitions: [{kind: suspend, event: Reply, onTimeout: VALUE}]", "{}")]
    [InlineData("definitions: [{kind: suspend, event: Reply, onTimeout: VALUE}]", "[{kind: unknown}]")]
    [InlineData("definitions: [{kind: try, catches: [{steps: []}], finally: VALUE}]", "{}")]
    [InlineData("demands: [{key: test, from: VALUE}]", "{kind: path, path: workflow.Value, unknown: true}")]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, parallel: VALUE}]", "[]")]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, parallel: VALUE}]", "{join: unknown}")]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, result: VALUE}]", "[]")]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, into: VALUE}]", "[]")]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, parallel: {maxConcurrency: VALUE}}]", "[]")]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, parallel: {condition: VALUE}}]", "[]")]
    [InlineData("definitions: [{kind: parallel, branches: [], condition: VALUE}]", "[]")]
    [InlineData("definitions: [{kind: invokeWorkflowType, workflowType: Child, onError: [{then: retry, retry: VALUE}]}]", "[]")]
    [InlineData("definitions: [{kind: invokeWorkflowType, workflowType: Child, onError: [{then: retry, retry: VALUE}]}]", "{unknown: true}")]
    [InlineData("definitions: [{kind: saga, onError: [{compensate: none, then: retry, retry: VALUE}]}]", "[]")]
    [InlineData("definitions: [{kind: request, activity: Test, key: reply, subject: VALUE}]", "[]")]
    [InlineData("definitions: [{kind: switch, value: {kind: constant, value: 1}, default: VALUE}]", "{}")]
    [InlineData("definitions: [{kind: flowchart, start: a, nodes: [{key: a, switch: VALUE}]}]", "[]")]
    [InlineData("definitions: [{kind: flowchart, start: a, nodes: [{key: a, transitions: [{when: VALUE, to: end}]}]}]", "[]")]
    [InlineData("definitions: [{kind: flowchart, start: a, nodes: [{key: a, transitions: [{default: VALUE, to: end}]}]}]", "[]")]
    [InlineData("definitions: [{kind: stateMachine, start: a, states: [{key: a, final: true, exit: VALUE}]}]", "{}")]
    [InlineData("definitions: [{kind: stateMachine, start: a, states: [{key: a, transitions: [{to: end, cases: VALUE}]}]}]", "{}")]
    [InlineData("definitions: [{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: reply, event: Reply, to: end, matches: VALUE}]}]}]", "[]")]
    [InlineData("definitions: [{kind: stateMachine, start: a, states: [{key: a, transitions: [{to: end, when: VALUE}]}]}]", "[]")]
    public async Task NullableWorkflowSitesRetainNonNullValidation(string template, string malformed)
    {
        var prefix = template.StartsWith("workflow:", StringComparison.Ordinal) ? "" : "workflow: {name: Nulls, dataType: Data}\n";
        var yaml = prefix + template.Replace("VALUE", "null");
        var json = WorkflowYamlSchemaValueTests.Read(yaml).GetRawText();
        foreach (var document in new[] { WorkflowYamlSerializer.Read(yaml), WorkflowJsonSerializer.Read(json) })
            Assert.Empty((await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowContracts())).Diagnostics);

        var invalid = prefix + template.Replace("VALUE", malformed);
        Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read(invalid));
        var invalidJson = WorkflowYamlSchemaValueTests.Read(invalid).GetRawText();
        Assert.Throws<JsonException>(() => WorkflowJsonSerializer.Read(invalidJson));
    }

    [Theory]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Items}, parallel: {maxConcurrency: 0}}]", "EACH004", "definitions[0].parallel.maxConcurrency")]
    [InlineData("definitions: [{kind: parallel, branches: [], join: conditionally, condition: null}]", "PAR001", "definitions[0].condition")]
    [InlineData("definitions: [{kind: suspend, event: Reply, resumeCondition: {path: invalid.Value}}]", "EXPR001", "definitions[0].resumeCondition")]
    [InlineData("demands: [{key: test, from: {path: invalid.Value}}]", "EXPR001", "demands[0].from")]
    public async Task NullableWorkflowMembersStillReceiveSemanticValidation(string members, string code, string path)
    {
        var yaml = "workflow: {name: Nulls, dataType: Data}\n" + members;
        var json = WorkflowYamlSchemaValueTests.Read(yaml).GetRawText();
        foreach (var document in new[] { WorkflowYamlSerializer.Read(yaml), WorkflowJsonSerializer.Read(json) })
        {
            var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowContracts());
            Assert.Contains(validation.Diagnostics, error => error.Code == code && error.Path == path);
        }
    }

    private sealed class WorkflowContracts : IWorkflowValidationContext
    {
        public Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token) => Task.FromResult<ActivityDocumentContract?>(new(key, "1"));
        public Task<bool> HasDataTypeAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasEventAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasStepTemplateAsync(string key, CancellationToken token) => Task.FromResult(false);
        public Task<TemplateDocumentContract?> FindTemplateAsync(string key, int? version, CancellationToken token) => Task.FromResult<TemplateDocumentContract?>(null);
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("workflow")]
    public void SharedContractAndSchemaHarnessDispatchByRoot(string kind)
    {
        var yaml = kind == "activity" ? Complete : "workflow: {name: Example, dataType: Data}\ndefinitions: []";
        Assert.Equal(kind, WorkflowYamlSerializer.ReadRootKind(yaml));
        DocumentNode ReadYaml(string text) => kind == "activity" ? WorkflowYamlSerializer.ReadActivity(text) : WorkflowYamlSerializer.Read(text);
        DocumentNode ReadJson(string text) => kind == "activity" ? WorkflowJsonSerializer.ReadActivity(text) : WorkflowJsonSerializer.Read(text);
        var document = ReadYaml(yaml);
        var schema = JsonSchema.FromText(DocumentSchema.Write(document.GetType()), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        foreach (var copy in new[] { document, ReadYaml(WorkflowYamlSerializer.Write(document)), ReadJson(WorkflowJsonSerializer.Write(document)) })
        {
            Assert.True(DocumentRules.Validate(copy).IsValid);
            using var json = JsonDocument.Parse(WorkflowJsonSerializer.Write(copy));
            var keys = DocumentModel.GetKeys(copy.GetType()).Select(key => key.Name).ToHashSet(StringComparer.Ordinal);
            Assert.All(json.RootElement.EnumerateObject(), property => Assert.Contains(property.Name, keys));
            Assert.True(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(WorkflowYamlSerializer.Write(copy))).IsValid);
        }
    }
}
