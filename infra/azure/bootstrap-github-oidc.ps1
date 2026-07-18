[CmdletBinding()]
param(
    [Parameter()]
    [string] $SubscriptionId,

    [Parameter()]
    [string] $ResourceGroup,

    [Parameter()]
    [string] $Location = 'canadacentral',

    [Parameter()]
    [string] $IdentityName,

    [Parameter()]
    [string] $GitHubOwner = 'MarvelousJade',

    [Parameter()]
    [string] $GitHubRepository = 'CaseLedger',

    [Parameter()]
    [string] $GitHubOidcSubjectPrefix,

    [Parameter()]
    [ValidateSet('azure-production', 'azure-staging', 'azure-dev')]
    [string] $GitHubEnvironment = 'azure-production'
)

$ErrorActionPreference = 'Stop'

$environmentDefaults = switch ($GitHubEnvironment) {
    'azure-production' {
        @{
            ResourceGroup = 'caseledger-prod'
            IdentityName = 'caseledger-github-deploy'
        }
    }
    'azure-staging' {
        @{
            ResourceGroup = 'caseledger-staging'
            IdentityName = 'caseledger-github-staging-deploy'
        }
    }
    'azure-dev' {
        @{
            ResourceGroup = 'caseledger-dev'
            IdentityName = 'caseledger-github-dev-deploy'
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = $environmentDefaults.ResourceGroup
}
if ([string]::IsNullOrWhiteSpace($IdentityName)) {
    $IdentityName = $environmentDefaults.IdentityName
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Reopen PowerShell after installing it.'
}

if ([string]::IsNullOrWhiteSpace($GitHubOidcSubjectPrefix)) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'GitHub CLI is required to resolve the repository OIDC subject. Install gh, run gh auth login, then retry.'
    }

    $oidcSettingsJson = & gh api `
        "repos/$GitHubOwner/$GitHubRepository/actions/oidc/customization/sub" `
        2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'The GitHub OIDC subject could not be read. Confirm gh is signed in with repository administration access.'
    }

    $oidcSettings = $oidcSettingsJson | ConvertFrom-Json
    $GitHubOidcSubjectPrefix = $oidcSettings.sub_claim_prefix
}

$nameOnlySubjectPrefix = "repo:${GitHubOwner}/${GitHubRepository}"
$immutableSubjectPattern = '^repo:' +
    [regex]::Escape($GitHubOwner) + '@\d+/' +
    [regex]::Escape($GitHubRepository) + '@\d+$'
if ($GitHubOidcSubjectPrefix -ne $nameOnlySubjectPrefix -and
    $GitHubOidcSubjectPrefix -notmatch $immutableSubjectPattern) {
    throw "GitHub returned an unexpected OIDC subject prefix for $GitHubOwner/$GitHubRepository. Refusing to broaden Azure trust."
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

$selectedAccount = & az account show --output json 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'The selected Azure subscription could not be read after selection.'
}
$accountDetails = $selectedAccount | ConvertFrom-Json
$SubscriptionId = $accountDetails.id

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

$credentialName = "github-$GitHubEnvironment"
$subject = "${GitHubOidcSubjectPrefix}:environment:${GitHubEnvironment}"
$credentialJson = & az identity federated-credential show `
    --resource-group $ResourceGroup `
    --identity-name $IdentityName `
    --name $credentialName `
    --output json `
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
else {
    $credential = $credentialJson | ConvertFrom-Json
    if ($credential.subject -ne $subject -or
        $credential.issuer -ne 'https://token.actions.githubusercontent.com' -or
        $credential.audiences -notcontains 'api://AzureADTokenExchange') {
        throw "Federated credential '$credentialName' exists but does not match the requested GitHub environment subject. Remove or rename it after reviewing the existing trust relationship."
    }
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
Write-Host "Add AZURE_RESOURCE_GROUP='$ResourceGroup' as a variable in that same protected GitHub environment."
Write-Host 'Also add a strong AZURE_POSTGRES_ADMIN_PASSWORD environment secret.'
Write-Host 'For Entra authentication, add the separate CaseLedger app-registration variables and CASELEDGER_ENTRA_CLIENT_SECRET documented in infra/azure/README.md.'
Write-Host 'For Demo authentication, add distinct CASELEDGER_SEED_ADMIN_PASSWORD and CASELEDGER_SEED_ANALYST_PASSWORD secrets.'
