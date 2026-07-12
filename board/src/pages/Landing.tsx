import { motion } from 'framer-motion'
import {
  Activity,
  ArrowRight,
  BadgeCheck,
  Boxes,
  Database,
  LineChart,
  ShieldCheck,
  Sparkles,
  Target,
  Trophy,
} from 'lucide-react'
import { Link } from 'react-router-dom'
import { NavChart } from '../components/NavChart'
import { Button, Container, Pill, Reveal } from '../components/ui'

// Drop the generated hero art here (e.g. import heroImg from '../assets/hero.jpg') and set heroImage = heroImg.
const heroImage: string | null = null

export function Landing() {
  return (
    <div className="relative min-h-screen bg-ink text-fg">
      <Backdrop />
      <Nav />
      <Hero />
      <Stats />
      <HowItWorks />
      <FundPreview />
      <UnderTheHood />
      <CTA />
      <Footer />
    </div>
  )
}

/* ------------------------------------------------------------------ backdrop */
function Backdrop() {
  return (
    <div aria-hidden className="pointer-events-none fixed inset-0 z-0">
      <div className="absolute inset-0 bg-grid opacity-60" />
      <div className="absolute -top-40 left-1/2 h-[520px] w-[900px] -translate-x-1/2 rounded-full bg-brand/20 blur-[130px]" />
      <div className="absolute inset-x-0 bottom-0 h-64 bg-gradient-to-t from-ink to-transparent" />
    </div>
  )
}

/* ----------------------------------------------------------------------- nav */
function Nav() {
  return (
    <header className="sticky top-0 z-50 border-b border-line/60 bg-ink/70 backdrop-blur-xl">
      <Container className="flex h-16 items-center justify-between">
        <a href="#top" className="flex items-center gap-2">
          <span className="grid h-7 w-7 place-items-center rounded-lg bg-brand text-ink font-display font-bold">B</span>
          <span className="font-display text-lg font-semibold tracking-tight">BOYS</span>
        </a>
        <nav className="hidden items-center gap-8 text-sm text-fg-dim md:flex">
          <a href="#how" className="transition-colors hover:text-fg">How it works</a>
          <a href="#fund" className="transition-colors hover:text-fg">The fund</a>
          <a href="#stack" className="transition-colors hover:text-fg">Under the hood</a>
        </nav>
        <Button href="/demo" className="px-4 py-2">
          Launch demo <ArrowRight className="h-4 w-4 transition-transform group-hover:translate-x-0.5" />
        </Button>
      </Container>
    </header>
  )
}

/* --------------------------------------------------------------------- hero */
function Hero() {
  return (
    <section id="top" className="relative z-10">
      <Container className="grid items-center gap-12 pt-20 pb-16 lg:grid-cols-[1.05fr_1fr] lg:pt-28 lg:pb-24">
        <div>
          <motion.div initial={{ opacity: 0, y: 16 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6 }}>
            <Pill className="mb-6">
              <span className="relative flex h-1.5 w-1.5">
                <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-brand-2 opacity-75" />
                <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-brand" />
              </span>
              Live sports-outcomes fund · settled to the cent
            </Pill>
          </motion.div>

          <motion.h1
            initial={{ opacity: 0, y: 20 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.7, delay: 0.05 }}
            className="font-display text-5xl font-bold leading-[0.98] tracking-tight sm:text-6xl lg:text-7xl"
          >
            Bet on <span className="text-gradient">your self.</span>
          </motion.h1>

          <motion.p
            initial={{ opacity: 0, y: 20 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.7, delay: 0.15 }}
            className="mt-6 max-w-xl text-lg leading-relaxed text-fg-dim"
          >
            Stake real money on a real goal. While you chase it, your stake rides a live sports-outcomes fund.
            Prove your milestones — then cash out, or let it ride.
          </motion.p>

          <motion.div
            initial={{ opacity: 0, y: 20 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.7, delay: 0.25 }}
            className="mt-8 flex flex-wrap items-center gap-3"
          >
            <Button href="/demo" className="px-6 py-3 text-base">
              Launch the demo <ArrowRight className="h-4 w-4 transition-transform group-hover:translate-x-0.5" />
            </Button>
            <Button href="#how" variant="ghost" className="px-6 py-3 text-base">
              See how it works
            </Button>
          </motion.div>

          <motion.div
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            transition={{ duration: 0.8, delay: 0.4 }}
            className="mt-10 flex items-center gap-6 text-xs text-fg-mute"
          >
            <span className="flex items-center gap-1.5"><ShieldCheck className="h-4 w-4 text-brand" /> Correct to the cent</span>
            <span className="flex items-center gap-1.5"><Activity className="h-4 w-4 text-brand" /> Real-time NAV</span>
            <span className="flex items-center gap-1.5"><BadgeCheck className="h-4 w-4 text-brand" /> AI-refereed</span>
          </motion.div>
        </div>

        <HeroVisual />
      </Container>
    </section>
  )
}

