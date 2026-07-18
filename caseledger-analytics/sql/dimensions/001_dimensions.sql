IF OBJECT_ID(N'dw.DimDate', N'U') IS NULL
BEGIN
    CREATE TABLE dw.DimDate (
        date_key int NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,
        calendar_date date NOT NULL CONSTRAINT UQ_DimDate_date UNIQUE,
        calendar_year smallint NOT NULL,
        calendar_quarter tinyint NOT NULL,
        month_number tinyint NOT NULL,
        month_name nvarchar(20) NOT NULL,
        week_of_year tinyint NOT NULL,
        day_of_month tinyint NOT NULL,
        day_name nvarchar(20) NOT NULL,
        is_weekend bit NOT NULL
    );

    WITH dates AS (
        SELECT CONVERT(date, '2024-01-01') AS calendar_date
        UNION ALL
        SELECT DATEADD(day, 1, calendar_date)
        FROM dates
        WHERE calendar_date < CONVERT(date, '2035-12-31')
    )
    INSERT dw.DimDate(
        date_key, calendar_date, calendar_year, calendar_quarter,
        month_number, month_name, week_of_year, day_of_month, day_name, is_weekend
    )
    SELECT CONVERT(int, CONVERT(char(8), calendar_date, 112)),
           calendar_date,
           YEAR(calendar_date),
           DATEPART(quarter, calendar_date),
           MONTH(calendar_date),
           DATENAME(month, calendar_date),
           DATEPART(iso_week, calendar_date),
           DAY(calendar_date),
           DATENAME(weekday, calendar_date),
           IIF(DATEPART(weekday, calendar_date) IN (1, 7), 1, 0)
    FROM dates
    OPTION (MAXRECURSION 0);
END;

IF OBJECT_ID(N'dw.DimUser', N'U') IS NULL
BEGIN
    CREATE TABLE dw.DimUser (
        user_key int IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimUser PRIMARY KEY,
        source_user_id varchar(36) NOT NULL CONSTRAINT UQ_DimUser_source UNIQUE,
        synthetic_user_id varchar(16) NOT NULL,
        role_name nvarchar(40) NOT NULL,
        is_active bit NOT NULL,
        source_system nvarchar(80) NOT NULL,
        source_updated_at datetime2(6) NOT NULL,
        load_batch_id uniqueidentifier NOT NULL
    );
END;

IF OBJECT_ID(N'dw.DimStatus', N'U') IS NULL
BEGIN
    CREATE TABLE dw.DimStatus (
        status_key smallint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimStatus PRIMARY KEY,
        status_name nvarchar(24) NOT NULL CONSTRAINT UQ_DimStatus_name UNIQUE,
        is_terminal bit NOT NULL
    );
END;

IF OBJECT_ID(N'dw.DimCategory', N'U') IS NULL
BEGIN
    CREATE TABLE dw.DimCategory (
        category_key int IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimCategory PRIMARY KEY,
        category_name nvarchar(100) NOT NULL CONSTRAINT UQ_DimCategory_name UNIQUE
    );
END;

IF OBJECT_ID(N'dw.DimChannel', N'U') IS NULL
BEGIN
    CREATE TABLE dw.DimChannel (
        channel_key smallint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimChannel PRIMARY KEY,
        channel_name nvarchar(40) NOT NULL CONSTRAINT UQ_DimChannel_name UNIQUE
    );
END;

IF OBJECT_ID(N'dw.DimCase', N'U') IS NULL
BEGIN
    CREATE TABLE dw.DimCase (
        case_key bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimCase PRIMARY KEY,
        source_case_id varchar(36) NOT NULL,
        case_reference nvarchar(32) NOT NULL,
        case_title nvarchar(160) NOT NULL,
        priority_name nvarchar(24) NOT NULL,
        category_key int NOT NULL,
        assigned_user_key int NULL,
        assigned_team nvarchar(40) NOT NULL,
        service_tier nvarchar(24) NOT NULL,
        current_status_key smallint NOT NULL,
        created_at datetime2(6) NOT NULL,
        due_at datetime2(6) NULL,
        effective_from datetime2(6) NOT NULL,
        effective_to datetime2(6) NULL,
        is_current bit NOT NULL,
        source_updated_at datetime2(6) NOT NULL,
        load_batch_id uniqueidentifier NOT NULL,
        CONSTRAINT FK_DimCase_Category FOREIGN KEY(category_key) REFERENCES dw.DimCategory(category_key),
        CONSTRAINT FK_DimCase_User FOREIGN KEY(assigned_user_key) REFERENCES dw.DimUser(user_key),
        CONSTRAINT FK_DimCase_Status FOREIGN KEY(current_status_key) REFERENCES dw.DimStatus(status_key),
        CONSTRAINT UQ_DimCase_version UNIQUE(source_case_id, effective_from),
        CONSTRAINT CK_DimCase_effective CHECK (effective_to IS NULL OR effective_to > effective_from),
        CONSTRAINT CK_DimCase_current CHECK (
            (is_current = 1 AND effective_to IS NULL) OR
            (is_current = 0 AND effective_to IS NOT NULL)
        )
    );
    CREATE UNIQUE INDEX UX_DimCase_one_current
        ON dw.DimCase(source_case_id) WHERE is_current = 1;
    CREATE INDEX IX_DimCase_interval
        ON dw.DimCase(source_case_id, effective_from, effective_to);
END;
