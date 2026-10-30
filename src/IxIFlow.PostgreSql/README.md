# IxIFlow.PostgreSql

PostgreSQL persistence for IxIFlow instances, checkpoints, catalog, inbox, waits, traces and completion outbox, with LISTEN/NOTIFY wake-ups. Register with `services.AddIxIFlow().UsePostgreSql(connectionString)` using `IxIFlow.Extensions`.

PostgreSQL timestamps preserve whole microseconds. Binding a `DateTime` truncates its remaining submicrosecond ticks; it does not round to milliseconds.

See the [store guide](https://github.com/HumanHub/IxIFlow/blob/main/docs/stores.md) for database setup and deployment requirements.
