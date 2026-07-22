import { MockedProvider } from '@apollo/client/testing/react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { GatewayCasesDocument } from '../graphql/generated/graphql'
import { GatewayCasesPage } from './GatewayCasesPage'

describe('GatewayCasesPage', () => {
  it('renders a typed GraphQL case result and opens the existing case detail', async () => {
    const browser = userEvent.setup()
    const onSelectCase = vi.fn()
    render(
      <MockedProvider mocks={[{
        request: {
          query: GatewayCasesDocument,
          variables: {
            filter: { search: null, severity: null, status: null },
            pagination: { page: 1, pageSize: 10 },
            sort: { direction: 'DESC', field: 'UPDATED_AT' },
          },
        },
        result: {
          data: {
            cases: {
              nodes: [{
                category: 'Access Control',
                dueAt: '2026-07-30T12:00:00Z',
                id: '20000000-0000-0000-0000-000000000001',
                investigator: {
                  id: '10000000-0000-0000-0000-000000000002',
                  name: 'Avery Singh',
                },
                reference: 'CL-2026-001',
                severity: 'CRITICAL',
                status: 'IN_PROGRESS',
                title: 'Review access policy anomaly',
                updatedAt: '2026-07-21T12:00:00Z',
                version: '30000000-0000-0000-0000-000000000001',
              }],
              pageInfo: {
                hasNextPage: false,
                hasPreviousPage: false,
                page: 1,
                pageSize: 10,
                totalCount: 1,
                totalPages: 1,
              },
            },
          },
        },
      }]}>
        <GatewayCasesPage onSelectCase={onSelectCase} />
      </MockedProvider>,
    )

    const caseButton = await screen.findByRole('button', {
      name: 'Open Review access policy anomaly',
    })
    expect(screen.getByText('Avery Singh')).toBeVisible()
    expect(screen.getAllByText('In Progress')).toHaveLength(2)

    await browser.click(caseButton)

    expect(onSelectCase).toHaveBeenCalledWith(
      '20000000-0000-0000-0000-000000000001',
    )
  })
})
