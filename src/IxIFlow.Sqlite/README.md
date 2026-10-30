# IxIFlow.Sqlite

SQLite persistence for single-host IxIFlow applications. Register with `services.AddIxIFlow().UseSqlite(connectionString)` using `IxIFlow.Extensions`. The database must be a local file; memory, network paths and concurrent hosts are refused.

See the [store guide](https://github.com/HumanHub/IxIFlow/blob/main/docs/stores.md).
