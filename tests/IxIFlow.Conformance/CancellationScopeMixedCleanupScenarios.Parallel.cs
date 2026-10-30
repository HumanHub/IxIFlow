namespace IxIFlow.Tests.Conformance;

public sealed partial class CancellationScopeMixedCleanupScenarios
{
    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part1
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 0, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 0, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 0, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 0, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part2
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 1, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 1, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 1, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 1, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part3
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 2, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 2, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 2, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 2, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part4
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 3, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 3, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 3, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 3, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part5
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 4, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 4, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 4, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 4, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part6
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 5, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 5, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 5, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 5, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part7
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 6, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 6, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 6, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 6, 8);
    }

    [Trait("Feature", "scopes"), Trait("Spec", "W-06")]
    public sealed class Part8
    {
        [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
        public Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running,
            string failure, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(profile, kind,
                running, failure, takeover);

        public static IEnumerable<object[]> NestedRetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.NestedRetryCases(), 7, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
        public Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work,
            bool takeover) =>
            CancellationScopeMixedCleanupScenarios.ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(profile,
                finallyOutside, work, takeover);

        public static IEnumerable<object[]> RetryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RetryCases(), 7, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
        public Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure,
            string boundary, bool lost, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(profile, finallyOutside, failure,
                boundary, lost, takeover);

        public static IEnumerable<object[]> RecoveryCases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.RecoveryCases(), 7, 8);

        [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
        public Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
            bool compensation, bool unresolved, bool takeover) =>
            CancellationScopeMixedCleanupScenarios.CancellationPreservesMixedCleanupAcrossRestartOrTakeover(profile, finallyOutside,
                compensation, unresolved, takeover);

        public static IEnumerable<object[]> Cases =>
            ScenarioCaseShards.Select(CancellationScopeMixedCleanupScenarios.Cases(), 7, 8);
    }
}