namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string OutboxSchema = """
        IF OBJECT_ID(N'dbo.IxIFlowOutboxPublishers', N'U') IS NULL
        CREATE TABLE dbo.IxIFlowOutboxPublishers (
            Name NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            TypesJson NVARCHAR(MAX) NOT NULL,
            DisabledAtUtc DATETIME2 NULL,
            LastError NVARCHAR(MAX) NULL
        );
        IF OBJECT_ID(N'dbo.IxIFlowOutboxEvents', N'U') IS NULL
        CREATE TABLE dbo.IxIFlowOutboxEvents (
            EventId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            Incarnation UNIQUEIDENTIFIER NOT NULL,
            InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EventType NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            EventJson NVARCHAR(MAX) NOT NULL,
            OccurredAtUtc DATETIME2 NOT NULL
        );
        IF OBJECT_ID(N'dbo.IxIFlowOutboxDeliveries', N'U') IS NULL
        CREATE TABLE dbo.IxIFlowOutboxDeliveries (
            EventId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PublisherName NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Attempts INT NOT NULL,
            FirstAttemptAtUtc DATETIME2 NULL,
            NextAttemptAtUtc DATETIME2 NOT NULL,
            Status INT NOT NULL,
            LastError NVARCHAR(MAX) NULL,
            LeaseToken NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
            LeaseUntilUtc DATETIME2 NULL,
            PRIMARY KEY (EventId, PublisherName)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.IxIFlowOutboxDeliveries') AND name=N'IX_IxIFlowOutboxDeliveries_Pending')
        CREATE INDEX IX_IxIFlowOutboxDeliveries_Pending ON dbo.IxIFlowOutboxDeliveries (PublisherName, NextAttemptAtUtc, EventId) WHERE Status=0;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.IxIFlowOutboxEvents') AND name=N'IX_IxIFlowOutboxEvents_Instance')
            CREATE INDEX IX_IxIFlowOutboxEvents_Instance ON dbo.IxIFlowOutboxEvents (InstanceId, Incarnation, EventType);
        """;
}
