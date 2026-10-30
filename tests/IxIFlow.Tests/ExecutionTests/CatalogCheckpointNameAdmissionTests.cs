using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class CatalogCheckpointNameAdmissionTests
{
    public static IEnumerable<object[]> Cases => new[] { "local-memory", "local-sqlite" }
        .SelectMany(profile => CheckpointNameAdmissionScenario.Starts.SelectMany(start =>
            new[] { true, false }.Select(malformed => new object[] { profile, start, malformed })));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalNamesAreValidatedBeforeCheckpointAdmission(string profile, string start, bool malformed)
    {
        if (profile == "local-memory")
        {
            await CheckpointNameAdmissionScenario.VerifyAsync(new InMemoryWorkflowStateRepository(), start, malformed);

            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CheckpointNames", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "names.db"));
            await CheckpointNameAdmissionScenario.VerifyAsync(store, start, malformed);
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }
}