-- Canonical CaseLedger Analytics source contract, version 1.
-- Install with an owner account, then grant SELECT on only these views to the
-- read-only analytics identity. See README.md for the privacy and versioning
-- boundaries.

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE OR REPLACE VIEW analytics_export_users AS
WITH activity AS (
    SELECT u."Id" AS user_id,
           MIN(ts) AS first_seen_at,
           MAX(ts) AS last_seen_at
    FROM "Users" u
    LEFT JOIN LATERAL (
        SELECT c."UpdatedAt" AS ts FROM "Cases" c
        WHERE c."AssigneeId" = u."Id" OR c."CreatedById" = u."Id"
        UNION ALL
        SELECT e."CreatedAt" FROM "AuditEvents" e WHERE e."ActorId" = u."Id"
    ) seen ON true
    GROUP BY u."Id"
)
SELECT u."Id"::text AS source_user_id,
       'usr-' || SUBSTRING(encode(digest(u."Id"::text, 'sha256'), 'hex') FROM 1 FOR 12) AS synthetic_user_id,
       u."Role" AS role_name,
       u."IsActive" AS is_active,
       COALESCE(a.first_seen_at, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS created_at,
       COALESCE(a.last_seen_at, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS updated_at,
       false AS is_deleted,
       'caseledger-postgresql'::text AS source_system
FROM "Users" u
LEFT JOIN activity a ON a.user_id = u."Id";

CREATE OR REPLACE VIEW analytics_export_cases AS
SELECT c."Id"::text AS source_case_id,
       c."Reference" AS case_reference,
       c."Title" AS case_title,
       c."Status" AS status_name,
       c."Severity" AS priority_name,
       c."Category" AS category_name,
       c."AssigneeId"::text AS assignee_source_user_id,
       COALESCE(assignee."Role", 'Unassigned') AS assigned_team,
       CASE c."Severity"
           WHEN 'Critical' THEN 'Platinum'
           WHEN 'High' THEN 'Gold'
           WHEN 'Medium' THEN 'Standard'
           ELSE 'Basic'
       END AS service_tier,
       c."CreatedById"::text AS created_by_source_user_id,
       c."CreatedAt" AS created_at,
       c."UpdatedAt" AS updated_at,
       c."DueAt" AS due_at,
       false AS is_deleted,
       'caseledger-postgresql'::text AS source_system
FROM "Cases" c
LEFT JOIN "Users" assignee ON assignee."Id" = c."AssigneeId";

CREATE OR REPLACE VIEW analytics_export_case_events AS
SELECT e."Id"::text AS source_event_id,
       e."CaseId"::text AS source_case_id,
       e."Sequence" AS event_sequence,
       e."EventType" AS event_type,
       e."ActorId"::text AS actor_source_user_id,
       COALESCE(
           NULLIF((e."CanonicalData"::jsonb -> 'data' ->> 'businessOccurredAt'), '')::timestamptz,
           e."CreatedAt"
       ) AS occurred_at,
       e."CreatedAt" AS created_at,
       e."CreatedAt" AS updated_at,
       CASE
           WHEN e."EventType" ILIKE '%Webhook%' THEN 'Webhook'
           WHEN e."EventType" ILIKE '%Verification%' THEN 'Automation'
           WHEN e."EventType" ILIKE '%Evidence%' THEN 'Evidence'
           ELSE 'Portal'
       END AS channel_name,
       e."CanonicalData" AS canonical_data,
       false AS is_deleted,
       'caseledger-postgresql'::text AS source_system
FROM "AuditEvents" e;

CREATE OR REPLACE VIEW analytics_export_assignments AS
SELECT e."Id"::text AS source_assignment_id,
       e."CaseId"::text AS source_case_id,
       NULLIF(e."CanonicalData"::jsonb -> 'data' ->> 'assigneeId', '') AS assignee_source_user_id,
       e."CreatedAt" AS created_at,
       e."CreatedAt" AS updated_at,
       false AS is_deleted,
       'caseledger-postgresql'::text AS source_system
FROM "AuditEvents" e
WHERE e."CanonicalData"::jsonb -> 'data' ? 'assigneeId';

CREATE OR REPLACE VIEW analytics_export_delivery_attempts AS
SELECT d."Id"::text AS source_delivery_id,
       j."CaseId"::text AS source_case_id,
       d."VerificationJobId"::text AS source_verification_job_id,
       'Webhook'::text AS channel_name,
       d."CreatedAt" AS created_at,
       GREATEST(
           d."CreatedAt",
           d."NextAttemptAt",
           COALESCE(d."DeliveredAt", TIMESTAMPTZ '-infinity'),
           COALESCE(d."DeadLetteredAt", TIMESTAMPTZ '-infinity')
       ) AS updated_at,
       d."DeliveredAt" AS delivered_at,
       d."DeadLetteredAt" AS dead_lettered_at,
       d."AttemptCount" AS attempt_count,
       d."LastErrorCode" AS failure_reason,
       CASE WHEN d."DeliveredAt" IS NOT NULL
            THEN GREATEST(0, EXTRACT(EPOCH FROM (d."DeliveredAt" - d."CreatedAt")) * 1000)::bigint
            ELSE NULL END AS processing_time_ms,
       false AS is_deleted,
       'caseledger-postgresql'::text AS source_system
FROM "WebhookDeliveries" d
JOIN "AuditVerificationJobs" j ON j."Id" = d."VerificationJobId";
