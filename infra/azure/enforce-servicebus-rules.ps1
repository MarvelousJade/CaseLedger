[CmdletBinding(DefaultParameterSetName = 'ByDeploymentName')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroup,

    [Parameter(Mandatory, ParameterSetName = 'ByDeploymentName')]
    [ValidateNotNullOrEmpty()]
    [string] $DeploymentName,

    [Parameter(Mandatory, ParameterSetName = 'ByDeploymentFile')]
    [ValidateNotNullOrEmpty()]
    [string] $DeploymentFile,

    [Parameter()]
    [switch] $VerifyOnly,

    [Parameter()]
    [ValidateRange(1, 60)]
    [int] $MaxAttempts = 12,

    [Parameter()]
    [ValidateRange(0, 30)]
    [int] $RetryDelaySeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workerRuleName = 'verification-requests-v1'
$workerRuleLabel = 'caseledger.audit.verification.requested.v1'
$apiRuleName = 'verification-results-v1'
$apiRuleLabel = 'caseledger.audit.verification.result.v1'

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

function Get-RequiredDeploymentOutput {
    param(
        [Parameter(Mandatory)]
        [object] $Outputs,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $entry = Get-JsonPropertyValue -InputObject $Outputs -Name $Name
    if ($null -eq $entry) {
        throw "Deployment output '$Name' was not found."
    }

    $value = Get-JsonPropertyValue -InputObject $entry -Name 'value'
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string] $value)) {
        throw "Deployment output '$Name' is empty."
    }

    return [string] $value
}

function Get-ServiceBusRules {
    param(
        [Parameter(Mandatory)]
        [string] $SubscriptionName
    )

    $arguments = @(
        'servicebus', 'topic', 'subscription', 'rule', 'list',
        '--resource-group', $ResourceGroup,
        '--namespace-name', $namespaceName,
        '--topic-name', $topicName,
        '--subscription-name', $SubscriptionName,
        '--only-show-errors',
        '--output', 'json'
    )

    return @(Invoke-AzJson -Arguments $arguments)
}

function Get-TopicStatus {
    $topic = Invoke-AzJson -Arguments @(
        'servicebus', 'topic', 'show',
        '--resource-group', $ResourceGroup,
        '--namespace-name', $namespaceName,
        '--name', $topicName,
        '--only-show-errors',
        '--output', 'json'
    )
    $status = Get-JsonPropertyValue -InputObject $topic -Name 'status'
    if ($null -eq $status) {
        return ''
    }

    return [string] $status
}

