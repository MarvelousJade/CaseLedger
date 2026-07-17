# CaseLedger on Azure

This Bicep deployment creates the production-oriented Azure foundation for the CaseLedger API and audit worker. It does not contain subscription IDs, tenant IDs, passwords, connection strings, or other personal values.

## What it provisions

- A VNet with delegated subnets for Azure Container Apps and PostgreSQL Flexible Server
- A Log Analytics workspace and a Consumption-profile Container Apps environment
- Public-ingress API and internal worker Container Apps with startup, readiness, and liveness probes
- Private PostgreSQL Flexible Server networking, private DNS, and a CaseLedger database
- A Service Bus topic with separate request/result subscriptions and exact subject filters
- A StorageV2 account with blob versioning, soft deletion, and separate private `evidence` and `data-protection` containers
- A Key Vault with RBAC, purge protection, database secrets, conditional sign-in secrets, and a versionless Data Protection key identifier
- Separate API and worker managed identities with least-scope Key Vault, Service Bus, and Blob RBAC assignments

The API defaults to exactly one replica because SignalR currently uses process-local state and the result consumer is not coordinated across API replicas. The worker keeps at least one replica running so it can drain durable result-outbox work after broker recovery.

## Image policy

`main.bicep` defaults to the currently published public GHCR images pinned by OCI digest. It never pulls `latest` during deployment. After publishing a new application revision, override `apiImage` and `workerImage` with that revision's immutable digest:

```bicep
param apiImage = 'ghcr.io/marvelousjade/caseledger@sha256:<digest>'
param workerImage = 'ghcr.io/marvelousjade/caseledger-audit-worker@sha256:<digest>'
```

The manual GitHub workflow overrides these defaults with the immutable 40-character commit tag
published by the repository image workflow. Confirm that image publication is green before an
Azure deployment.

## Service Bus payload decision

The lower-cost default is Service Bus Standard. Its broker limit is 256 KiB, so the template sets both applications to a 192 KiB serialized-message ceiling, leaving headroom for broker metadata. Standard deployments automatically clamp `maximumAuditMessageBytes` to 192 KiB.

Set `serviceBusSkuName` to `Premium` when larger snapshots are required. Premium is dedicated capacity and materially more expensive; the template makes the tier and messaging-unit count explicit. The current application validation accepts at most 1 MiB, and the topic is configured to match that ceiling.

## Security model and current boundaries

- PostgreSQL is reachable only through the deployment VNet and always requires TLS certificate verification.
- Container Apps retrieve database and configured authentication secrets through versionless Key Vault references; values are not embedded in app definitions or emitted as outputs. Seeded-password secrets are created only for a selected demo mode.
- Service Bus local/SAS authentication is disabled. The API and worker use their user-assigned managed identities for send/receive operations, and the worker's KEDA scaler uses the same identity.
- Blob shared-key access and public blob access are disabled. The API identity has data-plane access only to the evidence and Data Protection containers.
- Evidence uploads use the private `evidence` container. ASP.NET Core Data Protection keys use a separate blob and are wrapped by the versionless Key Vault RSA key, allowing session cookies to survive revisions without granting broad vault access.
- Key Vault, Service Bus, and Storage use encrypted public endpoints in this baseline. Add private endpoints and DNS zones if an organization's network policy requires private-only data-plane access.
- Webhook delivery is disabled in the deployed API until a production callback policy and destination allowlist are configured.

## Authentication choice

`authenticationMode` is required and accepts `Entra`, `Demo`, or `DemoAndEntra`; there is no
implicit production sign-in method. For Entra, register the output `entraRedirectUri` on the app
registration and provide the tenant ID, client ID, and client secret. Prefer
`entraBootstrapAdministratorObjectId`: the first startup maps exactly that Entra object ID to the
seeded administrator and refuses to replace a conflicting mapping later. Tenant-wide Analyst
auto-provisioning is an explicit alternative and should normally remain off.

Demo modes require separate administrator and analyst passwords. Public credential hints remain
off by default even when local login is enabled. The known Render demo credentials must not be
reused for Azure.

## Prerequisites

