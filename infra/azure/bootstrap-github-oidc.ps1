[CmdletBinding()]
param(
    [Parameter()]
    [string] $SubscriptionId,

    [Parameter()]
    [string] $ResourceGroup = 'caseledger-prod',

    [Parameter()]
    [string] $Location = 'canadacentral',

    [Parameter()]
    [string] $IdentityName = 'caseledger-github-deploy',

    [Parameter()]
    [string] $GitHubOwner = 'MarvelousJade',

    [Parameter()]
    [string] $GitHubRepository = 'CaseLedger',

    [Parameter()]
    [string] $GitHubEnvironment = 'azure-production'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Reopen PowerShell after installing it.'
}

$account = & az account show --output json 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'Azure CLI is not signed in. Run az login, then run this script again.'
}

$accountDetails = $account | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    $SubscriptionId = $accountDetails.id
}

& az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) {
    throw 'The requested Azure subscription could not be selected.'
}

& az group create `
    --name $ResourceGroup `
    --location $Location `
    --only-show-errors `
    --output none
if ($LASTEXITCODE -ne 0) {
    throw 'The Azure resource group could not be created or updated.'
}

$identityJson = & az identity show `
    --resource-group $ResourceGroup `
    --name $IdentityName `
    --output json `
    2>$null
if ($LASTEXITCODE -ne 0) {
    $identityJson = & az identity create `
        --resource-group $ResourceGroup `
        --name $IdentityName `
        --location $Location `
        --only-show-errors `
        --output json
}
if ($LASTEXITCODE -ne 0) {
    throw 'The GitHub deployment identity could not be created.'
}

$identity = $identityJson | ConvertFrom-Json
$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
foreach ($role in @('Contributor', 'Role Based Access Control Administrator')) {
    & az role assignment create `
        --assignee-object-id $identity.principalId `
        --assignee-principal-type ServicePrincipal `
        --role $role `
        --scope $scope `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "The '$role' role assignment could not be created."
    }
}

$credentialName = 'github-azure-production'
$subject = "repo:${GitHubOwner}/${GitHubRepository}:environment:${GitHubEnvironment}"
& az identity federated-credential show `
    --resource-group $ResourceGroup `
    --identity-name $IdentityName `
    --name $credentialName `
    --output none `
    2>$null
if ($LASTEXITCODE -ne 0) {
    & az identity federated-credential create `
        --resource-group $ResourceGroup `
        --identity-name $IdentityName `
        --name $credentialName `
        --issuer 'https://token.actions.githubusercontent.com' `
        --subject $subject `
        --audiences 'api://AzureADTokenExchange' `
        --only-show-errors `
        --output none
}
if ($LASTEXITCODE -ne 0) {
    throw 'The GitHub federated credential could not be created.'
}

$result = [ordered]@{
    GitHubEnvironment = $GitHubEnvironment
    AZURE_CLIENT_ID = $identity.clientId
    AZURE_TENANT_ID = $accountDetails.tenantId
    AZURE_SUBSCRIPTION_ID = $SubscriptionId
}

Write-Host 'OIDC bootstrap completed. Add these values as secrets in the protected GitHub environment:'
$result | ConvertTo-Json
Write-Host 'Also add strong AZURE_POSTGRES_ADMIN_PASSWORD and CASELEDGER_SEED_ADMIN_PASSWORD environment secrets.'
