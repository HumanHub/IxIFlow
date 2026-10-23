using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class CatalogScopeDraftTests
{
    [Fact]
    public void NormalizedScopesHaveStableHashesAndIndependentKinds()
    {
        var first = new CatalogScope(new([" B ", "A", "A"]), new(Prefixes: ["%_[]"]), new(["PACKAGE"]), new([]));
        var same = new CatalogScope(new(["A", "B"], []), new([], ["%_[]"]), new(["package"], []), new([], []));
        Assert.Equal(first.Hash, same.Hash);
        Assert.True(first.Allows("workflow", "A"));
        Assert.False(first.Allows("workflow", "a"));
        Assert.True(first.Allows("activity", "%_[]key"));
        Assert.False(first.Allows("activity", "arbitrary"));
        Assert.True(first.Allows("package", "Package"));
        Assert.False(first.Allows("template", "A"));
        Assert.True(CatalogScope.Unrestricted.Allows("template", "A"));
        Assert.NotEqual(first.Hash, CatalogScope.Unrestricted.Hash);
        var mutable = new List<string> { "A" };
        var frozen = new CatalogScope(new(mutable), new(), new(), new());
        mutable.Add("B");
        Assert.False(frozen.Allows("workflow", "B"));
    }

    [Fact]
    public void ScopeBoundsAndRequiredMembersAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => new CatalogScope(null!, new(), new(), new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogScope(new(Enumerable.Repeat("a", 101).ToArray()), new(), new(), new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogScope(new(), new([new string('a', 101)]), new(), new()));
        Assert.Throws<ArgumentException>(() => new CatalogScope(new([" "]), new(), new(), new()));
    }
}
