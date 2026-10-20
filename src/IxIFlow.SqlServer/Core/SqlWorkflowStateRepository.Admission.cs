namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string AdmissionSchema = """
        CREATE TABLE dbo.IxIFlowWorkflowAdmissions (
            [Incarnation] UNIQUEIDENTIFIER NOT NULL,
            [Sender] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Trigger] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Key] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Fingerprint] NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [InstanceId] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [WorkflowName] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [WorkflowVersion] INT NOT NULL,
            [ExpiresAtUtc] DATETIME2 NOT NULL,
            [NoticeJson] NVARCHAR(MAX) NULL,
            PRIMARY KEY ([Sender], [Trigger], [Key])
        );
        CREATE INDEX IX_IxIFlowAdmissions_Expiry ON dbo.IxIFlowWorkflowAdmissions ([ExpiresAtUtc]);
        CREATE TABLE dbo.IxIFlowWorkflowOwnership (
            [Incarnation] UNIQUEIDENTIFIER NOT NULL,
            [InstanceId] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Sender] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Trigger] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OwnerKind] INT NOT NULL,
            [Owner] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Tenant] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            PRIMARY KEY ([InstanceId])
        );
        CREATE TABLE dbo.IxIFlowWebhookDeliveries (
            DeliveryIdentity varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            PayloadHash varbinary(32) NOT NULL,
            Sequence BIGINT NOT NULL,
            Rejection SMALLINT NULL,
            NoticeJson NVARCHAR(MAX) NULL,
            ExpiresAtUtc DATETIME2 NOT NULL
        );
        CREATE INDEX IX_IxIFlowWebhookDeliveries_Expiry ON dbo.IxIFlowWebhookDeliveries (ExpiresAtUtc);
        """;

    private static string? AdmissionSql(Statement statement) => statement switch
    {
        Statement.AdmissionLock => "DECLARE @r INT; EXEC @r=sys.sp_getapplock @Resource=@Identity, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000; IF @r<0 THROW 51000, 'Could not lock workflow admission', 1;",
        Statement.AdmissionRead => """
            SELECT [Incarnation] AS [Incarnation], [Sender] AS [Sender], [Trigger] AS [Trigger], [Key] AS [Key], [Fingerprint] AS [Fingerprint], [InstanceId] AS [InstanceId], [WorkflowName] AS [WorkflowName], [WorkflowVersion] AS [WorkflowVersion], [ExpiresAtUtc] AS [ExpiresAtUtc], [NoticeJson] FROM dbo.IxIFlowWorkflowAdmissions WHERE [Sender]=@Sender AND [Trigger]=@Trigger AND [Key]=@Key AND [ExpiresAtUtc]>@Now
            """,
        Statement.AdmissionDelete => """
            DELETE FROM dbo.IxIFlowWorkflowAdmissions WHERE [Sender]=@Sender AND [Trigger]=@Trigger AND [Key]=@Key
            """,
        Statement.AdmissionInsert => """
            INSERT INTO dbo.IxIFlowWorkflowAdmissions ([Incarnation], [Sender], [Trigger], [Key], [Fingerprint], [InstanceId], [WorkflowName], [WorkflowVersion], [ExpiresAtUtc], [NoticeJson]) VALUES (@Incarnation, @Sender, @Trigger, @Key, @Fingerprint, @InstanceId, @WorkflowName, @WorkflowVersion, @ExpiresAtUtc, @NoticeJson)
            """,
        Statement.OwnershipDelete => """
            DELETE FROM dbo.IxIFlowWorkflowOwnership WHERE [InstanceId]=@InstanceId
            """,
        Statement.OwnershipInsert => """
            INSERT INTO dbo.IxIFlowWorkflowOwnership ([Incarnation], [InstanceId], [Sender], [Trigger], [Owner], [Tenant], [OwnerKind]) VALUES (@Incarnation, @InstanceId, @Sender, @Trigger, @Owner, @Tenant, @OwnerKind)
            """,
        Statement.OwnershipRead => """
            SELECT [InstanceId] AS [InstanceId], [Sender] AS [Sender], [Trigger] AS [Trigger], [Owner] AS [Owner], [Tenant] AS [Tenant], [Incarnation] AS [Incarnation], [OwnerKind] AS [OwnerKind] FROM dbo.IxIFlowWorkflowOwnership o WHERE o.[InstanceId]=@InstanceId AND (EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances i WHERE i.InstanceId=o.[InstanceId] AND i.Incarnation=o.[Incarnation]) OR EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowTombstones t WHERE t.InstanceId=o.[InstanceId] AND t.ExpiresAtUtc>@Now))
            """,
        Statement.AdmissionExpire => """
            DELETE TOP (@Limit) FROM dbo.IxIFlowWorkflowAdmissions WHERE [ExpiresAtUtc]<=@Now
            """,
        Statement.OwnershipExpire => """
            DELETE FROM dbo.IxIFlowWorkflowOwnership WHERE [InstanceId] IN (SELECT TOP (@Limit) o.[InstanceId] FROM dbo.IxIFlowWorkflowOwnership o WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances i WHERE i.InstanceId=o.[InstanceId] AND i.Incarnation=o.[Incarnation]) AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowTombstones t WHERE t.InstanceId=o.[InstanceId] AND t.ExpiresAtUtc>@Now))
            """,
        Statement.WebhookDeliveryRead => """
            SELECT PayloadHash AS PayloadHash, Sequence AS [Sequence], Rejection AS Rejection, NoticeJson FROM dbo.IxIFlowWebhookDeliveries WHERE DeliveryIdentity=@DeliveryIdentity AND ExpiresAtUtc>@Now
            """,
        Statement.WebhookDeliveryDelete => """
            DELETE FROM dbo.IxIFlowWebhookDeliveries WHERE DeliveryIdentity=@DeliveryIdentity
            """,
        Statement.WebhookDeliveryInsert => """
            INSERT INTO dbo.IxIFlowWebhookDeliveries (DeliveryIdentity, PayloadHash, Sequence, Rejection, ExpiresAtUtc, NoticeJson) VALUES (@DeliveryIdentity, @PayloadHash, @Sequence, @Rejection, @Expires, @NoticeJson)
            """,
        Statement.WebhookDeliveryExpire => """
            DELETE TOP (@Limit) FROM dbo.IxIFlowWebhookDeliveries WHERE ExpiresAtUtc<=@Now
            """,
        _ => RetentionSql(statement)
    };
}
