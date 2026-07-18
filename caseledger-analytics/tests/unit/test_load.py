from __future__ import annotations

import pandas as pd

from etl.load import normalize_for_sql_server


def test_null_and_timestamp_normalization() -> None:
    frame = pd.DataFrame(
        {
            "updated_at": [pd.Timestamp("2026-07-18T10:30:00Z")],
            "due_at": [pd.NaT],
            "failure_reason": [None],
        }
    )

    normalized = normalize_for_sql_server(frame)

    assert "updated_at" in normalized.columns
    assert normalized.loc[0, "updated_at"].tzinfo is None
    assert normalized.loc[0, "due_at"] is None
