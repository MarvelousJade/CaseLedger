from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from reportlab.lib.colors import HexColor
from reportlab.pdfbase.pdfmetrics import stringWidth
from reportlab.pdfgen.canvas import Canvas

PAGE = (960, 540)
NAVY = HexColor("#0B1F33")
BLUE = HexColor("#2F6BFF")
TEAL = HexColor("#007C83")
AMBER = HexColor("#D97706")
RED = HexColor("#C73E1D")
GREEN = HexColor("#2E7D32")
INK = HexColor("#172B3A")
MUTED = HexColor("#5D6B78")
PALE = HexColor("#F5F7FA")
BORDER = HexColor("#DCE3EA")
WHITE = HexColor("#FFFFFF")


def fmt(value: Any, kind: str = "count") -> str:
    if value is None:
        return "-"
    number = float(value)
    if kind == "percent":
        return f"{number:.1%}"
    if kind == "days":
        return f"{number:.1f} d"
    if kind == "minutes":
        return f"{number:.1f} min"
    if kind == "milliseconds":
        return f"{number:,.0f} ms"
    return f"{number:,.0f}"


def panel(canvas: Canvas, x: float, y: float, w: float, h: float, title: str) -> None:
    canvas.setFillColor(WHITE)
    canvas.setStrokeColor(BORDER)
    canvas.roundRect(x, y, w, h, 8, fill=1, stroke=1)
    canvas.setFillColor(INK)
    canvas.setFont("Helvetica-Bold", 11)
    canvas.drawString(x + 14, y + h - 22, title)


def header(canvas: Canvas, title: str, subtitle: str, page_number: int) -> None:
    canvas.setFillColor(PALE)
    canvas.rect(0, 0, *PAGE, fill=1, stroke=0)
    canvas.setFillColor(NAVY)
    canvas.rect(0, PAGE[1] - 62, PAGE[0], 62, fill=1, stroke=0)
    canvas.setFillColor(WHITE)
    canvas.setFont("Helvetica-Bold", 18)
    canvas.drawString(28, PAGE[1] - 31, title)
    canvas.setFont("Helvetica", 8.5)
    canvas.setFillColor(HexColor("#C7D5E0"))
    canvas.drawString(28, PAGE[1] - 47, subtitle)
    canvas.setFillColor(WHITE)
    canvas.setFont("Helvetica-Bold", 9)
    canvas.drawRightString(PAGE[0] - 28, PAGE[1] - 35, f"CASELEDGER ANALYTICS  |  {page_number}/4")


def footer(canvas: Canvas, generated_at: str) -> None:
    canvas.setStrokeColor(BORDER)
    canvas.line(28, 23, PAGE[0] - 28, 23)
    canvas.setFont("Helvetica", 7)
    canvas.setFillColor(MUTED)
    canvas.drawString(
        28, 10, "Synthetic portfolio data - metrics governed by SQL Server mart views"
    )
    canvas.drawRightString(PAGE[0] - 28, 10, f"Generated {generated_at}")


def card(
    canvas: Canvas,
    x: float,
    y: float,
    w: float,
    label: str,
    value: str,
    accent=BLUE,
) -> None:
    canvas.setFillColor(WHITE)
    canvas.setStrokeColor(BORDER)
    canvas.roundRect(x, y, w, 70, 8, fill=1, stroke=1)
    canvas.setFillColor(accent)
    canvas.roundRect(x, y, 5, 70, 3, fill=1, stroke=0)
    canvas.setFillColor(MUTED)
    canvas.setFont("Helvetica-Bold", 8)
    canvas.drawString(x + 16, y + 49, label.upper())
    canvas.setFillColor(INK)
    canvas.setFont("Helvetica-Bold", 22)
    canvas.drawString(x + 16, y + 19, value)


def bars(
    canvas: Canvas, x: float, y: float, w: float, h: float, data: list[dict], color=TEAL
) -> None:
    if not data:
        return
    max_value = max(float(item.get("value") or 0) for item in data) or 1
    usable = h - 15
    row_h = usable / max(len(data), 1)
    label_w = min(105, w * 0.36)
    for index, item in enumerate(data):
        baseline = y + usable - (index + 1) * row_h + 6
        label = str(item.get("label") or "Unknown")[:18]
        value = float(item.get("value") or 0)
        canvas.setFillColor(MUTED)
        canvas.setFont("Helvetica", 7.5)
        canvas.drawString(x, baseline + 3, label)
        bar_w = (w - label_w - 32) * value / max_value
        canvas.setFillColor(HexColor("#E7EDF3"))
        canvas.roundRect(x + label_w, baseline, w - label_w - 32, 10, 4, fill=1, stroke=0)
        canvas.setFillColor(color)
        canvas.roundRect(x + label_w, baseline, bar_w, 10, 4, fill=1, stroke=0)
        canvas.setFillColor(INK)
        canvas.setFont("Helvetica-Bold", 7.5)
        canvas.drawRightString(x + w, baseline + 3, fmt(value))


