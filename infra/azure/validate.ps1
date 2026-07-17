[CmdletBinding()]
param(
    [Parameter()]
    [string] $ResourceGroup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'main.bicep'
$parameterPath = Join-Path $PSScriptRoot 'main.bicepparam'
$compiledPath = Join-Path ([System.IO.Path]::GetTempPath()) "caseledger-azure-$PID.json"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI was not found. Install it from https://learn.microsoft.com/cli/azure/install-azure-cli, then reopen PowerShell.'
}

try {
    & az bicep build --file $templatePath --outfile $compiledPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Bicep compilation failed.'
    }

    Write-Host 'Bicep compilation passed.'

    if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
        Write-Host 'Cloud validation skipped. Pass -ResourceGroup <name> to validate against Azure.'
        exit 0
    }

    if ([string]::IsNullOrWhiteSpace($env:CASELEDGER_POSTGRES_ADMIN_PASSWORD)) {
        throw 'Set CASELEDGER_POSTGRES_ADMIN_PASSWORD in this PowerShell session before cloud validation.'
    }

    $authenticationMode = $env:CASELEDGER_AUTHENTICATION_MODE
    if ($authenticationMode -notin @('Entra', 'Demo', 'DemoAndEntra')) {
        throw 'Set CASELEDGER_AUTHENTICATION_MODE to Entra, Demo, or DemoAndEntra.'
    }

    if ($authenticationMode -in @('Demo', 'DemoAndEntra') -and
        ([string]::IsNullOrWhiteSpace($env:CASELEDGER_SEED_ADMIN_PASSWORD) -or
         [string]::IsNullOrWhiteSpace($env:CASELEDGER_SEED_ANALYST_PASSWORD))) {
        throw 'Demo authentication requires CASELEDGER_SEED_ADMIN_PASSWORD and CASELEDGER_SEED_ANALYST_PASSWORD.'
    }

    if ($authenticationMode -in @('Entra', 'DemoAndEntra') -and
        ([string]::IsNullOrWhiteSpace($env:CASELEDGER_ENTRA_TENANT_ID) -or
         [string]::IsNullOrWhiteSpace($env:CASELEDGER_ENTRA_CLIENT_ID) -or
         [string]::IsNullOrWhiteSpace($env:CASELEDGER_ENTRA_CLIENT_SECRET))) {
        throw 'Entra authentication requires CASELEDGER_ENTRA_TENANT_ID, CASELEDGER_ENTRA_CLIENT_ID, and CASELEDGER_ENTRA_CLIENT_SECRET.'
    }

    & az deployment group validate `
        --resource-group $ResourceGroup `
        --template-file $templatePath `
        --parameters $parameterPath `
        --only-show-errors

    if ($LASTEXITCODE -ne 0) {
        throw 'Azure resource-group validation failed.'
    }

    Write-Host 'Azure resource-group validation passed.'
}
finally {
    Remove-Item -LiteralPath $compiledPath -Force -ErrorAction SilentlyContinue
}
