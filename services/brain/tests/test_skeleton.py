"""B07 skeleton tests: health endpoints, fail-fast config, gRPC routing."""

from __future__ import annotations

import grpc
import pytest
from fastapi.testclient import TestClient

from app import db, main
from app.config import Settings
from app.grpc.server import create_server


def test_live_returns_ok() -> None:
    client = TestClient(main.app)
    resp = client.get("/health/live")
    assert resp.status_code == 200
    assert resp.json()["status"] == "ok"


def test_ready_is_503_when_oracle_down(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(db, "check_oracle", lambda: False)
    resp = TestClient(main.app).get("/health/ready")
    assert resp.status_code == 503
    assert resp.json()["oracle"] is False


def test_ready_is_200_when_oracle_up(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(db, "check_oracle", lambda: True)
    resp = TestClient(main.app).get("/health/ready")
    assert resp.status_code == 200
    assert resp.json()["oracle"] is True


def test_config_fails_fast_without_prod_secret() -> None:
    settings = Settings(env="prod", oracle_app_password="")
    with pytest.raises(RuntimeError):
        settings.require_prod_secrets()


def test_config_local_defaults_ok() -> None:
    settings = Settings(env="local")
    settings.require_prod_secrets()  # must not raise
    assert settings.oracle_dsn.endswith("/FREEPDB1")


def test_grpc_server_boots_and_accepts_connections() -> None:
    server, port = create_server(0)  # ephemeral port
    server.start()
    try:
        with grpc.insecure_channel(f"localhost:{port}") as channel:
            grpc.channel_ready_future(channel).result(timeout=5)  # connects without error
    finally:
        server.stop(None)
