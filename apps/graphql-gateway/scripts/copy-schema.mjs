import { copyFile, mkdir } from 'node:fs/promises'

await mkdir(new URL('../dist/', import.meta.url), { recursive: true })
await copyFile(
  new URL('../schema.graphql', import.meta.url),
  new URL('../dist/schema.graphql', import.meta.url),
)
