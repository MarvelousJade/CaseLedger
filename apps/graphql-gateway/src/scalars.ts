import { GraphQLError, GraphQLScalarType, Kind } from 'graphql'

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export const UUIDScalar = new GraphQLScalarType<string, string>({
  name: 'UUID',
  description: 'A canonical UUID string.',
  serialize: validateUuid,
  parseValue: validateUuid,
  parseLiteral(node) {
    if (node.kind !== Kind.STRING) {
      throw badScalar('UUID values must be strings.')
    }
    return validateUuid(node.value)
  },
})

export const DateTimeScalar = new GraphQLScalarType<string, string>({
  name: 'DateTime',
  description: 'An ISO-8601 UTC timestamp.',
  serialize: validateDateTime,
  parseValue: validateDateTime,
  parseLiteral(node) {
    if (node.kind !== Kind.STRING) {
      throw badScalar('DateTime values must be strings.')
    }
    return validateDateTime(node.value)
  },
})

function validateUuid(value: unknown): string {
  if (typeof value !== 'string' || !uuidPattern.test(value)) {
    throw badScalar('The value must be a valid UUID.')
  }
  return value.toLowerCase()
}

function validateDateTime(value: unknown): string {
  if (typeof value !== 'string' || Number.isNaN(Date.parse(value))) {
    throw badScalar('The value must be a valid ISO-8601 timestamp.')
  }
  return value
}

function badScalar(message: string): GraphQLError {
  return new GraphQLError(message, {
    extensions: { code: 'BAD_USER_INPUT' },
  })
}
