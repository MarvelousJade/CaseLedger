using './main.bicep'

// Set these in the current shell before validation or deployment. They are never
// stored in this repository or emitted as deployment outputs.
param postgresAdministratorPassword = readEnvironmentVariable('CASELEDGER_POSTGRES_ADMIN_PASSWORD')
param authenticationMode = readEnvironmentVariable('CASELEDGER_AUTHENTICATION_MODE')
param seedAdministratorPassword = readEnvironmentVariable('CASELEDGER_SEED_ADMIN_PASSWORD', '')
param seedAnalystPassword = readEnvironmentVariable('CASELEDGER_SEED_ANALYST_PASSWORD', '')
param showDemoCredentials = bool(readEnvironmentVariable('CASELEDGER_SHOW_DEMO_CREDENTIALS', 'false'))
param entraTenantId = readEnvironmentVariable('CASELEDGER_ENTRA_TENANT_ID', '')
param entraClientId = readEnvironmentVariable('CASELEDGER_ENTRA_CLIENT_ID', '')
param entraClientSecret = readEnvironmentVariable('CASELEDGER_ENTRA_CLIENT_SECRET', '')
param entraAutoProvisionAnalyst = bool(readEnvironmentVariable('CASELEDGER_ENTRA_AUTO_PROVISION_ANALYST', 'false'))
param entraBootstrapAdministratorObjectId = readEnvironmentVariable('CASELEDGER_ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID', '')