function Set-TopicStatus {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Active', 'Disabled')]
        [string] $Status
    )

    & az servicebus topic update `
        --resource-group $ResourceGroup `
        --namespace-name $namespaceName `
        --name $topicName `
        --status $Status `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set Service Bus topic '$topicName' to '$Status'."
    }
}

function Wait-TopicStatus {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Active', 'Disabled')]
        [string] $ExpectedStatus
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $observedStatus = Get-TopicStatus
        if ($observedStatus -eq $ExpectedStatus) {
            return
        }
        if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    throw "Service Bus topic '$topicName' did not reach '$ExpectedStatus'."
}

function Get-SubscriptionStatus {
    param(
        [Parameter(Mandatory)]
        [string] $SubscriptionName
    )

    $subscription = Invoke-AzJson -Arguments @(
        'servicebus', 'topic', 'subscription', 'show',
        '--resource-group', $ResourceGroup,
        '--namespace-name', $namespaceName,
        '--topic-name', $topicName,
        '--name', $SubscriptionName,
        '--only-show-errors',
        '--output', 'json'
    )
    $status = Get-JsonPropertyValue -InputObject $subscription -Name 'status'
    if ($null -eq $status) {
        return ''
    }

    return [string] $status
}

function Set-SubscriptionStatus {
    param(
        [Parameter(Mandatory)]
        [string] $SubscriptionName,

        [Parameter(Mandatory)]
        [ValidateSet('Active', 'Disabled')]
        [string] $Status
    )

    & az servicebus topic subscription update `
        --resource-group $ResourceGroup `
        --namespace-name $namespaceName `
        --topic-name $topicName `
        --name $SubscriptionName `
        --status $Status `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set Service Bus subscription '$SubscriptionName' to '$Status'."
    }
}

function Wait-SubscriptionStatus {
    param(
        [Parameter(Mandatory)]
        [string] $SubscriptionName,

        [Parameter(Mandatory)]
        [ValidateSet('Active', 'Disabled')]
        [string] $ExpectedStatus
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $observedStatus = Get-SubscriptionStatus -SubscriptionName $SubscriptionName
        if ($observedStatus -eq $ExpectedStatus) {
            return
        }
        if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    throw "Service Bus subscription '$SubscriptionName' did not reach '$ExpectedStatus'."
}

function Get-RuleField {
    param(
        [Parameter(Mandatory)]
        [object] $Rule,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $value = Get-JsonPropertyValue -InputObject $Rule -Name $Name
    if ($null -eq $value) {
        return ''
    }

    return [string] $value
}

function Get-RuleLabel {
    param(
        [Parameter(Mandatory)]
        [object] $Rule
    )

    $filter = Get-JsonPropertyValue -InputObject $Rule -Name 'correlationFilter'
    if ($null -eq $filter) {
        return ''
    }

    $label = Get-JsonPropertyValue -InputObject $filter -Name 'label'
    if ($null -eq $label) {
        return ''
    }

    return [string] $label
}

function Test-IsEmptyValue {
    param(
        [Parameter()]
        [AllowNull()]
        [object] $Value
    )

    if ($null -eq $Value) {
        return $true
    }
    if ($Value -is [string]) {
        return [string]::IsNullOrEmpty($Value)
    }
    if ($Value -is [System.Collections.IDictionary]) {
        return $Value.Count -eq 0
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        return @($Value).Count -eq 0
    }
    if ($Value -isnot [pscustomobject]) {
        return $false
    }

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -eq 0) {
        return $true
    }
    foreach ($property in $properties) {
        if (-not (Test-IsEmptyValue -Value $property.Value)) {
            return $false
        }
    }

    return $true
}

function Test-ExactRule {
    param(
        [Parameter(Mandatory)]
        [object] $Rule,

        [Parameter(Mandatory)]
        [string] $ExpectedName,

        [Parameter(Mandatory)]
        [string] $ExpectedLabel
    )

    if ((Get-RuleField -Rule $Rule -Name 'name') -ne $ExpectedName -or
        (Get-RuleField -Rule $Rule -Name 'filterType') -ne 'CorrelationFilter') {
        return $false
    }

    $filter = Get-JsonPropertyValue -InputObject $Rule -Name 'correlationFilter'
    if ($null -eq $filter) {
        return $false
    }

    $hasExpectedLabel = $false
    foreach ($property in @($filter.PSObject.Properties)) {
        if ($property.Name -eq 'label') {
            if ([string] $property.Value -ne $ExpectedLabel) {
                return $false
            }
            $hasExpectedLabel = $true
        }
        elseif (-not (Test-IsEmptyValue -Value $property.Value)) {
            return $false
        }
    }
    if (-not $hasExpectedLabel) {
        return $false
    }

    $action = Get-JsonPropertyValue -InputObject $Rule -Name 'action'
    $sqlFilter = Get-JsonPropertyValue -InputObject $Rule -Name 'sqlFilter'
    return (Test-IsEmptyValue -Value $action) -and
        (Test-IsEmptyValue -Value $sqlFilter)
}

function Remove-ServiceBusRule {
    param(
        [Parameter(Mandatory)]
        [string] $SubscriptionName,

        [Parameter(Mandatory)]
        [string] $RuleName
    )

    $arguments = @(
        'servicebus', 'topic', 'subscription', 'rule', 'delete',
        '--resource-group', $ResourceGroup,
        '--namespace-name', $namespaceName,
        '--topic-name', $topicName,
        '--subscription-name', $SubscriptionName,
        '--name', $RuleName,
        '--only-show-errors',
        '--output', 'none'
    )

    & az @arguments
    if ($LASTEXITCODE -eq 0) {
        return
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $remainingRules = @(Get-ServiceBusRules -SubscriptionName $SubscriptionName)
        $ruleStillExists = $remainingRules | Where-Object {
            (Get-RuleField -Rule $_ -Name 'name') -eq $RuleName
        }
        if (-not $ruleStillExists) {
            Write-Host "Rule '$RuleName' was already absent from subscription '$SubscriptionName'."
            return
        }
        if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    throw "Could not remove unexpected rule '$RuleName' from subscription '$SubscriptionName'."
}

function Assert-ExactRule {
    param(
        [Parameter(Mandatory)]
        [string] $SubscriptionName,

        [Parameter(Mandatory)]
        [string] $ExpectedName,

        [Parameter(Mandatory)]
        [string] $ExpectedLabel,

        [Parameter()]
        [switch] $ReadOnly
    )

    $rules = @()
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $rules = @(Get-ServiceBusRules -SubscriptionName $SubscriptionName)
        $unexpectedRules = @($rules | Where-Object {
            (Get-RuleField -Rule $_ -Name 'name') -ne $ExpectedName
        })
        if (-not $ReadOnly) {
            foreach ($unexpectedRule in $unexpectedRules) {
                $unexpectedName = Get-RuleField -Rule $unexpectedRule -Name 'name'
                if ([string]::IsNullOrWhiteSpace($unexpectedName)) {
                    throw "Subscription '$SubscriptionName' returned an unnamed rule; refusing cleanup."
                }
                Remove-ServiceBusRule `
                    -SubscriptionName $SubscriptionName `
                    -RuleName $unexpectedName
                Write-Host "Removed unexpected rule '$unexpectedName' from subscription '$SubscriptionName'."
            }
        }

        $rules = @(Get-ServiceBusRules -SubscriptionName $SubscriptionName)
        if ($rules.Count -eq 1 -and
            (Test-ExactRule `
                -Rule $rules[0] `
                -ExpectedName $ExpectedName `
                -ExpectedLabel $ExpectedLabel)) {
            Write-Host "Service Bus subscription '$SubscriptionName' has the expected correlation rule."
            return
        }

        if ($ReadOnly) {
            break
        }

        if ($attempt -lt $MaxAttempts -and $RetryDelaySeconds -gt 0) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    $observed = @($rules | ForEach-Object {
        $name = Get-RuleField -Rule $_ -Name 'name'
        $type = Get-RuleField -Rule $_ -Name 'filterType'
        $label = Get-RuleLabel -Rule $_
        "${name}/${type}/${label}"
    }) -join ', '
    if ([string]::IsNullOrWhiteSpace($observed)) {
        $observed = '<none>'
    }

    throw "Service Bus subscription '$SubscriptionName' does not have exactly '$ExpectedName/CorrelationFilter/$ExpectedLabel'. Observed: $observed"
}

