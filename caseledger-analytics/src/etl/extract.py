from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime

import pandas as pd
from sqlalchemy import Engine, text


@dataclass(frozen=True)
class EntityContract:
    name: str
    view_name: str
    staging_table: str
    columns: tuple[str, ...]


ENTITY_CONTRACTS = (
    EntityContract(
        "cases",
        "analytics_export_cases",
        "CaseSource",
        (
            "source_case_id",
            "case_reference",
            "case_title",
            "status_name",
            "priority_name",
            "category_name",
            "assignee_source_user_id",
            "assigned_team",
            "service_tier",
            "created_by_source_user_id",
            "created_at",
            "updated_at",
            "due_at",
            "is_deleted",
            "source_system",
        ),
    ),
    EntityContract(
        "case_events",
        "analytics_export_case_events",
        "CaseEventSource",
        (
            "source_event_id",
            "source_case_id",
            "event_sequence",
            "event_type",
            "actor_source_user_id",
            "occurred_at",
            "created_at",
            "updated_at",
            "channel_name",
            "canonical_data",
            "is_deleted",
            "source_system",
        ),
    ),
    EntityContract(
        "users",
        "analytics_export_users",
        "UserSource",
        (
            "source_user_id",
            "synthetic_user_id",
            "role_name",
            "is_active",
            "created_at",
            "updated_at",
            "is_deleted",
            "source_system",
        ),
    ),
    EntityContract(
        "integration_deliveries",
        "analytics_export_delivery_attempts",
        "IntegrationDeliverySource",
        (
            "source_delivery_id",
            "source_case_id",
            "source_verification_job_id",
            "channel_name",
            "created_at",
            "updated_at",
            "delivered_at",
            "dead_lettered_at",
            "attempt_count",
            "failure_reason",
            "processing_time_ms",
            "is_deleted",
            "source_system",
        ),
    ),
)


def build_window_predicate(timestamp_column: str = "updated_at") -> str:
    if not timestamp_column.replace("_", "").isalnum():
        raise ValueError("timestamp_column must be a simple identifier")
    return f"{timestamp_column} > :previous_watermark AND {timestamp_column} <= :high_watermark"


def capture_high_watermark(engine: Engine) -> datetime | None:
    union = " UNION ALL ".join(
        f"SELECT MAX(updated_at) AS updated_at FROM {contract.view_name}"
        for contract in ENTITY_CONTRACTS
    )
    statement = text(f"SELECT MAX(updated_at) FROM ({union}) watermarks")
    with engine.connect() as connection:
        return connection.execute(statement).scalar_one_or_none()


def extract_entity(
    engine: Engine,
    contract: EntityContract,
    previous_watermark: datetime,
    high_watermark: datetime,
) -> pd.DataFrame:
    selected_columns = ", ".join(contract.columns)
    statement = text(
        f"SELECT {selected_columns} FROM {contract.view_name} "
        f"WHERE {build_window_predicate()} ORDER BY updated_at"
    )
    with engine.connect() as connection:
        return pd.read_sql_query(
            statement,
            connection,
            params={
                "previous_watermark": previous_watermark,
                "high_watermark": high_watermark,
            },
        )


def extract_all(
    engine: Engine,
    previous_watermark: datetime,
    high_watermark: datetime,
) -> dict[str, pd.DataFrame]:
    return {
        contract.name: extract_entity(engine, contract, previous_watermark, high_watermark)
        for contract in ENTITY_CONTRACTS
    }
