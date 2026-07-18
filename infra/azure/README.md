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

The production defaults keep exactly one API replica because SignalR currently uses process-local
state and the result consumer is not coordinated across API replicas. Production also keeps at least
one worker replica running so it can drain durable result-outbox work after broker recovery. The
GitHub workflow applies a separate low-cost profile to `staging` and `dev`: one HTTP API and one
worker remain running for the durability rehearsal. The API owns a database outbox dispatcher and
the result consumer, so an HTTP-only scale rule cannot safely wake it for that background work. The
template permits an operator to reduce minimums to zero after testing, but a zero worker cannot wake
solely for result rows stranded in its PostgreSQL outbox either.

## Image policy

`main.bicep` defaults to the currently published public GHCR images pinned by OCI digest. It never pulls `latest` during deployment. After publishing a new application revision, override `apiImage` and `workerImage` with that revision's immutable digest:

```bicep
param apiImage = 'ghcr.io/marvelousjade/caseledger@sha256:<digest>'
param workerImage = 'ghcr.io/marvelousjade/caseledger-audit-worker@sha256:<digest>'
```

The manual GitHub workflow overrides these defaults with the 40-character commit tag published by
the repository image workflow and proves both images can be pulled anonymously before contacting
Azure. Confirm that image publication is green before a deployment. A registry tag can technically
be moved; record the resolved image digests when a revision must be exactly reproducible.

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

The CaseLedger sign-in app registration is separate from the user-assigned identity used by GitHub
Actions. `AZURE_CLIENT_ID` identifies the deployment identity used for OIDC, while
`CASELEDGER_ENTRA_CLIENT_ID` identifies the interactive CaseLedger app registration. Create the
single-tenant app registration before deployment, then add the emitted HTTPS `/signin-oidc`
redirect URI before testing sign-in.

Demo modes require separate administrator and analyst passwords. Public credential hints remain
off by default even when local login is enabled. The known Render demo credentials must not be
reused for Azure.

## Prerequisites

1. Azure CLI with Bicep support
2. Azure Container Apps CLI extension `1.3.0b4` (the workflow installs this exact version)
3. An Azure subscription and an existing resource group
4. Permission to create the listed resources and role assignments
5. Registered resource providers: `Microsoft.App`, `Microsoft.Authorization`, `Microsoft.DBforPostgreSQL`, `Microsoft.Insights`, `Microsoft.KeyVault`, `Microsoft.ManagedIdentity`, `Microsoft.Network`, `Microsoft.OperationalInsights`, `Microsoft.ServiceBus`, and `Microsoft.Storage`

Provider registration example:

```powershell
az extension add `
  --name containerapp `
  --version 1.3.0b4 `
  --allow-preview true `
  --yes `
  --only-show-errors

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
./infra/azure/validate.ps1 -ResourceGroup 'caseledger-staging' -EnvironmentName staging

$resourceGroup = 'caseledger-staging'
$deploymentRevision = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$deploymentArguments = @(
  'deployment', 'group', 'create',
  '--resource-group', $resourceGroup,
  '--template-file', './infra/azure/main.bicep',
  '--parameters', './infra/azure/main.bicepparam',
  '--parameters',
  'environmentName=staging',
  "deploymentRevision=$deploymentRevision",
  'postgresSkuName=Standard_B1ms',
  'postgresSkuTier=Burstable',
  'postgresStorageSizeGb=32',
  'postgresBackupRetentionDays=7',
  'postgresHighAvailabilityMode=Disabled',
  'apiMinReplicas=1',
  'apiMaxReplicas=1',
  'workerMinReplicas=1',
  'workerMaxReplicas=1',
  '--mode', 'Incremental',
  '--only-show-errors'
)

./infra/azure/prepare-servicebus-maintenance.ps1 `
  -ResourceGroup $resourceGroup `
  -EnvironmentName staging

$foundationDeploymentName = 'caseledger-staging-foundation'
az @deploymentArguments --name $foundationDeploymentName --parameters deploymentPhase=foundation
if ($LASTEXITCODE -ne 0) {
  throw "Azure foundation deployment '$foundationDeploymentName' failed."
}
$foundationOutputs = az deployment group show `
  --resource-group $resourceGroup `
  --name $foundationDeploymentName `
  --query properties.outputs `
  --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
  throw "Could not read outputs for '$foundationDeploymentName'."
}
$routingDeploymentName = 'caseledger-staging-routing'
az deployment group create `
  --resource-group $resourceGroup `
  --name $routingDeploymentName `
  --template-file ./infra/azure/servicebus-routing.bicep `
  --parameters `
    "serviceBusNamespaceName=$($foundationOutputs.serviceBusNamespaceName.value)" `
    "apiIdentityName=$($foundationOutputs.apiIdentityName.value)" `
    "workerIdentityName=$($foundationOutputs.workerIdentityName.value)" `
  --mode Incremental `
  --only-show-errors `
  --output none
