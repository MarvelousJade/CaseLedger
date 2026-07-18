[CmdletBinding()]
param(
    [string]$TenantId = '3d6b17d1-e64e-4b1a-b63d-b6c328ac5b22',
    [string]$KeyVaultName = 'clanalytics-dev-kv-i6uwu',
    [string]$KeyVaultSecretName = 'caseledger-powerbi-sql-connection',
    [string]$SemanticModelName = 'CaseLedgerAnalytics',
    [int]$RefreshTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

function Invoke-AzCli {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: $($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine)
}

function Invoke-PowerBIRest {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PATCH')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body
    )

    $request = @{
        Method      = $Method
        Uri         = "https://api.powerbi.com/v1.0/myorg/$Path"
        Headers     = @{ Authorization = "Bearer $script:PowerBIAccessToken" }
        ContentType = 'application/json'
    }

    if ($null -ne $Body) {
        $request.Body = $Body | ConvertTo-Json -Depth 12 -Compress
    }

    Invoke-RestMethod @request
}

function ConvertTo-NormalizedSqlServerName {
    param([Parameter(Mandatory)][string]$Value)

    return (($Value -replace '^tcp:', '') -replace ',\d+$', '').Trim().ToLowerInvariant()
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Install it and run az login before using this script.'
}

$originalAccount = Invoke-AzCli -Arguments @('account', 'show', '--output', 'json') |
    ConvertFrom-Json
$originalSubscriptionId = [string]$originalAccount.id

Write-Host "Reading the Power BI SQL credential from Key Vault '$KeyVaultName'..."
$connectionString = [string](Invoke-AzCli -Arguments @(
        'keyvault', 'secret', 'show',
        '--vault-name', $KeyVaultName,
        '--name', $KeyVaultSecretName,
        '--query', 'value',
        '--output', 'tsv'
    ))

if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "Key Vault secret '$KeyVaultSecretName' is empty."
}

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
$connectionString = $null
$sqlUsername = $builder.UserID
$sqlPassword = $builder.Password
$sqlServer = $builder.DataSource
$sqlDatabase = $builder.InitialCatalog
$builder.Clear()

if ([string]::IsNullOrWhiteSpace($sqlUsername) -or
    [string]::IsNullOrWhiteSpace($sqlPassword) -or
    [string]::IsNullOrWhiteSpace($sqlServer) -or
    [string]::IsNullOrWhiteSpace($sqlDatabase)) {
    throw "Key Vault secret '$KeyVaultSecretName' isn't a complete SQL connection string."
}

try {
    Write-Host 'A Microsoft sign-in window will open.'
    Write-Host 'Sign in with the organizational account that published the report.'
    Invoke-AzCli -Arguments @(
        'login',
        '--tenant', $TenantId,
        '--allow-no-subscriptions',
        '--scope', 'https://analysis.windows.net/powerbi/api/.default',
        '--output', 'none'
    ) | Out-Null

    $script:PowerBIAccessToken = [string](Invoke-AzCli -Arguments @(
            'account', 'get-access-token',
            '--tenant', $TenantId,
            '--resource', 'https://analysis.windows.net/powerbi/api',
            '--query', 'accessToken',
            '--output', 'tsv'
        ))

    $datasets = (Invoke-PowerBIRest -Method GET -Path 'datasets').value
    $matchingDatasets = @($datasets | Where-Object {
            $_.name -eq $SemanticModelName -or $_.name -eq "$SemanticModelName.pbip"
        })

    if ($matchingDatasets.Count -ne 1) {
        $availableNames = ($datasets.name | Sort-Object) -join ', '
        throw "Expected one semantic model named '$SemanticModelName', found $($matchingDatasets.Count). Available models: $availableNames"
    }

    $dataset = $matchingDatasets[0]
    Write-Host "Configuring semantic model '$($dataset.name)'..."
    $datasources = (Invoke-PowerBIRest -Method GET -Path "datasets/$($dataset.id)/datasources").value
    $normalizedSqlServer = ConvertTo-NormalizedSqlServerName -Value $sqlServer
    $matchingSources = @($datasources | Where-Object {
            $_.datasourceType -eq 'Sql' -and
            (ConvertTo-NormalizedSqlServerName -Value $_.connectionDetails.server) -eq $normalizedSqlServer -and
            $_.connectionDetails.database -eq $sqlDatabase
        })

    if ($matchingSources.Count -eq 0) {
        throw "No Azure SQL cloud connection matched server '$sqlServer' and database '$sqlDatabase'."
    }

    $credentialData = @{
        credentialData = @(
            @{ name = 'username'; value = $sqlUsername },
            @{ name = 'password'; value = $sqlPassword }
        )
    } | ConvertTo-Json -Depth 5 -Compress

    $credentialBody = @{
        credentialDetails = @{
            credentialType               = 'Basic'
            credentials                  = $credentialData
            encryptedConnection          = 'Encrypted'
            encryptionAlgorithm          = 'None'
            privacyLevel                 = 'Organizational'
            useEndUserOAuth2Credentials  = $false
        }
    }

    $uniqueSources = $matchingSources | Sort-Object gatewayId, datasourceId -Unique
    foreach ($source in $uniqueSources) {
        if ([string]::IsNullOrWhiteSpace([string]$source.gatewayId) -or
            [string]::IsNullOrWhiteSpace([string]$source.datasourceId)) {
            throw 'The published model has no bound cloud connection. Open its settings once in Power BI Service, then rerun this script.'
        }

        $updateRequest = @{
            Method = 'PATCH'
            Path   = "gateways/$($source.gatewayId)/datasources/$($source.datasourceId)"
            Body   = $credentialBody
        }
        Invoke-PowerBIRest @updateRequest | Out-Null
    }

    $sqlPassword = $null
    $credentialData = $null
    $credentialBody = $null

    Write-Host 'Credentials updated. Starting an on-demand refresh...'
    Invoke-PowerBIRest -Method POST -Path "datasets/$($dataset.id)/refreshes" -Body @{
        notifyOption = 'NoNotification'
    } | Out-Null

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($RefreshTimeoutSeconds)
    do {
        Start-Sleep -Seconds 5
        $refreshPath = "datasets/$($dataset.id)/refreshes?`$top=1"
        $latestRefresh = (Invoke-PowerBIRest -Method GET -Path $refreshPath).value |
            Select-Object -First 1
        Write-Host "Refresh status: $($latestRefresh.status)"
    } while ($latestRefresh.status -in @('Unknown', 'InProgress') -and
        [DateTimeOffset]::UtcNow -lt $deadline)

    if ($latestRefresh.status -ne 'Completed') {
        $detail = $latestRefresh.serviceExceptionJson
        throw "Power BI refresh ended with status '$($latestRefresh.status)'. $detail"
    }

    Write-Host 'Power BI credentials are configured and the refresh completed successfully.' -ForegroundColor Green
}
finally {
    $script:PowerBIAccessToken = $null
    $sqlPassword = $null
    if (-not [string]::IsNullOrWhiteSpace($originalSubscriptionId)) {
        Invoke-AzCli -Arguments @(
            'account', 'set', '--subscription', $originalSubscriptionId
        ) | Out-Null
    }
}
