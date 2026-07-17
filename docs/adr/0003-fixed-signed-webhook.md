# ADR 0003: Fixed signed completion webhook

- Status: Accepted
- Date: 2026-07-16

## Context

External systems may need a verification-completed signal. Accepting a destination from case data
would introduce an SSRF boundary, and sending case content would disclose more information than the
integration requires.

## Decision

An operator configures one fixed destination and signing secret. Webhooks are disabled by default.
Applying the first terminal verification result stages one delivery row in the same API database
transaction; duplicate results do not create another delivery.

The stored raw UTF-8 body contains only delivery/result/job/case identifiers, terminal status,
verification counts and hashes, a sanitized error code, and completion time. Each request includes a
delivery/idempotency ID, event type, timestamp, and a versioned HMAC-SHA256 signature over the
timestamp and exact body. Redirects are disabled.

Network errors, timeouts, 408, 429, and 5xx responses retry with a bounded delay. Other 4xx responses
and exhausted attempts enter a terminal database dead-letter state.

## Consequences

- Receivers can authenticate raw bytes and deduplicate deliveries.
- Case titles, descriptions, actors, evidence metadata, and audit snapshots are not disclosed.
- Destination changes are deployment operations, not end-user actions.
- Delivery remains at least once, so receivers must implement idempotency.
