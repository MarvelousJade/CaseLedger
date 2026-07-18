DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'caseledger_analytics') THEN
        CREATE ROLE caseledger_analytics LOGIN PASSWORD 'caseledger-analytics-local';
    END IF;
END
$$;

GRANT CONNECT ON DATABASE caseledger_source TO caseledger_analytics;
GRANT USAGE ON SCHEMA public TO caseledger_analytics;
GRANT SELECT ON analytics_export_users,
                analytics_export_cases,
                analytics_export_case_events,
                analytics_export_assignments,
                analytics_export_delivery_attempts
TO caseledger_analytics;
