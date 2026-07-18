CREATE OR ALTER PROCEDURE ctl.usp_ValidateBatch
    @batch_id uniqueidentifier,
    @expected_cases bigint = NULL,
    @expected_case_events bigint = NULL,
    @expected_users bigint = NULL,
    @expected_integration_deliveries bigint = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @results TABLE (
        check_name nvarchar(200) NOT NULL PRIMARY KEY,
        expected_value bigint NOT NULL,
        actual_value bigint NOT NULL,
        details nvarchar(2000) NOT NULL
    );

    DECLARE @actual bigint;

    SELECT @actual = COUNT_BIG(*) FROM stg.CaseSource WHERE batch_id = @batch_id;
    INSERT @results VALUES(
        N'source_to_staging_cases', COALESCE(@expected_cases, @actual), @actual,
        N'Incremental source count must reconcile to its staging batch.'
    );

    SELECT @actual = COUNT_BIG(*) FROM stg.CaseEventSource WHERE batch_id = @batch_id;
    INSERT @results VALUES(
        N'source_to_staging_case_events', COALESCE(@expected_case_events, @actual), @actual,
        N'Incremental source count must reconcile to its staging batch.'
    );

    SELECT @actual = COUNT_BIG(*) FROM stg.UserSource WHERE batch_id = @batch_id;
    INSERT @results VALUES(
        N'source_to_staging_users', COALESCE(@expected_users, @actual), @actual,
        N'Incremental source count must reconcile to its staging batch.'
    );

    SELECT @actual = COUNT_BIG(*) FROM stg.IntegrationDeliverySource WHERE batch_id = @batch_id;
    INSERT @results VALUES(
        N'source_to_staging_integration_deliveries',
        COALESCE(@expected_integration_deliveries, @actual), @actual,
        N'Incremental source count must reconcile to its staging batch.'
    );

    INSERT @results VALUES(
        N'required_case_fields', 0,
        (SELECT COUNT_BIG(*) FROM stg.CaseSource
         WHERE batch_id = @batch_id AND (
             source_case_id IS NULL OR case_reference IS NULL OR status_name IS NULL OR
             category_name IS NULL OR updated_at IS NULL
         )),
        N'Case natural keys and analytical attributes are required.'
    );

    INSERT @results VALUES(
        N'required_event_fields', 0,
        (SELECT COUNT_BIG(*) FROM stg.CaseEventSource
         WHERE batch_id = @batch_id AND (
             source_event_id IS NULL OR source_case_id IS NULL OR occurred_at IS NULL
         )),
        N'Event grain keys and business timestamps are required.'
    );

    INSERT @results VALUES(
        N'valid_status_values', 0,
        (SELECT COUNT_BIG(*) FROM stg.CaseSource
         WHERE batch_id = @batch_id AND status_name NOT IN (N'New', N'InProgress', N'Resolved')),
        N'Only CaseLedger governed status values are accepted.'
    );

    INSERT @results VALUES(
        N'one_current_case_version', 0,
        (SELECT COUNT_BIG(*) FROM (
            SELECT source_case_id FROM dw.DimCase
            GROUP BY source_case_id HAVING SUM(CONVERT(int, is_current)) <> 1
        ) invalid),
        N'Every case natural key has exactly one current SCD version.'
    );

    INSERT @results VALUES(
        N'non_overlapping_case_versions', 0,
        (SELECT COUNT_BIG(*) FROM dw.DimCase a
         JOIN dw.DimCase b
           ON a.source_case_id = b.source_case_id
          AND a.case_key < b.case_key
          AND a.effective_from < ISNULL(b.effective_to, CONVERT(datetime2(6), '9999-12-31'))
          AND b.effective_from < ISNULL(a.effective_to, CONVERT(datetime2(6), '9999-12-31'))),
        N'SCD Type 2 effective intervals use non-overlapping [from,to) ranges.'
    );

    INSERT @results VALUES(
        N'unique_case_event_facts', 0,
        (SELECT COUNT_BIG(*) FROM (
            SELECT source_event_id FROM dw.FactCaseEvent
            GROUP BY source_event_id HAVING COUNT_BIG(*) > 1
        ) duplicates),
        N'One fact row is allowed per source lifecycle event.'
    );

    INSERT @results VALUES(
        N'unique_integration_delivery_facts', 0,
        (SELECT COUNT_BIG(*) FROM (
            SELECT source_delivery_id FROM dw.FactIntegrationDelivery
            GROUP BY source_delivery_id HAVING COUNT_BIG(*) > 1
        ) duplicates),
        N'One fact row is allowed per delivery attempt.'
    );

    INSERT @results VALUES(
        N'unique_daily_snapshot_grain', 0,
        (SELECT COUNT_BIG(*) FROM (
            SELECT snapshot_date_key, source_case_id FROM dw.FactCaseDailySnapshot
            GROUP BY snapshot_date_key, source_case_id HAVING COUNT_BIG(*) > 1
        ) duplicates),
        N'Daily snapshots have one row per case and calendar date.'
    );

    INSERT @results VALUES(
        N'latest_snapshot_matches_current_cases',
        (SELECT COUNT_BIG(*) FROM dw.DimCase WHERE is_current = 1),
        (SELECT COUNT_BIG(*) FROM dw.FactCaseDailySnapshot
         WHERE snapshot_date_key = (
             SELECT MAX(snapshot_date_key) FROM dw.FactCaseDailySnapshot
         )),
        N'The latest daily snapshot includes every current case.'
    );

    INSERT @results VALUES(
        N'case_event_dimension_integrity', 0,
        (SELECT COUNT_BIG(*) FROM dw.FactCaseEvent f
         LEFT JOIN dw.DimCase c ON c.case_key = f.case_key
         LEFT JOIN dw.DimDate d ON d.date_key = f.event_date_key
         LEFT JOIN dw.DimUser u ON u.user_key = f.actor_user_key
         WHERE c.case_key IS NULL OR d.date_key IS NULL OR u.user_key IS NULL),
        N'All event fact foreign keys resolve to conformed dimensions.'
    );

    INSERT @results VALUES(
        N'snapshot_dimension_integrity', 0,
        (SELECT COUNT_BIG(*) FROM dw.FactCaseDailySnapshot f
         LEFT JOIN dw.DimCase c ON c.case_key = f.case_key
         LEFT JOIN dw.DimStatus s ON s.status_key = f.status_key
         LEFT JOIN dw.DimCategory category ON category.category_key = f.category_key
         WHERE c.case_key IS NULL OR s.status_key IS NULL OR category.category_key IS NULL),
        N'All snapshot fact foreign keys resolve to conformed dimensions.'
    );

    INSERT @results VALUES(
        N'non_negative_case_age', 0,
        (SELECT COUNT_BIG(*) FROM dw.FactCaseDailySnapshot WHERE age_days < 0),
        N'A case cannot be younger than zero days on a valid snapshot date.'
    );

    INSERT @results VALUES(
        N'non_negative_delivery_duration', 0,
        (SELECT COUNT_BIG(*) FROM dw.FactIntegrationDelivery WHERE processing_time_ms < 0),
        N'Delivery processing durations cannot be negative.'
    );

    MERGE ctl.DataQualityResult WITH (HOLDLOCK) AS target
    USING @results AS source
       ON target.batch_id = @batch_id AND target.check_name = source.check_name
    WHEN MATCHED THEN UPDATE SET
        expected_value = CONVERT(nvarchar(200), source.expected_value),
        actual_value = CONVERT(nvarchar(200), source.actual_value),
        passed = IIF(source.expected_value = source.actual_value, 1, 0),
        details = source.details,
        executed_at = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT(
        batch_id, check_name, expected_value, actual_value, passed, details
    ) VALUES (
        @batch_id, source.check_name,
        CONVERT(nvarchar(200), source.expected_value),
        CONVERT(nvarchar(200), source.actual_value),
        IIF(source.expected_value = source.actual_value, 1, 0), source.details
    );

    SELECT check_name,
           CONVERT(nvarchar(200), expected_value) AS expected_value,
           CONVERT(nvarchar(200), actual_value) AS actual_value,
           CONVERT(bit, IIF(expected_value = actual_value, 1, 0)) AS passed,
           details
    FROM @results
    ORDER BY check_name;
END;
