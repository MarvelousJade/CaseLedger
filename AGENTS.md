## Git workflow

- After completing and validating each coherent, meaningful unit of work, create a Git commit.
- Use a concise, imperative commit subject that describes the change.
- Never mention AI, Codex, ChatGPT, or automated assistance in commit messages or commit metadata.
- Do not include unrelated user changes in a commit.

## End-of-session cleanup

- Delete unused code and imports.
- Remove commented-out implementation blocks while preserving explanatory comments.
- Merge duplicate helpers when a shared abstraction keeps the contracts clear.
- Run the relevant linters, builds, and tests after cleanup.

## Secrets

- Never commit `.env*` files except placeholder-only `.env.example` templates.
- Never put live credentials in examples, tests, documentation, or source files.
- If a credential may have been committed, report it for immediate provider-side rotation; deleting the file is not sufficient.
