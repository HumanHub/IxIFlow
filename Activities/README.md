# Demo activities

`IxIFlow.DemoActivities` is a sample NuGet activity package. The activity classes define their inputs, outputs, display metadata, and designer IDs in C#. The engine host registers the assembly with `ActivityPackageRegistry.AddAssembly` using the resolved NuGet package ID and version.

The file activities execute without additional services. `QueryCustomerActivity` requires the host to register `IDemoConnectionFactory`; the workflow contains a connection reference, not credentials.

The matching Vue designers live in the separate Site repository under `src/activities/demo`. A production editor host will serve designer assets from installed activity packages. The site demo currently uses a local catalog fixture.
