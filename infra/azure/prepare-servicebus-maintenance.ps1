[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [ValidateSet('dev', 'staging', 'prod')]
    [string] $EnvironmentName,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $ApiContainerAppName,

    [Parameter()]
    [ValidateRange(25, 100)]
    [int] $MinimumOutboxAttempts = 100,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int] $MaxAttempts = 60,

    [Parameter()]
    [ValidateRange(0, 30)]
    [int] $RetryDelaySeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI was not found.'
}

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $json = (& az @Arguments | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments[0..([Math]::Min(2, $Arguments.Count - 1))] -join ' ')"
    }
    if ([string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    return $json | ConvertFrom-Json
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory)]
        [object] $InputObject,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-ContainerEnvironmentValue {
    param(
        [Parameter(Mandatory)]
        [object] $ContainerApp,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $properties = Get-JsonPropertyValue -InputObject $ContainerApp -Name 'properties'
    if ($null -eq $properties) {
        return $null
    }
    $template = Get-JsonPropertyValue -InputObject $properties -Name 'template'
    if ($null -eq $template) {
        return $null
    }
    $containers = Get-JsonPropertyValue -InputObject $template -Name 'containers'
    foreach ($container in @($containers)) {
        $environment = Get-JsonPropertyValue -InputObject $container -Name 'env'
        foreach ($entry in @($environment)) {
            if ((Get-JsonPropertyValue -InputObject $entry -Name 'name') -eq $Name) {
                return Get-JsonPropertyValue -InputObject $entry -Name 'value'
            }
        }
    }

    return $null
}

function Get-OutboxAttemptBudget {
    param(
        [Parameter(Mandatory)]
        [object] $ContainerApp
    )

    $rawValue = Get-ContainerEnvironmentValue `
        -ContainerApp $ContainerApp `
        -Name 'Messaging__MaxAttempts'
    $parsedValue = 0
    if ($null -ne $rawValue -and
        [int]::TryParse([string] $rawValue, [ref] $parsedValue)) {
        return $parsedValue
    }

    return 0
}

function Get-ContainerApp {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    return Invoke-AzJson -Arguments @(
        'containerapp', 'show',
        '--resource-group', $ResourceGroup,
        '--name', $Name,
        '--only-show-errors',
        '--output', 'json'
    )
}

if ([string]::IsNullOrWhiteSpace($ApiContainerAppName)) {
    $allApps = @(Invoke-AzJson -Arguments @(
        'containerapp', 'list',
        '--resource-group', $ResourceGroup,
        '--only-show-errors',
        '--output', 'json'
    ))
    $likelyApiApps = @($allApps | Where-Object {
        $name = [string] (Get-JsonPropertyValue -InputObject $_ -Name 'name')
        $name.EndsWith('-api', [StringComparison]::Ordinal)
    })
    $candidates = @($likelyApiApps | Where-Object {
        $name = [string] (Get-JsonPropertyValue -InputObject $_ -Name 'name')
        $tags = Get-JsonPropertyValue -InputObject $_ -Name 'tags'
        if ($null -eq $tags) {
            return $false
        }
        $applicationTag = Get-JsonPropertyValue -InputObject $tags -Name 'application'
        $environmentTag = Get-JsonPropertyValue -InputObject $tags -Name 'environment'
        return $applicationTag -eq 'CaseLedger' -and
            $environmentTag -eq $EnvironmentName -and
            $name.EndsWith('-api', [StringComparison]::Ordinal)
    })

    if ($candidates.Count -eq 0) {
        if ($likelyApiApps.Count -gt 0) {
            $likelyNames = @($likelyApiApps | ForEach-Object {
                Get-JsonPropertyValue -InputObject $_ -Name 'name'
            }) -join ', '
            throw "Found API-like Container Apps without the required CaseLedger '$EnvironmentName' tags: $likelyNames. Pass -ApiContainerAppName only after confirming the target."
        }
        Write-Host 'No existing CaseLedger API was found; maintenance preflight is not needed for a fresh deployment.'
        return
    }
    if ($candidates.Count -ne 1) {
        $candidateNames = @($candidates | ForEach-Object {
            Get-JsonPropertyValue -InputObject $_ -Name 'name'
        }) -join ', '
        throw "Expected one existing CaseLedger API for '$EnvironmentName'; found: $candidateNames"
    }

    $ApiContainerAppName = [string] (
        Get-JsonPropertyValue -InputObject $candidates[0] -Name 'name')
}

$app = Get-ContainerApp -Name $ApiContainerAppName
$currentBudget = Get-OutboxAttemptBudget -ContainerApp $app
if ($currentBudget -lt $MinimumOutboxAttempts) {
    Write-Host "Raising '$ApiContainerAppName' request-outbox attempts from $currentBudget to $MinimumOutboxAttempts before routing maintenance."
    & az containerapp update `
        --resource-group $ResourceGroup `
        --name $ApiContainerAppName `
        --set-env-vars "Messaging__MaxAttempts=$MinimumOutboxAttempts" `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Could not raise the request-outbox retry budget for '$ApiContainerAppName'."
    }
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $app = Get-ContainerApp -Name $ApiContainerAppName
    $properties = Get-JsonPropertyValue -InputObject $app -Name 'properties'
    $provisioningState = Get-JsonPropertyValue -InputObject $properties -Name 'provisioningState'
    $latestRevision = Get-JsonPropertyValue -InputObject $properties -Name 'latestRevisionName'
    $latestReadyRevision = Get-JsonPropertyValue -InputObject $properties -Name 'latestReadyRevisionName'
    $currentBudget = Get-OutboxAttemptBudget -ContainerApp $app
    $revisions = @(Invoke-AzJson -Arguments @(
        'containerapp', 'revision', 'list',
        '--resource-group', $ResourceGroup,
        '--name', $ApiContainerAppName,
        '--only-show-errors',
        '--output', 'json'
    ))
    $activeRevisions = @($revisions | Where-Object {
        (Get-JsonPropertyValue -InputObject $_ -Name 'properties').active -eq $true
    })
    $readyActiveRevision = @($activeRevisions | Where-Object {
        $revisionName = Get-JsonPropertyValue -InputObject $_ -Name 'name'
        $revisionProperties = Get-JsonPropertyValue -InputObject $_ -Name 'properties'
        $healthState = Get-JsonPropertyValue -InputObject $revisionProperties -Name 'healthState'
        $revisionName -eq $latestRevision -and $healthState -eq 'Healthy'
    })

    if ($provisioningState -eq 'Succeeded' -and
        $latestRevision -eq $latestReadyRevision -and
        $currentBudget -ge $MinimumOutboxAttempts -and
        $activeRevisions.Count -eq 1 -and
        $readyActiveRevision.Count -eq 1) {
        Write-Host "Maintenance preflight passed for '$ApiContainerAppName' at $currentBudget outbox attempts."
        return
    }

    if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

throw "The existing API '$ApiContainerAppName' did not become uniquely active and healthy with at least $MinimumOutboxAttempts outbox attempts. Service Bus was not changed."
