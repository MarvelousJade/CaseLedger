from __future__ import annotations

import argparse
import logging
import sys
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from uuid import UUID, uuid4

from sqlalchemy import Engine, text

from etl.config import Settings
from etl.db import create_source_admin_engine, create_source_engine, create_warehouse_engine
from etl.deploy import bootstrap
from etl.extract import ENTITY_CONTRACTS, capture_high_watermark, extract_all
from etl.load import stage_all
from etl.logging_config import configure_logging
from etl.quality import run_quality_checks
from etl.synthetic import apply_incremental_changes, seed_source
from etl.transform import process_batch

LOGGER = logging.getLogger("caseledger.analytics")
EPOCH = datetime(1900, 1, 1, tzinfo=UTC)


@dataclass(frozen=True)
class PipelineSummary:
    batch_id: str
    previous_watermark: str
    high_watermark: str
    rows_extracted: int
    rows_staged: int
    rows_loaded: int
    quality_checks: int
    status: str


def aware_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)


def naive_utc(value: datetime) -> datetime:
    return aware_utc(value).replace(tzinfo=None)


def classify_error(error: BaseException) -> str:
    name = type(error).__name__.lower()
    message = str(error).lower()
    if "quality" in name or "quality" in message:
        return "data_quality"
    if any(token in message for token in ("timeout", "connection", "network", "login failed")):
        return "transient_connectivity"
    if any(token in name for token in ("integrity", "dataerror", "programming")):
        return "data_or_schema"
    return "unexpected"


def get_previous_watermark(engine: Engine, source_entity: str) -> datetime:
    with engine.connect() as connection:
        value = connection.execute(
            text(
                "SELECT last_successful_watermark FROM ctl.Watermark "
                "WHERE source_entity = :source_entity"
            ),
            {"source_entity": source_entity},
        ).scalar_one_or_none()
    return aware_utc(value) if value else EPOCH


def begin_run(
    engine: Engine,
    settings: Settings,
    batch_id: UUID,
    previous_watermark: datetime,
    high_watermark: datetime,
) -> None:
    with engine.begin() as connection:
        connection.execute(
            text(
                "EXEC ctl.usp_BeginETLRun @batch_id=:batch_id, @pipeline_name=:pipeline_name, "
                "@previous_watermark=:previous_watermark, @high_watermark=:high_watermark"
            ),
            {
                "batch_id": batch_id,
                "pipeline_name": settings.pipeline_name,
                "previous_watermark": naive_utc(previous_watermark),
                "high_watermark": naive_utc(high_watermark),
            },
        )


def complete_run(
    engine: Engine,
    settings: Settings,
    batch_id: UUID,
    high_watermark: datetime,
    rows_extracted: int,
    rows_staged: int,
    rows_loaded: int,
) -> None:
    with engine.begin() as connection:
        connection.execute(
            text(
                "EXEC ctl.usp_CompleteETLRun @batch_id=:batch_id, "
                "@source_entity=:source_entity, @rows_extracted=:rows_extracted, "
                "@rows_staged=:rows_staged, @rows_loaded=:rows_loaded, @rows_rejected=0"
            ),
            {
                "batch_id": batch_id,
                "source_entity": settings.source_entity,
                "rows_extracted": rows_extracted,
                "rows_staged": rows_staged,
                "rows_loaded": rows_loaded,
            },
        )
    LOGGER.info(
        "watermark advanced after committed warehouse load",
        extra={"batch_id": str(batch_id), "high_watermark": high_watermark.isoformat()},
    )


def fail_run(engine: Engine, batch_id: UUID, error: BaseException) -> None:
    with engine.begin() as connection:
        connection.execute(
            text("EXEC ctl.usp_FailETLRun @batch_id=:batch_id, @error_message=:error_message"),
            {"batch_id": batch_id, "error_message": str(error)[:2000]},
        )


