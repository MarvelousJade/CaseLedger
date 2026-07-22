import { GraphQLError } from 'graphql'
import { jwtVerify, type JWTPayload } from 'jose'
import type { GatewayConfig } from './config.js'

export interface AuthenticatedUser {
  email: string | null
  id: string
  name: string
  roles: string[]
}

export interface AuthenticatedRequest {
  token: string
  user: AuthenticatedUser
}

export async function authenticateRequest(
  request: Request,
  config: GatewayConfig,
): Promise<AuthenticatedRequest> {
  const authorization = request.headers.get('authorization')
  if (!authorization?.startsWith('Bearer ')) {
    throw new GraphQLError('A bearer access token is required.', {
      extensions: { code: 'UNAUTHENTICATED' },
    })
  }

  const token = authorization.slice('Bearer '.length).trim()
  if (!token) {
    throw new GraphQLError('A bearer access token is required.', {
      extensions: { code: 'UNAUTHENTICATED' },
    })
  }

  try {
    const { payload } = await jwtVerify(
      token,
      new TextEncoder().encode(config.jwtSigningKey),
      {
        algorithms: ['HS256'],
        audience: config.jwtAudience,
        issuer: config.jwtIssuer,
      },
    )

    if (!payload.sub) {
      throw new Error('The token has no subject.')
    }

    return {
      token,
      user: {
        email: stringClaim(payload, 'email'),
        id: payload.sub,
        name: stringClaim(payload, 'name') ?? payload.sub,
        roles: roleClaims(payload),
      },
    }
  } catch {
    throw new GraphQLError('The bearer access token is invalid or expired.', {
      extensions: { code: 'UNAUTHENTICATED' },
    })
  }
}

export function requireRole(user: AuthenticatedUser, requiredRole: string): void {
  if (!user.roles.some((role) => role.toLowerCase() === requiredRole.toLowerCase())) {
    throw new GraphQLError(`${requiredRole} authorization is required.`, {
      extensions: {
        code: 'FORBIDDEN',
        requiredRole,
      },
    })
  }
}

function stringClaim(payload: JWTPayload, name: string): string | null {
  const value = payload[name]
  return typeof value === 'string' ? value : null
}

function roleClaims(payload: JWTPayload): string[] {
  const value = payload.role ?? payload.roles
  if (typeof value === 'string') {
    return [value]
  }

  return Array.isArray(value)
    ? value.filter((role): role is string => typeof role === 'string')
    : []
}
