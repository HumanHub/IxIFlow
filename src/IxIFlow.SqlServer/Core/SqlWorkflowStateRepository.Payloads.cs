namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string PayloadSchema = """

        CREATE TABLE dbo.IxIFlowWorkflowPayloads (
            Digest VARCHAR(71) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            Bytes BIGINT NOT NULL,
            Body VARBINARY(MAX) NOT NULL, MetadataJson NVARCHAR(MAX) NOT NULL
        );
        CREATE TABLE dbo.IxIFlowWorkflowPayloadRefs (
            Digest VARCHAR(71) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OwnerKind NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            OwnerId NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY (Digest, OwnerKind, OwnerId),
            FOREIGN KEY (Digest) REFERENCES dbo.IxIFlowWorkflowPayloads(Digest)
        );
        CREATE INDEX IX_IxIFlowPayloadRefs_Owner ON dbo.IxIFlowWorkflowPayloadRefs(OwnerKind, OwnerId, Digest);
        """;
}
