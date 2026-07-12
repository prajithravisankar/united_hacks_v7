import { motion } from 'framer-motion'
import { useMemo } from 'react'

// A live-market-feeling NAV curve: the $100 stake climbing to ~$149.60 with real volatility. Draws itself in,
// glows, and pulses at the leading edge. Self-contained — the hero's WOW without needing the image.
const VALS = [
  0.28, 0.31, 0.29, 0.35, 0.39, 0.36, 0.41, 0.45, 0.43, 0.5, 0.47, 0.54, 0.58, 0.54, 0.6, 0.64, 0.61, 0.68,
  0.66, 0.73, 0.7, 0.77, 0.81, 0.78, 0.84, 0.87, 0.85, 0.92,
]
const W = 620
const H = 320
const PAD = 24

export function NavChart({ className }: { className?: string }) {
  const { line, area, last, gridYs } = useMemo(() => {
    const n = VALS.length
    const x = (i: number) => PAD + (i / (n - 1)) * (W - PAD * 2)
    const y = (v: number) => H - PAD - v * (H - PAD * 2)
    const pts = VALS.map((v, i) => [x(i), y(v)] as const)
    const line = pts.map(([px, py], i) => `${i ? 'L' : 'M'}${px.toFixed(1)},${py.toFixed(1)}`).join(' ')
    const area = `${line} L${x(n - 1).toFixed(1)},${H - PAD} L${x(0).toFixed(1)},${H - PAD} Z`
    const gridYs = [0.2, 0.4, 0.6, 0.8].map((g) => y(g))
    return { line, area, last: pts[n - 1], gridYs }
  }, [])

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className={className} preserveAspectRatio="xMidYMid meet" role="img" aria-label="NAV curve rising">
      <defs>
        <linearGradient id="navFill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#10b981" stopOpacity="0.35" />
          <stop offset="100%" stopColor="#10b981" stopOpacity="0" />
        </linearGradient>
        <linearGradient id="navLine" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#059669" />
          <stop offset="60%" stopColor="#34d399" />
          <stop offset="100%" stopColor="#6ee7b7" />
        </linearGradient>
        <filter id="navGlow" x="-20%" y="-40%" width="140%" height="180%">
          <feGaussianBlur stdDeviation="6" result="b" />
          <feMerge>
            <feMergeNode in="b" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>

      {/* faint horizontal gridlines */}
      {gridYs.map((gy, i) => (
        <line key={i} x1={PAD} x2={W - PAD} y1={gy} y2={gy} stroke="white" strokeOpacity="0.05" strokeWidth="1" />
      ))}

      {/* area fill fades in after the line draws */}
      <motion.path
        d={area}
        fill="url(#navFill)"
        initial={{ opacity: 0 }}
        animate={{ opacity: 1 }}
        transition={{ duration: 0.8, delay: 1.3 }}
      />

      {/* the line draws itself in */}
      <motion.path
        d={line}
        fill="none"
        stroke="url(#navLine)"
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        filter="url(#navGlow)"
        initial={{ pathLength: 0 }}
        animate={{ pathLength: 1 }}
        transition={{ duration: 1.8, ease: 'easeInOut' }}
      />

      {/* pulsing leading dot */}
      <motion.g initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: 1.7 }}>
        <motion.circle
          cx={last[0]}
          cy={last[1]}
          r="10"
          fill="#34d399"
          fillOpacity="0.25"
          animate={{ r: [8, 18, 8], fillOpacity: [0.3, 0, 0.3] }}
          transition={{ duration: 2.4, repeat: Infinity, ease: 'easeInOut' }}
        />
        <circle cx={last[0]} cy={last[1]} r="4.5" fill="#6ee7b7" />
      </motion.g>
    </svg>
  )
}
