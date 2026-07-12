import { motion } from 'framer-motion'
import { ArrowLeft, Pause, Play, RotateCcw, Target, Trophy, Wallet } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { LiveNavChart } from '../components/LiveNavChart'
import { api, dollars } from '../lib/api'
import { useLiveFund } from '../hooks/useLiveFund'

const PRINCIPAL = 10000

export function Demo() {
  const fund = useLiveFund(1)
  const [speed, setSpeed] = useState(6)
  const [busy, setBusy] = useState(false)

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    try {
      await fn()
    } catch (e) {
      console.error(e)
    } finally {
      setBusy(false)
    }
  }
  const pickSpeed = (s: number) => {
    setSpeed(s)
    if (fund.running) void api.replay.speed(s)
  }

  const gain = fund.nav != null ? fund.nav - PRINCIPAL : 0
  const gainPct = fund.nav != null ? (gain / PRINCIPAL) * 100 : 0

  return (
    <div className="relative min-h-screen bg-ink text-fg">
      <div aria-hidden className="pointer-events-none fixed inset-0 bg-grid opacity-50" />
      <div aria-hidden className="pointer-events-none fixed -top-40 left-1/2 h-[460px] w-[820px] -translate-x-1/2 rounded-full bg-brand/15 blur-[130px]" />

      <header className="sticky top-0 z-40 border-b border-line/60 bg-ink/70 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between px-6">
          <Link to="/" className="flex items-center gap-2 text-sm text-fg-dim transition-colors hover:text-fg">
            <ArrowLeft className="h-4 w-4" /> <span className="font-display font-semibold text-fg">BOYS</span>
            <span className="text-fg-mute">/ demo</span>
          </Link>
          <StatusBadge connected={fund.connected} status={fund.status} />
        </div>
      </header>

      <main className="relative z-10 mx-auto max-w-6xl px-6 py-10">
        {/* the fund — live */}
        <div className="glow-brand overflow-hidden rounded-3xl border border-line bg-gradient-to-b from-surface to-ink">
          <div className="flex flex-wrap items-end justify-between gap-4 px-6 pt-6">
            <div>
              <div className="text-sm text-fg-dim">Your $100 · riding the fund</div>
              <div className="mt-1 flex items-baseline gap-3">
                <span className="font-display text-4xl font-semibold">{dollars(fund.nav ?? PRINCIPAL)}</span>
                <span className={`text-sm font-semibold ${gain >= 0 ? 'text-brand-2' : 'text-amber-400'}`}>
                  {gain >= 0 ? '+' : ''}{gainPct.toFixed(1)}%
                </span>
              </div>
              <div className="mt-1 text-xs text-fg-mute">{fund.date ? `sim date ${fund.date}` : 'press play to start the fund'}</div>
            </div>
            <div className="flex items-center gap-2">
              {!fund.running ? (
                <Ctl onClick={() => run(() => api.replay.restart(speed))} disabled={busy} primary>
                  <Play className="h-4 w-4" /> {fund.position > 0 ? 'Restart' : 'Start'} the fund
                </Ctl>
              ) : (
                <Ctl onClick={() => run(() => api.replay.pause())} disabled={busy}>
                  <Pause className="h-4 w-4" /> Pause
                </Ctl>
              )}
              {fund.running && (
                <Ctl onClick={() => run(() => api.replay.restart(speed))} disabled={busy}>
                  <RotateCcw className="h-4 w-4" />
                </Ctl>
              )}
              <div className="ml-1 flex overflow-hidden rounded-full border border-line">
                {[1, 6, 30].map((s) => (
                  <button
                    key={s}
                    onClick={() => pickSpeed(s)}
                    className={`px-3 py-1.5 text-xs font-medium transition-colors ${
                      speed === s ? 'bg-brand text-ink' : 'text-fg-dim hover:text-fg'
                    }`}
                  >
                    {s}×
                  </button>
                ))}
              </div>
            </div>
          </div>

          <div className="relative mt-4 h-72">
            <LiveNavChart history={fund.history} className="h-full w-full" />
            {fund.history.length < 2 && (
              <div className="absolute inset-0 grid place-items-center text-sm text-fg-mute">
                <span>The fund is idle — press <span className="text-brand">Start the fund</span>.</span>
              </div>
            )}
          </div>
        </div>

        {/* the journey — wired next */}
        <div className="mt-10">
          <h2 className="font-display text-xl font-semibold">Your journey</h2>
          <p className="mt-1 text-sm text-fg-dim">Create a goal, prove your milestones, and settle to the cent — landing next.</p>
          <div className="mt-5 grid gap-4 md:grid-cols-3">
            {[
              { icon: Target, t: 'Set a goal', b: 'Stake $100 through the AI gate and escrow it.' },
              { icon: Trophy, t: 'Prove & referee', b: 'Submit evidence, get an AI verdict, decide as referee.' },
              { icon: Wallet, t: 'Settle', b: 'Cash out, ride, or miss — a receipt to the cent.' },
            ].map((s, i) => (
              <motion.div
                key={s.t}
                initial={{ opacity: 0, y: 16 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: 0.05 * i }}
                className="rounded-2xl border border-line bg-surface/40 p-6"
              >
                <s.icon className="h-5 w-5 text-brand" />
                <div className="mt-3 font-semibold">{s.t}</div>
                <p className="mt-1 text-sm text-fg-dim">{s.b}</p>
                <div className="mt-3 text-xs text-fg-mute">coming next</div>
              </motion.div>
            ))}
          </div>
        </div>
      </main>
    </div>
  )
}

function Ctl({ children, onClick, disabled, primary }: { children: React.ReactNode; onClick: () => void; disabled?: boolean; primary?: boolean }) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className={`inline-flex items-center gap-2 rounded-full px-4 py-2 text-sm font-semibold transition-all disabled:opacity-50 ${
        primary
          ? 'bg-brand text-ink hover:bg-brand-2 shadow-[0_8px_30px_-8px_rgba(16,185,129,0.6)]'
          : 'border border-line bg-white/[0.02] text-fg hover:bg-white/[0.06]'
      }`}
    >
      {children}
    </button>
  )
}

function StatusBadge({ connected, status }: { connected: boolean; status: 'healthy' | 'degraded' }) {
  const degraded = status === 'degraded'
  return (
    <span
      className={`inline-flex items-center gap-2 rounded-full border px-3 py-1 text-xs font-medium ${
        degraded ? 'border-amber-500/40 bg-amber-500/10 text-amber-300' : 'border-line bg-white/[0.03] text-fg-dim'
      }`}
    >
      <span className={`h-1.5 w-1.5 rounded-full ${!connected ? 'bg-fg-mute' : degraded ? 'bg-amber-400 animate-pulse' : 'bg-brand animate-pulse'}`} />
      {!connected ? 'connecting…' : degraded ? 'brain degraded — serving cached fund' : 'live · healthy'}
    </span>
  )
}
