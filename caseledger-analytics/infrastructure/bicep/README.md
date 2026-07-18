# Azure deployment

The Bicep template provisions Azure SQL Database, Azure Data Factory, and a
soft-delete/purge-protected Key Vault. It intentionally does not create secret
values. Add the PostgreSQL read-only and Azure SQL ETL connection strings as
Key Vault secrets after deployment, then publish the versioned definitions in
`../../adf` with your normal ADF release process.

```powershell
$env:CASELEDGER_SQL_ADMIN_PASSWORD = Read-Host `
  -Prompt 'SQL administrator password' -AsSecureString `
  | ConvertFrom-SecureString -AsPlainText
az deployment group what-if `
  --resource-group <analytics-resource-group> `
  --parameters main.bicepparam
Remove-Item Env:CASELEDGER_SQL_ADMIN_PASSWORD
```

Review cost, networking, and the SQL firewall before applying. The checked-in
template permits connections from Azure services so ADF can reach Azure SQL.
Production deployments should replace that broad rule with an ADF managed
virtual network and private endpoints under the organization's network policy.
