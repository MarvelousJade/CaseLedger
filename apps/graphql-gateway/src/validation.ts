import {
  GraphQLError,
  Kind,
  type DocumentNode,
  type FragmentDefinitionNode,
  type SelectionSetNode,
  type ValidationRule,
} from 'graphql'

export function maxDepthRule(maxDepth: number): ValidationRule {
  return (context) => ({
    Document(document: DocumentNode) {
      const fragments = new Map<string, FragmentDefinitionNode>()
      for (const definition of document.definitions) {
        if (definition.kind === Kind.FRAGMENT_DEFINITION) {
          fragments.set(definition.name.value, definition)
        }
      }

      for (const definition of document.definitions) {
        if (definition.kind !== Kind.OPERATION_DEFINITION) {
          continue
        }
        const depth = selectionDepth(definition.selectionSet, fragments, new Set(), 0)
        if (depth > maxDepth) {
          context.reportError(new GraphQLError(
            `The operation depth of ${depth} exceeds the maximum of ${maxDepth}.`,
            {
              extensions: {
                code: 'GRAPHQL_VALIDATION_FAILED',
                maxDepth,
              },
              nodes: definition,
            },
          ))
        }
      }
    },
  })
}

function selectionDepth(
  selectionSet: SelectionSetNode,
  fragments: ReadonlyMap<string, FragmentDefinitionNode>,
  visitedFragments: ReadonlySet<string>,
  currentDepth: number,
): number {
  let greatestDepth = currentDepth
  for (const selection of selectionSet.selections) {
    if (selection.kind === Kind.FIELD) {
      if (selection.name.value.startsWith('__')) {
        continue
      }
      greatestDepth = Math.max(
        greatestDepth,
        selection.selectionSet
          ? selectionDepth(
              selection.selectionSet,
              fragments,
              visitedFragments,
              currentDepth + 1,
            )
          : currentDepth + 1,
      )
      continue
    }

    if (selection.kind === Kind.INLINE_FRAGMENT) {
      greatestDepth = Math.max(
        greatestDepth,
        selectionDepth(selection.selectionSet, fragments, visitedFragments, currentDepth),
      )
      continue
    }

    if (!visitedFragments.has(selection.name.value)) {
      const fragment = fragments.get(selection.name.value)
      if (fragment) {
        const nextVisited = new Set(visitedFragments)
        nextVisited.add(selection.name.value)
        greatestDepth = Math.max(
          greatestDepth,
          selectionDepth(fragment.selectionSet, fragments, nextVisited, currentDepth),
        )
      }
    }
  }
  return greatestDepth
}
