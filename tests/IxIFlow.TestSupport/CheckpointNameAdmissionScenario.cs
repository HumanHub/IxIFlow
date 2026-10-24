using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Xunit;

namespace IxIFlow.Tests.Infrastructure;

public static class CheckpointNameAdmissionScenario
{
    public static readonly string[] Starts = ["root", "admission", "child-checkpoint", "child-start"];

    public static async Task VerifyAsync(IWorkflowStateRepository store, string start, bool malformed)
    {
        var contract = WorkflowContract.OfType(typeof(object));
        foreach (var name in new[] { "D\ufffd", "D\U00010000", "Parent" })
            await store.RegisterWorkflowVersionAsync(new(name, 1, "hash", contract.ToJson(), contract.Hash, Descriptor: new WorkflowDescriptor(name, "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), []));

        foreach (var name in malformed ? new[] { "D\ud800", "D\udc00" } : new[] { "D\ufffd", "D\U00010000", "" })
        {
            var instance = Instance(name);
            WorkflowInstance? parent = null;
            if (start is "child-start" or "child-checkpoint")
            {
                parent = Instance("Parent");
                parent.Status = WorkflowStatus.Suspended;
                await store.SeedAsync(parent, wakeAfter: TimeSpan.Zero);
                instance.ParentInstanceId = parent.InstanceId;
                instance.ParentWaitId = "child";
            }
            var epoch = start == "admission" ? 0 : (await StoreTestSupport.LeaseAsync(store,
                start == "child-start" ? parent!.InstanceId : instance.InstanceId)).Epoch;
            var wrapped = DispatchProxy.Create<IWorkflowStateRepository, WriteProbe>();
            var probe = (WriteProbe)wrapped;
            probe.Store = store;
            var admission = new WorkflowAdmission("sender", "trigger", instance.InstanceId, new string('a', 64), "owner");

            CheckpointCommit Create()
            {
                if (start == "admission") return CheckpointCommit.CreateAdmission(instance);
                if (start != "child-start") return CheckpointCommit.Create(instance, epoch, 0);

                // Preserve malformed code units as JSON escapes instead of letting the encoder
                // replace them in the test setup. ChildStart accepts its instance as wire JSON.
                var payload = JsonSerializer.Serialize(instance);
                if (malformed)
                {
                    Assert.Contains("\"WorkflowName\":\"D\\uFFFD\"", payload);
                    payload = payload.Replace("\"WorkflowName\":\"D\\uFFFD\"",
                        "\"WorkflowName\":\"D\\u" + ((int)name[^1]).ToString("X4") + "\"", StringComparison.Ordinal);
                }
                return CheckpointCommit.Create(parent!, epoch, parent!.Revision,
                    childStarts: [new(instance.InstanceId, payload, "child")],
                    openedWaits: [new("child", WaitKind.Child, "child", ChildCompleted.EventType, null)]);
            }

            async Task<WorkflowCommitResult> WriteAsync()
            {
                var commit = Create();
                return start == "admission"
                    ? await wrapped.AdmitWorkflowInstanceAsync(commit, admission)
                    : await wrapped.CommitWorkflowInstanceAsync(commit);
            }

            if (malformed)
            {
                await Assert.ThrowsAnyAsync<ArgumentException>(WriteAsync);
                Assert.Equal(0, probe.Writes);
                Assert.Equal(name, instance.WorkflowName);
                if (start != "child-start") Assert.Equal(Guid.Empty, instance.Incarnation);
                Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
                Assert.Null(await store.GetWorkflowOwnershipAsync(instance.InstanceId));
                Assert.Null(await store.GetWorkflowAdmissionAsync(admission.Sender, admission.Trigger, admission.Key));
                if (parent != null)
                {
                    Assert.Equal(parent.Revision, (await store.GetWorkflowInstanceAsync(parent.InstanceId))!.Revision);
                    Assert.Null(await store.GetWaitAsync(parent.InstanceId, "child"));
                }
            }
            else
            {
                var result = await WriteAsync();
                Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
                Assert.Equal(1, probe.Writes);
                var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
                Assert.NotNull(saved);
                Assert.Equal(name, saved.WorkflowName);
                Assert.Equal(1, saved.Revision);
                Assert.Equal(instance.ParentInstanceId, saved.ParentInstanceId);
                if (start == "admission")
                {
                    Assert.Equal(name, result.Admission!.WorkflowName);
                    Assert.Equal(name, (await store.GetWorkflowAdmissionAsync(admission.Sender, admission.Trigger, admission.Key))!.WorkflowName);
                }
                if (start == "child-start")
                {
                    Assert.Equal(parent!.Revision + 1, (await store.GetWorkflowInstanceAsync(parent.InstanceId))!.Revision);
                    Assert.NotNull(await store.GetWaitAsync(parent.InstanceId, "child"));
                }
            }
        }
    }

    private static WorkflowInstance Instance(string name) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, WorkflowVersion = 1,
        Status = WorkflowStatus.Running, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(object))
    };

    public class WriteProbe : DispatchProxy
    {
        public IWorkflowStateRepository Store = null!;
        public int Writes;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync) or
                nameof(IWorkflowAdmissionStore.AdmitWorkflowInstanceAsync)) Writes++;
            try { return targetMethod.Invoke(Store, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
