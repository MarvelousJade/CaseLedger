import { readFile, writeFile } from 'node:fs/promises'

const generatedTypes = new URL(
  '../src/generated/resolvers-types.ts',
  import.meta.url,
)
const contents = await readFile(generatedTypes, 'utf8')
await writeFile(generatedTypes, `${contents.trimEnd()}\n`)
