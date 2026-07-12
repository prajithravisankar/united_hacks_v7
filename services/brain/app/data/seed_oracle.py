"""Load the seeded football data into Oracle's OLAP star schema.

Idempotent: DDL creation ignores "already exists", dimensions upsert by natural
key, and facts MERGE on (season, date, home, away) so re-running never duplicates.
"""

from __future__ import annotations

import os
from datetime import date
from pathlib import Path

import oracledb

from app.data.parsers import MatchOdds, load_odds


def _repo_root() -> Path:
    """Repo root on the host; a safe fallback in the container (where these files are
    absent — the brain only calls connect(), not the file-based load/apply_ddl)."""
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "docker-compose.yml").exists():
            return parent
    return here.parents[0]


REPO_ROOT = _repo_root()
# Paths default to the host repo layout but can be overridden so the loader runs inside a container (the
# compose `data-seed` one-shot mounts data/ and the DDL and points these at the mounts).
DDL_FILE = Path(os.environ.get("BOYS_DDL_FILE") or REPO_ROOT / "docker" / "oracle" / "init" / "01_warehouse.sql")
RAW = Path(os.environ.get("BOYS_RAW_DIR") or REPO_ROOT / "data" / "raw")

SEASONS = {
    "football_E0_2122.csv": "2122",
    "football_E0_2223.csv": "2223",
    "football_E0_2324.csv": "2324",
}


def _load_dotenv() -> None:
    """Populate os.environ from the repo .env without shell evaluation."""
    env = REPO_ROOT / ".env"
    if not env.exists():
        return
    for line in env.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, val = line.split("=", 1)
        os.environ.setdefault(key.strip(), val.strip())


def connect() -> oracledb.Connection:
    if "ORACLE_APP_PASSWORD" not in os.environ:
        _load_dotenv()
    host = os.environ.get("ORACLE_HOST", "127.0.0.1")
    port = os.environ.get("ORACLE_PORT", "15211")
    user = os.environ.get("ORACLE_APP_USER", "boys")
    pw = os.environ["ORACLE_APP_PASSWORD"]
    service = os.environ.get("ORACLE_SERVICE", "FREEPDB1")
    return oracledb.connect(
        user=user, password=pw, dsn=f"{host}:{port}/{service}", tcp_connect_timeout=5
    )


def apply_ddl(cur: oracledb.Cursor) -> None:
    # Drop full-line comments first so splitting on ';' yields clean statements.
    body = "\n".join(
        ln for ln in DDL_FILE.read_text().splitlines() if not ln.strip().startswith("--")
    )
    for stmt in (s.strip() for s in body.split(";")):
        if not stmt:
            continue
        try:
            cur.execute(stmt)
        except oracledb.DatabaseError as exc:
            (err,) = exc.args
            # 955 name already used / 1408 column already indexed / 2264 constraint name already used
            if err.code in (955, 1408, 2264):
                continue
            raise


def _upsert_dimension(
    cur: oracledb.Cursor, table: str, id_col: str, values: set[str]
) -> dict[str, int]:
    merge = (
        f"MERGE INTO {table} d USING (SELECT :1 v FROM dual) s ON (d.name = s.v) "
        f"WHEN NOT MATCHED THEN INSERT (name) VALUES (s.v)"
    )
    cur.executemany(merge, [(v,) for v in sorted(values)])
    cur.execute(f"SELECT name, {id_col} FROM {table}")
    return {name: oid for name, oid in cur.fetchall()}


def load() -> dict[str, int]:
    conn = connect()
    try:
        cur = conn.cursor()
        apply_ddl(cur)

        seasons: dict[str, list[MatchOdds]] = {}
        teams: set[str] = set()
        for fname, label in SEASONS.items():
            matches = load_odds(RAW / fname)
            seasons[label] = matches
            for m in matches:
                teams.add(m.home)
                teams.add(m.away)

        # dim_season shares the same (name-keyed) upsert shape as dim_team.
        season_merge = (
            "MERGE INTO dim_season d USING (SELECT :1 v FROM dual) s ON (d.label = s.v) "
            "WHEN NOT MATCHED THEN INSERT (label) VALUES (s.v)"
        )
        cur.executemany(season_merge, [(label,) for label in sorted(seasons)])
        cur.execute("SELECT label, season_id FROM dim_season")
        season_ids = {label: sid for label, sid in cur.fetchall()}

        team_ids = _upsert_dimension(cur, "dim_team", "team_id", teams)

        fact_rows = [
            (
                season_ids[label],
                team_ids[m.home],
                team_ids[m.away],
                date.fromisoformat(m.date),
                m.result,
                m.home_odds,
                m.draw_odds,
                m.away_odds,
            )
            for label, matches in seasons.items()
            for m in matches
        ]
        cur.executemany(
            "MERGE INTO fact_match f USING ("
            "  SELECT :1 sid, :2 hid, :3 aid, :4 mdate, :5 res, :6 ho, :7 dodds, :8 ao FROM dual"
            ") s ON (f.season_id = s.sid AND f.match_date = s.mdate "
            "        AND f.home_team_id = s.hid AND f.away_team_id = s.aid) "
            "WHEN MATCHED THEN UPDATE SET f.result = s.res, f.home_odds = s.ho, "
            "  f.draw_odds = s.dodds, f.away_odds = s.ao "
            "WHEN NOT MATCHED THEN INSERT "
            "  (season_id, home_team_id, away_team_id, match_date, result, home_odds, draw_odds, away_odds) "
            "  VALUES (s.sid, s.hid, s.aid, s.mdate, s.res, s.ho, s.dodds, s.ao)",
            fact_rows,
        )
        conn.commit()

        counts = {}
        for table, key in (
            ("dim_season", "seasons"),
            ("dim_team", "teams"),
            ("fact_match", "matches"),
        ):
            cur.execute(f"SELECT COUNT(*) FROM {table}")
            counts[key] = cur.fetchone()[0]
        return counts
    finally:
        conn.close()


if __name__ == "__main__":
    summary = load()
    print(
        f"  dim_season: {summary['seasons']}  dim_team: {summary['teams']}  fact_match: {summary['matches']}"
    )
