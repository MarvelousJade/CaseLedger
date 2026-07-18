# Metric catalogue

Power BI and Excel consume these definitions from the marts. Null durations are
excluded from averages/percentiles. A percentage with no evaluated denominator
returns blank rather than zero.

| Metric | Business definition / formula | Grain and filters | Owner |
|---|---|---|---|
| Cases Opened | Count of `CaseCreated` events | Event date; date/category/priority/status | `vw_OperationsDaily` |
| Cases Closed | Count of events whose governed new status is `Resolved` | Event date; date/category/priority/status | `vw_OperationsDaily` |
| Backlog EOD | Sum of open case/day snapshots at day end | Case/day; date/category/priority/status | `vw_OperationsDaily` |
| Current Backlog EOD | Backlog EOD on the latest calendar date in the selected context | Latest selected date; category/priority/status | Semantic measure over `vw_OperationsDaily` |
| Average Case Age | Arithmetic mean of current `age_days` | Current case; category/priority/status/team/tier | `vw_CaseAging` |
| Median Case Age | Median of current `age_days` | Current case; category/priority/status/team/tier | Semantic measure over `vw_CaseAging` |
| SLA Compliance Percentage | Compliant cases / evaluated cases | Current case; tier/category | `vw_SLACompliance` |
| SLA Breach Count | Count of current cases breached at snapshot EOD | Current case; tier/category | `vw_SLACompliance` |
| Average First Response Time | Mean minutes from case creation to first comment | Current case; excludes cases without response | `vw_CaseAging` |
| Delivery Success Percentage | Successful deliveries / all delivery attempts | Delivery/day; date/channel/failure | `vw_IntegrationReliability` |
| Retry Percentage | Attempts with `attempt_count > 1` / all attempts | Delivery/day; date/channel/failure | `vw_IntegrationReliability` |
| Dead-Letter Count | Count with terminal dead-letter timestamp | Delivery/day; date/channel/failure | `vw_IntegrationReliability` |
| P95 Processing Time | 95th percentile milliseconds, daily per channel | Displayed as max daily-channel P95 in selected context | `vw_IntegrationReliability` |
| ETL Success Percentage | Successful ETL runs / all terminal runs | Run; pipeline/date/status | Semantic measure over `vw_ETLRunHistory` |
| Average ETL Duration | Mean terminal run duration in milliseconds | Run; pipeline/date/status | `vw_ETLRunHistory` |
| Failed Quality Checks | Count of persisted checks with `passed = false` | Check/batch; pipeline/check/date | `vw_DataQuality` |

### SLA rule

The current portfolio rule marks an open day-end snapshot breached when
`due_at` precedes the next calendar-day boundary. Service tier derives from
CaseLedger severity: Critical/Platinum, High/Gold, Medium/Standard, Low/Basic.
This rule is centralized in the warehouse procedure, not duplicated in clients.
