using './main.bicep'

// Set these in the current shell before validation or deployment. They are never
// stored in this repository or emitted as deployment outputs.
param postgresAdministratorPassword = readEnvironmentVariable('CASELEDGER_POSTGRES_ADMIN_PASSWORD')
param seedAdministratorPassword = readEnvironmentVariable('CASELEDGER_SEED_ADMIN_PASSWORD')

