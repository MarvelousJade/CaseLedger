# CaseLedger audit verifier

This dependency-free Node.js 24 tool verifies a CaseLedger audit export without
contacting the API. It checks contiguous sequence numbers, every `previousHash`
link, and each stored lowercase SHA-256 hash.

## Run

```powershell
cd tools/audit-verifier
npm run verify -- path/to/audit-export.json
```

The input can be either the exported object with an `events` array or the raw
event array. Event fields may be camelCase or PascalCase. For each event, the
tool treats `canonicalData` as an exact opaque string and recomputes:

```text
SHA-256(previousHash + "\n" + canonicalData)
```

A valid export prints the event count and chain head. Invalid JSON, malformed
events, broken links, and changed hashes print a useful error and exit nonzero.

## Test

```powershell
npm test
```
