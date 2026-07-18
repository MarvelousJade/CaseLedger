# Operations runbook

## Local start and demo

```powershell
docker compose up -d --wait
docker compose --profile tools build etl
docker compose --profile tools run --rm etl demo --cases 160
```

`demo` is destructive only to the disposable Compose PostgreSQL source. It does
not target a configured external CaseLedger database.

## Normal incremental run

```powershell
docker compose --profile tools run --rm etl run
```

Capture the printed `batch_id`. Verify `status = Succeeded`, seventeen checks
passed, and `ctl.Watermark.last_batch_id` matches. A zero-row safe window is a
successful no-op.

## Replay

```powershell
docker compose --profile tools run --rm etl replay --batch-id <successful-guid>
```

Replay copies the original staged rows to a new batch and reruns the warehouse
procedure. Fact natural-key counts must remain unchanged; affected daily
snapshots are transactionally recomputed rather than duplicated.

## Failure investigation

1. Find the failed record in `mart.vw_ETLRunHistory`.
2. Inspect its bounded `error_message` and structured log `error_classification`.
3. Confirm the watermark still references the prior successful batch.
4. Correct configuration, source contract, or failed quality condition.
5. Run the pipeline normally; do not edit the watermark by hand.

To rehearse this invariant locally:

```powershell
docker compose --profile tools run --rm etl simulate-failure --stage stage
```

The command is expected to return nonzero.

## Credential and permission setup

- Run `sql/source/001_export_views.sql` as the operational database owner.
- Create a production read-only role outside Git and grant only view `SELECT`.
- Give the ETL SQL identity execute rights on the control/warehouse procedures
  plus staging DML; do not grant it application database ownership.
- Add BI identities to `caseledger_report_reader`; do not grant `stg` or `dw`.
- Store deployed connection strings in Key Vault under the secret names used by
  the ADF linked services.

## ADF release

1. Deploy and review Bicep with `what-if`.
2. Apply all SQL scripts in `src/etl/deploy.py::TARGET_SCRIPTS` order.
3. Set Key Vault secrets; grant Data Factory's managed identity Secrets User.
4. Publish linked services, datasets, pipeline, then the stopped trigger.
5. Run a manual debug load and verify its ETLRun/checks.
6. Disable the Python scheduler for that environment before enabling the ADF trigger.

## Shutdown

```powershell
docker compose down
```

Use `docker compose down -v` only when intentionally discarding both local data
volumes. This cannot be recovered from the project after deletion.
