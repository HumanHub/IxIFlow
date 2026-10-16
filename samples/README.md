# Demo activities

`IxIFlow.DemoActivities` contains sample activities. The classes define inputs, outputs, display metadata, and designer IDs in C#. A host can register the installed assembly with `ActivityPackageRegistry.AddAssembly` using its package ID and version.

The file activities execute without additional services. `QueryCustomerActivity` requires the host to register `IDemoConnectionFactory`; the workflow contains a connection reference, not credentials.

The Site project contains matching Vue designers under `src/activities/demo` and a local catalog fixture for its editor exercise. Runtime activity types and editor assets are separate: the host loads the .NET assembly, while the browser renders catalog metadata and its registered Vue components.

# Wait and reply recipes

`IxIFlow.Recipes` contains workflows for the common ways a workflow waits for someone or something. Each recipe is a definition with its activities and data, written as an application would write it; the conformance tests run every recipe against each store. Register the activities with `services.AddWaitRecipes()` and provide the services they use.

| Recipe | Shows |
| --- | --- |
| `OneApproverRecipe` | A request with a reply token, a subject the answer must match, a timeout path, and a second click refused as already answered |
| `PaymentRecipe` | Requesting now and waiting later: a provider callback that arrives during the fraud check is kept, "pending" is refused while the wait stays open, and a callback received before the deadline beats it |
| `AnyApproverRecipe` | Any one of several approvers: a rejection keeps waiting, the first approval wins, and the other approver's late click is refused as cancelled |
| `EveryApproverRecipe` | Every approver must answer, in any order |
| `ApprovalPerLineRecipe` | An approval per loop iteration: an earlier line's token can never approve a later line |
| `ConfirmShipmentRecipe` | A human task with no request, found through `GetOpenWaitsAsync` and answered by its listed token |
| `DeliveryRecipe` | A notification by key: taken by a wait that is open when it arrives, refused if sent earlier |
| `FraudRecipe` | One workflow cancelling another, whose late answers are refused as cancelled |
| `ChildApprovalRecipe` | A child workflow sends the request; the answer goes to the child and the parent continues |

An answer that must not reach the wrong wait goes through `IWorkflowClient.ReplyAsync(token, answer)`. The token comes from `IActivityContext.Reply` in a request activity, or from `GetOpenWaitsAsync` for a wait without a request.
