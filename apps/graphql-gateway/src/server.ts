import { readFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import { useValidationRule } from '@envelop/core'
import { createSchema, createYoga } from 'graphql-yoga'
import type { GatewayConfig } from './config.js'
import { createGatewayContext } from './context.js'
import { resolvers } from './resolvers.js'
import type { FetchImplementation } from './rest/client.js'
import { maxDepthRule } from './validation.js'

export interface GatewayDependencies {
  config: GatewayConfig
  fetchImplementation?: FetchImplementation
}

export function createGateway({
  config,
  fetchImplementation = fetch,
}: GatewayDependencies) {
  const typeDefs = readFileSync(
    new URL('../schema.graphql', import.meta.url),
    'utf8',
  )

  return createYoga({
    context: async ({ request }) => await createGatewayContext(
      request,
      config,
      fetchImplementation,
    ),
    cors: {
      allowedHeaders: ['authorization', 'content-type', 'x-request-id'],
      credentials: false,
      methods: ['GET', 'POST', 'OPTIONS'],
      origin: config.corsOrigins,
    },
    graphiql: !config.isProduction,
    graphqlEndpoint: '/graphql',
    healthCheckEndpoint: '/health',
    logging: config.isProduction ? 'info' : 'debug',
    maskedErrors: config.isProduction,
    plugins: [useValidationRule(maxDepthRule(10))],
    schema: createSchema({ resolvers, typeDefs }),
  })
}

export async function startGateway(
  dependencies: GatewayDependencies,
): Promise<Server> {
  const yoga = createGateway(dependencies)
  const server = createServer(yoga)
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject)
    server.listen(
      dependencies.config.port,
      dependencies.config.host,
      () => {
        server.off('error', reject)
        resolve()
      },
    )
  })
  return server
}
