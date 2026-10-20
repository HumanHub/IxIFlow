namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string OutboxSchema = """
        IF OBJECT_ID(N'dbo.IxIFlowOutboxPublishers', N'U') IS NULL
        CREATE TABLE dbo.IxIFlowOutboxPublishers (
            Name NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            TypesJson NVARCHAR(MAX) NOT NULL,
            DisabledAtUtc DATETIME2 NULL,
            EnabledAtUtc DATETIME2 NULL,
            EnabledBy NVARCHAR(MAX) NULL,
            UnregisteredAtUtc DATETIME2 NULL,
            UnregisteredBy NVARCHAR(MAX) NULL,
            LastError NVARCHAR(MAX) NULL
        );
        IF OBJECT_ID(N'dbo.IxIFlowOutboxEvents', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.IxIFlowOutboxEvents (
                EventId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Incarnation UNIQUEIDENTIFIER NOT NULL,
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                EventType NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
                EventJson NVARCHAR(MAX) NOT NULL,
                OccurredAtUtc DATETIME2 NOT NULL
            );
            CREATE INDEX IX_IxIFlowOutboxEvents_Listing ON dbo.IxIFlowOutboxEvents (InstanceId, EventId);
            CREATE INDEX IX_IxIFlowOutboxEvents_Instance ON dbo.IxIFlowOutboxEvents (InstanceId, Incarnation, EventType);
        END
        IF OBJECT_ID(N'dbo.IxIFlowOutboxDeliveries', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.IxIFlowOutboxDeliveries (
                EventId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PublisherName NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Attempts INT NOT NULL,
                FirstAttemptAtUtc DATETIME2 NULL,
                NextAttemptAtUtc DATETIME2 NOT NULL,
                Status INT NOT NULL,
                GivenUpReason INT NULL,
                RetriedAtUtc DATETIME2 NULL,
                RetriedBy NVARCHAR(MAX) NULL,
                LastError NVARCHAR(MAX) NULL,
                LeaseToken NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
                LeaseUntilUtc DATETIME2 NULL,
                PRIMARY KEY (EventId, PublisherName)
            );
            CREATE INDEX IX_IxIFlowOutboxDeliveries_PublisherListing ON dbo.IxIFlowOutboxDeliveries (PublisherName, EventId);
            CREATE INDEX IX_IxIFlowOutboxDeliveries_Listing ON dbo.IxIFlowOutboxDeliveries (PublisherName, Status, EventId);
            CREATE INDEX IX_IxIFlowOutboxDeliveries_StatusListing ON dbo.IxIFlowOutboxDeliveries (Status, EventId, PublisherName);
            CREATE INDEX IX_IxIFlowOutboxDeliveries_Pending ON dbo.IxIFlowOutboxDeliveries (PublisherName, NextAttemptAtUtc, EventId) WHERE Status=0;
        END
        """;
}
