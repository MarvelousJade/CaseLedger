import DataLoader from 'dataloader'
import type { CaseLedgerRestClient } from './rest/client.js'
import { RestApiError } from './rest/errors.js'
import type {
  RestAuditVerification,
  RestCaseDetail,
  RestUser,
} from './rest/types.js'

export interface GatewayLoaders {
  caseDetails: DataLoader<string, RestCaseDetail | null>
  integrity: DataLoader<string, RestAuditVerification>
  investigators: DataLoader<string, RestUser | null>
  listInvestigators: () => Promise<RestUser[]>
}

export function createLoaders(rest: CaseLedgerRestClient): GatewayLoaders {
  let investigatorsPromise: Promise<RestUser[]> | null = null
  const listInvestigators = (): Promise<RestUser[]> => {
    investigatorsPromise ??= rest.listUsers()
    return investigatorsPromise
  }

  return {
    caseDetails: new DataLoader<string, RestCaseDetail | null>(
      async (ids) => await Promise.all(ids.map(async (id) => {
        try {
          return await rest.getCase(id)
        } catch (error) {
          if (error instanceof RestApiError && error.status === 404) {
            return null
          }
          throw error
        }
      })),
      { name: 'case-details' },
    ),
    integrity: new DataLoader<string, RestAuditVerification>(
      async (ids) => await Promise.all(ids.map(async (id) => await rest.verifyAudit(id))),
      { name: 'case-integrity' },
    ),
    investigators: new DataLoader<string, RestUser | null>(
      async (ids) => {
        const users = await listInvestigators()
        const byId = new Map(users.map((user) => [user.id.toLowerCase(), user]))
        return ids.map((id) => byId.get(id.toLowerCase()) ?? null)
      },
      { name: 'investigators' },
    ),
    listInvestigators,
  }
}
