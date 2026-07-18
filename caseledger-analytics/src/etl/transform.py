from __future__ import annotations

from datetime import datetime
from uuid import UUID

from sqlalchemy import Engine, text


def process_batch(
    engine: Engine,
    batch_id: UUID,
    previous_watermark: datetime,
    high_watermark: datetime,
) -> int:
    with engine.begin() as connection:
        result = connection.execute(
            text(
                "EXEC dw.usp_ProcessBatch @batch_id=:batch_id, "
                "@previous_watermark=:previous_watermark, @high_watermark=:high_watermark"
            ),
            {
                "batch_id": batch_id,
                "previous_watermark": previous_watermark,
                "high_watermark": high_watermark,
            },
        )
        return int(result.scalar_one())
