[CmdletBinding()]
param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..\powerbi')
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path $ProjectRoot).Path
$report = Join-Path $project 'CaseLedgerAnalytics.Report'
$modelDefinition = Join-Path $project 'CaseLedgerAnalytics.SemanticModel\definition'

$requiredReportFiles = @(
    'definition.pbir'
    'definition\version.json'
    'definition\report.json'
    'definition\pages\pages.json'
)

foreach ($relativePath in $requiredReportFiles) {
    $path = Join-Path $report $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required PBIR file is missing: $path"
    }
}

$jsonFiles = Get-ChildItem -LiteralPath $project -Recurse -Filter '*.json' -File
foreach ($file in $jsonFiles) {
    Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json | Out-Null
}

$storePackage = Get-AppxPackage Microsoft.MicrosoftPowerBIDesktop -ErrorAction SilentlyContinue |
    Select-Object -First 1
$candidateBins = @(
    if ($storePackage) { Join-Path $storePackage.InstallLocation 'bin' }
    'C:\Program Files\Microsoft Power BI Desktop\bin'
    'C:\Program Files\Microsoft Power BI Desktop RS\bin'
)
$powerBiBin = $candidateBins |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Microsoft.PowerBI.Tabular.dll') } |
    Select-Object -First 1

if (-not $powerBiBin) {
    throw 'Power BI Desktop parser assemblies were not found.'
}

$assemblyNames = @(
    'Newtonsoft.Json.dll'
    'Microsoft.PowerBI.Tabular.dll'
    'Microsoft.PowerBI.Tabular.Json.dll'
    'Microsoft.PowerBI.Amo.Core.dll'
    'Microsoft.PowerBI.Amo.dll'
)
$assemblyCache = Join-Path ([IO.Path]::GetTempPath()) 'caseledger-powerbi-validator'
[IO.Directory]::CreateDirectory($assemblyCache) | Out-Null

foreach ($name in $assemblyNames) {
    $source = Join-Path $powerBiBin $name
    $destination = Join-Path $assemblyCache $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required Power BI assembly is missing: $source"
    }

    # Store-app assemblies are encrypted in WindowsApps. Reading and rewriting
    # the bytes produces a loadable, disposable copy without changing the app.
    [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($source))
    [Reflection.Assembly]::LoadFrom($destination) | Out-Null
}

$serializer = [Type]::GetType(
    'Microsoft.AnalysisServices.Tabular.TmdlSerializer, Microsoft.PowerBI.Tabular',
    $true
)
$database = $serializer::DeserializeDatabaseFromFolder($modelDefinition)

[pscustomobject]@{
    JsonFiles  = $jsonFiles.Count
    Database   = $database.Name
    Tables     = $database.Model.Tables.Count
    Expressions = $database.Model.Expressions.Count
    Status     = 'Valid'
}