function HeroVisual() {
  return (
    <motion.div
      initial={{ opacity: 0, scale: 0.96, y: 20 }}
      animate={{ opacity: 1, scale: 1, y: 0 }}
      transition={{ duration: 0.8, delay: 0.2, ease: [0.22, 1, 0.36, 1] }}
      className="relative"
    >
      <div className="glow-brand relative overflow-hidden rounded-3xl border border-line bg-gradient-to-b from-surface to-ink p-5">
        {heroImage && (
          <img src={heroImage} alt="" className="absolute inset-0 h-full w-full object-cover opacity-40" />
        )}
        <div className="relative flex items-center justify-between px-1 pb-3">
          <div>
            <div className="text-xs text-fg-mute">Your stake · commitment #1</div>
            <div className="font-display text-2xl font-semibold text-fg">$149.60</div>
          </div>
          <div className="flex items-center gap-2">
            <span className="rounded-full bg-brand/15 px-2.5 py-1 text-xs font-semibold text-brand-2">+49.6%</span>
            <span className="flex items-center gap-1 rounded-full border border-line px-2.5 py-1 text-[10px] font-medium tracking-wide text-fg-dim">
              <span className="h-1.5 w-1.5 rounded-full bg-brand animate-pulse" /> LIVE
            </span>
          </div>
        </div>
        <NavChart className="h-56 w-full" />
        <div className="mt-3 grid grid-cols-3 gap-2 border-t border-line pt-3 text-center">
          {[
            ['Principal', '$100.00'],
            ['Carry (15%)', '$7.44'],
            ['Take-home', '$142.16'],
          ].map(([k, v]) => (
            <div key={k}>
              <div className="text-[10px] uppercase tracking-wide text-fg-mute">{k}</div>
              <div className="mt-0.5 font-medium text-fg">{v}</div>
            </div>
          ))}
        </div>
      </div>
    </motion.div>
  )
}

/* -------------------------------------------------------------------- stats */
function Stats() {
  const items = [
    ['3 seasons', 'of real EPL + NBA data backtested'],
    ['6 services', '3 languages, 2 databases, one origin'],
    ['to the cent', 'every settlement, reconciled'],
    ['sub-second', 'NAV ticks over WebSocket'],
  ]
  return (
    <section className="relative z-10 border-y border-line/60 bg-ink-2/40">
      <Container className="grid grid-cols-2 gap-px overflow-hidden md:grid-cols-4">
        {items.map(([big, small], i) => (
          <Reveal key={big} i={i} className="px-6 py-8">
            <div className="font-display text-2xl font-semibold text-fg">{big}</div>
            <div className="mt-1 text-sm text-fg-dim">{small}</div>
          </Reveal>
        ))}
      </Container>
    </section>
  )
}

