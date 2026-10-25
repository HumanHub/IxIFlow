using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

public sealed partial class SwitchSteppingScenarios
{
    [Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
    public sealed class RoutesPart1
    {
        [Theory]
        [MemberData(nameof(Routes))]
        public Task LiveAndReplayStepsAgreeAcrossEverySwitchRoute(string profile, string route, DebugStepKind kind) =>
            SwitchSteppingScenarios.LiveAndReplayStepsAgreeAcrossEverySwitchRoute(profile, route, kind);

        public static IEnumerable<object[]> Routes =>
            ScenarioCaseShards.Select(SwitchSteppingScenarios.Routes, 0, 2);
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
    public sealed class RoutesPart2
    {
        [Theory]
        [MemberData(nameof(Routes))]
        public Task LiveAndReplayStepsAgreeAcrossEverySwitchRoute(string profile, string route, DebugStepKind kind) =>
            SwitchSteppingScenarios.LiveAndReplayStepsAgreeAcrossEverySwitchRoute(profile, route, kind);

        public static IEnumerable<object[]> Routes =>
            ScenarioCaseShards.Select(SwitchSteppingScenarios.Routes, 1, 2);
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
    public sealed class NestedStartsPart1
    {
        [Theory]
        [MemberData(nameof(NestedStarts))]
        public Task NestedSwitchStepsPreserveScopeFramesAndOccurrencesAcrossRecovery(string profile, string shape, bool fallback, DebugStepKind kind) =>
            SwitchSteppingScenarios.NestedSwitchStepsPreserveScopeFramesAndOccurrencesAcrossRecovery(profile, shape, fallback, kind);

        public static IEnumerable<object[]> NestedStarts =>
            ScenarioCaseShards.Select(SwitchSteppingScenarios.NestedStarts, 0, 2);
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
    public sealed class NestedStartsPart2
    {
        [Theory]
        [MemberData(nameof(NestedStarts))]
        public Task NestedSwitchStepsPreserveScopeFramesAndOccurrencesAcrossRecovery(string profile, string shape, bool fallback, DebugStepKind kind) =>
            SwitchSteppingScenarios.NestedSwitchStepsPreserveScopeFramesAndOccurrencesAcrossRecovery(profile, shape, fallback, kind);

        public static IEnumerable<object[]> NestedStarts =>
            ScenarioCaseShards.Select(SwitchSteppingScenarios.NestedStarts, 1, 2);
    }
}
