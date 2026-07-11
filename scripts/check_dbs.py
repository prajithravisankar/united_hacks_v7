"""B02 connectivity check: prove both databases accept a real query.

Pure-Python clients (python-tds + oracledb thin) so no host-side DB drivers or
Oracle Instant Client are required. Run via scripts/check_dbs.sh.
"""

from __future__ import annotations

import os
import sys


def check_mssql() -> None:
    import pytds

    pw = os.environ["MSSQL_SA_PASSWORD"]
    host = os.environ.get("MSSQL_HOST", "127.0.0.1")
    port = int(os.environ.get("MSSQL_PORT", "14333"))
    with pytds.connect(
        dsn=host, port=port, user="sa", password=pw, database="master", login_timeout=5
    ) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT 1")
            row = cur.fetchone()
            assert row is not None and row[0] == 1
    print(f"  OK  SQL Server (mssql 2022)     {host}:{port}  -> SELECT 1")


def check_oracle() -> None:
    import oracledb

    pw = os.environ["ORACLE_PASSWORD"]
    host = os.environ.get("ORACLE_HOST", "127.0.0.1")
    port = int(os.environ.get("ORACLE_PORT", "15211"))
    service = os.environ.get("ORACLE_SERVICE", "FREEPDB1")
    conn = oracledb.connect(user="system", password=pw, dsn=f"{host}:{port}/{service}")
    try:
        cur = conn.cursor()
        cur.execute("SELECT 1 FROM dual")
        row = cur.fetchone()
        assert row is not None and row[0] == 1
    finally:
        conn.close()
    print(f"  OK  Oracle (gvenzl/oracle-free)   {host}:{port}/{service}  -> SELECT 1 FROM dual")


def main() -> int:
    ok = True
    for name, fn in (("SQL Server", check_mssql), ("Oracle", check_oracle)):
        try:
            fn()
        except Exception as exc:  # noqa: BLE001 - report any failure clearly
            ok = False
            print(f"  FAIL {name}: {type(exc).__name__}: {exc}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
