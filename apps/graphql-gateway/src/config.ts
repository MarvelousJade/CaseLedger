export interface GatewayConfig {
  apiBaseUrl: string
  corsOrigins: string[]
  host: string
  isProduction: boolean
  jwtAudience: string
  jwtIssuer: string
  jwtSigningKey: string
  port: number
}

const developmentSigningKey = 'caseledger-development-signing-key-change-me'

export function loadConfig(
  environment: NodeJS.ProcessEnv = process.env,
): GatewayConfig {
  const isProduction = environment.NODE_ENV === 'production'
  const jwtSigningKey = environment.JWT_SIGNING_KEY ??
    (isProduction ? '' : developmentSigningKey)
  if (jwtSigningKey.length < 32) {
    throw new Error('JWT_SIGNING_KEY must contain at least 32 characters.')
  }

  const port = Number.parseInt(environment.PORT ?? '5155', 10)
  if (!Number.isInteger(port) || port < 1 || port > 65_535) {
    throw new Error('PORT must be an integer from 1 to 65535.')
  }

  const apiBaseUrl = new URL(environment.CASELEDGER_API_URL ?? 'http://localhost:5150')
  if (!['http:', 'https:'].includes(apiBaseUrl.protocol)) {
    throw new Error('CASELEDGER_API_URL must use HTTP or HTTPS.')
  }

  return {
    apiBaseUrl: apiBaseUrl.toString().replace(/\/$/, ''),
    corsOrigins: (environment.CORS_ORIGINS ??
      'http://localhost:5150,http://localhost:5173,http://127.0.0.1:5173')
      .split(',')
      .map((origin) => origin.trim())
      .filter(Boolean),
    host: environment.HOST ?? '127.0.0.1',
    isProduction,
    jwtAudience: environment.JWT_AUDIENCE ?? 'CaseLedger.GraphQL',
    jwtIssuer: environment.JWT_ISSUER ?? 'CaseLedger.Api',
    jwtSigningKey,
    port,
  }
}
