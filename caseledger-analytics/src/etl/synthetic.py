from __future__ import annotations

import hashlib
import json
import random
from datetime import UTC, datetime, timedelta
from uuid import UUID, uuid5

from sqlalchemy import Engine, text

NAMESPACE = UUID("fe9818e3-a098-4c36-9ae5-c89e4c6a43d8")
ZERO_HASH = "0" * 64


def stable_uuid(kind: str, number: int) -> UUID:
    return uuid5(NAMESPACE, f"{kind}:{number}")


def canonical(event_type: str, data: dict[str, object]) -> str:
    return json.dumps(
        {"eventType": event_type, "data": data},
        sort_keys=True,
        separators=(",", ":"),
    )


def event_hash(previous_hash: str, canonical_data: str) -> str:
    return hashlib.sha256(f"{previous_hash}\n{canonical_data}".encode()).hexdigest()


def seed_source(engine: Engine, case_count: int = 160) -> dict[str, int]:
    """Replace only the disposable Compose source with deterministic portfolio data."""

    rng = random.Random(20260718)
    start = datetime(2026, 1, 1, 9, 0, tzinfo=UTC)
    categories = ("Access", "Billing", "Compliance", "Operations", "Security")
    severities = ("Low", "Medium", "High", "Critical")

    users = []
    for index in range(8):
        user_id = stable_uuid("user", index)
        role = "Administrator" if index == 0 else ("SeniorAnalyst" if index < 3 else "Analyst")
        users.append(
            {
                "id": user_id,
                "name": f"Synthetic User {index + 1}",
                "email": f"synthetic{index + 1}@caseledger.invalid",
                "normalized": f"SYNTHETIC{index + 1}@CASELEDGER.INVALID",
                "role": role,
                "password_hash": "not-a-login-credential",
            }
        )

    cases: list[dict[str, object]] = []
    events: list[dict[str, object]] = []
    jobs: list[dict[str, object]] = []
    deliveries: list[dict[str, object]] = []

    for index in range(case_count):
        case_id = stable_uuid("case", index)
        creator = users[index % len(users)]
        assignee = users[(index + 2) % len(users)]
        created_at = start + timedelta(hours=index * 18)
        severity = severities[index % len(severities)]
        category = categories[index % len(categories)]
        sla_hours = {"Critical": 8, "High": 24, "Medium": 72, "Low": 120}[severity]
        due_at = created_at + timedelta(hours=sla_hours)
        path = index % 5
        status = "Resolved" if path in {0, 1} else ("InProgress" if path in {2, 3} else "New")

        case_events: list[tuple[str, datetime, dict[str, object]]] = [
            (
                "CaseCreated",
                created_at,
                {
                    "status": "New",
                    "severity": severity,
                    "category": category,
                    "assigneeId": str(assignee["id"]),
                },
            )
        ]
        if status in {"InProgress", "Resolved"}:
            case_events.append(
                ("CaseUpdated", created_at + timedelta(hours=2), {"status": "InProgress"})
            )
            case_events.append(
                (
                    "CommentAdded",
                    created_at + timedelta(hours=4 + index % 6),
                    {"body": "Synthetic analyst response"},
                )
            )
        if status == "Resolved":
            case_events.append(
                (
                    "CaseUpdated",
                    created_at + timedelta(hours=6 + index % 40),
                    {"status": "Resolved"},
                )
            )

        previous_hash = ZERO_HASH
        for sequence, (event_type, event_at, data) in enumerate(case_events, start=1):
            canonical_data = canonical(event_type, data)
            current_hash = event_hash(previous_hash, canonical_data)
            events.append(
                {
                    "id": stable_uuid("event", index * 10 + sequence),
                    "case_id": case_id,
                    "sequence": sequence,
                    "event_type": event_type,
                    "description": f"Synthetic {event_type}",
                    "actor_id": assignee["id"] if sequence > 1 else creator["id"],
                    "actor_name": "Synthetic identity",
                    "created_at": event_at,
                    "previous_hash": previous_hash,
                    "hash": current_hash,
                    "canonical_data": canonical_data,
                }
            )
            previous_hash = current_hash

        updated_at = max(event[1] for event in case_events)
        cases.append(
            {
                "id": case_id,
                "reference": f"CL-2026-{index + 1:05d}",
                "title": f"Synthetic {category} case {index + 1}",
                "summary": "Deterministic portfolio data; contains no real person or organization.",
                "status": status,
                "severity": severity,
                "category": category,
                "assignee_id": assignee["id"],
                "created_by_id": creator["id"],
                "created_at": created_at,
                "updated_at": updated_at,
                "due_at": due_at,
                "tags_json": "[]",
            }
        )

        if rng.random() < 0.82:
            job_id = stable_uuid("job", index)
            delivery_id = stable_uuid("delivery", index)
            delivery_created = updated_at + timedelta(minutes=5)
            attempt_count = 1 if index % 6 else 2
            dead_lettered = index % 29 == 0
            delivered_at = (
                None if dead_lettered else delivery_created + timedelta(seconds=1 + index % 20)
            )
            dead_lettered_at = delivery_created + timedelta(minutes=15) if dead_lettered else None
            jobs.append(
                {
                    "id": job_id,
                    "case_id": case_id,
                    "status": "DeadLettered" if dead_lettered else "Completed",
                    "requested_at": delivery_created - timedelta(minutes=1),
                    "completed_at": delivered_at or dead_lettered_at,
                }
            )
            deliveries.append(
                {
                    "id": delivery_id,
                    "job_id": job_id,
                    "result_id": f"result-{index:05d}",
                    "event_type": "caseledger.audit-verification.completed.v1",
                    "payload": "{}",
                    "created_at": delivery_created,
                    "next_attempt_at": delivered_at or dead_lettered_at,
                    "delivered_at": delivered_at,
                    "dead_lettered_at": dead_lettered_at,
                    "attempt_count": attempt_count,
                    "error_code": "receiver_rejected" if dead_lettered else None,
                }
            )

    with engine.begin() as connection:
        connection.exec_driver_sql(
            'TRUNCATE TABLE "WebhookDeliveries", "AuditVerificationJobs", '
            '"AuditEvents", "Cases", "Users" CASCADE'
        )
        connection.execute(
            text(
                'INSERT INTO "Users" '
                '("Id", "Name", "Email", "NormalizedEmail", "Role", "PasswordHash") '
                "VALUES (:id, :name, :email, :normalized, :role, :password_hash)"
            ),
            users,
        )
        connection.execute(
            text(
                'INSERT INTO "Cases" '
                '("Id", "Reference", "Title", "Summary", "Status", "Severity", '
                '"Category", "AssigneeId", "CreatedById", "CreatedAt", "UpdatedAt", '
                '"DueAt", "TagsJson") VALUES '
                "(:id, :reference, :title, :summary, :status, :severity, :category, "
                ":assignee_id, :created_by_id, :created_at, :updated_at, :due_at, :tags_json)"
            ),
            cases,
        )
        connection.execute(
            text(
                'INSERT INTO "AuditEvents" '
                '("Id", "CaseId", "Sequence", "EventType", "Description", "ActorId", '
                '"ActorName", "CreatedAt", "PreviousHash", "Hash", "CanonicalData") VALUES '
                "(:id, :case_id, :sequence, :event_type, :description, :actor_id, "
                ":actor_name, :created_at, :previous_hash, :hash, :canonical_data)"
            ),
            events,
        )
        if jobs:
            connection.execute(
                text(
                    'INSERT INTO "AuditVerificationJobs" '
                    '("Id", "CaseId", "Status", "RequestedAt", "CompletedAt") '
                    "VALUES (:id, :case_id, :status, :requested_at, :completed_at)"
                ),
                jobs,
            )
            connection.execute(
                text(
                    'INSERT INTO "WebhookDeliveries" '
                    '("Id", "VerificationJobId", "ResultId", "EventType", "PayloadJson", '
                    '"CreatedAt", "NextAttemptAt", "DeliveredAt", "DeadLetteredAt", '
                    '"AttemptCount", "LastErrorCode") VALUES '
                    "(:id, :job_id, :result_id, :event_type, :payload, :created_at, "
                    ":next_attempt_at, :delivered_at, :dead_lettered_at, "
                    ":attempt_count, :error_code)"
                ),
                deliveries,
            )

    return {
        "users": len(users),
        "cases": len(cases),
        "case_events": len(events),
        "integration_deliveries": len(deliveries),
    }


