import type { CodegenConfig } from '@graphql-codegen/cli'

const config: CodegenConfig = {
  schema: './schema.graphql',
  generates: {
    './src/generated/resolvers-types.ts': {
      plugins: ['typescript', 'typescript-resolvers'],
      config: {
        contextType: '../context.js#GatewayContext',
        enumsAsTypes: true,
        mappers: {
          Case: '../rest/types.js#RestCaseDetail',
          CaseSummary: '../rest/types.js#RestCaseSummary',
          Investigator: '../rest/types.js#RestUser',
        },
        scalars: {
          DateTime: { input: 'string', output: 'string' },
          UUID: { input: 'string', output: 'string' },
        },
        useIndexSignature: true,
      },
    },
  },
}

export default config
