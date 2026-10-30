using IxIFlow.ActivitySdk;

namespace IxIFlow.BundleFixtures;

public interface IPublishedGreetingService
{
    string Greet(string value);
}

public sealed class PublishedActivityData
{
    [WorkflowInputOutput] public int Result { get; set; }
    [WorkflowInputOutput] public int Attempts { get; set; }
    [WorkflowInputOutput] public string Text { get; set; } = "";
}