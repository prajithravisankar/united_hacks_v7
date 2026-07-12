import { ArrowLeft } from 'lucide-react'
import { NavChart } from '../components/NavChart'
import { Button } from '../components/ui'

// Placeholder until the interactive demo (the 8-beat judge journey) is wired to the live stack.
export function Demo() {
  return (
    <div className="relative grid min-h-screen place-items-center bg-ink px-6 text-fg">
      <div aria-hidden className="pointer-events-none fixed inset-0 bg-grid opacity-50" />
      <div className="pointer-events-none fixed left-1/2 top-1/3 h-[420px] w-[720px] -translate-x-1/2 rounded-full bg-brand/15 blur-[130px]" />
      <div className="relative w-full max-w-lg text-center">
        <div className="mx-auto mb-8 w-full overflow-hidden rounded-3xl border border-line bg-gradient-to-b from-surface to-ink p-5">
          <NavChart className="h-44 w-full" />
        </div>
        <span className="inline-flex items-center gap-1.5 rounded-full border border-line bg-white/[0.03] px-3 py-1 text-xs text-fg-dim">
          <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-brand" /> Wiring the live demo
        </span>
        <h1 className="mt-5 font-display text-3xl font-semibold tracking-tight">The interactive demo is coming.</h1>
        <p className="mt-3 text-fg-dim">
          Create a goal, ride the fund live, prove a milestone, and settle to the cent — all in-browser, next up.
        </p>
        <div className="mt-8 flex justify-center">
          <Button href="/" variant="ghost" className="px-5 py-2.5">
            <ArrowLeft className="h-4 w-4" /> Back to home
          </Button>
        </div>
      </div>
    </div>
  )
}
