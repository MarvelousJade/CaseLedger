from __future__ import annotations

from datetime import UTC, datetime
from uuid import UUID

import pandas as pd
from sqlalchemy import Engine, text

from etl.extract import ENTITY_CONTRACTS, EntityContract


def normalize_for_sql_server(frame: pd.DataFrame) -> pd.DataFrame:
    normalized = frame.copy()
    for column in normalized.columns:
        if pd.api.types.is_datetime64_any_dtype(normalized[column]):
            series = pd.to_datetime(normalized[column], utc=True)
            normalized[column] = series.dt.tz_localize(None)
    return normalized.astype(object).where(pd.notna(normalized), None)


def stage_entity(
    engine: Engine,
    contract: EntityContract,
    frame: pd.DataFrame,
    batch_id: UUID,
    extracted_at: datetime | None = None,
) -> int:
    staged = normalize_for_sql_server(frame)
    staged.insert(0, "extracted_at", (extracted_at or datetime.now(UTC)).replace(tzinfo=None))
    staged.insert(0, "batch_id", batch_id)

    with engine.begin() as connection:
        connection.execute(
            text(f"DELETE FROM stg.{contract.staging_table} WHERE batch_id = :batch_id"),
            {"batch_id": batch_id},
        )
        if not staged.empty:
            staged.to_sql(
                contract.staging_table,
                connection,
                schema="stg",
                if_exists="append",
                index=False,
                chunksize=500,
            )
    return len(staged.index)


def stage_all(
    engine: Engine,
    frames: dict[str, pd.DataFrame],
    batch_id: UUID,
) -> dict[str, int]:
    return {
        contract.name: stage_entity(engine, contract, frames[contract.name], batch_id)
        for contract in ENTITY_CONTRACTS
    }
