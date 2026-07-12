import { motion } from 'framer-motion'
import { useMemo } from 'react'
import type { FundPoint } from '../hooks/useLiveFund'

const W = 640
const H = 300
const PAD = 20

// Plots the live NAV history from the WebSocket. Auto-scales to the data, glows, and pulses at the leading
// edge — the "watch your stake climb" moment, driven by real streamed points.
export function LiveNavChart({ history, className }: { history: FundPoint[]; className?: string }) {
  const geo = useMemo(() => {
    if (history.length < 2) return null
    const navs = history.map((p) => p.nav)
    const positions = history.map((p) => p.position)
    const minN = Math.min(...navs)
    const maxN = Math.max(...navs)
    const span = Math.max(maxN - minN, 1)
    const pad = span * 0.12
    const lo = minN - pad
    const hi = maxN + pad
    const minP = Math.min(...positions)
    const maxP = Math.max(...positions)
    const x = (p: number) => PAD + ((p - minP) / Math.max(maxP - minP, 1)) * (W - PAD * 2)
    const y = (n: number) => H - PAD - ((n - lo) / (hi - lo)) * (H - PAD * 2)
    const pts = history.map((p) => [x(p.position), y(p.nav)] as const)
    const line = pts.map(([px, py], i) => `${i ? 'L' : 'M'}${px.toFixed(1)},${py.toFixed(1)}`).join(' ')
    const area = `${line} L${x(maxP).toFixed(1)},${H - PAD} L${x(minP).toFixed(1)},${H - PAD} Z`
    return { line, area, last: pts[pts.length - 1] }
  }, [history])

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className={className} preserveAspectRatio="none" role="img" aria-label="Live NAV">
      <defs>
        <linearGradient id="liveFill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#10b981" stopOpacity="0.34" />
          <stop offset="100%" stopColor="#10b981" stopOpacity="0" />
        </linearGradient>
        <linearGradient id="liveLine" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#059669" />
          <stop offset="70%" stopColor="#34d399" />
          <stop offset="100%" stopColor="#6ee7b7" />
        </linearGradient>
        <filter id="liveGlow" x="-10%" y="-30%" width="120%" height="160%">
          <feGaussianBlur stdDeviation="5" result="b" />
          <feMerge>
            <feMergeNode in="b" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>

      {[0.25, 0.5, 0.75].map((g) => (
        <line key={g} x1={PAD} x2={W - PAD} y1={PAD + g * (H - PAD * 2)} y2={PAD + g * (H - PAD * 2)} stroke="white" strokeOpacity="0.05" />
      ))}

      {geo && (
        <>
          <path d={geo.area} fill="url(#liveFill)" />
          <path
            d={geo.line}
            fill="none"
            stroke="url(#liveLine)"
            strokeWidth="2.5"
            strokeLinecap="round"
            strokeLinejoin="round"
            filter="url(#liveGlow)"
            vectorEffect="non-scaling-stroke"
          />
          <motion.circle
            cx={geo.last[0]}
            cy={geo.last[1]}
            r="9"
            fill="#34d399"
            fillOpacity="0.25"
            animate={{ r: [7, 15, 7], fillOpacity: [0.3, 0, 0.3] }}
            transition={{ duration: 2, repeat: Infinity }}
          />
          <circle cx={geo.last[0]} cy={geo.last[1]} r="4" fill="#6ee7b7" />
        </>
      )}
    </svg>
  )
}
