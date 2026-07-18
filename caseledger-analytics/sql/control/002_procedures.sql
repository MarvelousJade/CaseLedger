CREATE OR ALTER PROCEDURE ctl.usp_BeginETLRun
    @batch_id uniqueidentifier,
    @pipeline_name nvarchar(160),
    @previous_watermark datetime2(6),
    @high_watermark datetime2(6)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @high_watermark < @previous_watermark
        THROW 51000, 'The high watermark cannot precede the previous watermark.', 1;

    INSERT ctl.ETLRun(
        batch_id, pipeline_name, previous_watermark, high_watermark, status
    ) VALUES (
        @batch_id, @pipeline_name, @previous_watermark, @high_watermark, 'Running'
    );
END;

GO

CREATE OR ALTER PROCEDURE ctl.usp_RecordDataQualityResult
    @batch_id uniqueidentifier,
    @check_name nvarchar(200),
    @expected_value nvarchar(200) = NULL,
    @actual_value nvarchar(200) = NULL,
    @passed bit,
    @details nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    MERGE ctl.DataQualityResult WITH (HOLDLOCK) AS target
    USING (SELECT @batch_id AS batch_id, @check_name AS check_name) AS source
       ON target.batch_id = source.batch_id AND target.check_name = source.check_name
    WHEN MATCHED THEN UPDATE SET
        expected_value = @expected_value,
        actual_value = @actual_value,
        passed = @passed,
        details = @details,
        executed_at = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT(
        batch_id, check_name, expected_value, actual_value, passed, details
    ) VALUES (
        @batch_id, @check_name, @expected_value, @actual_value, @passed, @details
    );
END;

GO

CREATE OR ALTER PROCEDURE ctl.usp_CompleteETLRun
    @batch_id uniqueidentifier,
    @source_entity nvarchar(160),
    @rows_extracted bigint,
    @rows_staged bigint,
    @rows_loaded bigint,
    @rows_rejected bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1 FROM ctl.DataQualityResult
        WHERE batch_id = @batch_id AND passed = 0
    )
        THROW 51001, 'A batch with failed data-quality checks cannot be completed.', 1;

    DECLARE @high_watermark datetime2(6) = (
        SELECT high_watermark FROM ctl.ETLRun WITH (UPDLOCK, HOLDLOCK)
        WHERE batch_id = @batch_id AND status = 'Running'
    );

    IF @high_watermark IS NULL
        THROW 51002, 'The running ETL batch was not found.', 1;

    UPDATE ctl.ETLRun
    SET completed_at = SYSUTCDATETIME(),
        rows_extracted = @rows_extracted,
        rows_staged = @rows_staged,
        rows_loaded = @rows_loaded,
        rows_rejected = @rows_rejected,
        duration_ms = DATEDIFF_BIG(millisecond, started_at, SYSUTCDATETIME()),
        status = 'Succeeded',
        error_message = NULL
    WHERE batch_id = @batch_id;

    MERGE ctl.Watermark WITH (HOLDLOCK) AS target
    USING (SELECT @source_entity AS source_entity) AS source
       ON target.source_entity = source.source_entity
    WHEN MATCHED THEN UPDATE SET
        last_successful_watermark = @high_watermark,
        last_batch_id = @batch_id,
        updated_at = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT(
        source_entity, last_successful_watermark, last_batch_id
    ) VALUES (
        @source_entity, @high_watermark, @batch_id
    );

    COMMIT TRANSACTION;
END;

GO

CREATE OR ALTER PROCEDURE ctl.usp_FailETLRun
    @batch_id uniqueidentifier,
    @error_message nvarchar(2000)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE ctl.ETLRun
    SET completed_at = SYSUTCDATETIME(),
        duration_ms = DATEDIFF_BIG(millisecond, started_at, SYSUTCDATETIME()),
        status = 'Failed',
        error_message = LEFT(@error_message, 2000)
    WHERE batch_id = @batch_id AND status = 'Running';
END;
