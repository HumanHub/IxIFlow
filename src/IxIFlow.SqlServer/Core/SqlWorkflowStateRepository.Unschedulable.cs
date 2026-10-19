namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    internal override string AllDemandSetsSql => """
        SELECT sets.DemandSetId, demand.DemandKey AS [Key], demand.DemandValue AS Value,
            demand.ExpiresAtUtc, demand.Inherit FROM dbo.IxIFlowDemandSets AS sets
        LEFT JOIN dbo.IxIFlowDemandSetRows AS demand ON demand.DemandSetId = sets.DemandSetId;
        """;

    internal override string UnschedulableSql => """
        SELECT Id COLLATE Latin1_General_100_BIN2 AS Id, Failed, Pins INTO #BacklogSets
        FROM OPENJSON(@Sets) WITH (Id VARCHAR(64), Failed BIT, Pins NVARCHAR(MAX) AS JSON);
        CREATE UNIQUE CLUSTERED INDEX IX_BacklogSets ON #BacklogSets(Id);
        SELECT sets.Id, pin.value COLLATE Latin1_General_100_BIN2 AS Value INTO #BacklogPins
        FROM #BacklogSets AS sets CROSS APPLY OPENJSON(sets.Pins) AS pin;
        WITH candidates AS (
            SELECT candidate.* FROM #BacklogSets AS sets
            CROSS APPLY (
                SELECT TOP (@Limit) instance.InstanceId, instance.RunnableAtUtc
                FROM dbo.IxIFlowWorkflowInstances AS instance WITH (FORCESEEK, INDEX(IX_IxIFlowInstances_SetRunnable))
                WHERE instance.DemandSetId = sets.Id AND sets.Failed = 1
                  AND instance.RunnableAtUtc <= @Now
                  AND (@AfterTime IS NULL OR instance.RunnableAtUtc > @AfterTime OR
                      (instance.RunnableAtUtc = @AfterTime AND instance.InstanceId > @AfterId))
                  AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowLeases AS lease
                      WHERE lease.InstanceId = instance.InstanceId AND lease.ExpiresAtUtc > @Now)
                ORDER BY instance.RunnableAtUtc, instance.InstanceId) AS candidate
            UNION ALL
            SELECT candidate.* FROM #BacklogSets AS sets
            CROSS APPLY (
                SELECT TOP (@Limit) instance.InstanceId, instance.RunnableAtUtc
                FROM dbo.IxIFlowWorkflowInstances AS instance WITH (FORCESEEK, INDEX(IX_IxIFlowInstances_SetPin))
                WHERE instance.DemandSetId = sets.Id AND sets.Failed = 0 AND instance.PinnedHost IS NOT NULL
                  AND (instance.PinExpiresAtUtc IS NULL OR instance.PinExpiresAtUtc > @Now)
                  AND NOT EXISTS (SELECT 1 FROM #BacklogPins AS pin WHERE pin.Id = sets.Id
                      AND pin.Value = instance.PinnedHost AND DATALENGTH(pin.Value) = DATALENGTH(instance.PinnedHost))
                  AND instance.RunnableAtUtc <= @Now
                  AND (@AfterTime IS NULL OR instance.RunnableAtUtc > @AfterTime OR
                      (instance.RunnableAtUtc = @AfterTime AND instance.InstanceId > @AfterId))
                  AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowLeases AS lease
                      WHERE lease.InstanceId = instance.InstanceId AND lease.ExpiresAtUtc > @Now)
                ORDER BY instance.RunnableAtUtc, instance.InstanceId) AS candidate),
        page AS (SELECT TOP (@Limit) InstanceId, MIN(RunnableAtUtc) AS RunnableAtUtc FROM candidates
            GROUP BY InstanceId ORDER BY MIN(RunnableAtUtc), InstanceId)
        SELECT instance.InstanceId, instance.WorkflowName, JSON_VALUE(instance.StateJson, '$.WorkflowVersion') AS WorkflowVersion,
            page.RunnableAtUtc, instance.DemandSetId, instance.PinnedHost, instance.PinExpiresAtUtc, instance.PinInherit
        FROM page JOIN dbo.IxIFlowWorkflowInstances AS instance WITH (FORCESEEK) ON instance.InstanceId = page.InstanceId
        ORDER BY page.RunnableAtUtc, page.InstanceId OPTION (LOOP JOIN, RECOMPILE);
        DROP TABLE #BacklogPins;
        DROP TABLE #BacklogSets;
        """;
}