def run_pipeline(
    settings: Settings,
    *,
    simulate_failure_stage: str | None = None,
) -> PipelineSummary:
    source = create_source_engine(settings)
    warehouse = create_warehouse_engine(settings)
    batch_id = uuid4()
    registered = False
    previous = EPOCH
    high = EPOCH

    try:
        previous = get_previous_watermark(warehouse, settings.source_entity)
        high = aware_utc(capture_high_watermark(source) or previous)
        if high < previous:
            raise RuntimeError("Source high watermark is behind the committed warehouse watermark")

        begin_run(warehouse, settings, batch_id, previous, high)
        registered = True
        LOGGER.info(
            "incremental batch started",
            extra={
                "batch_id": str(batch_id),
                "pipeline_name": settings.pipeline_name,
                "previous_watermark": previous.isoformat(),
                "high_watermark": high.isoformat(),
                "stage": "extract",
            },
        )

        frames = extract_all(source, previous, high)
        extracted_counts = {name: len(frame.index) for name, frame in frames.items()}
        rows_extracted = sum(extracted_counts.values())
        LOGGER.info(
            "source window extracted",
            extra={
                "batch_id": str(batch_id),
                "record_counts": extracted_counts,
                "stage": "extract",
            },
        )
        if simulate_failure_stage == "extract":
            raise RuntimeError("Simulated failure after extraction")

        staged_counts = stage_all(warehouse, frames, batch_id)
        rows_staged = sum(staged_counts.values())
        if simulate_failure_stage == "stage":
            raise RuntimeError("Simulated failure after staging")

        rows_loaded = process_batch(
            warehouse,
            batch_id,
            naive_utc(previous),
            naive_utc(high),
        )
        if simulate_failure_stage == "transform":
            raise RuntimeError("Simulated failure after transformation")

        quality_results = run_quality_checks(warehouse, batch_id, extracted_counts)
        complete_run(
            warehouse,
            settings,
            batch_id,
            high,
            rows_extracted,
            rows_staged,
            rows_loaded,
        )
        summary = PipelineSummary(
            str(batch_id),
            previous.isoformat(),
            high.isoformat(),
            rows_extracted,
            rows_staged,
            rows_loaded,
            len(quality_results),
            "Succeeded",
        )
        LOGGER.info("incremental batch completed", extra=asdict(summary))
        return summary
    except Exception as error:
        if registered:
            fail_run(warehouse, batch_id, error)
        LOGGER.exception(
            "incremental batch failed",
            extra={
                "batch_id": str(batch_id),
                "previous_watermark": previous.isoformat(),
                "high_watermark": high.isoformat(),
                "error_classification": classify_error(error),
                "status": "Failed",
            },
        )
        raise
    finally:
        source.dispose()
        warehouse.dispose()


def replay_batch(settings: Settings, original_batch_id: UUID) -> PipelineSummary:
    warehouse = create_warehouse_engine(settings)
    replay_id = uuid4()
    try:
        with warehouse.connect() as connection:
            original = (
                connection.execute(
                    text(
                        "SELECT previous_watermark, high_watermark FROM ctl.ETLRun "
                        "WHERE batch_id = :batch_id AND status = 'Succeeded'"
                    ),
                    {"batch_id": original_batch_id},
                )
                .mappings()
                .one_or_none()
            )
        if original is None:
            raise ValueError("Replay requires an existing successful batch identifier")

        previous = aware_utc(original["previous_watermark"])
        high = aware_utc(original["high_watermark"])
        begin_run(warehouse, settings, replay_id, previous, high)

        tables = {contract.name: contract.staging_table for contract in ENTITY_CONTRACTS}
        replay_counts: dict[str, int] = {}
        with warehouse.begin() as connection:
            for entity, table_name in tables.items():
                columns = (
                    connection.execute(
                        text(
                            "SELECT name FROM sys.columns "
                            "WHERE object_id = OBJECT_ID(:object_name) "
                            "AND name <> 'batch_id' ORDER BY column_id"
                        ),
                        {"object_name": f"stg.{table_name}"},
                    )
                    .scalars()
                    .all()
                )
                column_list = ", ".join(f"[{column}]" for column in columns)
                result = connection.execute(
                    text(
                        f"INSERT stg.{table_name}(batch_id, {column_list}) "
                        f"SELECT :replay_id, {column_list} FROM stg.{table_name} "
                        "WHERE batch_id = :original_id"
                    ),
                    {"replay_id": replay_id, "original_id": original_batch_id},
                )
                replay_counts[entity] = result.rowcount

        rows_staged = sum(replay_counts.values())
        rows_loaded = process_batch(
            warehouse,
            replay_id,
            naive_utc(previous),
            naive_utc(high),
        )
        quality_results = run_quality_checks(warehouse, replay_id, replay_counts)
        complete_run(
            warehouse,
            settings,
            replay_id,
            high,
            rows_staged,
            rows_staged,
            rows_loaded,
        )
        return PipelineSummary(
            str(replay_id),
            previous.isoformat(),
            high.isoformat(),
            rows_staged,
            rows_staged,
            rows_loaded,
            len(quality_results),
            "Succeeded",
        )
    except Exception as error:
        try:
            fail_run(warehouse, replay_id, error)
        except Exception:
            LOGGER.debug("Replay failed before batch registration", exc_info=True)
        raise
    finally:
        warehouse.dispose()


