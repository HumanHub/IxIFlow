using System.Runtime.CompilerServices;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// Every commit in these tests checks its split against one that serializes every part, so a change to a branch
/// that was not marked fails the test that made it (A-08).
/// </summary>
internal static class VerifyCheckpointParts
{
    [ModuleInitializer]
    internal static void Enable() => StoredCheckpoint.VerifyAlways = true;
}
