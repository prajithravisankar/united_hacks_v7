"""Structured JSON logging with a request/RPC correlation id."""

from __future__ import annotations

import contextvars
from typing import Any

import structlog

request_id_var: contextvars.ContextVar[str] = contextvars.ContextVar("request_id", default="-")


def _add_request_id(_logger: Any, _method: str, event_dict: dict[str, Any]) -> dict[str, Any]:
    event_dict["request_id"] = request_id_var.get()
    return event_dict


def configure_logging(json_logs: bool = True) -> None:
    processors: list[Any] = [
        structlog.processors.add_log_level,
        structlog.processors.TimeStamper(fmt="iso"),
        _add_request_id,
        structlog.processors.JSONRenderer() if json_logs else structlog.dev.ConsoleRenderer(),
    ]
    structlog.configure(processors=processors, cache_logger_on_first_use=True)


def get_logger(name: str = "brain") -> Any:
    return structlog.get_logger(name)
