# Proto Contract Changelog

The `.proto` files here are the **single source of truth** for every cross-service call.
C#, Go, and Python code is generated from them (`scripts/gen_protos.sh`) — never hand-edited.

## The one rule (wire compatibility)
Fields are only ever **added** with new tag numbers. Never renumber, never reuse a number,
never change the type of an existing field. To remove a field, `reserve` its number and name.
This keeps old and new service builds able to talk to each other.

## History
- **2026-07-11 — Initial contracts.**
  - `boys.common.v1` — `Money { int64 cents; string currency }` (the only money type in any contract).
  - `boys.brain.v1` — `QuantService` (GetNavCurve, GetValuation, ProjectOutcomes, ListOpenMarkets, PlaceUserBet) + `RefereeService` (ValidateGoal, CheckProof).
  - `boys.engine.v1` — `EngineService` (StartReplay, Pause, SetSpeed, GetReplayState).
- **2026-07-11 — R1 foundation review.** (Pre-release, nothing deployed — safe.)
  - Renamed engine `goal_id` → `commitment_id` so engine + brain share one key (one goal = one commitment). Field numbers unchanged, so wire-compatible regardless.
  - Added `DriveMode` enum + `drive_mode` field to the quant read requests (you-drive vs auto).
  - Added `current_sim_date` to `ReplayState` — the authoritative clock for ledger's deadline hard-gate.
  - Documented `milestone_id` ↔ `commitment_id` on `CheckProofRequest`.