def latest_successful_batch(settings: Settings) -> UUID:
    engine = create_warehouse_engine(settings)
    try:
        with engine.connect() as connection:
            value = connection.execute(
                text(
                    "SELECT TOP (1) batch_id FROM ctl.ETLRun "
                    "WHERE status='Succeeded' ORDER BY started_at DESC"
                )
            ).scalar_one()
            return value
    finally:
        engine.dispose()


def run_demo(settings: Settings, case_count: int) -> list[PipelineSummary]:
    bootstrap(settings)
    source = create_source_admin_engine(settings)
    try:
        counts = seed_source(source, case_count=case_count)
        LOGGER.info("synthetic source seeded", extra={"record_counts": counts, "stage": "seed"})
    finally:
        source.dispose()

    full = run_pipeline(settings)
    source = create_source_admin_engine(settings)
    try:
        apply_incremental_changes(source)
    finally:
        source.dispose()
    incremental = run_pipeline(settings)
    replay = replay_batch(settings, UUID(incremental.batch_id))
    return [full, incremental, replay]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="CaseLedger Analytics pipeline")
    subcommands = parser.add_subparsers(dest="command", required=True)
    subcommands.add_parser("bootstrap", help="Create or upgrade the SQL Server warehouse")
    subcommands.add_parser("run", help="Run one safe incremental watermark window")
    seed = subcommands.add_parser(
        "seed", help="Replace the disposable local source with synthetic data"
    )
    seed.add_argument("--cases", type=int, default=160)
    demo = subcommands.add_parser("demo", help="Bootstrap, seed, full-load, increment, and replay")
    demo.add_argument("--cases", type=int, default=160)
    replay = subcommands.add_parser("replay", help="Replay a successful staged batch idempotently")
    replay.add_argument("--batch-id", type=UUID)
    failure = subcommands.add_parser(
        "simulate-failure", help="Prove failure does not advance watermark"
    )
    failure.add_argument("--stage", choices=("extract", "stage", "transform"), default="stage")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    settings = Settings()
    configure_logging(settings.log_level)

    try:
        if args.command == "bootstrap":
            bootstrap(settings)
            print("Warehouse bootstrap completed.")
        elif args.command == "seed":
            engine = create_source_admin_engine(settings)
            try:
                print(seed_source(engine, args.cases))
            finally:
                engine.dispose()
        elif args.command == "run":
            print(asdict(run_pipeline(settings)))
        elif args.command == "demo":
            for summary in run_demo(settings, args.cases):
                print(asdict(summary))
        elif args.command == "replay":
            batch_id = args.batch_id or latest_successful_batch(settings)
            print(asdict(replay_batch(settings, batch_id)))
        elif args.command == "simulate-failure":
            run_pipeline(settings, simulate_failure_stage=args.stage)
        return 0
    except Exception as error:
        LOGGER.error("command failed", extra={"error": str(error), "command": args.command})
        return 1


if __name__ == "__main__":
    sys.exit(main())
