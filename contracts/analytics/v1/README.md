# CaseLedger analytics export contract v1

This directory is the canonical PostgreSQL source contract consumed by
[CaseLedger Analytics](https://github.com/MarvelousJade/CaseLedger-Analytics).
CaseLedger owns the operational schema and these read-only views; the analytics
repository owns extraction, warehouse transformation, marts, and BI assets.

## Installation

Install the views with a PostgreSQL owner account after the CaseLedger schema
has been migrated:

```powershell
psql $env:DATABASE_URL -f contracts/analytics/v1/postgresql-export-views.sql
```

Grant the analytics identity `CONNECT` to the database and `SELECT` only on the
five `analytics_export_*` views. Do not grant it access to the underlying
operational tables.

## Privacy boundary

The contract excludes user names and email addresses, case summaries, evidence
metadata and content, stored audit hashes, and webhook payload bodies. User
UUIDs remain internal join keys, while report-facing user labels use
deterministic SHA-256 aliases.

Version 1 still includes the case title and the canonical event JSON required
by the current history reconstruction. Treat the views as confidential
reporting data, not anonymized public data, and apply the same database,
retention, and access controls as the operational application.

## Versioning

- Additive nullable columns may extend `v1` after the Analytics extractor is
  updated to tolerate them.
- Renames, removals, type changes, grain changes, or altered watermark
  semantics require a new versioned directory.
- Analytics must pin a contract version and retain a local synthetic mirror for
  reproducible CI; production installations must use this CaseLedger-owned
  canonical file.
