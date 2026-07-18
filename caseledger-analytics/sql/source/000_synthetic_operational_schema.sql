CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS "Users" (
    "Id" uuid PRIMARY KEY,
    "Name" varchar(100) NOT NULL,
    "Email" varchar(200) NOT NULL,
    "NormalizedEmail" varchar(200) NOT NULL UNIQUE,
    "Role" varchar(40) NOT NULL,
    "PasswordHash" varchar(300) NOT NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "LocalLoginEnabled" boolean NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS "Cases" (
    "Id" uuid PRIMARY KEY,
    "Reference" varchar(32) NOT NULL UNIQUE,
    "Title" varchar(160) NOT NULL,
    "Summary" text NOT NULL,
    "Status" varchar(24) NOT NULL,
    "Severity" varchar(24) NOT NULL,
    "Category" varchar(100) NOT NULL,
    "AssigneeId" uuid NULL REFERENCES "Users"("Id"),
    "CreatedById" uuid NOT NULL REFERENCES "Users"("Id"),
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    "DueAt" timestamptz NULL,
    "TagsJson" text NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS "IX_Cases_UpdatedAt" ON "Cases"("UpdatedAt");

CREATE TABLE IF NOT EXISTS "AuditEvents" (
    "Id" uuid PRIMARY KEY,
    "CaseId" uuid NOT NULL REFERENCES "Cases"("Id") ON DELETE CASCADE,
    "Sequence" integer NOT NULL,
    "EventType" varchar(80) NOT NULL,
    "Description" varchar(500) NOT NULL,
    "ActorId" uuid NOT NULL REFERENCES "Users"("Id"),
    "ActorName" varchar(100) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "PreviousHash" char(64) NOT NULL,
    "Hash" char(64) NOT NULL,
    "CanonicalData" text NOT NULL,
    CONSTRAINT "UQ_AuditEvents_Case_Sequence" UNIQUE ("CaseId", "Sequence")
);

CREATE INDEX IF NOT EXISTS "IX_AuditEvents_CreatedAt" ON "AuditEvents"("CreatedAt");

CREATE TABLE IF NOT EXISTS "AuditVerificationJobs" (
    "Id" uuid PRIMARY KEY,
    "CaseId" uuid NOT NULL REFERENCES "Cases"("Id") ON DELETE CASCADE,
    "Status" varchar(24) NOT NULL,
    "RequestedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz NULL
);

CREATE TABLE IF NOT EXISTS "WebhookDeliveries" (
    "Id" uuid PRIMARY KEY,
    "VerificationJobId" uuid NOT NULL UNIQUE REFERENCES "AuditVerificationJobs"("Id") ON DELETE CASCADE,
    "ResultId" varchar(100) NOT NULL UNIQUE,
    "EventType" varchar(160) NOT NULL,
    "PayloadJson" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "NextAttemptAt" timestamptz NOT NULL,
    "DeliveredAt" timestamptz NULL,
    "DeadLetteredAt" timestamptz NULL,
    "AttemptCount" integer NOT NULL,
    "LastErrorCode" varchar(80) NULL
);

CREATE INDEX IF NOT EXISTS "IX_WebhookDeliveries_AnalyticsUpdated"
    ON "WebhookDeliveries"((GREATEST("CreatedAt", "NextAttemptAt", COALESCE("DeliveredAt", '-infinity'), COALESCE("DeadLetteredAt", '-infinity'))));
