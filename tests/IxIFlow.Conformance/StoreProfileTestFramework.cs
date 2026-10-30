using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("IxIFlow.Tests.Conformance.StoreProfileTestFramework", "IxIFlow.Conformance")]

namespace IxIFlow.Tests.Conformance;

/// <summary>Apply profile selection to generated data, InlineData and profile-less tests alike.</summary>
public sealed class StoreProfileTestFramework(IMessageSink diagnostics) : XunitTestFramework(diagnostics)
{
    private static readonly ConformanceShard? Shard = ConformanceShard.Parse(Environment.GetEnvironmentVariable("IXIFLOW_SHARD"));

    protected override ITestFrameworkDiscoverer CreateDiscoverer(IAssemblyInfo assemblyInfo) =>
        new ProfileDiscoverer(assemblyInfo, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class ProfileDiscoverer(IAssemblyInfo assembly, ISourceInformationProvider source, IMessageSink diagnostics)
        : XunitTestFrameworkDiscoverer(assembly, source, diagnostics)
    {
        protected override bool FindTestsForMethod(ITestMethod method, bool includeSourceInformation,
            IMessageBus messageBus, ITestFrameworkDiscoveryOptions discoveryOptions)
        {
            // Enumerate the catalog before filtering rows: xUnit v2 reports an empty theory as a
            // failure. This also covers local-only/coordinator-only lists and hard-coded data.
            discoveryOptions.SetValue("xunit.discovery.PreEnumerateTheories", true);
            var previous = StoreProfiles.DiscoveringCatalog;
            StoreProfiles.DiscoveringCatalog = true;

            try
            {
                return base.FindTestsForMethod(method, includeSourceInformation, new ProfileMessageBus(messageBus), discoveryOptions);
            }
            finally
            {
                StoreProfiles.DiscoveringCatalog = previous;
            }
        }
    }

    private sealed class ProfileMessageBus(IMessageBus inner) : IMessageBus
    {
        public bool QueueMessage(IMessageSinkMessage message)
        {
            // Preserve discovery errors rather than accidentally hiding broken theories.
            if (message is ITestCaseDiscoveryMessage { TestCase: var testCase } && testCase is not ExecutionErrorTestCase)
            {
                var profile = testCase.TestMethodArguments?.OfType<string>()
                    .FirstOrDefault(argument => StoreProfiles.Catalog.Contains(argument, StringComparer.Ordinal));

                if (!StoreProfiles.Selected(profile ?? StoreProfiles.Unprofiled)
                    || Shard?.Includes(testCase.TestMethod.TestClass.Class.Name) == false)
                    return true;
            }

            return inner.QueueMessage(message);
        }

        public void Dispose()
        {
        } // The discoverer owns the underlying bus.
    }
}