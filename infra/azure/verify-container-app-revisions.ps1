[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $DeploymentFile,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ExpectedDeploymentRevision,

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

function Get-DeploymentOutput {
    param(
        [Parameter(Mandatory)]
        [object] $Deployment,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $properties = Get-JsonPropertyValue -InputObject $Deployment -Name 'properties'
    $outputs = Get-JsonPropertyValue -InputObject $properties -Name 'outputs'
    $output = Get-JsonPropertyValue -InputObject $outputs -Name $Name
    $value = if ($null -eq $output) {
        $null
    }
    else {
        Get-JsonPropertyValue -InputObject $output -Name 'value'
    }
    if ([string]::IsNullOrWhiteSpace([string] $value)) {
        throw "Deployment output '$Name' was missing."
    }

    return [string] $value
}

function Get-ContainerEnvironmentValue {
    param(
        [Parameter(Mandatory)]
        [object] $Resource,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $properties = Get-JsonPropertyValue -InputObject $Resource -Name 'properties'
    $template = Get-JsonPropertyValue -InputObject $properties -Name 'template'
    $containers = if ($null -eq $template) {
        @()
    }
    else {
        @(Get-JsonPropertyValue -InputObject $template -Name 'containers')
    }
    foreach ($container in $containers) {
        $environment = @(Get-JsonPropertyValue -InputObject $container -Name 'env')
        foreach ($entry in $environment) {
            if ((Get-JsonPropertyValue -InputObject $entry -Name 'name') -eq $Name) {
                return Get-JsonPropertyValue -InputObject $entry -Name 'value'
            }
        }
    }

    return $null
}

$deployment = Get-Content -LiteralPath $DeploymentFile -Raw | ConvertFrom-Json
$deploymentProperties = Get-JsonPropertyValue -InputObject $deployment -Name 'properties'
$deploymentState = Get-JsonPropertyValue -InputObject $deploymentProperties -Name 'provisioningState'
if ($deploymentState -ne 'Succeeded') {
    throw "Application deployment state was '$deploymentState'; Container App revisions were not verified."
}

$containerAppNames = @(
    Get-DeploymentOutput -Deployment $deployment -Name 'apiContainerAppName'
    Get-DeploymentOutput -Deployment $deployment -Name 'workerContainerAppName'
)
$apiUrl = Get-DeploymentOutput -Deployment $deployment -Name 'apiUrl'
if ($apiUrl -notmatch '^https://[^/]+$') {
    throw "Deployment output apiUrl was not a valid HTTPS origin: '$apiUrl'."
}
$revisionsReady = $false

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $allReady = $true

    foreach ($containerAppName in $containerAppNames) {
        $app = Invoke-AzJson -Arguments @(
            'containerapp', 'show',
            '--resource-group', $ResourceGroup,
            '--name', $containerAppName,
            '--only-show-errors',
            '--output', 'json'
        )
        $appProperties = Get-JsonPropertyValue -InputObject $app -Name 'properties'
        $provisioningState = Get-JsonPropertyValue -InputObject $appProperties -Name 'provisioningState'
        $latestRevision = [string] (
            Get-JsonPropertyValue -InputObject $appProperties -Name 'latestRevisionName')
        $latestReadyRevision = [string] (
            Get-JsonPropertyValue -InputObject $appProperties -Name 'latestReadyRevisionName')
        $revisions = @(Invoke-AzJson -Arguments @(
            'containerapp', 'revision', 'list',
            '--resource-group', $ResourceGroup,
            '--name', $containerAppName,
            '--only-show-errors',
            '--output', 'json'
        ))
        $activeRevisions = @($revisions | Where-Object {
            (Get-JsonPropertyValue -InputObject $_ -Name 'properties').active -eq $true
        })
        $latestActiveRevision = @($activeRevisions | Where-Object {
            (Get-JsonPropertyValue -InputObject $_ -Name 'name') -eq $latestRevision
        })

        $healthState = $null
        $revisionMarker = $null
        if ($latestActiveRevision.Count -eq 1) {
            $latestProperties = Get-JsonPropertyValue `
                -InputObject $latestActiveRevision[0] `
                -Name 'properties'
            $healthState = Get-JsonPropertyValue -InputObject $latestProperties -Name 'healthState'
            $revisionMarker = Get-ContainerEnvironmentValue `
                -Resource $latestActiveRevision[0] `
                -Name 'CASELEDGER_DEPLOYMENT_REVISION'
        }

        $ready = $provisioningState -eq 'Succeeded' -and
            -not [string]::IsNullOrWhiteSpace($latestRevision) -and
            $latestRevision -eq $latestReadyRevision -and
            $activeRevisions.Count -eq 1 -and
            $latestActiveRevision.Count -eq 1 -and
            $healthState -eq 'Healthy' -and
            $revisionMarker -eq $ExpectedDeploymentRevision

        if (-not $ready) {
            $allReady = $false
            Write-Host "Revision check $attempt/$MaxAttempts for '$containerAppName': provisioning=$provisioningState latest='$latestRevision' ready='$latestReadyRevision' active=$($activeRevisions.Count) health='$healthState' marker='$revisionMarker'."
        }
    }

    if ($allReady) {
        Write-Host "API and worker revisions are uniquely active, healthy, and marked '$ExpectedDeploymentRevision'."
        $revisionsReady = $true
        break
    }

    if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

if (-not $revisionsReady) {
    throw "The API and worker did not become uniquely active and healthy with deployment marker '$ExpectedDeploymentRevision'."
}

$messagingHealthUrl = "$apiUrl/health/messaging"
for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    try {
        $healthResponse = Invoke-RestMethod `
            -Uri $messagingHealthUrl `
            -Method Get `
            -TimeoutSec 10
        $status = Get-JsonPropertyValue -InputObject $healthResponse -Name 'status'
        if ($status -eq 'healthy') {
            Write-Host "API messaging readiness passed on attempt $attempt."
            return
        }
    }
    catch {
        Write-Host "API messaging readiness attempt $attempt was not ready."
    }

    if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

throw "API messaging readiness did not pass: '$messagingHealthUrl'."
