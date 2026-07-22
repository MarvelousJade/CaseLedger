import { GraphQLError } from 'graphql'
import type { RestProblemDetails } from './types.js'

export class RestApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: RestProblemDetails | null,
  ) {
    super(problem?.detail ?? problem?.title ?? `CaseLedger API returned HTTP ${status}.`)
    this.name = 'RestApiError'
  }
}

export function restErrorToGraphQLError(error: unknown): GraphQLError {
  if (error instanceof GraphQLError) {
    return error
  }

  if (!(error instanceof RestApiError)) {
    return new GraphQLError('The CaseLedger API request failed.', {
      extensions: { code: 'UPSTREAM_UNAVAILABLE' },
    })
  }

  const code = error.status === 400
    ? 'BAD_USER_INPUT'
    : error.status === 401
      ? 'UNAUTHENTICATED'
      : error.status === 403
        ? 'FORBIDDEN'
        : error.status === 404
          ? 'NOT_FOUND'
          : error.status === 409 || error.status === 412 || error.status === 428
            ? 'CONFLICT'
            : error.status === 429
              ? 'RATE_LIMITED'
              : error.status >= 500
                ? 'UPSTREAM_UNAVAILABLE'
                : 'UPSTREAM_ERROR'

  return new GraphQLError(error.message, {
    extensions: {
      code,
      fieldErrors: error.problem?.errors,
      traceId: error.problem?.traceId,
      upstream: { status: error.status },
    },
  })
}
