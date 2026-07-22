import { GraphQLError } from 'graphql'
import { requireRole } from './auth.js'
import type { GatewayContext } from './context.js'
import type {
  CaseSeverity,
  CaseStatus,
  CaseSummaryResolvers,
  DueState,
  Resolvers,
} from './generated/resolvers-types.js'
import { DateTimeScalar, UUIDScalar } from './scalars.js'
import { restErrorToGraphQLError } from './rest/errors.js'
import type {
  RestAuditVerification,
  RestCaseDetail,
  RestCaseSummary,
} from './rest/types.js'

const statusToRest = {
  IN_PROGRESS: 'InProgress',
  NEW: 'New',
  RESOLVED: 'Resolved',
} as const

const severityToRest = {
  CRITICAL: 'Critical',
  HIGH: 'High',
  LOW: 'Low',
  MEDIUM: 'Medium',
} as const

const sortFieldToRest = {
  CREATED_AT: 'createdAt',
  DUE_AT: 'dueAt',
  REFERENCE: 'reference',
  SEVERITY: 'severity',
  STATUS: 'status',
  TITLE: 'title',
  UPDATED_AT: 'updatedAt',
} as const

export const resolvers: Resolvers = {
  DateTime: DateTimeScalar,
  UUID: UUIDScalar,
  Query: {
    async cases(_parent, { filter, pagination, sort }, context) {
      const page = pagination?.page ?? 1
      const pageSize = pagination?.pageSize ?? 20
      validatePagination(page, pageSize)
      const search = filter?.search?.trim()
      if (search && search.length > 200) {
        throw badInput('Search must contain no more than 200 characters.', 'filter.search')
      }

      const collection = await translateRestError(async () => await context.rest.listCases({
        page,
        pageSize,
        search: search || undefined,
        severity: filter?.severity ? severityToRest[filter.severity] : undefined,
        sortBy: sortFieldToRest[sort?.field ?? 'UPDATED_AT'],
        sortDirection: (sort?.direction ?? 'DESC').toLowerCase(),
        status: filter?.status ? statusToRest[filter.status] : undefined,
      }))

      return {
        nodes: collection.items,
        pageInfo: {
          hasNextPage: collection.hasNextPage,
          hasPreviousPage: collection.hasPreviousPage,
          page: collection.page,
          pageSize: collection.pageSize,
          totalCount: collection.total,
          totalPages: collection.totalPages,
        },
      }
    },
    async case(_parent, { id }, context) {
      return await translateRestError(async () => await context.loaders.caseDetails.load(id))
    },
    async investigators(_parent, _arguments, context) {
      return await translateRestError(context.loaders.listInvestigators)
    },
  },
  Mutation: {
    async updateCaseStatus(_parent, { input }, context) {
      const updated = await translateRestError(async () => await context.rest.updateCase(
        input.id,
        input.expectedVersion,
        { status: statusToRest[input.status] },
      ))
      primeUpdatedCase(context, updated)
      return { case: updated }
    },
    async assignInvestigator(_parent, { input }, context) {
      requireRole(context.user, 'Admin')
      const updated = await translateRestError(async () => await context.rest.updateCase(
        input.id,
        input.expectedVersion,
        { assigneeId: input.investigatorId ?? null },
      ))
      primeUpdatedCase(context, updated)
      return { case: updated }
    },
  },
  CaseSummary: caseResolvers(),
  Case: {
    ...caseResolvers(),
    events(parent) {
      return parent.activity
    },
    async analytics(parent, _arguments, context) {
      const integrity = await translateRestError(
        async () => await context.loaders.integrity.load(parent.id),
      )
      return buildAnalytics(parent, integrity)
    },
  },
}

function caseResolvers(): CaseSummaryResolvers<GatewayContext, RestCaseSummary> {
  return {
    investigator(
      parent: RestCaseSummary,
      _arguments: unknown,
      context: GatewayContext,
    ) {
      return parent.assigneeId
        ? translateRestError(async () => await context.loaders.investigators.load(parent.assigneeId!))
        : null
    },
    severity(parent: RestCaseSummary): CaseSeverity {
      return parent.severity.toUpperCase() as CaseSeverity
    },
    status(parent: RestCaseSummary): CaseStatus {
      return parent.status === 'InProgress'
        ? 'IN_PROGRESS'
        : parent.status.toUpperCase() as CaseStatus
    },
  }
}

function buildAnalytics(
  item: RestCaseDetail,
  integrity: RestAuditVerification,
) {
  const now = Date.now()
  const ageDays = Math.max(
    0,
    Math.floor((now - Date.parse(item.createdAt)) / 86_400_000),
  )
  return {
    ageDays,
    dueState: dueState(item, now),
    eventCount: item.activity.length,
    evidenceCount: item.evidence.length,
    integrity,
  }
}

function dueState(item: RestCaseDetail, now: number): DueState {
  if (item.status === 'Resolved') {
    return 'RESOLVED'
  }
  if (!item.dueAt) {
    return 'NO_DUE_DATE'
  }
  const dueAt = Date.parse(item.dueAt)
  if (dueAt < now) {
    return 'OVERDUE'
  }
  return dueAt - now <= 2 * 86_400_000 ? 'DUE_SOON' : 'ON_TRACK'
}

function validatePagination(page: number, pageSize: number): void {
  if (page < 1) {
    throw badInput('Page must be 1 or greater.', 'pagination.page')
  }
  if (pageSize < 1 || pageSize > 100) {
    throw badInput('Page size must be between 1 and 100.', 'pagination.pageSize')
  }
}

function badInput(message: string, field: string): GraphQLError {
  return new GraphQLError(message, {
    extensions: {
      code: 'BAD_USER_INPUT',
      field,
    },
  })
}

async function translateRestError<T>(operation: () => Promise<T>): Promise<T> {
  try {
    return await operation()
  } catch (error) {
    throw restErrorToGraphQLError(error)
  }
}

function primeUpdatedCase(
  context: GatewayContext,
  item: RestCaseDetail,
): void {
  context.loaders.caseDetails.clear(item.id).prime(item.id, item)
  context.loaders.integrity.clear(item.id)
}
