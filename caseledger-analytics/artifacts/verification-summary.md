# Verification summary

Verified on 2026-07-18 with Python 3.12, Docker Compose, PostgreSQL 17, and
SQL Server 2022. The end-to-end scenario used a new isolated Compose project
and the exact SQL and Python code in this repository.

## Clean-room ETL results

| Scenario | Batch | Extracted | Staged | Warehouse writes | Checks | Result |
|---|---|---:|---:|---:|---:|---|
| Full | `eda46201-ac02-45e2-ac0c-bff7d1159f6e` | 226 | 226 | 1,011 | 17/17 | Succeeded |
| Incremental | `6b84056f-87ec-4cd9-81ed-2370ade5590d` | 8 | 8 | 113 | 17/17 | Succeeded |
| Replay | `61f0044c-be43-447c-803b-c4b5a0f1069a` | 8 | 8 | 110 | 17/17 | Succeeded |
| Forced stage failure | `39a586c2-77cf-4fe0-984a-4df2e399e5ce` | 0 | 0 | 0 | n/a | Failed as expected |
| Recovery/no-op | `75684115-d4a5-4327-a859-562c49c74b8d` | 0 | 0 | 0 | 17/17 | Succeeded |

The forced failure and recovery both reported the same prior and high
watermark, `2026-02-03T14:05:05+00:00`. This demonstrates that the failed run
did not advance pipeline state.

## Warehouse assertions

| Assertion | Verified value |
|---|---:|
| Current case dimension rows | 45 |
| Cases in latest daily snapshot | 45 |
| Natural cases with SCD Type 2 history | 1 |
| Late-arriving lifecycle events | 1 |
| Duplicate case-event fact grains | 0 |
| Duplicate delivery fact grains | 0 |
| Persisted failed quality checks | 0 |

## Static and artifact checks

- Ruff: passed.
- Unit tests: 11 passed; Docker-only integration test deselected on the host.
- ADF and enhanced PBIR JSON: parsed successfully.
- Azure Bicep template: compiled successfully with Azure CLI.
- Docker Compose configuration: validated.
- Dashboard PDF: four 960x540 pages, expected page titles extracted, metadata
  verified, and every rendered page visually inspected.

## Environment-limited validation

- Power BI Desktop was not installed, so the TMDL/DAX model and enhanced PBIR
  page definitions were checked as source but not opened in Desktop.
- The required spreadsheet artifact-tool dependency loader was unavailable.
  Excel Power Query sources and the workbook layout contract are included, but
  no unsupported `.xlsx` fallback was generated.
- No Azure subscription was provisioned, so the compiled Bicep template and
  ADF assets were not deployed.
