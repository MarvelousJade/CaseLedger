# Power BI project

`CaseLedgerAnalytics.pbip` points to an enhanced PBIR report definition and a
TMDL semantic model. The semantic model uses import mode, reads only `mart`
views, defines shared relationships, and contains the governed DAX measures in
the metric catalogue.

The four report page definitions carry the dashboard layout contract and link
to the verified recruiter-facing previews under `../docs/screenshots`. Open the
PBIP in a current Power BI Desktop release, set `SqlServerHost` and
`SqlServerDatabase`, refresh, and bind visuals using the page annotations and
previews. PBIR is schema-versioned preview functionality, so Desktop remains
the final validator/author for visual-container metadata.

This repository deliberately does not claim that Power BI Desktop was run in
CI. The machine-readable semantic model, DAX, page definitions, screenshot
previews, and PDF export are independently versioned and reviewable.
