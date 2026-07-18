from __future__ import annotations

from dataclasses import dataclass
from uuid import UUID

from sqlalchemy import Engine, text


class DataQualityError(RuntimeError):
    pass


@dataclass(frozen=True)
class QualityResult:
    check_name: str
    expected_value: str
    actual_value: str
    passed: bool
    details: str


def run_quality_checks(
    engine: Engine,
    batch_id: UUID,
    expected_counts: dict[str, int],
) -> list[QualityResult]:
    """Run the same persisted T-SQL checks used by Azure Data Factory."""

    with engine.begin() as connection:
        rows = (
            connection.execute(
                text(
                    "EXEC ctl.usp_ValidateBatch @batch_id=:batch_id, "
                    "@expected_cases=:cases, @expected_case_events=:case_events, "
                    "@expected_users=:users, "
                    "@expected_integration_deliveries=:integration_deliveries"
                ),
                {"batch_id": batch_id, **expected_counts},
            )
            .mappings()
            .all()
        )

    results = [
        QualityResult(
            str(row["check_name"]),
            str(row["expected_value"]),
            str(row["actual_value"]),
            bool(row["passed"]),
            str(row["details"]),
        )
        for row in rows
    ]
    failures = [result for result in results if not result.passed]
    if failures:
        names = ", ".join(result.check_name for result in failures)
        raise DataQualityError(f"Data-quality checks failed: {names}")
    return results
