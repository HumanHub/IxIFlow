using System;
using System.Collections.Generic;

namespace IxIFlow.Authoring;

/// <summary>The terminal location is written, followed by its contiguous value-type receivers.</summary>
internal static class AssignmentLocationChain
{
    // Links are ordered from the workflow data toward the terminal location. A reference receiver
    // ends write-back: changing an object reached through a struct does not change the struct itself.
    internal static IEnumerable<int> WrittenLinks<T>(IReadOnlyList<T> links, Func<T, bool> valueType)
    {
        if (links.Count == 0)
            yield break;

        yield return links.Count - 1;

        for (var index = links.Count - 2; index >= 0 && valueType(links[index]); index--)
            yield return index;
    }
}