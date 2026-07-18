CREATE OR ALTER PROCEDURE dw.usp_ProcessBatch
    @batch_id uniqueidentifier,
    @previous_watermark datetime2(6),
    @high_watermark datetime2(6)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (
        SELECT 1 FROM ctl.ETLRun WHERE batch_id = @batch_id AND status = 'Running'
    )
        THROW 51010, 'The batch must be registered and running before transformation.', 1;

    BEGIN TRANSACTION;

    MERGE dw.DimStatus WITH (HOLDLOCK) AS target
    USING (
        SELECT DISTINCT status_name
        FROM stg.CaseSource
        WHERE batch_id = @batch_id AND is_deleted = 0
    ) AS source
       ON target.status_name = source.status_name
    WHEN NOT MATCHED THEN INSERT(status_name, is_terminal)
        VALUES(source.status_name, IIF(source.status_name = N'Resolved', 1, 0));

    MERGE dw.DimCategory WITH (HOLDLOCK) AS target
    USING (
        SELECT DISTINCT category_name
        FROM stg.CaseSource
        WHERE batch_id = @batch_id AND is_deleted = 0
    ) AS source
       ON target.category_name = source.category_name
    WHEN NOT MATCHED THEN INSERT(category_name) VALUES(source.category_name);

    MERGE dw.DimChannel WITH (HOLDLOCK) AS target
    USING (
        SELECT channel_name FROM stg.CaseEventSource
        WHERE batch_id = @batch_id AND is_deleted = 0
        UNION
        SELECT channel_name FROM stg.IntegrationDeliverySource
        WHERE batch_id = @batch_id AND is_deleted = 0
    ) AS source
       ON target.channel_name = source.channel_name
    WHEN NOT MATCHED THEN INSERT(channel_name) VALUES(source.channel_name);

    MERGE dw.DimUser WITH (HOLDLOCK) AS target
    USING (
        SELECT source_user_id, synthetic_user_id, role_name, is_active,
               source_system, updated_at
        FROM stg.UserSource
        WHERE batch_id = @batch_id AND is_deleted = 0
    ) AS source
       ON target.source_user_id = source.source_user_id
    WHEN MATCHED AND source.updated_at >= target.source_updated_at THEN UPDATE SET
        synthetic_user_id = source.synthetic_user_id,
        role_name = source.role_name,
        is_active = source.is_active,
        source_system = source.source_system,
        source_updated_at = source.updated_at,
        load_batch_id = @batch_id
    WHEN NOT MATCHED THEN INSERT(
        source_user_id, synthetic_user_id, role_name, is_active,
        source_system, source_updated_at, load_batch_id
    ) VALUES (
        source.source_user_id, source.synthetic_user_id, source.role_name, source.is_active,
        source.source_system, source.updated_at, @batch_id
    );

    SELECT source.source_case_id,
           source.case_reference,
           source.case_title,
           source.priority_name,
           category.category_key,
           assigned_user.user_key AS assigned_user_key,
           source.assigned_team,
           source.service_tier,
           status.status_key,
           source.created_at,
           source.due_at,
           source.updated_at AS source_updated_at
    INTO #IncomingCases
    FROM stg.CaseSource source
    JOIN dw.DimCategory category ON category.category_name = source.category_name
    JOIN dw.DimStatus status ON status.status_name = source.status_name
    LEFT JOIN dw.DimUser assigned_user
      ON assigned_user.source_user_id = source.assignee_source_user_id
    WHERE source.batch_id = @batch_id AND source.is_deleted = 0;

    UPDATE current_version
    SET effective_to = incoming.source_updated_at,
        is_current = 0
    FROM dw.DimCase current_version
    JOIN #IncomingCases incoming ON incoming.source_case_id = current_version.source_case_id
    WHERE current_version.is_current = 1
      AND incoming.source_updated_at > current_version.effective_from
      AND (
          incoming.case_reference <> current_version.case_reference OR
          incoming.case_title <> current_version.case_title OR
          incoming.priority_name <> current_version.priority_name OR
          incoming.category_key <> current_version.category_key OR
          ISNULL(incoming.assigned_user_key, -1) <> ISNULL(current_version.assigned_user_key, -1) OR
          incoming.assigned_team <> current_version.assigned_team OR
          incoming.service_tier <> current_version.service_tier OR
          incoming.status_key <> current_version.current_status_key OR
          ISNULL(incoming.due_at, CONVERT(datetime2(6), '1900-01-01')) <>
              ISNULL(current_version.due_at, CONVERT(datetime2(6), '1900-01-01'))
      );

    INSERT dw.DimCase(
        source_case_id, case_reference, case_title, priority_name, category_key,
        assigned_user_key, assigned_team, service_tier, current_status_key,
        created_at, due_at, effective_from, effective_to, is_current,
        source_updated_at, load_batch_id
    )
    SELECT incoming.source_case_id,
           incoming.case_reference,
           incoming.case_title,
           incoming.priority_name,
           incoming.category_key,
           incoming.assigned_user_key,
           incoming.assigned_team,
           incoming.service_tier,
           incoming.status_key,
           incoming.created_at,
           incoming.due_at,
           CASE WHEN EXISTS (
               SELECT 1 FROM dw.DimCase history
               WHERE history.source_case_id = incoming.source_case_id
           ) THEN incoming.source_updated_at ELSE incoming.created_at END,
           NULL,
           1,
           incoming.source_updated_at,
           @batch_id
    FROM #IncomingCases incoming
    WHERE NOT EXISTS (
        SELECT 1 FROM dw.DimCase current_version
        WHERE current_version.source_case_id = incoming.source_case_id
          AND current_version.is_current = 1
    )
      AND NOT EXISTS (
        SELECT 1 FROM dw.DimCase same_version
        WHERE same_version.source_case_id = incoming.source_case_id
          AND same_version.effective_from = CASE WHEN EXISTS (
              SELECT 1 FROM dw.DimCase history
              WHERE history.source_case_id = incoming.source_case_id
          ) THEN incoming.source_updated_at ELSE incoming.created_at END
    );

    INSERT dw.FactCaseEvent(
        source_event_id, source_case_id, case_key, event_date_key,
        actor_user_key, channel_key, event_sequence, event_type,
        new_status_name, occurred_at, source_updated_at, load_batch_id
    )
    SELECT source.source_event_id,
           source.source_case_id,
           case_version.case_key,
           date_dimension.date_key,
           actor.user_key,
           channel.channel_key,
           source.event_sequence,
           source.event_type,
           JSON_VALUE(source.canonical_data, '$.data.status'),
           source.occurred_at,
           source.updated_at,
           @batch_id
    FROM stg.CaseEventSource source
    JOIN dw.DimDate date_dimension
      ON date_dimension.calendar_date = CONVERT(date, source.occurred_at)
    JOIN dw.DimUser actor ON actor.source_user_id = source.actor_source_user_id
    JOIN dw.DimChannel channel ON channel.channel_name = source.channel_name
    CROSS APPLY (
        SELECT TOP (1) candidate.case_key
        FROM dw.DimCase candidate
        WHERE candidate.source_case_id = source.source_case_id
          AND candidate.effective_from <= source.occurred_at
          AND (candidate.effective_to IS NULL OR source.occurred_at < candidate.effective_to)
        ORDER BY candidate.effective_from DESC
    ) case_version
    WHERE source.batch_id = @batch_id
      AND source.is_deleted = 0
      AND NOT EXISTS (
          SELECT 1 FROM dw.FactCaseEvent fact
          WHERE fact.source_event_id = source.source_event_id
      );

    INSERT dw.FactIntegrationDelivery(
        source_delivery_id, source_case_id, source_verification_job_id,
        case_key, delivery_date_key, channel_key, created_at, delivered_at,
        dead_lettered_at, attempt_count, failure_reason, processing_time_ms,
        is_success, is_dead_letter, load_batch_id
    )
    SELECT source.source_delivery_id,
           source.source_case_id,
           source.source_verification_job_id,
           case_version.case_key,
           date_dimension.date_key,
           channel.channel_key,
           source.created_at,
           source.delivered_at,
           source.dead_lettered_at,
           source.attempt_count,
           source.failure_reason,
           source.processing_time_ms,
           IIF(source.delivered_at IS NOT NULL, 1, 0),
           IIF(source.dead_lettered_at IS NOT NULL, 1, 0),
           @batch_id
    FROM stg.IntegrationDeliverySource source
    JOIN dw.DimDate date_dimension
      ON date_dimension.calendar_date = CONVERT(date, source.created_at)
    JOIN dw.DimChannel channel ON channel.channel_name = source.channel_name
    CROSS APPLY (
        SELECT TOP (1) candidate.case_key
        FROM dw.DimCase candidate
        WHERE candidate.source_case_id = source.source_case_id
          AND candidate.effective_from <= source.created_at
          AND (candidate.effective_to IS NULL OR source.created_at < candidate.effective_to)
        ORDER BY candidate.effective_from DESC
    ) case_version
    WHERE source.batch_id = @batch_id
      AND source.is_deleted = 0
      AND NOT EXISTS (
          SELECT 1 FROM dw.FactIntegrationDelivery fact
          WHERE fact.source_delivery_id = source.source_delivery_id
      );

    SELECT DISTINCT source_case_id INTO #AffectedCases
    FROM (
        SELECT source_case_id FROM stg.CaseSource WHERE batch_id = @batch_id
        UNION
        SELECT source_case_id FROM stg.CaseEventSource WHERE batch_id = @batch_id
    ) affected;

    DELETE snapshot
    FROM dw.FactCaseDailySnapshot snapshot
    JOIN dw.DimDate snapshot_date ON snapshot_date.date_key = snapshot.snapshot_date_key
    WHERE (
           @high_watermark > @previous_watermark
       AND snapshot_date.calendar_date >= CONVERT(date, @previous_watermark)
    )
       OR EXISTS (
           SELECT 1 FROM #AffectedCases affected
           WHERE affected.source_case_id = snapshot.source_case_id
       );

    SELECT work.source_case_id, work.date_key, work.calendar_date
    INTO #SnapshotWork
    FROM (
        SELECT affected.source_case_id,
               date_dimension.date_key,
               date_dimension.calendar_date
        FROM #AffectedCases affected
        JOIN dw.DimDate date_dimension
          ON date_dimension.calendar_date <= CONVERT(date, @high_watermark)

        UNION

        SELECT cases.source_case_id,
               date_dimension.date_key,
               date_dimension.calendar_date
        FROM (SELECT DISTINCT source_case_id FROM dw.DimCase) cases
        JOIN dw.DimDate date_dimension
          ON @high_watermark > @previous_watermark
         AND date_dimension.calendar_date >= CONVERT(date, @previous_watermark)
         AND date_dimension.calendar_date <= CONVERT(date, @high_watermark)
    ) work;

    INSERT dw.FactCaseDailySnapshot(
        snapshot_date_key, source_case_id, case_key, status_key, category_key,
        assigned_user_key, age_days, is_open, is_sla_breached, load_batch_id
    )
    SELECT snapshot_work.date_key,
           snapshot_work.source_case_id,
           case_version.case_key,
           status_dimension.status_key,
           case_version.category_key,
           case_version.assigned_user_key,
           DATEDIFF(day, CONVERT(date, case_version.created_at), snapshot_work.calendar_date),
           IIF(status_dimension.is_terminal = 0, 1, 0),
           IIF(
               status_dimension.is_terminal = 0 AND
               case_version.due_at IS NOT NULL AND
               case_version.due_at <
                   DATEADD(day, 1, CONVERT(datetime2(6), snapshot_work.calendar_date)),
               1, 0
           ),
           @batch_id
    FROM #SnapshotWork snapshot_work
    CROSS APPLY (
        SELECT TOP (1) candidate.case_key,
               candidate.current_status_key,
               candidate.category_key,
               candidate.assigned_user_key,
               candidate.created_at,
               candidate.due_at
        FROM dw.DimCase candidate
        WHERE candidate.source_case_id = snapshot_work.source_case_id
          AND candidate.created_at <
              DATEADD(day, 1, CONVERT(datetime2(6), snapshot_work.calendar_date))
          AND candidate.effective_from <
              DATEADD(day, 1, CONVERT(datetime2(6), snapshot_work.calendar_date))
          AND (
              candidate.effective_to IS NULL OR
              candidate.effective_to >=
                  DATEADD(day, 1, CONVERT(datetime2(6), snapshot_work.calendar_date))
          )
        ORDER BY candidate.effective_from DESC
    ) case_version
    OUTER APPLY (
        SELECT TOP (1) event.new_status_name
        FROM dw.FactCaseEvent event
        WHERE event.source_case_id = snapshot_work.source_case_id
          AND event.new_status_name IS NOT NULL
          AND event.occurred_at <
              DATEADD(day, 1, CONVERT(datetime2(6), snapshot_work.calendar_date))
        ORDER BY event.occurred_at DESC, event.event_sequence DESC
    ) event_status
    JOIN dw.DimStatus status_dimension
      ON status_dimension.status_name = COALESCE(
          event_status.new_status_name,
          (SELECT fallback.status_name
           FROM dw.DimStatus fallback
           WHERE fallback.status_key = case_version.current_status_key)
      );

    DECLARE @rows_loaded bigint =
        (SELECT COUNT_BIG(*) FROM dw.DimCase WHERE load_batch_id = @batch_id) +
        (SELECT COUNT_BIG(*) FROM dw.FactCaseEvent WHERE load_batch_id = @batch_id) +
        (SELECT COUNT_BIG(*) FROM dw.FactIntegrationDelivery WHERE load_batch_id = @batch_id) +
        (SELECT COUNT_BIG(*) FROM dw.FactCaseDailySnapshot WHERE load_batch_id = @batch_id);

    COMMIT TRANSACTION;

    SELECT @rows_loaded AS rows_loaded;
END;