function Enter-FailClosedState {
    param(
        [Parameter(Mandatory)]
        [string[]] $SubscriptionNames
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    try {
        Set-TopicStatus -Status Disabled
    }
    catch {
        $failures.Add("topic disable request failed: $($_.Exception.Message)")
    }
    foreach ($subscriptionName in $SubscriptionNames) {
        try {
            Set-SubscriptionStatus -SubscriptionName $subscriptionName -Status Disabled
        }
        catch {
            $failures.Add("subscription '$subscriptionName' disable request failed: $($_.Exception.Message)")
        }
    }

    try {
        Wait-TopicStatus -ExpectedStatus Disabled
    }
    catch {
        $failures.Add("topic disabled state was not confirmed: $($_.Exception.Message)")
    }
    foreach ($subscriptionName in $SubscriptionNames) {
        try {
            Wait-SubscriptionStatus -SubscriptionName $subscriptionName -ExpectedStatus Disabled
        }
        catch {
            $failures.Add("subscription '$subscriptionName' disabled state was not confirmed: $($_.Exception.Message)")
        }
    }

    if ($failures.Count -gt 0) {
        throw "Could not confirm fail-closed Service Bus state. $($failures -join ' | ')"
    }
}

if ($PSCmdlet.ParameterSetName -eq 'ByDeploymentName') {
    $deploymentProperties = Invoke-AzJson -Arguments @(
        'deployment', 'group', 'show',
        '--resource-group', $ResourceGroup,
        '--name', $DeploymentName,
        '--query', 'properties',
        '--only-show-errors',
        '--output', 'json'
    )
}
else {
    $resolvedDeploymentFile = Resolve-Path -LiteralPath $DeploymentFile -ErrorAction Stop
    $deployment = Get-Content -LiteralPath $resolvedDeploymentFile -Raw | ConvertFrom-Json
    $deploymentProperties = Get-JsonPropertyValue -InputObject $deployment -Name 'properties'
    if ($null -eq $deploymentProperties) {
        throw "Deployment file '$DeploymentFile' does not contain properties."
    }
}

$provisioningState = Get-JsonPropertyValue -InputObject $deploymentProperties -Name 'provisioningState'
if ([string] $provisioningState -ne 'Succeeded') {
    $observedState = if ($null -eq $provisioningState) { '<missing>' } else { [string] $provisioningState }
    throw "Refusing Service Bus activation because the ARM deployment state is '$observedState', not 'Succeeded'."
}
$deploymentOutputs = Get-JsonPropertyValue -InputObject $deploymentProperties -Name 'outputs'
if ($null -eq $deploymentOutputs) {
    throw 'Deployment outputs are missing.'
}

$namespaceHost = Get-RequiredDeploymentOutput -Outputs $deploymentOutputs -Name 'serviceBusNamespaceHost'
if ($namespaceHost -notmatch '^([a-z0-9-]+)\.servicebus\.windows\.net$') {
    throw "Deployment output serviceBusNamespaceHost is invalid: '$namespaceHost'."
}
$namespaceName = $Matches[1]
$topicName = Get-RequiredDeploymentOutput -Outputs $deploymentOutputs -Name 'serviceBusTopic'
$workerSubscription = Get-RequiredDeploymentOutput -Outputs $deploymentOutputs -Name 'workerRequestSubscription'
$apiSubscription = Get-RequiredDeploymentOutput -Outputs $deploymentOutputs -Name 'apiResultSubscription'

$managedSubscriptions = @($workerSubscription, $apiSubscription)
if ($VerifyOnly) {
    Wait-TopicStatus -ExpectedStatus Active
    foreach ($subscriptionName in $managedSubscriptions) {
        Wait-SubscriptionStatus `
            -SubscriptionName $subscriptionName `
            -ExpectedStatus Active
    }
    Assert-ExactRule `
        -SubscriptionName $workerSubscription `
        -ExpectedName $workerRuleName `
        -ExpectedLabel $workerRuleLabel `
        -ReadOnly
    Assert-ExactRule `
        -SubscriptionName $apiSubscription `
        -ExpectedName $apiRuleName `
        -ExpectedLabel $apiRuleLabel `
        -ReadOnly

    Write-Host 'Service Bus topic and subscription routing were verified exact and active without suspension.'
    return
}

try {
    Enter-FailClosedState -SubscriptionNames $managedSubscriptions

    Assert-ExactRule `
        -SubscriptionName $workerSubscription `
        -ExpectedName $workerRuleName `
        -ExpectedLabel $workerRuleLabel
    Assert-ExactRule `
        -SubscriptionName $apiSubscription `
        -ExpectedName $apiRuleName `
        -ExpectedLabel $apiRuleLabel

    foreach ($subscriptionName in $managedSubscriptions) {
        Set-SubscriptionStatus -SubscriptionName $subscriptionName -Status Active
    }
    foreach ($subscriptionName in $managedSubscriptions) {
        Wait-SubscriptionStatus -SubscriptionName $subscriptionName -ExpectedStatus Active
    }
    Set-TopicStatus -Status Active
    Wait-TopicStatus -ExpectedStatus Active
}
catch {
    $enforcementFailure = $_
    try {
        Enter-FailClosedState -SubscriptionNames $managedSubscriptions
    }
    catch {
        throw "Service Bus rule enforcement failed: $($enforcementFailure.Exception.Message) Fail-closed recovery also failed: $($_.Exception.Message)"
    }
    throw $enforcementFailure
}

Write-Host 'Service Bus topic and subscription routing are exact and active.'
