IF OBJECT_ID(N'stg.CaseSource', N'U') IS NULL
BEGIN
    CREATE TABLE stg.CaseSource (
        batch_id uniqueidentifier NOT NULL,
        extracted_at datetime2(6) NOT NULL,
        source_case_id varchar(36) NOT NULL,
        case_reference nvarchar(32) NOT NULL,
        case_title nvarchar(160) NOT NULL,
        status_name nvarchar(24) NOT NULL,
        priority_name nvarchar(24) NOT NULL,
        category_name nvarchar(100) NOT NULL,
        assignee_source_user_id varchar(36) NULL,
        assigned_team nvarchar(40) NOT NULL,
        service_tier nvarchar(24) NOT NULL,
        created_by_source_user_id varchar(36) NOT NULL,
        created_at datetime2(6) NOT NULL,
        updated_at datetime2(6) NOT NULL,
        due_at datetime2(6) NULL,
        is_deleted bit NOT NULL,
        source_system nvarchar(80) NOT NULL,
        CONSTRAINT PK_CaseSource PRIMARY KEY(batch_id, source_case_id)
    );
    CREATE INDEX IX_CaseSource_updated ON stg.CaseSource(updated_at);
END;

IF OBJECT_ID(N'stg.CaseEventSource', N'U') IS NULL
BEGIN
    CREATE TABLE stg.CaseEventSource (
        batch_id uniqueidentifier NOT NULL,
        extracted_at datetime2(6) NOT NULL,
        source_event_id varchar(36) NOT NULL,
        source_case_id varchar(36) NOT NULL,
        event_sequence int NOT NULL,
        event_type nvarchar(80) NOT NULL,
        actor_source_user_id varchar(36) NOT NULL,
        occurred_at datetime2(6) NOT NULL,
        created_at datetime2(6) NOT NULL,
        updated_at datetime2(6) NOT NULL,
        channel_name nvarchar(40) NOT NULL,
        canonical_data nvarchar(max) NOT NULL,
        is_deleted bit NOT NULL,
        source_system nvarchar(80) NOT NULL,
        CONSTRAINT PK_CaseEventSource PRIMARY KEY(batch_id, source_event_id)
    );
    CREATE INDEX IX_CaseEventSource_updated ON stg.CaseEventSource(updated_at);
END;

IF OBJECT_ID(N'stg.UserSource', N'U') IS NULL
BEGIN
    CREATE TABLE stg.UserSource (
        batch_id uniqueidentifier NOT NULL,
        extracted_at datetime2(6) NOT NULL,
        source_user_id varchar(36) NOT NULL,
        synthetic_user_id varchar(16) NOT NULL,
        role_name nvarchar(40) NOT NULL,
        is_active bit NOT NULL,
        created_at datetime2(6) NOT NULL,
        updated_at datetime2(6) NOT NULL,
        is_deleted bit NOT NULL,
        source_system nvarchar(80) NOT NULL,
        CONSTRAINT PK_UserSource PRIMARY KEY(batch_id, source_user_id)
    );
END;

IF OBJECT_ID(N'stg.IntegrationDeliverySource', N'U') IS NULL
BEGIN
    CREATE TABLE stg.IntegrationDeliverySource (
        batch_id uniqueidentifier NOT NULL,
        extracted_at datetime2(6) NOT NULL,
        source_delivery_id varchar(36) NOT NULL,
        source_case_id varchar(36) NOT NULL,
        source_verification_job_id varchar(36) NOT NULL,
        channel_name nvarchar(40) NOT NULL,
        created_at datetime2(6) NOT NULL,
        updated_at datetime2(6) NOT NULL,
        delivered_at datetime2(6) NULL,
        dead_lettered_at datetime2(6) NULL,
        attempt_count int NOT NULL,
        failure_reason nvarchar(80) NULL,
        processing_time_ms bigint NULL,
        is_deleted bit NOT NULL,
        source_system nvarchar(80) NOT NULL,
        CONSTRAINT PK_IntegrationDeliverySource PRIMARY KEY(batch_id, source_delivery_id)
    );
END;

-- Forward-compatible rename for early local builds of the staging contract.
IF COL_LENGTH(N'stg.CaseSource', N'source_updated_at') IS NOT NULL
   AND COL_LENGTH(N'stg.CaseSource', N'updated_at') IS NULL
    EXEC sys.sp_rename N'stg.CaseSource.source_updated_at', N'updated_at', N'COLUMN';
IF COL_LENGTH(N'stg.CaseEventSource', N'source_updated_at') IS NOT NULL
   AND COL_LENGTH(N'stg.CaseEventSource', N'updated_at') IS NULL
    EXEC sys.sp_rename N'stg.CaseEventSource.source_updated_at', N'updated_at', N'COLUMN';
IF COL_LENGTH(N'stg.UserSource', N'source_updated_at') IS NOT NULL
   AND COL_LENGTH(N'stg.UserSource', N'updated_at') IS NULL
    EXEC sys.sp_rename N'stg.UserSource.source_updated_at', N'updated_at', N'COLUMN';
IF COL_LENGTH(N'stg.IntegrationDeliverySource', N'source_updated_at') IS NOT NULL
   AND COL_LENGTH(N'stg.IntegrationDeliverySource', N'updated_at') IS NULL
    EXEC sys.sp_rename N'stg.IntegrationDeliverySource.source_updated_at', N'updated_at', N'COLUMN';