def apply_incremental_changes(engine: Engine) -> datetime:
    """Create an SCD change plus one late-arriving business event."""

    with engine.begin() as connection:
        rows = (
            connection.execute(
                text(
                    'SELECT "Id", "CreatedAt", "UpdatedAt", "AssigneeId" '
                    'FROM "Cases" ORDER BY "Reference" LIMIT 2'
                )
            )
            .mappings()
            .all()
        )
        if len(rows) < 2:
            raise RuntimeError("Seed the synthetic source before applying incremental changes")

        high = connection.execute(
            text(
                'SELECT GREATEST(MAX("UpdatedAt"), '
                '(SELECT MAX("CreatedAt") FROM "AuditEvents"), '
                '(SELECT MAX("NextAttemptAt") FROM "WebhookDeliveries")) FROM "Cases"'
            )
        ).scalar_one()
        changed_at = high + timedelta(hours=4)

        changed_case = rows[0]
        sequence = connection.execute(
            text('SELECT MAX("Sequence") + 1 FROM "AuditEvents" WHERE "CaseId" = :case_id'),
            {"case_id": changed_case["Id"]},
        ).scalar_one()
        previous_hash = connection.execute(
            text(
                'SELECT "Hash" FROM "AuditEvents" '
                'WHERE "CaseId" = :case_id ORDER BY "Sequence" DESC LIMIT 1'
            ),
            {"case_id": changed_case["Id"]},
        ).scalar_one()
        changed_data = canonical(
            "CaseUpdated",
            {"status": "InProgress", "severity": "Critical", "category": "Escalations"},
        )
        connection.execute(
            text(
                'UPDATE "Cases" SET "Status" = :status, "Severity" = :severity, '
                '"Category" = :category, "UpdatedAt" = :updated_at WHERE "Id" = :case_id'
            ),
            {
                "status": "InProgress",
                "severity": "Critical",
                "category": "Escalations",
                "updated_at": changed_at,
                "case_id": changed_case["Id"],
            },
        )
        connection.execute(
            text(
                'INSERT INTO "AuditEvents" '
                '("Id", "CaseId", "Sequence", "EventType", "Description", "ActorId", '
                '"ActorName", "CreatedAt", "PreviousHash", "Hash", "CanonicalData") VALUES '
                "(:id, :case_id, :sequence, :event_type, :description, :actor_id, "
                ":actor_name, :created_at, :previous_hash, :hash, :canonical_data)"
            ),
            {
                "id": stable_uuid("incremental-event", 1),
                "case_id": changed_case["Id"],
                "sequence": sequence,
                "event_type": "CaseUpdated",
                "description": "Synthetic incremental SCD change",
                "actor_id": changed_case["AssigneeId"],
                "actor_name": "Synthetic identity",
                "created_at": changed_at,
                "previous_hash": previous_hash,
                "hash": event_hash(previous_hash, changed_data),
                "canonical_data": changed_data,
            },
        )

        late_case = rows[1]
        late_created_at = changed_at + timedelta(hours=1)
        late_business_at = late_case["CreatedAt"] + timedelta(hours=12)
        late_sequence = connection.execute(
            text('SELECT MAX("Sequence") + 1 FROM "AuditEvents" WHERE "CaseId" = :case_id'),
            {"case_id": late_case["Id"]},
        ).scalar_one()
        late_previous_hash = connection.execute(
            text(
                'SELECT "Hash" FROM "AuditEvents" '
                'WHERE "CaseId" = :case_id ORDER BY "Sequence" DESC LIMIT 1'
            ),
            {"case_id": late_case["Id"]},
        ).scalar_one()
        late_data = canonical(
            "CommentAdded",
            {
                "body": "Synthetic late-arriving event",
                "businessOccurredAt": late_business_at.isoformat(),
            },
        )
        connection.execute(
            text('UPDATE "Cases" SET "UpdatedAt" = :updated_at WHERE "Id" = :case_id'),
            {"updated_at": late_created_at, "case_id": late_case["Id"]},
        )
        connection.execute(
            text(
                'INSERT INTO "AuditEvents" '
                '("Id", "CaseId", "Sequence", "EventType", "Description", "ActorId", '
                '"ActorName", "CreatedAt", "PreviousHash", "Hash", "CanonicalData") VALUES '
                "(:id, :case_id, :sequence, :event_type, :description, :actor_id, "
                ":actor_name, :created_at, :previous_hash, :hash, :canonical_data)"
            ),
            {
                "id": stable_uuid("incremental-event", 2),
                "case_id": late_case["Id"],
                "sequence": late_sequence,
                "event_type": "CommentAdded",
                "description": "Synthetic late-arriving business event",
                "actor_id": late_case["AssigneeId"],
                "actor_name": "Synthetic identity",
                "created_at": late_created_at,
                "previous_hash": late_previous_hash,
                "hash": event_hash(late_previous_hash, late_data),
                "canonical_data": late_data,
            },
        )
    return changed_at
