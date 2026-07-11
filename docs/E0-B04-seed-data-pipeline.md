# E0-B04 — Seed-Data Acquisition Pipeline (Offline-First Inputs)

## What we built (plain English)
A scripted, checksum-verified download of the real sports datasets into `data/raw/`, plus strict parsers that turn each raw file into typed, validated records. From here on, nothing on the critical path needs the network — the demo replays from disk.

## Key decisions
- **The soccer edge is self-contained in football-data.co.uk.** Those CSVs carry results *and* opening + closing bookmaker odds, so the edge signal (closing-line movement) needs no second file. This matters because **FiveThirtyEight's soccer SPI file is gone (404)** — checked at build time rather than discovered later. `load_probs` (the SPI-format parser) stays fully tested against a committed fixture for any SPI-shaped data.
- **Completed seasons only** (2021–22, 2022–23, 2023–24). Past-season files are frozen, so their SHA-256 is stable and committable; a mismatch on a future run correctly flags upstream data drift.
- **Parsers are strict where it bites later**: both football date formats (DD/MM/YY *and* DD/MM/YYYY), missing bookmaker columns on old rows, junk/extreme odds, duplicate rows, empty files, UTF-8 BOM, and out-of-order / duplicate Polymarket ticks. Each is a named test.
- **No pandas** — Python's stdlib `csv`/`json` keep brain's dependency surface small for parsing.
- **Polymarket is best-effort.** The event→market→token discovery is genuinely fiddly and the public price-history endpoint returned empty across 50 candidate tokens, so the step is non-fatal; E5 can supply a real ticker or use the committed sample. It never blocks the seed.

## How it works
- `scripts/fetch_data.sh` — idempotent (`curl --retry`, skips present files), validates football via `load_odds` and nbaallelo by header, then records/verifies `data/checksums.txt`.
- `app/data/parsers.py` — `load_odds`, `load_probs`, `load_price_history` returning frozen dataclasses (`MatchOdds`, `ModelProb`, `PriceTick`). Probabilities validated to `[0,1]`, odds to `> 1.0`, dates normalized to ISO-8601.
- Fixtures in `services/brain/tests/data/fixtures/` are tiny hand-made excerpts (incl. a BOM file and a 0-byte file) so the edge-case tests need no network.

## How to run / verify it
```bash
./scripts/fetch_data.sh                 # downloads once, validates, records checksums; re-run = no-op + verify
cd services/brain && uv run pytest tests/data/   # 9 parser edge-case tests
```
Verified: first run fetched 3 EPL seasons (**380 matches each**) + nbaallelo (17 MB, header OK); second run skipped all and **checksum-verified OK**; 9/9 parser tests green.

## Gotchas / follow-ups
- `data/raw/*` is gitignored (payloads are large / re-downloadable); `data/checksums.txt` and the test fixtures are committed.
- `load_odds` currently reads **opening** odds (`B365H/D/A`) + results; the **closing** columns (`B365C*`/`PSC*`) that power the closing-line edge get added when the quant engine needs them in **E2-B08** (the real files already contain them).
- `nbaallelo.csv` has a `forecast` column (pre-game model win prob) — that's the NBA model probability the quant layer will use; it isn't parsed by `load_probs` (different shape), which is E2's job.
- Polymarket auto-discovery may need a hand-picked market/token id later; `load_price_history` is ready and tested regardless.
