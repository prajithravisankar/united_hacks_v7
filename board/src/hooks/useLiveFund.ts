import { useEffect, useRef, useState } from 'react'

// Subscribes to the engine's live NAV stream through the edge (/ws/live). Maintains the current value, the
// running/health state, and a rolling history for the chart. Auto-reconnects.

export interface FundPoint { position: number; nav: number }
export interface LiveFund {
  connected: boolean
  status: 'healthy' | 'degraded'
  running: boolean
  position: number
  date: string
  nav: number | null
  terminal: boolean
  events: string[]
  history: FundPoint[]
}

interface WsMessage {
  type: 'snapshot' | 'tick' | 'status'
  position: number
  date?: string
  navCents: number
  events?: string[]
  running: boolean
  terminal?: boolean
  status?: string
}

const INITIAL: LiveFund = {
  connected: false, status: 'healthy', running: false, position: 0, date: '', nav: null,
  terminal: false, events: [], history: [],
}

export function useLiveFund(goal = 1): LiveFund {
  const [fund, setFund] = useState<LiveFund>(INITIAL)
  const wsRef = useRef<WebSocket | null>(null)

  useEffect(() => {
    let stopped = false
    let retry: ReturnType<typeof setTimeout> | undefined

    const connect = () => {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws'
      const ws = new WebSocket(`${proto}://${location.host}/ws/live?goal=${goal}`)
      wsRef.current = ws

      ws.onopen = () => setFund((f) => ({ ...f, connected: true }))

      ws.onmessage = (e) => {
        const m: WsMessage = JSON.parse(e.data)
        setFund((f) => {
          if (m.type === 'status') {
            return { ...f, status: (m.status as LiveFund['status']) ?? f.status, running: m.running }
          }
          // snapshot | tick
          const point: FundPoint = { position: m.position, nav: m.navCents }
          const history =
            m.type === 'snapshot'
              ? m.navCents > 0 ? [point] : []
              : f.history.length && f.history[f.history.length - 1].position === m.position
                ? f.history
                : [...f.history, point].slice(-600)
          return {
            ...f,
            position: m.position,
            nav: m.navCents,
            date: m.date ?? f.date,
            running: m.running,
            terminal: !!m.terminal,
            events: m.events?.length ? m.events : f.events,
            status: m.type === 'snapshot' && m.status ? (m.status as LiveFund['status']) : f.status,
            history,
          }
        })
      }

      ws.onclose = () => {
        setFund((f) => ({ ...f, connected: false }))
        if (!stopped) retry = setTimeout(connect, 1500)
      }
      ws.onerror = () => ws.close()
    }

    connect()
    return () => {
      stopped = true
      if (retry) clearTimeout(retry)
      wsRef.current?.close()
    }
  }, [goal])

  return fund
}
