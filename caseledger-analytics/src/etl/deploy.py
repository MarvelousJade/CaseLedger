from __future__ import annotations

import os
import re
from collections.abc import Iterator
from pathlib import Path

from sqlalchemy import Engine, text
from sqlalchemy.exc import DBAPIError

from etl.config import Settings
from etl.db import create_warehouse_engine

WORKING_ROOT = Path(os.getenv("CASELEDGER_PROJECT_ROOT", Path.cwd())).resolve()
PROJECT_ROOT = (
    WORKING_ROOT if (WORKING_ROOT / "sql").is_dir() else Path(__file__).resolve().parents[2]
)

TARGET_SCRIPTS = (
    "sql/schemas/001_schemas.sql",
    "sql/control/001_tables.sql",
    "sql/control/002_procedures.sql",
    "sql/staging/001_tables.sql",
    "sql/dimensions/001_dimensions.sql",
    "sql/facts/001_facts.sql",
    "sql/procedures/001_process_batch.sql",
    "sql/control/003_quality_procedure.sql",
    "sql/marts/001_views.sql",
)


def split_sql_batches(script: str) -> Iterator[str]:
    for batch in re.split(r"^\s*GO\s*(?:--.*)?$", script, flags=re.IGNORECASE | re.MULTILINE):
        cleaned = batch.strip()
        if cleaned:
            yield cleaned


def is_database_already_exists_error(error: BaseException) -> bool:
    original = getattr(error, "orig", error)
    details = " ".join(str(argument) for argument in getattr(original, "args", (original,)))
    return re.search(r"(?:^|\D)1801(?:\D|$)", details) is not None


def create_database(settings: Settings) -> None:
    engine = create_warehouse_engine(settings, master=True)
    database = settings.sqlserver_database
    try:
        with engine.connect().execution_options(isolation_level="AUTOCOMMIT") as connection:
            exists = connection.execute(
                text("SELECT IIF(DB_ID(:database_name) IS NULL, 0, 1)"),
                {"database_name": database},
            ).scalar_one()
            if not exists:
                try:
                    connection.exec_driver_sql(f"CREATE DATABASE [{database}]")
                except DBAPIError as error:
                    # Azure SQL can hide databases from DB_ID even when they already exist.
                    # SQL Server error 1801 makes this operation safely idempotent.
                    if not is_database_already_exists_error(error):
                        raise
    finally:
        engine.dispose()


def apply_target_schema(engine: Engine, root: Path = PROJECT_ROOT) -> None:
    with engine.connect() as connection:
        for relative_path in TARGET_SCRIPTS:
            script = (root / relative_path).read_text(encoding="utf-8")
            for batch in split_sql_batches(script):
                connection.exec_driver_sql(batch)
                connection.commit()


def bootstrap(settings: Settings) -> None:
    create_database(settings)
    engine = create_warehouse_engine(settings)
    try:
        apply_target_schema(engine)
    finally:
        engine.dispose()
