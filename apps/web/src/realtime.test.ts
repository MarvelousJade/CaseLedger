import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => {
  const connection = {
    on: vi.fn(),
    off: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    state: 'Connected',
  }
  const builder = {
    withUrl: vi.fn(),
    withAutomaticReconnect: vi.fn(),
    configureLogging: vi.fn(),
    build: vi.fn(),
  }
  builder.withUrl.mockReturnValue(builder)
  builder.withAutomaticReconnect.mockReturnValue(builder)
  builder.configureLogging.mockReturnValue(builder)
  builder.build.mockReturnValue(connection)
  return { builder, connection }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl = mocks.builder.withUrl
    withAutomaticReconnect = mocks.builder.withAutomaticReconnect
    configureLogging = mocks.builder.configureLogging
    build = mocks.builder.build
  },
  HubConnectionState: { Connected: 'Connected' },
  LogLevel: { Warning: 3 },
}))

import { subscribeToCaseUpdates } from './realtime'

const caseId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'

beforeEach(() => {
  vi.clearAllMocks()
  mocks.builder.withUrl.mockReturnValue(mocks.builder)
  mocks.builder.withAutomaticReconnect.mockReturnValue(mocks.builder)
  mocks.builder.configureLogging.mockReturnValue(mocks.builder)
  mocks.builder.build.mockReturnValue(mocks.connection)
  mocks.connection.start.mockResolvedValue(undefined)
  mocks.connection.stop.mockResolvedValue(undefined)
  mocks.connection.invoke.mockResolvedValue(undefined)
  mocks.connection.state = 'Connected'
})

describe('case update subscription', () => {
  it('subscribes again after reconnect and unsubscribes before cleanup', async () => {
    const onUpdate = vi.fn()
    const stop = await subscribeToCaseUpdates(caseId, onUpdate)

    expect(mocks.builder.withUrl).toHaveBeenCalledWith('/hubs/cases', { withCredentials: true })
    expect(mocks.connection.on).toHaveBeenCalledWith('VerificationUpdated', onUpdate)
    expect(mocks.connection.invoke).toHaveBeenCalledWith('SubscribeCase', caseId)

    const onReconnected = mocks.connection.onreconnected.mock.calls[0][0]
    await onReconnected()
    expect(mocks.connection.invoke).toHaveBeenLastCalledWith('SubscribeCase', caseId)

    await stop()
    expect(mocks.connection.off).toHaveBeenCalledWith('VerificationUpdated', onUpdate)
    expect(mocks.connection.invoke).toHaveBeenLastCalledWith('UnsubscribeCase', caseId)
    expect(mocks.connection.stop).toHaveBeenCalledOnce()
  })

  it('stops the connection when the initial case subscription fails', async () => {
    mocks.connection.invoke.mockRejectedValueOnce(new Error('Case not found'))

    await expect(subscribeToCaseUpdates(caseId, vi.fn())).rejects.toThrow('Case not found')

    expect(mocks.connection.stop).toHaveBeenCalledOnce()
  })
})
