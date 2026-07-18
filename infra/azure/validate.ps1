[CmdletBinding()]
param(
    [Parameter()]
    [string] $ResourceGroup,

    [Parameter()]
    [ValidateSet('dev', 'staging', 'prod')]
    [string] $EnvironmentName = 'prod'
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

    $postgresPassword = $env:CASELEDGER_POSTGRES_ADMIN_PASSWORD
    if ([string]::IsNullOrWhiteSpace($postgresPassword) -or $postgresPassword.Length -lt 16) {
        throw 'Set CASELEDGER_POSTGRES_ADMIN_PASSWORD to at least 16 characters in this PowerShell session before cloud validation.'
    }

    $authenticationMode = $env:CASELEDGER_AUTHENTICATION_MODE
    if ($authenticationMode -notin @('Entra', 'Demo', 'DemoAndEntra')) {
        throw 'Set CASELEDGER_AUTHENTICATION_MODE to Entra, Demo, or DemoAndEntra.'
    }

    $demoEnabled = $authenticationMode -in @('Demo', 'DemoAndEntra')
    $entraEnabled = $authenticationMode -in @('Entra', 'DemoAndEntra')
    $seedAdminPassword = $env:CASELEDGER_SEED_ADMIN_PASSWORD
    $seedAnalystPassword = $env:CASELEDGER_SEED_ANALYST_PASSWORD
    if ($demoEnabled -and
        ([string]::IsNullOrEmpty($seedAdminPassword) -or
         $seedAdminPassword.Length -lt 12 -or
         [string]::IsNullOrEmpty($seedAnalystPassword) -or
         $seedAnalystPassword.Length -lt 12)) {
        throw 'Demo authentication requires CASELEDGER_SEED_ADMIN_PASSWORD and CASELEDGER_SEED_ANALYST_PASSWORD of at least 12 characters.'
    }

    $entraAutoProvision = if ([string]::IsNullOrWhiteSpace($env:CASELEDGER_ENTRA_AUTO_PROVISION_ANALYST)) {
        'false'
    }
    else {
        $env:CASELEDGER_ENTRA_AUTO_PROVISION_ANALYST
    }
    if ($entraAutoProvision -cnotin @('true', 'false')) {
        throw 'CASELEDGER_ENTRA_AUTO_PROVISION_ANALYST must be exactly true or false.'
    }
    if (-not $entraEnabled -and $entraAutoProvision -ceq 'true') {
        throw 'Analyst auto-provisioning cannot be enabled while Entra authentication is disabled.'
    }

    if ($entraEnabled) {
        $guidPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
        if ($env:CASELEDGER_ENTRA_TENANT_ID -notmatch $guidPattern -or
            $env:CASELEDGER_ENTRA_CLIENT_ID -notmatch $guidPattern -or
            [string]::IsNullOrEmpty($env:CASELEDGER_ENTRA_CLIENT_SECRET) -or
            $env:CASELEDGER_ENTRA_CLIENT_SECRET.Length -lt 16) {
            throw 'Enabled Entra authentication requires tenant/client GUID variables and a client secret of at least 16 characters.'
        }
        if ($entraAutoProvision -cne 'true' -and
            $env:CASELEDGER_ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID -notmatch $guidPattern) {
            throw 'Entra authentication requires an immutable bootstrap administrator object ID unless Analyst auto-provisioning is explicitly enabled.'
        }
    }

    $validationArguments = @(
        'deployment', 'group', 'validate',
        '--resource-group', $ResourceGroup,
        '--template-file', $templatePath,
        '--parameters', $parameterPath,
        "environmentName=$EnvironmentName"
    )
    if ($EnvironmentName -eq 'prod') {
        $validationArguments += @(
            'postgresSkuName=Standard_D2ds_v5',
            'postgresSkuTier=GeneralPurpose',
            'postgresStorageSizeGb=64',
            'postgresBackupRetentionDays=14',
            'postgresHighAvailabilityMode=Disabled',
            'apiMinReplicas=1',
            'apiMaxReplicas=1',
            'workerMinReplicas=1',
            'workerMaxReplicas=3'
        )
    }
    else {
        $validationArguments += @(
            'postgresSkuName=Standard_B1ms',
            'postgresSkuTier=Burstable',
            'postgresStorageSizeGb=32',
            'postgresBackupRetentionDays=7',
            'postgresHighAvailabilityMode=Disabled',
            'apiMinReplicas=1',
            'apiMaxReplicas=1',
            'workerMinReplicas=1',
            'workerMaxReplicas=1'
        )
    }
    $validationArguments += '--only-show-errors'

    & az @validationArguments

    if ($LASTEXITCODE -ne 0) {
        throw 'Azure resource-group validation failed.'
    }

    Write-Host 'Azure resource-group validation passed.'
}
finally {
    Remove-Item -LiteralPath $compiledPath -Force -ErrorAction SilentlyContinue
}
