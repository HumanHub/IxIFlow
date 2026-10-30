namespace IxIFlow.Tests.Conformance;

public sealed partial class SwitchScenarios
{
    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-03")]
    public sealed class CompositionsPart1
    {
        [Theory]
        [MemberData(nameof(Compositions))]
        public Task SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(string profile, string shape, string fault,
            string authoring) =>
            SwitchScenarios.SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Compositions =>
            ScenarioCaseShards.Select(SwitchScenarios.Compositions, 0, 4);
    }

    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-03")]
    public sealed class CompositionsPart2
    {
        [Theory]
        [MemberData(nameof(Compositions))]
        public Task SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(string profile, string shape, string fault,
            string authoring) =>
            SwitchScenarios.SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Compositions =>
            ScenarioCaseShards.Select(SwitchScenarios.Compositions, 1, 4);
    }

    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-03")]
    public sealed class CompositionsPart3
    {
        [Theory]
        [MemberData(nameof(Compositions))]
        public Task SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(string profile, string shape, string fault,
            string authoring) =>
            SwitchScenarios.SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Compositions =>
            ScenarioCaseShards.Select(SwitchScenarios.Compositions, 2, 4);
    }

    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-03")]
    public sealed class CompositionsPart4
    {
        [Theory]
        [MemberData(nameof(Compositions))]
        public Task SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(string profile, string shape, string fault,
            string authoring) =>
            SwitchScenarios.SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Compositions =>
            ScenarioCaseShards.Select(SwitchScenarios.Compositions, 3, 4);
    }
}