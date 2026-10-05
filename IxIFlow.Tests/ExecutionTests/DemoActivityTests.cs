using IxIFlow.ActivitySdk;
using IxIFlow.DemoActivities;
using IxIFlow.Core;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class DemoActivityTests
{
    [Fact]
    public async Task AssemblyCatalog_UsesActivityCodeForFieldsAndDesignerIds()
    {
        var registry = new ActivityPackageRegistry();
        registry.AddAssembly(typeof(ReadJsonActivity).Assembly, "IxIFlow.DemoActivities", "1.0.0");

        var package = Assert.Single(registry.Packages);
        Assert.Equal(3, package.Activities.Count);
        var read = Assert.Single(package.Activities, activity => activity.Key == "demo.json.read");
        Assert.Equal("read-json", read.Designer);
        Assert.Equal("file-input", read.Icon);
        Assert.Equal("Path", Assert.Single(read.Fields).Key);
        Assert.Contains("Content", read.Outputs);
        Assert.Equal(typeof(ReadJsonActivity), (await registry.FindAsync(read.Key))!.ActivityType);
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
