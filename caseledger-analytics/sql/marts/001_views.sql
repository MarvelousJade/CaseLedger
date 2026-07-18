CREATE OR ALTER VIEW mart.vw_Date
AS
SELECT date_key,
       calendar_date,
       calendar_year,
       calendar_quarter,
       month_number,
       month_name,
       week_of_year,
       day_of_month,
       day_name,
       is_weekend
FROM dw.DimDate;

GO

CREATE OR ALTER VIEW mart.vw_OperationsDaily
AS
WITH snapshot AS (
    SELECT d.calendar_date,
           s.status_name,
           c.category_name,
           dc.priority_name,
           COUNT_BIG(*) AS cases_at_eod,
           SUM(CONVERT(bigint, f.is_open)) AS backlog_eod,
           AVG(CONVERT(decimal(18,2), f.age_days)) AS average_case_age_days,
           SUM(CONVERT(bigint, f.is_sla_breached)) AS sla_breach_count
    FROM dw.FactCaseDailySnapshot f
    JOIN dw.DimDate d ON d.date_key = f.snapshot_date_key
    JOIN dw.DimCase dc ON dc.case_key = f.case_key
    JOIN dw.DimStatus s ON s.status_key = f.status_key
    JOIN dw.DimCategory c ON c.category_key = f.category_key
    GROUP BY d.calendar_date, s.status_name, c.category_name, dc.priority_name
), opened AS (
    SELECT d.calendar_date,
           status.status_name,
           category.category_name,
           case_dimension.priority_name,
           COUNT_BIG(*) AS cases_opened
    FROM dw.FactCaseEvent f
    JOIN dw.DimDate d ON d.date_key = f.event_date_key
    JOIN dw.DimCase case_dimension ON case_dimension.case_key = f.case_key
    JOIN dw.DimStatus status ON status.status_key = case_dimension.current_status_key
    JOIN dw.DimCategory category ON category.category_key = case_dimension.category_key
    WHERE f.event_type = N'CaseCreated'
    GROUP BY d.calendar_date, status.status_name, category.category_name,
             case_dimension.priority_name
), closed AS (
    SELECT d.calendar_date,
           N'Resolved' AS status_name,
           category.category_name,
           case_dimension.priority_name,
           COUNT_BIG(*) AS cases_closed
    FROM dw.FactCaseEvent f
    JOIN dw.DimDate d ON d.date_key = f.event_date_key
    JOIN dw.DimCase case_dimension ON case_dimension.case_key = f.case_key
    JOIN dw.DimCategory category ON category.category_key = case_dimension.category_key
    WHERE f.new_status_name = N'Resolved'
    GROUP BY d.calendar_date, category.category_name, case_dimension.priority_name
), metric_keys AS (
    SELECT calendar_date, status_name, category_name, priority_name FROM snapshot
    UNION
    SELECT calendar_date, status_name, category_name, priority_name FROM opened
    UNION
    SELECT calendar_date, status_name, category_name, priority_name FROM closed
)
SELECT metric_keys.calendar_date,
       metric_keys.status_name,
       metric_keys.category_name,
       metric_keys.priority_name,
       ISNULL(opened.cases_opened, 0) AS cases_opened,
       ISNULL(closed.cases_closed, 0) AS cases_closed,
       ISNULL(snapshot.cases_at_eod, 0) AS cases_at_eod,
       ISNULL(snapshot.backlog_eod, 0) AS backlog_eod,
       snapshot.average_case_age_days,
       ISNULL(snapshot.sla_breach_count, 0) AS sla_breach_count
FROM metric_keys
LEFT JOIN snapshot
  ON snapshot.calendar_date = metric_keys.calendar_date
 AND snapshot.status_name = metric_keys.status_name
 AND snapshot.category_name = metric_keys.category_name
 AND snapshot.priority_name = metric_keys.priority_name
LEFT JOIN opened
  ON opened.calendar_date = metric_keys.calendar_date
 AND opened.status_name = metric_keys.status_name
 AND opened.category_name = metric_keys.category_name
 AND opened.priority_name = metric_keys.priority_name
LEFT JOIN closed
  ON closed.calendar_date = metric_keys.calendar_date
 AND closed.status_name = metric_keys.status_name
 AND closed.category_name = metric_keys.category_name
 AND closed.priority_name = metric_keys.priority_name;

GO

CREATE OR ALTER VIEW mart.vw_CaseAging
AS
WITH latest_date AS (
    SELECT MAX(snapshot_date_key) AS snapshot_date_key FROM dw.FactCaseDailySnapshot
), first_response AS (
    SELECT source_case_id, MIN(occurred_at) AS first_response_at
    FROM dw.FactCaseEvent
    WHERE event_type = N'CommentAdded'
    GROUP BY source_case_id
)
SELECT d.calendar_date AS snapshot_date,
       c.source_case_id,
       c.case_reference,
       c.case_title,
       s.status_name,
       category.category_name,
       c.priority_name,
       c.assigned_team,
       c.service_tier,
       f.age_days,
       CASE
           WHEN f.age_days < 2 THEN N'0-1 days'
           WHEN f.age_days < 8 THEN N'2-7 days'
           WHEN f.age_days < 31 THEN N'8-30 days'
           ELSE N'31+ days'
       END AS aging_bucket,
       c.due_at,
       f.is_sla_breached,
       DATEDIFF(minute, c.created_at, response.first_response_at) AS first_response_minutes
