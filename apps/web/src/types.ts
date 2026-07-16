export type CaseStatus = 'New' | 'InProgress' | 'Resolved' | string

export type CaseSeverity = 'Low' | 'Medium' | 'High' | 'Critical' | string

export interface User {
  id: string
  name: string
  email: string
  role: string
}

export interface Evidence {
  id: string
  fileName: string
  sizeBytes: number
  mediaType: string
  sha256: string
  addedByName: string
  createdAt: string
}

export interface Activity {
  id: string
  eventType: string
  description: string
  actorName: string
  createdAt: string
}

export interface CaseItem {
  id: string
  reference: string
  title: string
  summary: string
  status: CaseStatus
  severity: CaseSeverity
  category: string
  assigneeId?: string
  assigneeName?: string
  createdByName: string
  createdAt: string
  updatedAt: string
  dueAt?: string
  tags: string[]
  evidence: Evidence[]
  activity: Activity[]
}

export interface CaseCollection {
  items: CaseItem[]
  total: number
}

export interface DashboardData {
  totalCases: number
  openCases: number
  criticalCases: number
  resolvedCases: number
  integrityStatus: string
  recentCases: CaseItem[]
}

export interface IntegrityResult {
  valid: boolean
  checkedEvents: number
  brokenAt?: string
}

export interface CreateCaseInput {
  title: string
  summary: string
  severity: CaseSeverity
  category: string
  assigneeId?: string
  dueAt?: string
  tags: string[]
}
