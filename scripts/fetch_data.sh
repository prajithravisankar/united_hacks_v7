#!/usr/bin/env bash
# Download the seed datasets ONCE into data/raw/. Idempotent (skips valid files),
# validates what it fetches, and checksum-verifies the static files. After this runs,
# nothing on the critical path ever needs the network again.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RAW="$ROOT/data/raw"
CHECKSUMS="$ROOT/data/checksums.txt"
mkdir -p "$RAW"

fetch() {  # url dest
  local url="$1" dest="$2"
  if [ -s "$RAW/$dest" ]; then echo "  skip (present): $dest"; return 0; fi
  echo "  fetching: $dest"
  curl -fsSL --retry 3 -o "$RAW/$dest" "$url"
}

echo "== football-data.co.uk (EPL results + opening & closing odds; completed seasons) =="
fetch "https://www.football-data.co.uk/mmz4281/2122/E0.csv" "football_E0_2122.csv"
fetch "https://www.football-data.co.uk/mmz4281/2223/E0.csv" "football_E0_2223.csv"
fetch "https://www.football-data.co.uk/mmz4281/2324/E0.csv" "football_E0_2324.csv"

echo "== FiveThirtyEight NBA Elo (model win probabilities + results) =="
# Note: 538's soccer SPI file (spi_matches.csv) was discontinued (404). The soccer
# edge in E2 uses football-data's opening->closing line movement instead; load_probs
# stays validated against its committed SPI-format fixture.
fetch "https://raw.githubusercontent.com/fivethirtyeight/data/master/nba-elo/nbaallelo.csv" "nbaallelo.csv"

echo "== validate fetched files are parseable / well-formed =="
( cd "$ROOT/services/brain" && uv run --quiet python - "$RAW" <<'PY'
import sys
from pathlib import Path
from app.data.parsers import load_odds
raw = Path(sys.argv[1])
for f in ("football_E0_2122.csv", "football_E0_2223.csv", "football_E0_2324.csv"):
    print(f"  parsed {f}: {len(load_odds(raw / f))} matches")
head = (raw / "nbaallelo.csv").open(encoding="utf-8").readline().lower()
assert "date_game" in head and "forecast" in head, "nbaallelo.csv missing expected columns"
print("  nbaallelo.csv: header OK (has date_game + forecast)")
PY
)

echo "== Polymarket price history (on-theme ticker; best-effort) =="
# The event->market->clobTokenId mapping is fiddly and the API is live, so this step
# is best-effort: a failure here never fails the seed (E5 can also use a cached sample).
if bash "$ROOT/scripts/fetch_polymarket.sh" 2>/dev/null; then
  echo "  polymarket price history fetched"
else
  echo "  polymarket skipped (discovery failed or offline) — non-fatal"
fi

echo "== checksums (static files) =="
STATIC=(football_E0_2122.csv football_E0_2223.csv football_E0_2324.csv nbaallelo.csv)
if [ -f "$CHECKSUMS" ]; then
  echo "  verifying against data/checksums.txt"
  ( cd "$RAW" && shasum -a 256 -c "$CHECKSUMS" )
else
  echo "  recording data/checksums.txt (first run)"
  ( cd "$RAW" && shasum -a 256 "${STATIC[@]}" > "$CHECKSUMS" )
fi

echo "seed data ready in data/raw/"