FROM dw.FactCaseDailySnapshot f
JOIN latest_date latest ON latest.snapshot_date_key = f.snapshot_date_key
JOIN dw.DimDate d ON d.date_key = f.snapshot_date_key
JOIN dw.DimCase c ON c.case_key = f.case_key
JOIN dw.DimStatus s ON s.status_key = f.status_key
JOIN dw.DimCategory category ON category.category_key = f.category_key
LEFT JOIN first_response response ON response.source_case_id = f.source_case_id;

GO

CREATE OR ALTER VIEW mart.vw_SLACompliance
AS
SELECT snapshot_date,
       service_tier,
       category_name,
       COUNT_BIG(*) AS evaluated_cases,
       SUM(CONVERT(bigint, IIF(is_sla_breached = 0, 1, 0))) AS compliant_cases,
       SUM(CONVERT(bigint, is_sla_breached)) AS breached_cases,
       CONVERT(decimal(9,4),
           SUM(CONVERT(decimal(18,4), IIF(is_sla_breached = 0, 1, 0))) /
           NULLIF(COUNT_BIG(*), 0)
       ) AS sla_compliance_percentage
FROM mart.vw_CaseAging
GROUP BY snapshot_date, service_tier, category_name;

GO

CREATE OR ALTER VIEW mart.vw_IntegrationReliability
AS
WITH base AS (
    SELECT d.calendar_date,
           channel.channel_name,
           f.is_success,
           f.is_dead_letter,
           f.attempt_count,
           f.processing_time_ms,
           COALESCE(f.failure_reason, N'None') AS failure_reason,
           PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY f.processing_time_ms)
               OVER (PARTITION BY d.calendar_date, channel.channel_name) AS p95_processing_time_ms
    FROM dw.FactIntegrationDelivery f
    JOIN dw.DimDate d ON d.date_key = f.delivery_date_key
    JOIN dw.DimChannel channel ON channel.channel_key = f.channel_key
)
SELECT calendar_date,
       channel_name,
       failure_reason,
       COUNT_BIG(*) AS delivery_count,
       SUM(CONVERT(bigint, is_success)) AS successful_deliveries,
       SUM(CONVERT(bigint, IIF(attempt_count > 1, 1, 0))) AS retried_deliveries,
       SUM(CONVERT(bigint, is_dead_letter)) AS dead_letter_count,
       CONVERT(decimal(9,4), AVG(CONVERT(decimal(18,4), is_success))) AS delivery_success_percentage,
       CONVERT(decimal(9,4), AVG(CONVERT(decimal(18,4), IIF(attempt_count > 1, 1, 0))))
           AS retry_percentage,
       AVG(CONVERT(decimal(18,2), processing_time_ms)) AS average_processing_time_ms,
       MAX(CONVERT(decimal(18,2), p95_processing_time_ms)) AS p95_processing_time_ms
FROM base
GROUP BY calendar_date, channel_name, failure_reason;

GO

CREATE OR ALTER VIEW mart.vw_DataQuality
AS
SELECT q.batch_id,
       r.pipeline_name,
       q.check_name,
       q.expected_value,
       q.actual_value,
       q.passed,
       q.details,
       q.executed_at
FROM ctl.DataQualityResult q
JOIN ctl.ETLRun r ON r.batch_id = q.batch_id;

GO

CREATE OR ALTER VIEW mart.vw_ETLRunHistory
AS
SELECT run.batch_id,
       run.pipeline_name,
       run.started_at,
       run.completed_at,
       run.previous_watermark,
       run.high_watermark,
       run.rows_extracted,
       run.rows_staged,
       run.rows_loaded,
       run.rows_rejected,
       run.duration_ms,
       run.status,
       run.error_message,
       SUM(CONVERT(int, IIF(quality.passed = 1, 1, 0))) AS passed_checks,
       SUM(CONVERT(int, IIF(quality.passed = 0, 1, 0))) AS failed_checks
FROM ctl.ETLRun run
LEFT JOIN ctl.DataQualityResult quality ON quality.batch_id = run.batch_id
GROUP BY run.batch_id, run.pipeline_name, run.started_at, run.completed_at,
         run.previous_watermark, run.high_watermark, run.rows_extracted,
         run.rows_staged, run.rows_loaded, run.rows_rejected,
         run.duration_ms, run.status, run.error_message;

GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'caseledger_report_reader')
    CREATE ROLE caseledger_report_reader;
GRANT SELECT ON SCHEMA::mart TO caseledger_report_reader;
