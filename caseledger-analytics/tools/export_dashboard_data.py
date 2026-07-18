from __future__ import annotations

import argparse
import json
from datetime import UTC, date, datetime
from decimal import Decimal
from pathlib import Path

from sqlalchemy import Engine, text

from etl.config import Settings
from etl.db import create_warehouse_engine


def json_value(value: object) -> object:
    if isinstance(value, (date, datetime)):
        return value.isoformat()
    if isinstance(value, Decimal):
        return float(value)
    return value


def rows(engine: Engine, query: str) -> list[dict[str, object]]:
    with engine.connect() as connection:
        result = connection.execute(text(query)).mappings().all()
    return [{key: json_value(value) for key, value in row.items()} for row in result]


def build_payload(engine: Engine) -> dict[str, object]:
    summary = rows(
        engine,
        """
        WITH flow AS (
            SELECT SUM(cases_opened) cases_opened, SUM(cases_closed) cases_closed
            FROM mart.vw_OperationsDaily
        ), aging AS (
            SELECT COUNT_BIG(*) total_cases,
                   SUM(CONVERT(bigint, IIF(status_name <> 'Resolved', 1, 0))) backlog_eod,
                   AVG(CONVERT(decimal(18,2), age_days)) average_age_days,
                   AVG(CONVERT(decimal(18,2), first_response_minutes))
                       average_first_response_minutes
            FROM mart.vw_CaseAging
        ), median_age AS (
            SELECT TOP (1) PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY age_days)
                OVER () median_age_days
            FROM mart.vw_CaseAging
        ), sla AS (
            SELECT SUM(evaluated_cases) evaluated_cases,
                   SUM(compliant_cases) compliant_cases,
                   SUM(breached_cases) breached_cases
            FROM mart.vw_SLACompliance
        ), integration AS (
            SELECT SUM(delivery_count) delivery_count,
                   SUM(successful_deliveries) successful_deliveries,
                   SUM(retried_deliveries) retried_deliveries,
                   SUM(dead_letter_count) dead_letter_count,
                   MAX(p95_processing_time_ms) p95_processing_time_ms
            FROM mart.vw_IntegrationReliability
        ), pipeline AS (
            SELECT COUNT_BIG(*) run_count,
                   SUM(CONVERT(bigint, IIF(status='Succeeded', 1, 0))) successful_runs,
                   SUM(CONVERT(bigint, ISNULL(failed_checks, 0))) failed_checks
            FROM mart.vw_ETLRunHistory
        ), latest_success AS (
            SELECT TOP (1) duration_ms latest_duration_ms, rows_loaded latest_rows_loaded
            FROM mart.vw_ETLRunHistory
            WHERE status = 'Succeeded'
            ORDER BY started_at DESC
        )
        SELECT flow.cases_opened, flow.cases_closed, aging.backlog_eod,
               aging.average_age_days, median_age.median_age_days,
               aging.average_first_response_minutes,
               CONVERT(
                   decimal(9,4),
                   sla.compliant_cases
                       / NULLIF(CONVERT(decimal(18,4), sla.evaluated_cases), 0)
               ) sla_compliance,
               sla.breached_cases,
               CONVERT(
                   decimal(9,4),
                   integration.successful_deliveries
                       / NULLIF(CONVERT(decimal(18,4), integration.delivery_count), 0)
               ) delivery_success,
               CONVERT(
                   decimal(9,4),
                   integration.retried_deliveries
                       / NULLIF(CONVERT(decimal(18,4), integration.delivery_count), 0)
               ) retry_percentage,
               integration.dead_letter_count, integration.p95_processing_time_ms,
               CONVERT(
                   decimal(9,4),
                   pipeline.successful_runs
                       / NULLIF(CONVERT(decimal(18,4), pipeline.run_count), 0)
               ) etl_success,
               latest_success.latest_duration_ms, latest_success.latest_rows_loaded,
               pipeline.failed_checks
        FROM flow CROSS JOIN aging CROSS JOIN median_age CROSS JOIN sla
        CROSS JOIN integration CROSS JOIN pipeline CROSS JOIN latest_success
        """,
    )[0]

    return {
        "generated_at": datetime.now(UTC).isoformat(timespec="seconds"),
        "summary": summary,
        "operations_trend": rows(
            engine,
            """
            SELECT TOP (14) calendar_date,
                   SUM(cases_opened) cases_opened,
                   SUM(cases_closed) cases_closed,
                   SUM(backlog_eod) backlog_eod
            FROM mart.vw_OperationsDaily
            GROUP BY calendar_date
            ORDER BY calendar_date DESC
            """,
        )[::-1],
        "status_distribution": rows(
            engine,
            """
            SELECT status_name label, COUNT_BIG(*) value
            FROM mart.vw_CaseAging
            GROUP BY status_name ORDER BY value DESC
            """,
        ),
        "category_distribution": rows(
            engine,
            """
            SELECT category_name label, COUNT_BIG(*) value
            FROM mart.vw_CaseAging
            GROUP BY category_name ORDER BY value DESC
            """,
        ),
        "aging_buckets": rows(
            engine,
            """
            SELECT aging_bucket label, COUNT_BIG(*) value
            FROM mart.vw_CaseAging GROUP BY aging_bucket
            """,
        ),
        "sla_by_tier": rows(
            engine,
            """
            SELECT service_tier label, SUM(evaluated_cases) evaluated,
                   SUM(compliant_cases) compliant,
                   CONVERT(
                       decimal(9,4),
                       SUM(compliant_cases)
                           / NULLIF(CONVERT(decimal(18,4), SUM(evaluated_cases)), 0)
                   ) value
            FROM mart.vw_SLACompliance
            GROUP BY service_tier ORDER BY value
            """,
        ),
        "integration_trend": rows(
            engine,
            """
            SELECT TOP (14) calendar_date,
                   SUM(delivery_count) deliveries,
                   SUM(successful_deliveries) successful,
                   SUM(retried_deliveries) retried,
                   SUM(dead_letter_count) dead_letters
            FROM mart.vw_IntegrationReliability
            GROUP BY calendar_date ORDER BY calendar_date DESC
            """,
        )[::-1],
        "failure_reasons": rows(
            engine,
            """
            SELECT TOP (6) failure_reason label,
                   SUM(delivery_count - successful_deliveries) value
            FROM mart.vw_IntegrationReliability
            GROUP BY failure_reason
            HAVING SUM(delivery_count - successful_deliveries) > 0
            ORDER BY value DESC
            """,
        ),
        "etl_runs": rows(
            engine,
            """
            SELECT TOP (6) CONVERT(varchar(19), started_at, 120) started_at,
                   status, rows_extracted, rows_loaded, duration_ms,
                   ISNULL(passed_checks,0) passed_checks, ISNULL(failed_checks,0) failed_checks
            FROM mart.vw_ETLRunHistory ORDER BY started_at DESC
            """,
        ),
        "quality": rows(
            engine,
            """
            SELECT TOP (8) check_name, passed, expected_value, actual_value
            FROM mart.vw_DataQuality ORDER BY executed_at DESC, check_name
            """,
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Export verified mart data for dashboard artifacts"
    )
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    engine = create_warehouse_engine(Settings())
    try:
        payload = build_payload(engine)
    finally:
        engine.dispose()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
