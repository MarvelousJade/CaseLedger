IF OBJECT_ID(N'dw.FactCaseEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dw.FactCaseEvent (
        case_event_key bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FactCaseEvent PRIMARY KEY,
        source_event_id varchar(36) NOT NULL CONSTRAINT UQ_FactCaseEvent_source UNIQUE,
        source_case_id varchar(36) NOT NULL,
        case_key bigint NOT NULL,
        event_date_key int NOT NULL,
        actor_user_key int NOT NULL,
        channel_key smallint NOT NULL,
        event_sequence int NOT NULL,
        event_type nvarchar(80) NOT NULL,
        new_status_name nvarchar(24) NULL,
        occurred_at datetime2(6) NOT NULL,
        source_updated_at datetime2(6) NOT NULL,
        load_batch_id uniqueidentifier NOT NULL,
        CONSTRAINT FK_FactCaseEvent_Case FOREIGN KEY(case_key) REFERENCES dw.DimCase(case_key),
        CONSTRAINT FK_FactCaseEvent_Date FOREIGN KEY(event_date_key) REFERENCES dw.DimDate(date_key),
        CONSTRAINT FK_FactCaseEvent_User FOREIGN KEY(actor_user_key) REFERENCES dw.DimUser(user_key),
        CONSTRAINT FK_FactCaseEvent_Channel FOREIGN KEY(channel_key) REFERENCES dw.DimChannel(channel_key)
    );
    CREATE INDEX IX_FactCaseEvent_date_type ON dw.FactCaseEvent(event_date_key, event_type);
END;

IF OBJECT_ID(N'dw.FactCaseDailySnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE dw.FactCaseDailySnapshot (
        case_daily_snapshot_key bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_FactCaseDailySnapshot PRIMARY KEY,
        snapshot_date_key int NOT NULL,
        source_case_id varchar(36) NOT NULL,
        case_key bigint NOT NULL,
        status_key smallint NOT NULL,
        category_key int NOT NULL,
        assigned_user_key int NULL,
        age_days int NOT NULL,
        is_open bit NOT NULL,
        is_sla_breached bit NOT NULL,
        load_batch_id uniqueidentifier NOT NULL,
        CONSTRAINT UQ_FactCaseDailySnapshot_grain UNIQUE(snapshot_date_key, source_case_id),
        CONSTRAINT FK_FactCaseDailySnapshot_Date FOREIGN KEY(snapshot_date_key) REFERENCES dw.DimDate(date_key),
        CONSTRAINT FK_FactCaseDailySnapshot_Case FOREIGN KEY(case_key) REFERENCES dw.DimCase(case_key),
        CONSTRAINT FK_FactCaseDailySnapshot_Status FOREIGN KEY(status_key) REFERENCES dw.DimStatus(status_key),
        CONSTRAINT FK_FactCaseDailySnapshot_Category FOREIGN KEY(category_key) REFERENCES dw.DimCategory(category_key),
        CONSTRAINT FK_FactCaseDailySnapshot_User FOREIGN KEY(assigned_user_key) REFERENCES dw.DimUser(user_key)
    );
    CREATE INDEX IX_FactCaseDailySnapshot_date_open
        ON dw.FactCaseDailySnapshot(snapshot_date_key, is_open);
END;

IF OBJECT_ID(N'dw.FactIntegrationDelivery', N'U') IS NULL
BEGIN
    CREATE TABLE dw.FactIntegrationDelivery (
        integration_delivery_key bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_FactIntegrationDelivery PRIMARY KEY,
        source_delivery_id varchar(36) NOT NULL CONSTRAINT UQ_FactIntegrationDelivery_source UNIQUE,
        source_case_id varchar(36) NOT NULL,
        source_verification_job_id varchar(36) NOT NULL,
        case_key bigint NOT NULL,
        delivery_date_key int NOT NULL,
        channel_key smallint NOT NULL,
        created_at datetime2(6) NOT NULL,
        delivered_at datetime2(6) NULL,
        dead_lettered_at datetime2(6) NULL,
        attempt_count int NOT NULL,
        failure_reason nvarchar(80) NULL,
        processing_time_ms bigint NULL,
        is_success bit NOT NULL,
        is_dead_letter bit NOT NULL,
        load_batch_id uniqueidentifier NOT NULL,
        CONSTRAINT FK_FactIntegrationDelivery_Case FOREIGN KEY(case_key) REFERENCES dw.DimCase(case_key),
        CONSTRAINT FK_FactIntegrationDelivery_Date FOREIGN KEY(delivery_date_key) REFERENCES dw.DimDate(date_key),
        CONSTRAINT FK_FactIntegrationDelivery_Channel FOREIGN KEY(channel_key) REFERENCES dw.DimChannel(channel_key)
    );
    CREATE INDEX IX_FactIntegrationDelivery_date
        ON dw.FactIntegrationDelivery(delivery_date_key, is_success, is_dead_letter);
END;
