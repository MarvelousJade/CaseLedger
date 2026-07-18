IF OBJECT_ID(N'ctl.ETLRun', N'U') IS NULL
BEGIN
    CREATE TABLE ctl.ETLRun (
        batch_id uniqueidentifier NOT NULL CONSTRAINT PK_ETLRun PRIMARY KEY,
        pipeline_name nvarchar(160) NOT NULL,
        started_at datetime2(6) NOT NULL CONSTRAINT DF_ETLRun_started DEFAULT SYSUTCDATETIME(),
        completed_at datetime2(6) NULL,
        previous_watermark datetime2(6) NOT NULL,
        high_watermark datetime2(6) NOT NULL,
        rows_extracted bigint NOT NULL CONSTRAINT DF_ETLRun_extracted DEFAULT 0,
        rows_staged bigint NOT NULL CONSTRAINT DF_ETLRun_staged DEFAULT 0,
        rows_loaded bigint NOT NULL CONSTRAINT DF_ETLRun_loaded DEFAULT 0,
        rows_rejected bigint NOT NULL CONSTRAINT DF_ETLRun_rejected DEFAULT 0,
        duration_ms bigint NULL,
        status varchar(24) NOT NULL,
        error_message nvarchar(2000) NULL,
        CONSTRAINT CK_ETLRun_status CHECK (status IN ('Running', 'Succeeded', 'Failed')),
        CONSTRAINT CK_ETLRun_window CHECK (high_watermark >= previous_watermark)
    );
    CREATE INDEX IX_ETLRun_started_at ON ctl.ETLRun(started_at DESC);
END;

IF OBJECT_ID(N'ctl.Watermark', N'U') IS NULL
BEGIN
    CREATE TABLE ctl.Watermark (
        source_entity nvarchar(160) NOT NULL CONSTRAINT PK_Watermark PRIMARY KEY,
        last_successful_watermark datetime2(6) NOT NULL,
        last_batch_id uniqueidentifier NULL,
        updated_at datetime2(6) NOT NULL CONSTRAINT DF_Watermark_updated DEFAULT SYSUTCDATETIME()
    );

    INSERT ctl.Watermark(source_entity, last_successful_watermark)
    VALUES (N'caseledger', CONVERT(datetime2(6), '1900-01-01T00:00:00'));
END;

IF OBJECT_ID(N'ctl.DataQualityResult', N'U') IS NULL
BEGIN
    CREATE TABLE ctl.DataQualityResult (
        data_quality_result_id bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_DataQualityResult PRIMARY KEY,
        batch_id uniqueidentifier NOT NULL,
        check_name nvarchar(200) NOT NULL,
        expected_value nvarchar(200) NULL,
        actual_value nvarchar(200) NULL,
        passed bit NOT NULL,
        details nvarchar(2000) NULL,
        executed_at datetime2(6) NOT NULL CONSTRAINT DF_DataQualityResult_executed DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_DataQualityResult_ETLRun FOREIGN KEY (batch_id) REFERENCES ctl.ETLRun(batch_id),
        CONSTRAINT UQ_DataQualityResult_batch_check UNIQUE(batch_id, check_name)
    );
    CREATE INDEX IX_DataQualityResult_passed_executed
        ON ctl.DataQualityResult(passed, executed_at DESC);
END;
