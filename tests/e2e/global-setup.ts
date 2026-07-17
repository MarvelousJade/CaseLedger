import { setTimeout as delay } from 'node:timers/promises'
import { runCompose, stopE2eStack } from './support/compose'

const healthUrl = 'http://127.0.0.1:5151/health'

async function waitForApplication() {
  const deadline = Date.now() + 90_000

  while (Date.now() < deadline) {
    try {
      const response = await fetch(healthUrl)
      if (response.ok) {
        return
      }
    } catch {
      // The application container can be running before Kestrel starts listening.
    }

    await delay(1_000)
  }

  throw new Error(`CaseLedger did not become healthy at ${healthUrl} within 90 seconds.`)
}

export default async function globalSetup() {
  try {
    stopE2eStack(true)
  } catch {
    // A missing previous stack is the expected first-run state.
  }

  try {
    runCompose(['up', '--build', '--detach', '--wait', '--wait-timeout', '180'])
    await waitForApplication()
  } catch (error) {
    try {
      stopE2eStack(true)
    } catch {
      // Preserve the startup error, which has the useful failure context.
    }
    throw error
  }
}