/* ------------------------------------------------------------- how it works */
function HowItWorks() {
  const steps = [
    {
      icon: Target,
      k: '01',
      title: 'Stake on your goal',
      body: 'Set a SMART goal and lock a stake in escrow. An AI referee gates vague goals and suggests a sharper rewrite.',
    },
    {
      icon: LineChart,
      k: '02',
      title: 'Ride the fund',
      body: 'Your stake rides a backtested sports-outcomes fund. Watch the NAV move live, tick by tick, as you chase the goal.',
    },
    {
      icon: Trophy,
      k: '03',
      title: 'Prove & cash out',
      body: 'Clear each milestone with proof. Cash out your winnings, let it ride to the next leg — or miss, and 10% goes to charity.',
    },
  ]
  return (
    <section id="how" className="relative z-10 py-24">
      <Container>
        <Reveal className="max-w-2xl">
          <Pill className="mb-4">How it works</Pill>
          <h2 className="font-display text-3xl font-semibold tracking-tight sm:text-4xl">
            A commitment device with skin in the game.
          </h2>
          <p className="mt-4 text-fg-dim">
            Three steps from "I should" to "I did" — with your own money on the line and a fund pulling for you.
          </p>
        </Reveal>
        <div className="mt-14 grid gap-5 md:grid-cols-3">
          {steps.map((s, i) => (
            <Reveal key={s.k} i={i}>
              <div className="group h-full rounded-2xl border border-line bg-surface/50 p-7 transition-all duration-300 hover:-translate-y-1 hover:border-brand/40 hover:bg-surface">
                <div className="flex items-center justify-between">
                  <div className="grid h-11 w-11 place-items-center rounded-xl bg-brand/10 text-brand transition-colors group-hover:bg-brand/20">
                    <s.icon className="h-5 w-5" />
                  </div>
                  <span className="font-display text-sm text-fg-mute">{s.k}</span>
                </div>
                <h3 className="mt-5 text-lg font-semibold text-fg">{s.title}</h3>
                <p className="mt-2 text-sm leading-relaxed text-fg-dim">{s.body}</p>
              </div>
            </Reveal>
          ))}
        </div>
      </Container>
    </section>
  )
}

/* ------------------------------------------------------------- fund preview */
function FundPreview() {
  return (
    <section id="fund" className="relative z-10 py-24">
      <Container className="grid items-center gap-14 lg:grid-cols-2">
        <Reveal>
          <div className="glow-brand overflow-hidden rounded-3xl border border-line bg-gradient-to-b from-surface to-ink p-6">
            <div className="mb-2 flex items-center justify-between">
              <span className="text-sm text-fg-dim">Action pool · NAV</span>
              <span className="flex items-center gap-1 text-xs text-brand-2">
                <Activity className="h-3.5 w-3.5" /> streaming
              </span>
            </div>
            <NavChart className="h-64 w-full" />
          </div>
        </Reveal>
        <Reveal i={1}>
          <Pill className="mb-4">The fund</Pill>
          <h2 className="font-display text-3xl font-semibold tracking-tight sm:text-4xl">
            A real fund, not a toy.
          </h2>
          <p className="mt-4 text-fg-dim">
            Your $100 rides a deterministic backtest of three real football seasons and NBA outcomes. The floor
            is your principal — you only ever risk the winnings. Cash out and the house takes a 15% carry on the
            gain; nothing more.
          </p>
          <ul className="mt-6 space-y-3 text-sm">
            {[
              'Principal is protected — the floor never drops below your stake.',
              'Milestones unlock cash-out; ride to compound into the next leg.',
              'Miss a milestone and it settles 90% to you, 10% to charity — to the cent.',
            ].map((t) => (
              <li key={t} className="flex items-start gap-3 text-fg-dim">
                <BadgeCheck className="mt-0.5 h-4 w-4 shrink-0 text-brand" /> {t}
              </li>
            ))}
          </ul>
        </Reveal>
      </Container>
    </section>
  )
}

