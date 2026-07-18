from __future__ import annotations

from datetime import UTC, datetime

from etl.pipeline import aware_utc, classify_error, naive_utc
from etl.quality import DataQualityError


def test_utc_conversions_preserve_instant() -> None:
    naive = datetime(2026, 7, 18, 12, 0)

    assert aware_utc(naive) == datetime(2026, 7, 18, 12, 0, tzinfo=UTC)
    assert naive_utc(aware_utc(naive)) == naive


def test_error_classification_is_stable() -> None:
    assert classify_error(DataQualityError("check failed")) == "data_quality"
    assert classify_error(ConnectionError("network timeout")) == "transient_connectivity"
    assert classify_error(RuntimeError("unknown")) == "unexpected"
