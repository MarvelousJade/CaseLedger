CREATE SCHEMA IF NOT EXISTS audit_worker;

CREATE TABLE IF NOT EXISTS audit_worker.inbox (
    job_id uuid PRIMARY KEY,
    snapshot_sha256 char(64) NOT NULL,
    result_id text NOT NULL UNIQUE,
    result_payload jsonb NOT NULL,
    completed_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT inbox_snapshot_sha256_format CHECK (snapshot_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE TABLE IF NOT EXISTS audit_worker.result_outbox (
    result_id text PRIMARY KEY,
    job_id uuid NOT NULL REFERENCES audit_worker.inbox(job_id) ON DELETE CASCADE,
    routing_key text NOT NULL,
    result_payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL,
    publish_attempts integer NOT NULL DEFAULT 0,
    lock_id uuid NULL,
    locked_until timestamptz NULL,
    CONSTRAINT result_outbox_publish_attempts_nonnegative CHECK (publish_attempts >= 0)
);

CREATE INDEX IF NOT EXISTS ix_result_outbox_pending
    ON audit_worker.result_outbox (next_attempt_at, created_at)
    WHERE published_at IS NULL;
