# BOYS — Demo Runbook (minute-by-minute)

The pitch is judged output #1. This is the rehearsed choreography — on rails. Every beat has an exact command
and what should appear on screen. Total run ≈ 7 minutes.

## Before you hit record
```bash
./scripts/demo_up.sh        # cold-boot the whole stack + seed (≈45s), or if already up:
./scripts/preflight.sh      # must print "PRE-FLIGHT GREEN — cleared for the take"
```
Terminals to arrange on screen:
- **T1** — narration/commands.
- **T2, T3** — `./scripts/ws_watch.sh 1` in each (the two "screens" that will sync). Leave them idle for now.
- **T4** — `docker logs -f boys-engine` (for the resilience beat).

Everything is one origin at **`http://127.0.0.1:8888`** (`/api` → ledger, `/ws/live` → engine). Set:
```bash
API=http://127.0.0.1:8888/api
```
> **curl idiom:** body-less POSTs need an explicit empty body: `curl -X POST -d '' …` (bare `curl -X POST`
> gets a 400 from nginx). The board/frontend does this automatically.

---

## 0:00 — Cold open — the architecture (30s)
> "BOYS is a commitment device: stake money on a goal, your stake rides a simulated sports fund, prove
> milestones to cash out. The point of the build is the backend — a polyglot microservices system."
```bash
docker compose --profile demo ps        # 6 running containers, 3 languages, 2 databases, 1 edge
```
On screen: mssql, oracle, brain (Python), ledger (.NET), engine (Go), nginx — all healthy.

## 0:30 — The AI SMART-goal gate (1 min)
> "You can't stake on a vague goal. An AI referee gates it."

Reject a vague goal (seeded AI, deterministic):
```bash
curl -s -X POST "$API/goals" -H 'Content-Type: application/json' -d '{
  "goalText":"I want to wake up earlier","stakeCents":10000,"charityId":1,"driveMode":"AUTO",
  "deadline":"2026-12-01T00:00:00Z","milestones":[{"description":"m","targetMetric":"x","dueDate":"2026-11-01T00:00:00Z"}]
}' | python3 -m json.tool
```
On screen: **422** `goal_rejected` + a `suggestedRewrite`.
> (Optional authenticity: set `AI_MODE=live` + `GEMINI_API_KEY` and restart brain for real Gemini — the
> seeded provider stays armed as the offline fallback, so the demo never depends on the network.)

The demo goal is already seeded (commitment **1**, "Finish History with a 90% grade", $100, 3 milestones,
**active** with escrow posted):
```bash
curl -s "$API/goals/1" | python3 -m json.tool     # state:"active", 3 pending milestones
```

## 2:00 — Replay + two screens in lockstep (1 min)
Bring **T2** and **T3** to the front, then start the replay:
```bash
./scripts/replay.sh start 4        # 4× → the full fund window plays in ~90s (~30s per leg)
```
On screen: T2 and T3 tick **identically, in perfect sync** — the same NAV, same date, frame for frame.
> "Two browsers, one broadcast goroutine in Go, byte-identical streams. That's the concurrency payoff."

## 3:00 — Prove a milestone → AI + referee → reveal (1 min)
```bash
EV=$(base64 < data/seed/midterm1_grade.png | tr -d '\n')
curl -s -X POST "$API/goals/1/proof" -H 'Content-Type: application/json' \
  -d "{\"milestoneId\":1,\"claim\":\"Scored 88 on Midterm 1\",\"evidenceBase64\":\"$EV\",\"mime\":\"image/png\",\"idempotencyKey\":\"m1\"}" | python3 -m json.tool
# → ai.status:"Supported". Now the human referee decides:
curl -s -X POST "$API/milestones/1/decision" -H 'Content-Type: application/json' -H 'X-User-Id: 2' \
  -d '{"decision":"approve","idempotencyKey":"d1"}' | python3 -m json.tool     # → milestone_cleared
```
> "The AI advises; a human referee has the final call. Now they can cash out — or let it ride."

## 4:00 — Ride and compound
```bash
curl -s -X POST -d '' "$API/goals/1/ride"          # → riding (floor stays at the $100 principal)
```
> "Riding rebases the floor to their principal — they only ever risk the winnings from here."

## 5:00 — The money shot: kill brain, keep streaming (1 min)
With the replay still running and T2/T3 ticking:
```bash
docker kill boys-brain
```
On screen (watch T2/T3 and T4):
- Within ~4s the status flips to **DEGRADED** — but **the NAV keeps ticking**. The fund is live with the
  brain *gone*, because the engine serves its cached curve.
- The ledger degrades too, never errors:
  ```bash
  curl -s "$API/goals/1/valuation"      # → {"degraded":true,...}  HTTP 200, not a 500
  ```
Then recover:
```bash
docker start boys-brain                 # ~a few seconds later: status flips back to HEALTHY, automatically
```
> "`docker stop` on a live demo only works if you engineered for it — a health monitor with hysteresis and a
> cached curve."

## 6:00 — Failure-path encore (1 min)
```bash
./scripts/reset_demo.sh                  # pristine commitment 1 in ~1s
curl -s -X POST "$API/goals/1/proof" -H 'Content-Type: application/json' \
  -d "{\"milestoneId\":1,\"claim\":\"Scored 82 — a miss\",\"evidenceBase64\":\"$EV\",\"mime\":\"image/png\",\"idempotencyKey\":\"f1\"}" >/dev/null
curl -s -X POST "$API/milestones/1/decision" -H 'Content-Type: application/json' -H 'X-User-Id: 2' \
  -d '{"decision":"reject","idempotencyKey":"fd1"}' | python3 -m json.tool     # → failed (hard gate)
curl -s -X POST -d '' "$API/goals/1/settle" | python3 -m json.tool             # → Failure receipt
```
On screen: **Failure** receipt — `charityCents: 1000` (10% to charity), `takeHomeCents: 9000` (90% back), to
the cent. Then prove it balances:
```bash
./scripts/reconcile.sh | tail -1        # RECONCILE OK
```

## 7:00 — Close on the architecture
```bash
docker compose --profile demo ps        # 6 containers, still all healthy
```
> "Six containers, three languages, two databases, one edge — every settlement correct to the cent, proven by
> a reconciliation pass and an end-to-end suite (`./scripts/e2e.sh`)."

---

## Between takes
```bash
./scripts/reset_demo.sh                  # back to pristine in ~1s (commitment 1 active, $100 escrowed)
```
If a take goes badly wrong, full re-cold-boot: `docker compose --profile demo down -v && ./scripts/demo_up.sh`.

## If something breaks mid-demo
- **Status stuck degraded** → brain didn't restart: `docker start boys-brain` and wait ~10s.
- **A POST returns 400 through nginx** → you forgot `-d ''` on a body-less POST.
- **Replay not ticking** → `./scripts/replay.sh start 4` (it may be paused/finished; StartReplay restarts it).
- **Anything else** → `./scripts/preflight.sh` tells you exactly which subsystem is red.
