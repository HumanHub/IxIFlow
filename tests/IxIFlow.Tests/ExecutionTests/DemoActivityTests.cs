using IxIFlow.ActivitySdk;
using IxIFlow.DemoActivities;
using IxIFlow.Core;
using Moq;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "documents")]
public sealed class DemoActivityTests
{
    [Fact]
    public async Task AssemblyCatalog_UsesActivityCodeForFieldsAndDesignerIds()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivities(typeof(ReadJsonActivity).Assembly);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IActivityRegistry>();

        Assert.Single(registry.Packages);
        Assert.Equal(3, registry.Activities.Count);
        var read = Assert.Single(registry.Activities, activity => activity.Key == "demo.json.read");
        Assert.Equal("read-json", read.Designer);
        Assert.Equal("file-input", read.Icon);
        Assert.Equal("Path", Assert.Single(read.Inputs).Name);
        Assert.Contains(read.Outputs, member => member.Name == "Content");
        Assert.Equal(typeof(ReadJsonActivity), registry.Find(read.Key)!.ActivityType);
    }

    [Fact]
    public async Task JsonActivities_ReadAndWriteFiles()
    {
        var source = Path.GetTempFileName();
        var destination = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(source, "{\"value\":42}");
            var context = new Mock<IActivityContext>().Object;
            var read = new ReadJsonActivity { Path = source };
            await read.ExecuteAsync(context);
            var write = new WriteJsonActivity { Path = destination, Content = read.Content };
            await write.ExecuteAsync(context);

            Assert.Equal("{\"value\":42}", await File.ReadAllTextAsync(destination));
            Assert.Equal(destination, write.WrittenPath);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }
}
