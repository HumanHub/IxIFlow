namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository : IPayloadStoreProvider
{
    public IPayloadStore PayloadStore => Payloads;

    private const string PayloadSchema = """

        CREATE TABLE ixiflow_workflow_payloads (
            digest TEXT NOT NULL PRIMARY KEY,
            bytes BIGINT NOT NULL, generation BIGINT NOT NULL,
            body BLOB NULL, state_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        );
        CREATE TABLE ixiflow_workflow_payload_refs (
            digest TEXT NOT NULL REFERENCES ixiflow_workflow_payloads(digest),
            owner_kind TEXT NOT NULL, owner_id TEXT NOT NULL,
            PRIMARY KEY (digest, owner_kind, owner_id)
        );
        CREATE INDEX ixiflow_payload_refs_owner ON ixiflow_workflow_payload_refs(owner_kind, owner_id, digest);
        """;
}
