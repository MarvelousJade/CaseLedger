# Excel operations pack

The workbook contract consumes the same `mart` views as Power BI. Import the
checked-in `.pq` queries into Excel Power Query, bind `SqlServerHost` and
`SqlServerDatabase` to workbook parameters, and load the results to the named
sheets below.

| Sheet | Query / purpose |
|---|---|
| Executive Summary | KPI cards sourced from Operations, SLA, and Integration marts |
| Weekly Operations | `OperationsDaily` PivotTable/PivotChart |
| SLA Exceptions | Filtered `CaseAging` detail |
| Category and Team Analysis | `CaseAging` PivotTable with slicers |
| Integration Reliability | `IntegrationReliability` trend and failure table |
| Refresh Instructions | Parameters, refresh timestamp, support notes |

Recommended workbook conventions:

- Use PivotTables over loaded query tables; do not rewrite KPI formulas.
- Add slicers for Date, Category, Status, Priority, Team, and Service Tier.
- Use `XLOOKUP` only for display metadata such as owner/definition from the
  metric catalogue, never for KPI calculation.
- Add conditional formatting to SLA breach, dead-letter, and failed-check flags.
- Set a refresh timestamp with a one-row `RefreshMetadata` query using
  `DateTimeZone.FixedUtcNow()`.

The current environment did not expose the required artifact-tool dependency
loader, so an `.xlsx` binary is not synthesized with an unsupported fallback
library. The Power Query source and complete workbook layout remain versioned
here for deterministic assembly in Excel.
