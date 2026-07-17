import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { VerificationUpdated } from './types'

export async function subscribeToCaseUpdates(
  caseId: string,
  onVerificationUpdated: (update: VerificationUpdated) => void,
) {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/cases', { withCredentials: true })
    .withAutomaticReconnect([0, 2_000, 10_000, 30_000])
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('VerificationUpdated', onVerificationUpdated)
  connection.onreconnected(async () => {
    await connection.invoke('SubscribeCase', caseId)
  })

  try {
    await connection.start()
    await connection.invoke('SubscribeCase', caseId)
  } catch (error) {
    connection.off('VerificationUpdated', onVerificationUpdated)
    await connection.stop().catch(() => undefined)
    throw error
  }

  return async () => {
    connection.off('VerificationUpdated', onVerificationUpdated)
    if (connection.state === HubConnectionState.Connected) {
      await connection.invoke('UnsubscribeCase', caseId).catch(() => undefined)
    }
    await connection.stop().catch(() => undefined)
  }
}
