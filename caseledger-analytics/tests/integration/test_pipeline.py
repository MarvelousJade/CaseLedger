from __future__ import annotations

import os
from uuid import UUID

import pytest
from sqlalchemy import text

from etl.config import Settings
from etl.db import create_source_admin_engine, create_warehouse_engine
from etl.deploy import bootstrap
from etl.pipeline import get_previous_watermark, replay_batch, run_pipeline
from etl.synthetic import apply_incremental_changes, seed_source

pytestmark = pytest.mark.integration


@pytest.fixture(scope="module")
def settings() -> Settings:
    if os.getenv("CASELEDGER_RUN_INTEGRATION") != "1":
        pytest.skip("Set CASELEDGER_RUN_INTEGRATION=1 with Compose services running")
    return Settings()


def test_full_incremental_replay_and_failed_recovery(settings: Settings) -> None:
    bootstrap(settings)
    source = create_source_admin_engine(settings)
    warehouse = create_warehouse_engine(settings)
    try:
        seed_source(source, case_count=40)
        full = run_pipeline(settings)
        assert full.rows_extracted > 40
        assert full.quality_checks >= 10

        apply_incremental_changes(source)
        incremental = run_pipeline(settings)

        with warehouse.connect() as connection:
            changed_versions = connection.execute(
                text(
                    "SELECT COUNT_BIG(*) FROM dw.DimCase "
                    "WHERE source_case_id = ("
                    "SELECT TOP (1) source_case_id FROM dw.DimCase "
                    "GROUP BY source_case_id HAVING COUNT_BIG(*) > 1)"
                )
            ).scalar_one()
            late_events = connection.execute(
                text(
                    "SELECT COUNT_BIG(*) FROM dw.FactCaseEvent "
                    "WHERE occurred_at < source_updated_at AND event_type='CommentAdded'"
                )
            ).scalar_one()
            before_replay = connection.execute(
                text(
                    "SELECT (SELECT COUNT_BIG(*) FROM dw.FactCaseEvent) AS events, "
                    "(SELECT COUNT_BIG(*) FROM dw.FactIntegrationDelivery) AS deliveries"
                )
            ).one()
        assert changed_versions == 2
        assert late_events >= 1

        replay_batch(settings, UUID(incremental.batch_id))
        with warehouse.connect() as connection:
            after_replay = connection.execute(
                text(
                    "SELECT (SELECT COUNT_BIG(*) FROM dw.FactCaseEvent) AS events, "
                    "(SELECT COUNT_BIG(*) FROM dw.FactIntegrationDelivery) AS deliveries"
                )
            ).one()
        assert after_replay == before_replay

        watermark_before_failure = get_previous_watermark(warehouse, settings.source_entity)
        with pytest.raises(RuntimeError, match="Simulated failure"):
            run_pipeline(settings, simulate_failure_stage="stage")
        watermark_after_failure = get_previous_watermark(warehouse, settings.source_entity)
        assert watermark_after_failure == watermark_before_failure
    finally:
        source.dispose()
        warehouse.dispose()
