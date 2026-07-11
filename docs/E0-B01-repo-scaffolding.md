# E0-B01 — Monorepo Scaffolding & Per-Language Test Harnesses

## What we built (plain English)
The skeleton of the whole backend: one repo holding three services in three languages (`ledger` in .NET, `brain` in Python, `engine` in Go), each with a working, proven test harness. Before writing any real feature, we proved that every language can run a test — the foundation TDD depends on.

## Key decisions
- **.NET 9, not 8.** The installed SDK is 9.0.305; net9.0 is a strict superset for our purposes and avoids needing the net8 targeting pack. (`backend-todo.md` says .NET 8 — this is the one deliberate substitution.)
- **Python pinned to 3.12** (via `requires-python = ">=3.12,<3.13"`), even though the machine has 3.14. Downstream deps (grpcio, oracledb, fastapi) have reliable 3.12 wheels; 3.14 is too new. `uv` provisions 3.12 automatically.
- **FluentAssertions pinned to 6.12.2** (Apache-2.0). v8+ moved to a commercial license; 6.x stays free.
- **`uv` for Python** (fast, lockfile-based, manages the interpreter). `uv.lock` is committed for reproducibility.
- Go module path is `boys/engine` (local monorepo module; generated gRPC code will live under it in E0-B03).

## How it works
- `services/ledger/` — .NET solution `Boys.Ledger.sln` with `Boys.Ledger.Domain` (pure logic, no I/O), `Boys.Ledger.Api` (web host stub, fleshed out in E3), `Boys.Ledger.Tests` (xUnit + FluentAssertions).
- `services/brain/` — Python package: `app/` + `tests/`, managed by `uv` via `pyproject.toml`.
- `services/engine/` — Go module: `cmd/engine/` + `internal/harness/`.
- Each service has ONE trivial `Add(2,3)==5` test that was **written first and run red** (compile/import error) before the implementation existed, then made green. Commit history and the build output show the red→green sequence.
- `scripts/verify.sh` runs all three suites; `scripts/lint.sh` runs formatters + linters (dotnet format, ruff+black+mypy-strict, gofmt+go vet).

## How to run / verify it
```bash
./scripts/verify.sh   # → ALL GREEN  (1 test passes per service)
./scripts/lint.sh     # → LINT OK
```
Individually:
```bash
dotnet test services/ledger/Boys.Ledger.sln
cd services/brain && uv run pytest
cd services/engine && go test ./...
```

## Gotchas / follow-ups
- `dotnet format` does **not** accept `--nologo` (it prints help and exits) — the lint script avoids it.
- mypy runs in strict mode, so every test function needs an explicit `-> None` return annotation. Expect this on all future Python tests.
- The `Api` project is a bare template for now; its real host, middleware, and DI land in E3-B11.
- Placeholder `Add` functions exist only to prove wiring; they get deleted as real code arrives.
