# E0-B03 — gRPC Contracts: One `.proto` Source, Three Languages

## What we built (plain English)
Every call one service makes to another is now defined once, in `protos/`, and compiled into typed stubs for all three languages — C# (ledger), Go (engine), Python (brain). This is the headline "polyglot" flex: three languages, one shared contract, no ad-hoc JSON between services. A smoke test in each language proves the generated code imports and constructs.

## Key decisions
- **One money type everywhere**: `boys.common.v1.Money { int64 cents; string currency }`. No `double` appears in any contract — floats never touch a balance.
- **Go `go_package` includes the `boys/` prefix** (`boys/engine/gen/boys/<pkg>/v1`). With `protoc -I protos` + `paths=source_relative`, the on-disk path is `gen/boys/<pkg>/v1/…`, so the import path must match — hence the (slightly ugly but correct) doubled `boys`.
- **Generated code: Go + Python are committed; C# is not.** Grpc.Tools regenerates C# into `obj/` at every build, so committing it is pointless. Committing the Go/Python stubs means a fresh clone builds and `verify.sh` passes **without** needing `protoc` + plugins installed.
- **Services split**: `QuantService` + `RefereeService` in `brain.v1`; `EngineService` in `engine.v1`. Ledger is a pure gRPC *client* of both; brain *serves* Quant/Referee; engine *serves* Engine and *calls* Quant.

## How it works
- `scripts/gen_protos.sh` regenerates all three in one command:
  - **Go** via `protoc --go_out --go-grpc_out` (plugins from `go install …/protoc-gen-go` + `…/protoc-gen-go-grpc`) → `services/engine/gen/`.
  - **Python** via `python -m grpc_tools.protoc` (grpcio-tools) → `services/brain/gen/` (on pytest's `pythonpath`; linters exclude `gen/`).
  - **C#** via `dotnet build` of `Boys.Ledger.Contracts` — Grpc.Tools reads `<Protobuf Include="…/protos/**/*.proto">` and generates at build.
- `protos/CHANGELOG.md` records the wire-compatibility rule: fields are only ever added, never renumbered.

## How to run / verify it
```bash
./scripts/gen_protos.sh   # → proto codegen complete
./scripts/verify.sh       # ledger 2 / brain 2 / engine 2 → ALL GREEN
```
The three contract smoke tests:
- `services/engine/internal/contracts/contracts_test.go`
- `services/brain/tests/test_contracts.py`
- `services/ledger/tests/Boys.Ledger.Tests/ContractsTests.cs`

## Gotchas / follow-ups
- Regenerating requires `protoc`, the Go plugins on `PATH` (`$(go env GOPATH)/bin`), and `grpcio-tools` in brain's venv. A clone that only *builds* needs none of these (Go/Python stubs are committed; C# generates itself).
- Dates are ISO-8601 **strings** in the contracts (not `google.protobuf.Timestamp`) — simpler and deterministic for our seeded, replay-driven data.
- Actual client wiring (ledger→brain, engine→brain) and server implementations land in E2/E3/E4; B03 only proves the contract surface compiles everywhere.
- If a `go_package` ever changes, regenerate and `go mod tidy` — the engine module pulls grpc/protobuf deps transitively.