if ($LASTEXITCODE -ne 0) {
  throw "Azure routing deployment '$routingDeploymentName' failed."
}
./infra/azure/enforce-servicebus-rules.ps1 `
  -ResourceGroup $resourceGroup `
  -DeploymentName $routingDeploymentName

$applicationDeploymentName = 'caseledger-staging'
az @deploymentArguments --name $applicationDeploymentName --parameters deploymentPhase=application
if ($LASTEXITCODE -ne 0) {
  throw "Azure application deployment '$applicationDeploymentName' failed."
}
./infra/azure/enforce-servicebus-rules.ps1 `
  -ResourceGroup $resourceGroup `
  -DeploymentName $applicationDeploymentName `
  -VerifyOnly
az deployment group show `
  --resource-group $resourceGroup `
  --name $applicationDeploymentName `
  --output json | Set-Content application-deployment.json
./infra/azure/verify-container-app-revisions.ps1 `
  -ResourceGroup $resourceGroup `
  -DeploymentFile application-deployment.json `
  -ExpectedDeploymentRevision $deploymentRevision
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
$resourceGroup = '<resource-group-name>'
$deploymentRevision = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$deploymentArguments = @(
  'deployment', 'group', 'create',
  '--resource-group', $resourceGroup,
  '--template-file', './infra/azure/main.bicep',
  '--parameters', './infra/azure/main.bicepparam',
  '--parameters',
  'environmentName=prod',
  "deploymentRevision=$deploymentRevision",
  'apiImage=ghcr.io/marvelousjade/caseledger@sha256:<digest>',
  'workerImage=ghcr.io/marvelousjade/caseledger-audit-worker@sha256:<digest>',
  '--mode', 'Incremental',
  '--only-show-errors'
)

./infra/azure/prepare-servicebus-maintenance.ps1 `
  -ResourceGroup $resourceGroup `
  -EnvironmentName prod

$foundationDeploymentName = 'caseledger-prod-foundation'
az @deploymentArguments --name $foundationDeploymentName --parameters deploymentPhase=foundation
if ($LASTEXITCODE -ne 0) {
  throw "Azure foundation deployment '$foundationDeploymentName' failed."
}
$foundationOutputs = az deployment group show `
  --resource-group $resourceGroup `
  --name $foundationDeploymentName `
  --query properties.outputs `
  --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
  throw "Could not read outputs for '$foundationDeploymentName'."
}
$routingDeploymentName = 'caseledger-prod-routing'
az deployment group create `
  --resource-group $resourceGroup `
  --name $routingDeploymentName `
  --template-file ./infra/azure/servicebus-routing.bicep `
  --parameters `
    "serviceBusNamespaceName=$($foundationOutputs.serviceBusNamespaceName.value)" `
    "apiIdentityName=$($foundationOutputs.apiIdentityName.value)" `
    "workerIdentityName=$($foundationOutputs.workerIdentityName.value)" `
  --mode Incremental `
  --only-show-errors `
  --output none
if ($LASTEXITCODE -ne 0) {
  throw "Azure routing deployment '$routingDeploymentName' failed."
}
./infra/azure/enforce-servicebus-rules.ps1 `
  -ResourceGroup $resourceGroup `
  -DeploymentName $routingDeploymentName

$applicationDeploymentName = 'caseledger-prod'
az @deploymentArguments --name $applicationDeploymentName --parameters deploymentPhase=application
if ($LASTEXITCODE -ne 0) {
  throw "Azure application deployment '$applicationDeploymentName' failed."
}
./infra/azure/enforce-servicebus-rules.ps1 `
  -ResourceGroup $resourceGroup `
  -DeploymentName $applicationDeploymentName `
  -VerifyOnly
az deployment group show `
  --resource-group $resourceGroup `
  --name $applicationDeploymentName `
  --output json | Set-Content application-deployment.json
./infra/azure/verify-container-app-revisions.ps1 `
  -ResourceGroup $resourceGroup `
  -DeploymentFile application-deployment.json `
  -ExpectedDeploymentRevision $deploymentRevision
