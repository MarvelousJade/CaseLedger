targetScope = 'resourceGroup'

@description('Exact Service Bus namespace name returned by the foundation deployment.')
@minLength(6)
@maxLength(50)
param serviceBusNamespaceName string

@description('Exact API managed-identity name returned by the foundation deployment.')
@minLength(3)
@maxLength(128)
param apiIdentityName string

@description('Exact worker managed-identity name returned by the foundation deployment.')
@minLength(3)
@maxLength(128)
param workerIdentityName string

var requestMessageSubject = 'caseledger.audit.verification.requested.v1'
var resultMessageSubject = 'caseledger.audit.verification.result.v1'
var auditTopicName = 'caseledger-audit'
var workerRequestSubscriptionName = 'caseledger-audit-worker'
var apiResultSubscriptionName = 'caseledger-api-results'
var workerRequestRuleName = 'verification-requests-v1'
var apiResultRuleName = 'verification-results-v1'
var serviceBusDataSenderRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
)
var serviceBusDataReceiverRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '090c5cfd-751d-490a-894a-3ce6f1109419'
)

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName
}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: apiIdentityName
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: workerIdentityName
}

// This narrowly scoped deployment always moves publishers and consumers to a
// fail-closed state before touching child subscriptions or rules. The checked-in
// enforcer activates them only after it has removed every unexpected rule.
resource auditTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBusNamespace
  name: auditTopicName
  properties: {
    defaultMessageTimeToLive: 'P7D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    enableExpress: false
    enablePartitioning: serviceBusNamespace.sku.name == 'Standard'
    maxMessageSizeInKilobytes: serviceBusNamespace.sku.name == 'Premium' ? 1024 : 256
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: true
    status: 'Disabled'
    supportOrdering: false
  }
}

resource workerRequestSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: auditTopic
  name: workerRequestSubscriptionName
  properties: {
    deadLetteringOnFilterEvaluationExceptions: true
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    enableBatchedOperations: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    requiresSession: false
    status: 'Disabled'
  }
}

resource workerRequestFilter 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: workerRequestSubscription
  name: workerRequestRuleName
  properties: {
    correlationFilter: {
      label: requestMessageSubject
    }
    filterType: 'CorrelationFilter'
  }
}

resource apiResultSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: auditTopic
  name: apiResultSubscriptionName
  properties: {
    deadLetteringOnFilterEvaluationExceptions: true
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
    enableBatchedOperations: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    requiresSession: false
    status: 'Disabled'
  }
}

resource apiResultFilter 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: apiResultSubscription
  name: apiResultRuleName
  properties: {
    correlationFilter: {
      label: resultMessageSubject
    }
    filterType: 'CorrelationFilter'
  }
}

resource apiServiceBusSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(auditTopic.id, apiIdentity.id, serviceBusDataSenderRoleId)
  scope: auditTopic
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataSenderRoleId
  }
}

resource apiServiceBusReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(auditTopic.id, apiIdentity.id, serviceBusDataReceiverRoleId)
  scope: auditTopic
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataReceiverRoleId
  }
}

resource workerServiceBusSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(auditTopic.id, workerIdentity.id, serviceBusDataSenderRoleId)
  scope: auditTopic
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataSenderRoleId
  }
}

resource workerServiceBusReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(auditTopic.id, workerIdentity.id, serviceBusDataReceiverRoleId)
  scope: auditTopic
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataReceiverRoleId
  }
}

output serviceBusNamespaceHost string = '${serviceBusNamespace.name}.servicebus.windows.net'
output serviceBusTopic string = auditTopic.name
output workerRequestSubscription string = workerRequestSubscription.name
output apiResultSubscription string = apiResultSubscription.name
