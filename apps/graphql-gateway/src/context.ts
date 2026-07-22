import { randomUUID } from 'node:crypto'
import { authenticateRequest, type AuthenticatedUser } from './auth.js'
import type { GatewayConfig } from './config.js'
import { createLoaders, type GatewayLoaders } from './loaders.js'
import { CaseLedgerRestClient, type FetchImplementation } from './rest/client.js'

export interface GatewayContext {
  accessToken: string
  loaders: GatewayLoaders
  requestId: string
  rest: CaseLedgerRestClient
  user: AuthenticatedUser
}

export async function createGatewayContext(
  request: Request,
  config: GatewayConfig,
  fetchImplementation: FetchImplementation = fetch,
): Promise<GatewayContext> {
  const authentication = await authenticateRequest(request, config)
  const requestId = request.headers.get('x-request-id') ?? randomUUID()
  const rest = new CaseLedgerRestClient(
    config.apiBaseUrl,
    authentication.token,
    requestId,
    fetchImplementation,
  )

  return {
    accessToken: authentication.token,
    loaders: createLoaders(rest),
    requestId,
    rest,
    user: authentication.user,
  }
}