def line_chart(
    canvas: Canvas,
    x: float,
    y: float,
    w: float,
    h: float,
    data: list[dict],
    field: str,
    color=BLUE,
) -> None:
    if not data:
        return
    values = [float(item.get(field) or 0) for item in data]
    maximum = max(values) or 1
    minimum = min(values)
    spread = max(maximum - minimum, 1)
    left, bottom, width, height = x + 26, y + 22, w - 38, h - 42
    canvas.setStrokeColor(HexColor("#E6EBF0"))
    for step in range(4):
        grid_y = bottom + height * step / 3
        canvas.line(left, grid_y, left + width, grid_y)
    points = []
    for index, value in enumerate(values):
        px = left + width * index / max(len(values) - 1, 1)
        py = bottom + height * (value - minimum) / spread
        points.append((px, py))
    canvas.setStrokeColor(color)
    canvas.setLineWidth(2)
    path = canvas.beginPath()
    path.moveTo(*points[0])
    for point in points[1:]:
        path.lineTo(*point)
    canvas.drawPath(path)
    canvas.setFillColor(color)
    for point in points:
        canvas.circle(*point, 2.3, fill=1, stroke=0)
    canvas.setFillColor(MUTED)
    canvas.setFont("Helvetica", 6.5)
    first = str(data[0].get("calendar_date") or data[0].get("started_at") or "")[:10]
    last = str(data[-1].get("calendar_date") or data[-1].get("started_at") or "")[:10]
    canvas.drawString(left, y + 7, first)
    canvas.drawRightString(left + width, y + 7, last)
    canvas.drawString(x + 2, bottom + height - 2, fmt(maximum))


def table(
    canvas: Canvas,
    x: float,
    y: float,
    widths: list[float],
    headers: list[str],
    data: list[list[str]],
    row_h: float = 24,
) -> None:
    total = sum(widths)
    canvas.setFillColor(HexColor("#EAF0F5"))
    canvas.rect(x, y + len(data) * row_h, total, row_h, fill=1, stroke=0)
    cursor = x
    canvas.setFillColor(INK)
    canvas.setFont("Helvetica-Bold", 7)
    for width, header_text in zip(widths, headers, strict=True):
        canvas.drawString(cursor + 6, y + len(data) * row_h + 8, header_text.upper())
        cursor += width
    for row_index, row in enumerate(data):
        row_y = y + (len(data) - 1 - row_index) * row_h
        if row_index % 2:
            canvas.setFillColor(HexColor("#F8FAFC"))
            canvas.rect(x, row_y, total, row_h, fill=1, stroke=0)
        cursor = x
        canvas.setFillColor(INK)
        canvas.setFont("Helvetica", 7.2)
        for width, value in zip(widths, row, strict=True):
            clipped = value
            while stringWidth(clipped, "Helvetica", 7.2) > width - 12 and len(clipped) > 3:
                clipped = clipped[:-2]
            if clipped != value:
                clipped += "..."
            canvas.drawString(cursor + 6, row_y + 8, clipped)
            cursor += width
        canvas.setStrokeColor(BORDER)
        canvas.line(x, row_y, x + total, row_y)


