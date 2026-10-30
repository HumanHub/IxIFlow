# IxIFlow.Coordinator

Optional stateless coordinator and host links for IxIFlow. `AddCoordinator` hosts a coordinator; `UseCoordinators` connects a workflow host. Coordinators distribute wake-up hints over authenticated gRPC streams; the workflow store remains authoritative and polling remains available. SQLite is single-host and cannot use coordinators.

Configure TLS, peer trust and authentication explicitly. See the [coordinator guide](https://github.com/HumanHub/IxIFlow/blob/main/docs/coordinator-and-hosts.md).
