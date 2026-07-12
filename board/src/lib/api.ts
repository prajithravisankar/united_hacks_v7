// Typed client for the BOYS backend, through the single origin (nginx): /api → ledger, /control → engine.
// Relative URLs so the same build works in dev (Vite proxy) and behind the tunnel/deploy. Money is always
// integer cents. Body-less POSTs send an empty body (Content-Length: 0) as the backend requires.

const REFEREE_ID = '2' // the seeded referee user; the judge plays both learner and referee

export type MilestoneState = 'pending' | 'pending_verification' | 'cleared' | 'failed'
export type CommitmentState =
  | 'draft' | 'active' | 'pending_verification' | 'milestone_cleared'
  | 'riding' | 'cashed_out' | 'succeeded' | 'failed' | 'settled'

export interface Charity { charityId: number; name: string }
export interface Milestone {
  milestoneId: number
  ordinal: number
  description: string
  targetMetric: string
  dueDate: string
  state: MilestoneState
}
export interface TimelineEvent { fromState: string; toState: string; command: string; occurredAt: string }
export interface Goal {
  commitmentId: number
  state: CommitmentState
  deadline: string
  milestones: Milestone[]
  timeline: TimelineEvent[]
}
export interface CreateMilestoneInput { description: string; targetMetric: string; dueDate: string }
export interface CreateGoalInput {
  goalText: string
  stakeCents: number
  charityId: number
  driveMode: 'AUTO' | 'USER'
  deadline: string
  milestones: CreateMilestoneInput[]
}
export type CreateGoalResult =
  | { accepted: true; commitmentId: number; aiVerdict: string; degraded: boolean }
  | { accepted: false; reasoning: string; suggestedRewrite: string }

export interface ProofResult {
  commitmentState: CommitmentState
  milestoneState: MilestoneState
  ai: { status: 'Supported' | 'Insufficient' | 'PendingAi'; degraded: boolean; reasoning: string }
  resubmissionCount: number
}
export interface DecisionResult { commitmentState: CommitmentState; milestoneState: MilestoneState; wasApplied: boolean }
export interface Receipt {
  type: 'CashOut' | 'Success' | 'Failure'
  principalCents: number
  navCents: number
  gainCents: number
  carryCents: number
  charityCents: number
  bonusCents: number
  takeHomeCents: number
}
export interface Valuation {
  degraded: boolean
  navCents: number | null
  principalCents: number
  gainCents?: number
  carryPreviewCents?: number
  takeHomeCents?: number
}
export interface ReplayState { running: boolean; speed: number; position: number; date: string; done: boolean }

class ApiError extends Error {
  code: string
  status: number
  constructor(code: string, message: string, status: number) {
    super(message)
    this.code = code
    this.status = status
  }
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, init)
  const text = await res.text()
  const body = text ? JSON.parse(text) : {}
  if (!res.ok) {
    const err = body?.error ?? {}
    throw new ApiError(err.code ?? 'error', err.message ?? res.statusText, res.status)
  }
  return body as T
}

const post = (json?: unknown, headers?: Record<string, string>): RequestInit => ({
  method: 'POST',
  headers: { ...(json ? { 'Content-Type': 'application/json' } : {}), ...headers },
  body: json ? JSON.stringify(json) : '',
})

export const api = {
  charities: () => req<Charity[]>('/api/charities'),
  pool: () => req<{ committedPeople: number; poolCents: number }>('/api/pool'),

  async createGoal(input: CreateGoalInput): Promise<CreateGoalResult> {
    const res = await fetch('/api/goals', post(input))
    const body = await res.json()
    if (res.status === 201) return { accepted: true, ...body }
    if (res.status === 422 && body?.error?.code === 'goal_rejected') {
      return { accepted: false, reasoning: body.error.message, suggestedRewrite: body.suggestedRewrite ?? '' }
    }
    throw new ApiError(body?.error?.code ?? 'error', body?.error?.message ?? 'could not create goal', res.status)
  },

  goal: (id: number) => req<Goal>(`/api/goals/${id}`),
  activate: (id: number) => req<{ commitmentId: number; state: CommitmentState }>(`/api/goals/${id}/activate`, post()),
  proof: (id: number, p: { milestoneId: number; claim: string; evidenceBase64: string; mime: string; idempotencyKey: string }) =>
    req<ProofResult>(`/api/goals/${id}/proof`, post(p)),
  decide: (milestoneId: number, decision: 'approve' | 'reject', idempotencyKey: string) =>
    req<DecisionResult>(`/api/milestones/${milestoneId}/decision`, post({ decision, idempotencyKey }, { 'X-User-Id': REFEREE_ID })),
  ride: (id: number) => req<{ state: CommitmentState }>(`/api/goals/${id}/ride`, post()),
  cashout: (id: number) => req<{ state: CommitmentState }>(`/api/goals/${id}/cashout`, post()),
  succeed: (id: number) => req<{ state: CommitmentState }>(`/api/goals/${id}/succeed`, post()),
  settle: (id: number) => req<Receipt>(`/api/goals/${id}/settle`, post()),
  receipt: (id: number) => req<Receipt>(`/api/goals/${id}/receipt`),
  valuation: (id: number) => req<Valuation>(`/api/goals/${id}/valuation`),

  replay: {
    start: (speed = 6) => req<ReplayState>(`/control/start?speed=${speed}`, post()),
    restart: (speed = 6) => req<ReplayState>(`/control/restart?speed=${speed}`, post()),
    pause: () => req<ReplayState>('/control/pause', post()),
    speed: (speed: number) => req<ReplayState>(`/control/speed?speed=${speed}`, post()),
    state: () => req<ReplayState>('/control/state'),
  },
}

export const dollars = (cents: number | null | undefined) =>
  cents == null ? '—' : `$${(cents / 100).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`

export { ApiError }
