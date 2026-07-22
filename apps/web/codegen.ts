import type { CodegenConfig } from '@graphql-codegen/cli'

const config: CodegenConfig = {
  schema: '../graphql-gateway/schema.graphql',
  documents: ['src/graphql/**/*.graphql'],
  generates: {
    './src/graphql/generated/': {
      preset: 'client',
      config: {
        scalars: {
          DateTime: 'string',
          UUID: 'string',
        },
        useTypeImports: true,
      },
      presetConfig: {
        fragmentMasking: false,
      },
    },
  },
}

export default config
