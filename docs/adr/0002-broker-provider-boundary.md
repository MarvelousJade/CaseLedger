# ADR 0002: Broker provider boundary

- Status: Accepted
- Date: 2026-07-16

## Context

Local development needs a reproducible broker, while a hosted deployment may use a managed service.
Business contracts and idempotency rules should not depend on one broker SDK.

## Decision

RabbitMQ is the Compose and E2E default. Azure Service Bus is an alternative provider selected by
configuration. Both carry the same versioned request and result contracts and use manual consumer
settlement.

Service Bus uses one topic with two pre-provisioned subscriptions. The worker subscription filters
`Subject` to `caseledger.audit.verification.requested.v1`; the API subscription filters `Subject` to
`caseledger.audit.verification.result.v1`. The unfiltered default rule is removed from both
subscriptions. Consumers use PeekLock, explicitly complete valid work, dead-letter rejected
contracts, and abandon infrastructure failures. Scheduled retries use a unique broker message ID
per attempt while retaining the application `jobId`.

The application validates configuration but does not create Azure resources.

## Consequences

- Core processing and tests can use provider-neutral delivery interfaces.
- RabbitMQ topology is declared by the services; Service Bus topic, filters, and subscriptions are
  an operator responsibility.
- Provider-specific retry and settlement mechanics remain isolated at the transport boundary.
- Service Bus support does not imply that an Azure deployment exists.
