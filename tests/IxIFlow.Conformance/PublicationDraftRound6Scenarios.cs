using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
public sealed class PublicationDraftRound6Scenarios
{
    public static IEnumerable<object[]> PrefixCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "Order", "\U0001F600Order", "Order%_[x]" }.Select(stem => new object[] { profile, stem }));

    [Theory]
    [MemberData(nameof(PrefixCases))]
    public async Task DraftPrefixesAreLiteralAndLengthSensitiveAcrossLimitOnePages(string profile, string stem)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.SaveDefinitionAsync(new(stem, 1, "workflow", "shorter name"), null);
        var child = stem + " Child";
        await store.SaveDefinitionAsync(new(child, 1, "workflow", "first match"), null);
        await store.SaveDefinitionAsync(new(child, 2, "workflow", "second match"), null);
        await store.SaveDefinitionAsync(new(stem + "ly Child", 1, "workflow", "near prefix"), null);
        await store.SaveDefinitionAsync(new(stem + " child", 1, "workflow", "case control"), null);
        await store.SaveDefinitionAsync(new("OrderZZx Child", 1, "workflow", "wildcard control"), null);

        var query = new WorkflowDraftQuery(CatalogScope.Unrestricted, "workflow", stem + " ", Limit: 1);
        var first = await store.GetDefinitionsAsync(query);
        Assert.Equal((child, 1), Identity(Assert.Single(first.Items)));
        Assert.NotNull(first.NextCursor);
        var second = await store.GetDefinitionsAsync(query with { After = first.NextCursor });
        Assert.Equal((child, 2), Identity(Assert.Single(second.Items)));
        Assert.NotNull(second.NextCursor);
        var third = await store.GetDefinitionsAsync(query with { After = second.NextCursor });
        Assert.Equal((stem + " child", 1), Identity(Assert.Single(third.Items)));
        Assert.Null(third.NextCursor);
        Assert.Equal(first.CatalogRevision, third.CatalogRevision);
        Assert.Empty((await store.GetDefinitionsAsync(query with { NamePrefix = stem + "  " })).Items);

        // Exercise the shared scope predicate separately from the caller's name-prefix filter.
        // In the supplementary case these also fail if SQL Server binds a scalar count
        // instead of the UTF-16 length required by the Name column's non-SC collation.
        foreach (var names in new[] { new CatalogNames(Names: [child]), new CatalogNames(Prefixes: [stem + " C"]) })
        {
            var scope = new CatalogScope(names, new([]), new([]), new([]));
            var scopedQuery = query with { Scope = scope, NamePrefix = null };
            var scopedFirst = await store.GetDefinitionsAsync(scopedQuery);
            Assert.Equal((child, 1), Identity(Assert.Single(scopedFirst.Items)));
            Assert.NotNull(scopedFirst.NextCursor);
            var scopedSecond = await store.GetDefinitionsAsync(scopedQuery with { After = scopedFirst.NextCursor });
            Assert.Equal((child, 2), Identity(Assert.Single(scopedSecond.Items)));
            Assert.Null(scopedSecond.NextCursor);
        }
    }

    private static (string Name, int Version) Identity(WorkflowDraftSummary row) => (row.Name, row.Version);
}