def generate(data: dict[str, Any], output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas = Canvas(str(output), pagesize=PAGE)
    canvas.setTitle("CaseLedger Analytics Dashboard")
    canvas.setAuthor("CaseLedger Analytics")
    canvas.setSubject("Operations, SLA, integration reliability, and ETL quality dashboard")
    summary = data["summary"]
    generated_at = data["generated_at"]
    card_width = 211
    card_x = [28, 259, 490, 721]

    header(
        canvas, "Operations Overview", "Daily flow, end-of-day backlog, and current workload mix", 1
    )
    for x, label, value, accent in [
        (card_x[0], "Cases Opened", fmt(summary["cases_opened"]), BLUE),
        (card_x[1], "Cases Closed", fmt(summary["cases_closed"]), GREEN),
        (card_x[2], "Backlog EOD", fmt(summary["backlog_eod"]), AMBER),
        (card_x[3], "Average Case Age", fmt(summary["average_age_days"], "days"), TEAL),
    ]:
        card(canvas, x, 397, card_width, label, value, accent)
    panel(canvas, 28, 48, 564, 328, "Backlog trend - last 14 active dates")
    line_chart(canvas, 43, 74, 534, 268, data["operations_trend"], "backlog_eod", BLUE)
    panel(canvas, 612, 213, 320, 163, "Current cases by category")
    bars(canvas, 628, 233, 286, 110, data["category_distribution"], TEAL)
    panel(canvas, 612, 48, 320, 145, "Current cases by status")
    bars(canvas, 628, 68, 286, 93, data["status_distribution"], BLUE)
    footer(canvas, generated_at)
    canvas.showPage()

    header(canvas, "Aging and SLA", "Current aging profile, SLA exceptions, and first response", 2)
    for x, label, value, accent in [
        (card_x[0], "SLA Compliance", fmt(summary["sla_compliance"], "percent"), GREEN),
        (card_x[1], "SLA Breaches", fmt(summary["breached_cases"]), RED),
        (card_x[2], "Median Case Age", fmt(summary["median_age_days"], "days"), TEAL),
        (
            card_x[3],
            "Average First Response",
            fmt(summary["average_first_response_minutes"], "minutes"),
            BLUE,
        ),
    ]:
        card(canvas, x, 397, card_width, label, value, accent)
    panel(canvas, 28, 48, 430, 328, "Current aging buckets")
    ordered = {"0-1 days": 0, "2-7 days": 1, "8-30 days": 2, "31+ days": 3}
    aging = sorted(data["aging_buckets"], key=lambda item: ordered.get(item["label"], 99))
    bars(canvas, 46, 88, 390, 285, aging, AMBER)
    panel(canvas, 478, 48, 454, 328, "SLA performance by service tier")
    sla_rows = [
        [
            str(row["label"]),
            fmt(row["evaluated"]),
            fmt(row["compliant"]),
            fmt(row["value"], "percent"),
        ]
        for row in data["sla_by_tier"]
    ]
    table(
        canvas,
        494,
        222,
        [130, 90, 90, 110],
        ["Service Tier", "Evaluated", "Compliant", "Compliance"],
        sla_rows,
    )
    canvas.setFillColor(MUTED)
    canvas.setFont("Helvetica", 8)
    canvas.drawString(494, 177, "Rule: open at EOD and due before the next day boundary = breached")
    canvas.drawString(
        494, 160, "Exception drill-through is keyed by case reference in mart.vw_CaseAging"
    )
    footer(canvas, generated_at)
    canvas.showPage()

    header(
        canvas,
        "Integration Reliability",
        "Webhook outcomes, retries, dead letters, and processing latency",
        3,
    )
    for x, label, value, accent in [
        (card_x[0], "Delivery Success", fmt(summary["delivery_success"], "percent"), GREEN),
        (card_x[1], "Retry Percentage", fmt(summary["retry_percentage"], "percent"), AMBER),
        (card_x[2], "Dead-Letter Count", fmt(summary["dead_letter_count"]), RED),
        (
            card_x[3],
            "P95 Processing Time",
            fmt(summary["p95_processing_time_ms"], "milliseconds"),
            BLUE,
        ),
    ]:
        card(canvas, x, 397, card_width, label, value, accent)
    panel(canvas, 28, 48, 580, 328, "Delivery volume - last 14 active dates")
    line_chart(canvas, 43, 74, 550, 268, data["integration_trend"], "deliveries", TEAL)
    panel(canvas, 628, 48, 304, 328, "Failure reasons")
    failure_data = data["failure_reasons"] or [{"label": "No failures", "value": 0}]
    bars(canvas, 646, 90, 268, 285, failure_data, RED)
    footer(canvas, generated_at)
    canvas.showPage()

    header(
        canvas,
        "Pipeline and Data Quality",
        "Durable run history, reconciliation, and watermark health",
        4,
    )
    for x, label, value, accent in [
        (card_x[0], "ETL Success", fmt(summary["etl_success"], "percent"), GREEN),
        (card_x[1], "Latest Duration", fmt(summary["latest_duration_ms"], "milliseconds"), BLUE),
        (card_x[2], "Latest Rows Loaded", fmt(summary["latest_rows_loaded"]), TEAL),
        (card_x[3], "Failed Checks", fmt(summary["failed_checks"]), RED),
    ]:
        card(canvas, x, 397, card_width, label, value, accent)
    panel(canvas, 28, 211, 904, 165, "Recent ETL runs")
    run_rows = [
        [
            str(row["started_at"]),
            str(row["status"]),
            fmt(row["rows_extracted"]),
            fmt(row["rows_loaded"]),
            fmt(row["duration_ms"], "milliseconds"),
            f"{fmt(row['passed_checks'])}/{fmt(row['failed_checks'])}",
        ]
        for row in data["etl_runs"]
    ]
    table(
        canvas,
        44,
        220,
        [150, 95, 105, 105, 130, 130],
        ["Started UTC", "Status", "Extracted", "Loaded", "Duration", "Checks P/F"],
        run_rows,
        18,
    )
    panel(canvas, 28, 48, 904, 143, "Latest persisted quality checks")
    quality_rows = [
        [
            str(row["check_name"]),
            "PASS" if row["passed"] else "FAIL",
            str(row["expected_value"]),
            str(row["actual_value"]),
        ]
        for row in data["quality"][:4]
    ]
    table(
        canvas,
        44,
        68,
        [430, 90, 140, 140],
        ["Check", "Result", "Expected", "Actual"],
        quality_rows,
        18,
    )
    footer(canvas, generated_at)
    canvas.save()


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate the recruiter-facing dashboard PDF")
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(json.loads(args.data.read_text(encoding="utf-8")), args.output)


if __name__ == "__main__":
    main()