1. Azure CLI with Bicep support
2. An Azure subscription and an existing resource group
3. Permission to create the listed resources and role assignments
4. Registered resource providers: `Microsoft.App`, `Microsoft.Authorization`, `Microsoft.DBforPostgreSQL`, `Microsoft.Insights`, `Microsoft.KeyVault`, `Microsoft.ManagedIdentity`, `Microsoft.Network`, `Microsoft.OperationalInsights`, `Microsoft.ServiceBus`, and `Microsoft.Storage`

Provider registration example:

```powershell
$providers = @(
  'Microsoft.App',
  'Microsoft.Authorization',
  'Microsoft.DBforPostgreSQL',
  'Microsoft.Insights',
  'Microsoft.KeyVault',
  'Microsoft.ManagedIdentity',
  'Microsoft.Network',
  'Microsoft.OperationalInsights',
  'Microsoft.ServiceBus',
  'Microsoft.Storage'
)
$providers | ForEach-Object { az provider register --namespace $_ --wait }
```

## Validate and deploy

`main.bicepparam` reads values from the current process and does not persist them. This Entra example
uses a strict bootstrap mapping:

```powershell
$env:CASELEDGER_POSTGRES_ADMIN_PASSWORD = '<strong-generated-password>'
$env:CASELEDGER_AUTHENTICATION_MODE = 'Entra'
$env:CASELEDGER_ENTRA_TENANT_ID = '<tenant-guid>'
$env:CASELEDGER_ENTRA_CLIENT_ID = '<application-client-guid>'
$env:CASELEDGER_ENTRA_CLIENT_SECRET = '<protected-client-secret>'
$env:CASELEDGER_ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID = '<administrator-object-guid>'

./infra/azure/validate.ps1
./infra/azure/validate.ps1 -ResourceGroup '<resource-group-name>'

az deployment group create `
  --name 'caseledger-prod' `
  --resource-group '<resource-group-name>' `
  --template-file './infra/azure/main.bicep' `
  --parameters './infra/azure/main.bicepparam'
```

For a private local-login deployment, choose `Demo`, set both seed-password variables to distinct
strong values, and leave `CASELEDGER_SHOW_DEMO_CREDENTIALS` unset or `false`:

```powershell
$env:CASELEDGER_AUTHENTICATION_MODE = 'Demo'
$env:CASELEDGER_SEED_ADMIN_PASSWORD = '<strong-generated-password>'
$env:CASELEDGER_SEED_ANALYST_PASSWORD = '<different-strong-generated-password>'
```

For a freshly published revision, pass immutable images on the deployment command or place non-secret overrides in a separate, untracked `.bicepparam` file:

```powershell
az deployment group create `
  --name 'caseledger-prod' `
  --resource-group '<resource-group-name>' `
  --template-file './infra/azure/main.bicep' `
  --parameters './infra/azure/main.bicepparam' `
  --parameters apiImage='ghcr.io/marvelousjade/caseledger@sha256:<digest>' `
               workerImage='ghcr.io/marvelousjade/caseledger-audit-worker@sha256:<digest>'
```

The deployment outputs the API URL, Entra redirect URI, resource names, topic/subscription names,
identity client IDs, and Blob endpoint. It never outputs credentials.

## GitHub environment configuration

Run `bootstrap-github-oidc.ps1` only after signing in to the intended subscription and reviewing
costs. In the protected `azure-production` environment, store the emitted Azure identity values as
`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`, plus
`AZURE_POSTGRES_ADMIN_PASSWORD`. Set `CASELEDGER_AUTHENTICATION_MODE` as an environment variable.

For Entra mode, add the `CASELEDGER_ENTRA_TENANT_ID`, `CASELEDGER_ENTRA_CLIENT_ID`, and
`CASELEDGER_ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID` variables and the protected
`CASELEDGER_ENTRA_CLIENT_SECRET`. For a demo-inclusive mode, add protected
`CASELEDGER_SEED_ADMIN_PASSWORD` and `CASELEDGER_SEED_ANALYST_PASSWORD`. The workflow validates that
the selected mode has a viable sign-in path before running `what-if` or deployment.

## Cost notes

The main cost drivers are PostgreSQL Flexible Server, Service Bus Premium if selected, log ingestion, and always-on Container App replicas. The defaults choose Service Bus Standard, disabled PostgreSQL high availability, one API replica, and one minimum worker replica. Review Azure pricing for the target region before deployment; changing PostgreSQL to Burstable can reduce demo costs but is a weaker production baseline.