/* ------------------------------------------------------------- under the hood */
function UnderTheHood() {
  const tech = [
    { icon: Database, title: '.NET ledger', body: 'Escrow, double-entry ledger, state machine & settlement on SQL Server.' },
    { icon: Boxes, title: 'Python quant', body: 'Backtested NAV curve + an AI referee, served from an Oracle warehouse.' },
    { icon: Activity, title: 'Go engine', body: 'Deterministic 30× replay fanned out to every screen over WebSocket.' },
    { icon: ShieldCheck, title: 'nginx edge', body: 'One origin, no CORS. Health-gated boot, graceful degradation.' },
  ]
  return (
    <section id="stack" className="relative z-10 border-t border-line/60 py-24">
      <Container>
        <Reveal className="max-w-2xl">
          <Pill className="mb-4"><Sparkles className="h-3.5 w-3.5 text-brand" /> Under the hood</Pill>
          <h2 className="font-display text-3xl font-semibold tracking-tight sm:text-4xl">
            Built like production, not a hack.
          </h2>
          <p className="mt-4 text-fg-dim">
            Polyglot microservices with gRPC contracts, a double-entry ledger correct to the cent, deterministic
            replay, and graceful degradation — each layer adversarially reviewed and end-to-end tested.
          </p>
        </Reveal>
        <div className="mt-14 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
          {tech.map((t, i) => (
            <Reveal key={t.title} i={i}>
              <div className="h-full rounded-2xl border border-line bg-surface/40 p-6 transition-colors hover:border-brand/40">
                <t.icon className="h-6 w-6 text-brand" />
                <h3 className="mt-4 font-semibold text-fg">{t.title}</h3>
                <p className="mt-1.5 text-sm leading-relaxed text-fg-dim">{t.body}</p>
              </div>
            </Reveal>
          ))}
        </div>
      </Container>
    </section>
  )
}

/* ---------------------------------------------------------------------- CTA */
function CTA() {
  return (
    <section className="relative z-10 py-24">
      <Container>
        <Reveal>
          <div className="relative overflow-hidden rounded-3xl border border-brand/30 bg-gradient-to-br from-surface-2 to-ink px-8 py-16 text-center">
            <div className="pointer-events-none absolute -top-24 left-1/2 h-64 w-[600px] -translate-x-1/2 rounded-full bg-brand/25 blur-[100px]" />
            <div className="relative">
              <h2 className="font-display text-3xl font-semibold tracking-tight sm:text-4xl">
                Ready to bet on yourself?
              </h2>
              <p className="mx-auto mt-4 max-w-lg text-fg-dim">
                Walk the whole thing — create a goal, ride the fund live, prove a milestone, and settle to the cent.
              </p>
              <div className="mt-8 flex justify-center">
                <Button href="/demo" className="px-7 py-3 text-base">
                  Launch the demo <ArrowRight className="h-4 w-4 transition-transform group-hover:translate-x-0.5" />
                </Button>
              </div>
            </div>
          </div>
        </Reveal>
      </Container>
    </section>
  )
}

/* ------------------------------------------------------------------- footer */
function Footer() {
  return (
    <footer className="relative z-10 border-t border-line/60 py-10">
      <Container className="flex flex-col items-center justify-between gap-4 text-sm text-fg-mute sm:flex-row">
        <div className="flex items-center gap-2">
          <span className="grid h-6 w-6 place-items-center rounded-md bg-brand text-ink font-display text-xs font-bold">B</span>
          <span className="font-display font-semibold text-fg-dim">BOYS</span>
          <span>— Bet On Your Self</span>
        </div>
        <div className="flex items-center gap-6">
          <Link to="/demo" className="transition-colors hover:text-fg">Demo</Link>
          <span>United Hacks V7</span>
        </div>
      </Container>
    </footer>
  )
}
