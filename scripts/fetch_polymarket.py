"""Best-effort Polymarket price-history fetcher (stdlib only).

Discovers a resolved market's CLOB token id via the gamma API, then pulls its full
price path. Exits non-zero on any hiccup so the caller can treat it as optional.
"""

import json
import sys
import urllib.request

OUT = sys.argv[1]


def get(url: str):
    req = urllib.request.Request(url, headers={"User-Agent": "boys-seed/0.1"})
    with urllib.request.urlopen(req, timeout=20) as resp:
        return json.load(resp)


def main() -> int:
    events = get("https://gamma-api.polymarket.com/events?closed=true&limit=60&order=volume&ascending=false")
    tokens: list[str] = []
    for ev in events:
        for market in ev.get("markets", []):
            raw = market.get("clobTokenIds")
            if not raw:
                continue
            try:
                ids = json.loads(raw) if isinstance(raw, str) else raw
            except (ValueError, TypeError):
                continue
            tokens.extend(ids or [])
    if not tokens:
        print("no clobTokenIds found in gamma events", file=sys.stderr)
        return 1

    # Many resolved tokens return empty history; try candidates until one has a real path.
    for token in tokens[:50]:
        try:
            hist = get(
                f"https://clob.polymarket.com/prices-history?market={token}&interval=max&fidelity=1"
            )
        except Exception:  # noqa: BLE001 - just try the next token
            continue
        history = hist.get("history", []) if isinstance(hist, dict) else []
        if len(history) >= 10:
            with open(OUT, "w", encoding="utf-8") as fh:
                json.dump(hist, fh)
            print(f"saved {len(history)} ticks -> {OUT}")
            return 0

    print("no candidate token returned a usable price history", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
