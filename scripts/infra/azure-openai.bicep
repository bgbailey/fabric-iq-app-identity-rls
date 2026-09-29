targetScope = 'resourceGroup'

@description('Azure OpenAI account name. Must be globally unique.')
param accountName string

@description('Azure region for the Azure OpenAI account.')
param location string = resourceGroup().location

@description('Deployment name for the chat model.')
param deploymentName string = 'gpt-5-4'

@description('Model name to deploy.')
param modelName string = 'gpt-5.4'

@description('Model version to deploy. Confirm current availability in the target region before deployment.')
param modelVersion string = '2026-09-01'

@description('GlobalStandard capacity units for the model deployment.')
@minValue(1)
param capacity int = 1

@description('Optional principal object id to grant Cognitive Services OpenAI User on the account.')
param openAiUserPrincipalId string = ''

var openAiUserRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')

resource account 'Microsoft.CognitiveServices/accounts@2026-07-01' = {
  name: accountName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2026-07-01' = {
  parent: account
  name: deploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

resource openAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(openAiUserPrincipalId)) {
  name: guid(account.id, openAiUserPrincipalId, openAiUserRoleDefinitionId)
  scope: account
  properties: {
    roleDefinitionId: openAiUserRoleDefinitionId
    principalId: openAiUserPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output endpoint string = account.properties.endpoint
output accountId string = account.id
output deployment string = deployment.name
