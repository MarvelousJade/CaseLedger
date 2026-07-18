using './main.bicep'

param location = 'canadacentral'
param namePrefix = 'clanalytics'
param environmentName = 'dev'
param sqlAdministratorLogin = 'caseledgeradmin'
param sqlAdministratorPassword = readEnvironmentVariable('CASELEDGER_SQL_ADMIN_PASSWORD')