```

The application-phase deployment outputs the API URL, Entra redirect URI, resource names, topic/subscription names,
identity client IDs, and Blob endpoint. It never outputs credentials.

Azure creates a match-all `$Default` rule when it creates a topic subscription, so deployments are
deliberately staged. The `foundation` phase omits both application resources and does not own the
topic or subscriptions. After the full ARM deployment succeeds, the narrow
`servicebus-routing.bicep` deployment creates or updates those entities as `Disabled` before its child
rules, preserving a prior fail-closed state on retries. `enforce-servicebus-rules.ps1` then removes
every unexpected rule, validates the complete correlation filters, activates both subscriptions, and
enables publishing last. Only then may the `application` phase create new API and worker revisions; a final read-only
enforcer pass confirms the invariant without suspending healthy consumers. The GitHub workflow performs this sequence automatically, and direct
deployments must use the same sequence.

Before the foundation phase, `prepare-servicebus-maintenance.ps1` upgrades and verifies an existing API
at 100 durable request-outbox publish attempts; a fresh deployment safely skips this step because no
publisher exists. This protects the first upgrade while the existing image still uses a finite
transport budget. The deployed image additionally keeps retryable broker outages pending instead of
terminally dead-lettering them; non-retryable payload and programming failures retain the bounded
25-attempt policy. Each supported application phase also passes a unique `deploymentRevision`, forcing
a fresh worker revision after routing maintenance even when the image digest is unchanged. The phase
default is `foundation`, so a one-shot direct deployment cannot accidentally start publishers before
routing is exact. `verify-container-app-revisions.ps1` then requires the API and worker to expose that
rollout marker on one active, healthy, latest-ready revision each. The worker readiness endpoint and
the API `/health/messaging` deployment gate become healthy only after non-destructive Service Bus
receive and send-link probes succeed.

## GitHub environment configuration

Use separate protected GitHub environments so a staging dispatch cannot read production secrets:

| Logical environment | GitHub environment | Default resource group | Default deployment identity |
| --- | --- | --- | --- |
| `staging` | `azure-staging` | `caseledger-staging` | `caseledger-github-staging-deploy` |
| `dev` | `azure-dev` | `caseledger-dev` | `caseledger-github-dev-deploy` |
| `prod` | `azure-production` | `caseledger-prod` | `caseledger-github-deploy` |

Run the bootstrap only after signing in to the intended Azure subscription and GitHub CLI, and after
reviewing costs. The script reads GitHub's authoritative OIDC subject prefix so both classic and
immutable repository-ID subjects are configured exactly; it refuses unexpected prefixes rather than
broadening Azure trust. Selecting the GitHub environment also selects safe resource-group and
identity defaults; explicit overrides remain available:

```powershell
gh auth login
./infra/azure/bootstrap-github-oidc.ps1 -GitHubEnvironment azure-staging
```

If a GitHub Enterprise/API version does not return the subject prefix, preview the exact repository
OIDC subject in GitHub settings and pass it with `-GitHubOidcSubjectPrefix`; the script still validates
that the prefix names the requested repository before it changes Azure trust.

Store the bootstrap resource group as the `AZURE_RESOURCE_GROUP` variable in that GitHub
environment; the workflow intentionally takes it from the protected environment instead of a
free-form dispatch input. Store the emitted identity values there as `AZURE_CLIENT_ID`,
`AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`, plus a strong
`AZURE_POSTGRES_ADMIN_PASSWORD`. Set `CASELEDGER_AUTHENTICATION_MODE` as an environment variable.

For Entra mode, add the `CASELEDGER_ENTRA_TENANT_ID`, `CASELEDGER_ENTRA_CLIENT_ID`, and
`CASELEDGER_ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID` variables, set
`CASELEDGER_ENTRA_AUTO_PROVISION_ANALYST=false`, and add the protected
`CASELEDGER_ENTRA_CLIENT_SECRET`. For a demo-inclusive mode, add protected
`CASELEDGER_SEED_ADMIN_PASSWORD` and `CASELEDGER_SEED_ANALYST_PASSWORD`. The workflow validates that
the selected mode has a viable sign-in path before inspecting images, running `what-if`, or
deploying.

Every external action is pinned to a full upstream commit SHA. Validation receives only booleans
indicating whether required secrets exist, and `what-if` uses clearly marked non-secret placeholders.
Live database, demo-account, and Entra passwords are mapped only into the deployment step. Checkout,
image inspection, Bicep compilation, Azure login, output handling, and health checks do not receive
those values in their environment. Deployment uses `main.bicepparam` to read sensitive values from
the step environment, keeping them out of Azure CLI command arguments.

## Low-cost staging and cleanup

The workflow deliberately uses these settings for `staging` and `dev` while leaving the Bicep
production defaults unchanged:

- PostgreSQL `Burstable` / `Standard_B1ms`, 32 GiB storage, seven-day backup retention, and no HA
- Service Bus Standard
- API replicas `1..1` during the durability rehearsal
- worker replicas `1..1` during the durability rehearsal

The [Azure free-services offer](https://azure.microsoft.com/pricing/free-services/) may include 750
hours of PostgreSQL B1ms plus 32 GiB each of data and backup storage for eligible new accounts, but
free credit and offer eligibility are not guarantees.
Service Bus Standard, Log Analytics ingestion, Key Vault operations, Blob storage, and Container
Apps usage can still consume credit. Create a resource-group budget and alerts before deployment,
and inspect the `what-if` output for any General Purpose database, Premium broker, or HA resource.

After the rehearsal, set both Container Apps minimum replica counts to zero before stopping the
database. The worker's Service Bus scaler can wake it for queued requests, but scale it back to one
when testing durable result-outbox recovery. The API has only an HTTP scale rule, so scale it back to
one before expecting its database outbox or result consumer to make progress. A stopped PostgreSQL Flexible Server retains billable
storage and automatically starts again after seven days, so delete the staging group when it is no
longer needed or schedule repeated shutdown. Key Vault purge protection prevents immediate reuse of
the same deleted vault name for its 90-day retention period; recover the vault for an iterative
rehearsal or use a different deployment prefix.
