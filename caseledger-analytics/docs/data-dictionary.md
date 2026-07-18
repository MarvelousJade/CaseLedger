# Data dictionary

## Control schema

| Object | Purpose | Important columns |
|---|---|---|
| `ctl.ETLRun` | One orchestration attempt | Batch, fixed window, counts, duration, status, bounded error |
| `ctl.Watermark` | Last committed extraction boundary per source | Source entity, watermark, successful batch |
| `ctl.DataQualityResult` | One persisted check result per batch | Expected, actual, pass flag, details, execution time |

## Staging schema

Every staging table includes `batch_id`, `extracted_at`, the source natural key,
the privacy-minimal export attributes, `updated_at`, deletion state, and source
system. Staging is append-by-batch; replay creates a new batch copy while
warehouse natural keys remain idempotent.

| Table | Source view |
|---|---|
| `stg.CaseSource` | `analytics_export_cases` |
| `stg.CaseEventSource` | `analytics_export_case_events` |
| `stg.UserSource` | `analytics_export_users` |
| `stg.IntegrationDeliverySource` | `analytics_export_delivery_attempts` |

## Dimensions

| Dimension | Business key | History |
|---|---|---|
| `dw.DimDate` | Calendar date | Static |
| `dw.DimCase` | Source case UUID + effective start | Type 2 |
| `dw.DimUser` | Source user UUID | Type 1, synthetic display identifier |
| `dw.DimStatus` | Status name | Conformed lookup |
| `dw.DimCategory` | Category name | Conformed lookup |
| `dw.DimChannel` | Channel name | Conformed lookup |

`DimCase` tracks case reference/title, analytical priority, category, assigned
user/team, service tier, status, due date, effective interval, source update,
and load batch. `priority_name` maps the operational `Severity` field; that
mapping is explicit rather than pretending the source has a second priority
concept.

## Facts

| Fact | Grain | Degenerate/natural key | Measures/flags |
|---|---|---|---|
| `dw.FactCaseEvent` | One lifecycle event | Source event UUID | Sequence, occurrence, new status |
| `dw.FactCaseDailySnapshot` | One case/day EOD | Date + source case UUID | Age, open flag, SLA breach flag |
| `dw.FactIntegrationDelivery` | One delivery attempt | Source delivery UUID | Attempts, duration, success/dead-letter flags |

## Marts

| View | Consumer purpose |
|---|---|
| `mart.vw_Date` | Shared calendar filters |
| `mart.vw_OperationsDaily` | Open/close flow, backlog, age, category/status/priority trends |
| `mart.vw_CaseAging` | Current case-level aging and exception drill-through |
| `mart.vw_SLACompliance` | Governed compliance totals by tier/category |
| `mart.vw_IntegrationReliability` | Delivery, retry, dead-letter, latency, and failure reason |
| `mart.vw_DataQuality` | Persisted batch-check detail |
| `mart.vw_ETLRunHistory` | Pipeline result, duration, counts, and quality summary |
