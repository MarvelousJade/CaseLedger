# Power BI project

`CaseLedgerAnalytics.pbip` points to an enhanced PBIR report definition and a
TMDL semantic model. The semantic model uses import mode, reads only `mart`
views, defines shared relationships, and contains the governed DAX measures in
the metric catalogue.

The four report page definitions carry the dashboard layout contract and link
to the verified recruiter-facing previews under `../docs/screenshots`. The
checked-in development default points to the deployed Azure SQL server and the
`CaseLedgerAnalytics` database. On refresh, use Database authentication with
the `caseledger_report` user; its connection string is stored in Azure Key Vault
as `caseledger-powerbi-sql-connection`. Other environments should replace the
`SqlServerHost` and `SqlServerDatabase` parameter values before refresh.

PBIR is schema-versioned preview functionality, so Desktop remains the final
validator/author for visual-container metadata. Fully restart Desktop after
external PBIR edits because an already open session does not reload them.

Validate the PBIR JSON structure and deserialize the complete TMDL model with
the parser installed by Power BI Desktop:

```powershell
.\tools\validate_powerbi.ps1
```

After publishing to **My workspace**, configure the Azure SQL cloud connection
and run a verified on-demand refresh from the repository root:

```powershell
.\caseledger-analytics\tools\configure_powerbi_service.ps1
```

The script reads `caseledger-powerbi-sql-connection` directly from Azure Key
Vault, prompts for the organizational Power BI account, keeps credentials only
in process memory, updates the cloud data source through the Power BI REST API,
and restores the original Azure CLI subscription before exiting.

This repository deliberately does not claim that Power BI Desktop was run in
CI. The machine-readable semantic model, DAX, page definitions, screenshot
previews, and PDF export are independently versioned and reviewable.
