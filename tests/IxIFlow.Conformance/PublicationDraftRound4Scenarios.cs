using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
public sealed class PublicationDraftRound4Scenarios
{
    public static IEnumerable<object[]> Cases => StoreProfiles.ProfileNames
        .SelectMany(profile => CheckpointNameAdmissionScenario.Starts.SelectMany(start =>
            new[] { true, false }.Select(malformed => new object[] { profile, start, malformed })));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalNamesAreValidatedBeforeCheckpointAdmission(string profile, string start, bool malformed)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        await CheckpointNameAdmissionScenario.VerifyAsync(database.NewRepository(), start, malformed);
    }
}