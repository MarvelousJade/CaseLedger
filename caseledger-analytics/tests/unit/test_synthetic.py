from __future__ import annotations

from etl.synthetic import canonical, event_hash, stable_uuid


def test_synthetic_identifiers_are_deterministic() -> None:
    assert stable_uuid("case", 12) == stable_uuid("case", 12)
    assert stable_uuid("case", 12) != stable_uuid("case", 13)


def test_canonical_json_and_hash_are_stable() -> None:
    first = canonical("CaseUpdated", {"status": "Resolved", "severity": "High"})
    second = canonical("CaseUpdated", {"severity": "High", "status": "Resolved"})

    assert first == second
    assert event_hash("0" * 64, first) == event_hash("0" * 64, second)
