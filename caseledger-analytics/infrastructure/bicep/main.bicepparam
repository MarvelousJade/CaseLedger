using './main.bicep'

param location = 'canadacentral'
param namePrefix = 'clanalytics'
param environmentName = 'dev'
param sqlAdministratorLogin = 'caseledgeradmin'
// Supply sqlAdministratorPassword securely at deployment time; never commit it here.
