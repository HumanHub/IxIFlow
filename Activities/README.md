# Demo activities

`IxIFlow.DemoActivities` contains sample activities. The classes define inputs, outputs, display metadata, and designer IDs in C#. A host can register the installed assembly with `ActivityPackageRegistry.AddAssembly` using its package ID and version.

The file activities execute without additional services. `QueryCustomerActivity` requires the host to register `IDemoConnectionFactory`; the workflow contains a connection reference, not credentials.

The Site project contains matching Vue designers under `src/activities/demo` and a local catalog fixture for its editor exercise. Runtime activity types and editor assets are separate: the host loads the .NET assembly, while the browser renders catalog metadata and its registered Vue components.
