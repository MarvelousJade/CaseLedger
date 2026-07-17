# ADR 0001: Durable asynchronous audit verification

- Status: Accepted
- Date: 2026-07-16

## Context

Audit verification can run outside the API process, but saving a job and publishing directly to a
broker would create a database/broker dual-write. Broker delivery can also repeat after a crash.

## Decision

The API stores an immutable verification snapshot, `AuditVerificationJob`, and request
`OutboxMessage` in one EF Core transaction. A leased dispatcher publishes the outbox record.

The TypeScript worker verifies the snapshot and stores its `jobId` inbox record and deterministic
result outbox record in one PostgreSQL transaction before completing the broker delivery. A
versioned fingerprint covers the full immutable request, while a separate projection digest is
returned to the API. The API applies a matching terminal result once. Duplicate requests and results
are accepted only when their stable identifiers and content agree; conflicts are rejected.
Infrastructure failures use retry tiers and dead-letter handling.

SignalR is a notification optimization. The client also polls pending jobs, so correctness does not
depend on a persistent real-time connection.

## Consequences

- A committed job is not lost when a broker is briefly unavailable.
- At-least-once publication and duplicate delivery are expected and testable.
- The API and worker each need durable outbox monitoring and dead-letter recovery.
- The worker requires PostgreSQL even though lightweight API development can still use SQLite.
