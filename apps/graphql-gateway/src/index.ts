import { loadConfig } from './config.js'
import { startGateway } from './server.js'

const config = loadConfig()
const server = await startGateway({ config })

console.log(
  JSON.stringify({
    apiBaseUrl: config.apiBaseUrl,
    event: 'gateway.started',
    graphqlUrl: `http://${config.host}:${config.port}/graphql`,
  }),
)

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.once(signal, () => {
    server.close((error) => {
      if (error) {
        console.error(JSON.stringify({ event: 'gateway.shutdown_failed' }))
        process.exitCode = 1
      }
    })
  })
}
