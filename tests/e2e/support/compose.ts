import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import path from 'node:path'

const projectName = 'caseledger-e2e'
const repositoryRoot = path.resolve(process.cwd())
const composeFile = path.join(repositoryRoot, 'compose.e2e.yaml')
const windowsDocker = String.raw`C:\Program Files\Docker\Docker\resources\bin\docker.exe`

function dockerExecutable() {
  if (process.env.CASELEDGER_DOCKER_PATH) {
    return process.env.CASELEDGER_DOCKER_PATH
  }

  if (process.platform === 'win32' && existsSync(windowsDocker)) {
    return windowsDocker
  }

  return 'docker'
}

export function runCompose(args: string[], quiet = false) {
  if (!existsSync(composeFile)) {
    throw new Error(`E2E Compose file was not found at ${composeFile}. Run Playwright from the repository root.`)
  }

  const environment = { ...process.env }
  if (process.platform === 'win32' && existsSync(windowsDocker)) {
    environment.PATH = [path.dirname(windowsDocker), environment.PATH]
      .filter(Boolean)
      .join(path.delimiter)
  }

  const output = execFileSync(
    dockerExecutable(),
    ['compose', '--project-name', projectName, '--file', composeFile, ...args],
    {
      cwd: repositoryRoot,
      env: environment,
      encoding: 'utf8',
      stdio: quiet ? 'pipe' : 'inherit',
    },
  )

  return typeof output === 'string' ? output : ''
}

export function stopE2eStack(quiet = false) {
  runCompose(['down', '--volumes', '--remove-orphans', '--timeout', '10'], quiet)
}
