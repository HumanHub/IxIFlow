namespace IxIFlow.Tests.Conformance;

public sealed partial class AssignScenarios
{
    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-04")]
    public sealed class BoundariesPart1
    {
        [Theory, MemberData(nameof(Boundaries))]
        public Task AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(string profile, string shape, string fault,
            string authoring) =>
            AssignScenarios.AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Boundaries =>
            ScenarioCaseShards.Select(AssignScenarios.Boundaries, 0, 4);
    }

    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-04")]
    public sealed class BoundariesPart2
    {
        [Theory, MemberData(nameof(Boundaries))]
        public Task AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(string profile, string shape, string fault,
            string authoring) =>
            AssignScenarios.AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Boundaries =>
            ScenarioCaseShards.Select(AssignScenarios.Boundaries, 1, 4);
    }

    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-04")]
    public sealed class BoundariesPart3
    {
        [Theory, MemberData(nameof(Boundaries))]
        public Task AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(string profile, string shape, string fault,
            string authoring) =>
            AssignScenarios.AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Boundaries =>
            ScenarioCaseShards.Select(AssignScenarios.Boundaries, 2, 4);
    }

    [Trait("Feature", "control-flow")]
    [Trait("Spec", "W-04")]
    public sealed class BoundariesPart4
    {
        [Theory, MemberData(nameof(Boundaries))]
        public Task AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(string profile, string shape, string fault,
            string authoring) =>
            AssignScenarios.AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(profile, shape, fault, authoring);

        public static IEnumerable<object[]> Boundaries =>
            ScenarioCaseShards.Select(AssignScenarios.Boundaries, 3, 4);
    }
}