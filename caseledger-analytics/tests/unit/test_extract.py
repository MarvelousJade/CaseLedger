from __future__ import annotations

import pytest

from etl.extract import build_window_predicate


def test_incremental_window_is_open_then_closed() -> None:
    assert build_window_predicate() == (
        "updated_at > :previous_watermark AND updated_at <= :high_watermark"
    )


def test_window_column_must_be_a_simple_identifier() -> None:
    with pytest.raises(ValueError):
        build_window_predicate("updated_at; DROP TABLE cases")
