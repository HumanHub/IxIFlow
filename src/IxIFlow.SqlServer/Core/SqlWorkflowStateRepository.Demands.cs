namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string DemandsSchema = """
        CREATE TABLE dbo.IxIFlowWorkflowDemands (
            InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DemandKey NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DemandValue NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NULL,
            ExpiresAtUtc DATETIME2 NULL,
            Inherit BIT NOT NULL,
            PRIMARY KEY NONCLUSTERED (InstanceId, DemandKey)
        );
        """;

    internal override string ReadDemandsSql => """
        SELECT DemandKey AS [Key], DemandValue AS [Value], ExpiresAtUtc AS [ExpiresAtUtc], Inherit AS [Inherit] FROM dbo.IxIFlowWorkflowDemands WHERE InstanceId = @InstanceId ORDER BY DemandKey
        """;

    internal override string InsertDemandSql => """
        INSERT INTO dbo.IxIFlowWorkflowDemands (InstanceId, DemandKey, DemandValue, ExpiresAtUtc, Inherit)
        VALUES (@InstanceId, @Key, @Value, @ExpiresAtUtc, @Inherit)
        """;
}
