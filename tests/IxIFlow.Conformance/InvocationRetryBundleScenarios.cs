using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryBundleScenarios
{
    public static IEnumerable<object[]> Profiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { new object[] { profile, false }, new object[] { profile, true } });

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task ColdPublishedRetryKeepsPinsAndReadsEditedCallerInputs(string profile, bool invalidInput)
    {
        TimeProvider clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite
            ? new FakeTimeProvider(DateTimeOffset.UtcNow)
            : TimeProvider.System;

        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.InvocationRetryBundle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var firstStore = database.NewRepository(clock);

            var child = await Publish(firstStore, "ColdRetryChild", invalidInput
                ? """
                  workflow: {name: ColdRetryChild, dataType: ChildData}
                  definitions:
                    - kind: activity
                      activity: Echo
                      input: [{target: Value, from: {path: workflow.Value}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                  """
                : """
                  workflow: {name: ColdRetryChild, dataType: ChildData}
                  definitions:
                    - kind: if
                      condition: {path: workflow.Value == "fail"}
                      then: [{kind: activity, activity: Fail}]
                      else:
                        - kind: activity
                          activity: Echo
                          input: [{target: Value, from: {path: workflow.Value}}]
                          output: [{source: Value, to: {path: workflow.Result}}]
                  """, invalidInput);

            var parent = await Publish(firstStore, "ColdRetryParent", """
                                                                      workflow: {name: ColdRetryParent, dataType: Data}
                                                                      definitions:
                                                                        - kind: invokeWorkflow
                                                                          id: invoke
                                                                          workflow: {name: ColdRetryChild, version: 1}
                                                                          input: [{target: Value, from: {path: workflow.Value}}]
                                                                          output: [{source: Result, to: {path: workflow.Result}}]
                                                                          onError:
                                                                            - exception: ERROR
                                                                              then: retry
                                                                              retry: {maximumAttempts: 1, initialInterval: '00:00:04', maximumInterval: '00:00:04', backoffCoefficient: 1}
                                                                      """.Replace("ERROR",
                invalidInput ? "IxIFlow.Core.WorkflowEngineException" : typeof(ChildFailure).FullName!), invalidInput);

            string id;
            string logicalId;
            DateTime deadline;
            DateTime wakeAt;
            var parking = new InvocationRetryPark();

            await using (var host = Host(firstStore, Path.Combine(root, "first"), clock, database, parking))
            {
                try
                {
                    await database.StartHostLinksAsync(host);

                    id = await host.GetRequiredService<IWorkflowClient>().StartAsync(parent.Name, 1,
                        new FirstData { Value = invalidInput ? "" : "fail" });

                    var saved = await parking.Wait(id);
                    var record = Assert.Single(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values);
                    logicalId = record.Id;
                    deadline = record.RetryAfterUtc!.Value;
                    wakeAt = InvocationRetryProfile.RetryDeadline(saved);
                    Assert.Equal(child.ArtifactDigest, record.BundleDigest);
                    Assert.Equal(child.ContractHash, record.ContractHash);
                    Assert.Equal(invalidInput, record.Attempts[0].ChildInstanceId == null);
                }
                finally
                {
                    var stopped = host.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
                    parking.Close();
                    await stopped;
                }
            }

            if (firstStore is IAsyncDisposable firstDisposable)
                await firstDisposable.DisposeAsync();

            var secondStore = database.NewRepository(clock);
            await using var second = Host(secondStore, Path.Combine(root, "cold"), clock, database);
            await database.StartHostLinksAsync(second);
            var client = second.GetRequiredService<IWorkflowClient>();
            var operations = second.GetRequiredService<IWorkflowOperations>();
            await operations.PauseAsync(id, "edit-input");
            await InvocationRetryProfile.Until(secondStore, id, saved => saved.Status == WorkflowStatus.Paused);
            var inspection = (await operations.InspectAsync(id))!;
            Assert.Equal(deadline, Assert.Single(inspection.Invocations).RetryAfterUtc);

            await operations.EditDataAsync(id, "input-retry", inspection.Revision, new(inspection.DebugState.PauseGeneration),
                [new DataPatch(["Value"], JsonSerializer.SerializeToElement("corrected"))], "edit-input");

            await operations.ContinueAsync(id, "edit-input");
            await InvocationRetryProfile.Due(clock, secondStore, id, wakeAt);
            await client.RecoverAsync(id);
            var completed = await InvocationRetryProfile.Finished(client, id, TimeSpan.FromSeconds(4));
            Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
            Assert.Equal("corrected", completed.WorkflowData!.GetType().GetProperty("Result")!.GetValue(completed.WorkflowData));
            await InvocationTraceAssertions.PrunedAsync(secondStore, id);

            // Published definitions are acquired through the pinned bundle.
            using var parentLease = await second.GetRequiredService<IWorkflowCatalog>().AcquireWorkflowDefinitionAsync(
                "ColdRetryParent", 1, parent.ArtifactDigest);

            var final = await InvocationTraceAssertions.AttemptsAsync(secondStore, id, parentLease.Definition);
            Assert.Equal(logicalId, Assert.Single(final.Select(attempt => attempt.InvocationId).Distinct()));
            var completedChild = (await secondStore.GetWorkflowInstanceAsync(final[^1].ChildInstanceId!))!;
            Assert.Equal(child.ArtifactDigest, completedChild.BundleDigest);
            Assert.Equal(2, final.Length);

            Assert.Equal(invalidInput ? 1 : 2,
                (await secondStore.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items.Count);

            if (!invalidInput)
                Assert.NotEqual(final[0].ChildInstanceId, final[1].ChildInstanceId);

            await second.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);

            if (secondStore is IAsyncDisposable secondDisposable)
                await secondDisposable.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ServiceProvider Host(IWorkflowStateRepository store, string cache, TimeProvider clock, ProfileStore database,
        InvocationRetryPark? parking = null) =>
        PublishedBundleScenario.Host(store, cache, configure: services =>
        {
            services.AddSingleton(clock);

            if (parking != null)
                services.AddSingleton(InvocationRetryProfile.WithFault(store, new() { Parking = parking }));

            services.AddIxIFlow(options =>
            {
                options.Debugging = true;
                options.VerifyCheckpointParts = true;
            });

            database.ConfigureHost(services);
        });

    private static async Task<WorkflowVersion> Publish(IWorkflowStateRepository store, string name, string yaml, bool invalidInput)
    {
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = typeof(FirstData).FullName!;
        options.SourceOptions.Types["ChildData"] = (invalidInput ? typeof(WorkflowContractData) : typeof(FirstData)).FullName!;
        options.SourceOptions.Types["Echo"] = typeof(Echo).FullName!;
        options.SourceOptions.Types["Fail"] = typeof(Fail).FullName!;
        options.SourceOptions.Workflows["ColdRetryChild@1"] = new(options.SourceOptions.Types["ChildData"], 1);
        var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", yaml), null);
        var published = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));

        return published.Workflow!;
    }
}