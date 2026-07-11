"""Oracle connectivity (oracledb thin-mode pool) + a readiness probe."""

from __future__ import annotations

from typing import Any

import oracledb

from app.config import get_settings

_pool: Any = None


def get_pool() -> Any:
    global _pool
    if _pool is None:
        s = get_settings()
        _pool = oracledb.create_pool(
            user=s.oracle_app_user,
            password=s.oracle_app_password,
            dsn=s.oracle_dsn,
            min=1,
            max=4,
            increment=1,
        )
    return _pool


def check_oracle() -> bool:
    """True iff Oracle answers SELECT 1. Never raises — used by /health/ready."""
    try:
        with get_pool().acquire() as conn:
            with conn.cursor() as cur:
                cur.execute("SELECT 1 FROM dual")
                row = cur.fetchone()
                return bool(row and row[0] == 1)
    except Exception:
        return False
