ALTER TABLE audit_worker.inbox
    ADD COLUMN IF NOT EXISTS request_fingerprint char(64) NULL;

ALTER TABLE audit_worker.inbox
    ADD COLUMN IF NOT EXISTS fingerprint_version smallint NULL;

-- Existing inbox rows predate the immutable request fingerprint and do not
-- retain enough request data to reconstruct it. Mark them explicitly as
-- legacy (version 0) and use the old snapshot digest only as a non-null
-- placeholder. Repository comparison accepts version 1 only, so a reuse of a
-- legacy job id fails closed instead of allowing the first post-upgrade
-- delivery to redefine its identity.
UPDATE audit_worker.inbox
   SET request_fingerprint = snapshot_sha256,
       fingerprint_version = 0
 WHERE request_fingerprint IS NULL;

UPDATE audit_worker.inbox
   SET fingerprint_version = 0
 WHERE fingerprint_version IS NULL;

ALTER TABLE audit_worker.inbox
    ALTER COLUMN request_fingerprint SET NOT NULL,
    ALTER COLUMN fingerprint_version SET DEFAULT 1,
    ALTER COLUMN fingerprint_version SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conrelid = 'audit_worker.inbox'::regclass
           AND conname = 'inbox_request_fingerprint_format'
    ) THEN
        ALTER TABLE audit_worker.inbox
            ADD CONSTRAINT inbox_request_fingerprint_format
            CHECK (request_fingerprint ~ '^[0-9a-f]{64}$');
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conrelid = 'audit_worker.inbox'::regclass
           AND conname = 'inbox_fingerprint_version_supported'
    ) THEN
        ALTER TABLE audit_worker.inbox
            ADD CONSTRAINT inbox_fingerprint_version_supported
            CHECK (fingerprint_version IN (0, 1));
    END IF;
END
$$;
