"""Brain service entrypoint: FastAPI (health) + gRPC (the real API) in one process."""

from __future__ import annotations

from fastapi import FastAPI
from fastapi.responses import JSONResponse

from app import db
from app.config import get_settings
from app.logging_setup import configure_logging

configure_logging()
app = FastAPI(title="BOYS brain", version="0.1.0")


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/health/ready")
def ready() -> JSONResponse:
    healthy = db.check_oracle()
    return JSONResponse(
        status_code=200 if healthy else 503,
        content={"status": "ready" if healthy else "not_ready", "oracle": healthy},
    )


def serve() -> None:  # pragma: no cover - process entrypoint
    import uvicorn

    from app.grpc.server import create_server

    settings = get_settings()
    settings.require_prod_secrets()
    grpc_server, _ = create_server(settings.grpc_port)
    grpc_server.start()
    uvicorn.run(app, host="0.0.0.0", port=settings.http_port)


if __name__ == "__main__":  # pragma: no cover
    serve()
