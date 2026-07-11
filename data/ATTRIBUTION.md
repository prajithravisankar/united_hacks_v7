# Seed Data Attribution & Licenses

Raw payloads live in `data/raw/` (gitignored); `data/checksums.txt` pins the static files.

- **football-data.co.uk** — historical EPL results + opening/closing bookmaker odds (CSV per season).
  Free to use; credit **football-data.co.uk**. <https://www.football-data.co.uk/>
- **FiveThirtyEight NBA Elo** (`nbaallelo.csv`) — pre-game model win probabilities + results.
  Licensed **CC-BY-4.0**; credit **FiveThirtyEight**. <https://github.com/fivethirtyeight/data> (`nba-elo`).
- **Polymarket** — public market price history via the gamma + CLOB APIs (best-effort; the on-theme ticker for E5).

Note: FiveThirtyEight's soccer SPI feed (`spi_matches.csv`) was discontinued (404). The soccer edge
uses football-data's opening→closing line movement instead; `load_probs` remains validated against a
committed SPI-format fixture for any future SPI-shaped data.
