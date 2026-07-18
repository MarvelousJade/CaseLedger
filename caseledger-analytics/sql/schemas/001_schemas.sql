IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'ctl') EXEC(N'CREATE SCHEMA ctl');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'stg') EXEC(N'CREATE SCHEMA stg');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dw') EXEC(N'CREATE SCHEMA dw');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'mart') EXEC(N'CREATE SCHEMA mart');
