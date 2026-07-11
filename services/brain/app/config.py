"""Typed, env-driven configuration (pydantic-settings). Reads the repo .env in dev."""

from __future__ import annotations

from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

def _find_env_file() -> str:
    """Repo .env on the host; a missing path in the container (pydantic ignores it)."""
    for parent in Path(__file__).resolve().parents:
        if (parent / "docker-compose.yml").exists():
            return str(parent / ".env")
    return ".env"


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=_find_env_file(), extra="ignore", case_sensitive=False
    )

    env: str = "local"  # local | test | staging | prod

    # Oracle (brain owns it)
    oracle_host: str = "127.0.0.1"
    oracle_port: str = "15211"
    oracle_app_user: str = "boys"
    oracle_app_password: str = ""
    oracle_service: str = "FREEPDB1"

    # Servers
    http_port: int = 8081
    grpc_port: int = 50061

    # AI referee (B10)
    ai_mode: str = "seeded"  # seeded | live
    gemini_api_key: str = ""
    featherless_api_key: str = ""

    @property
    def oracle_dsn(self) -> str:
        return f"{self.oracle_host}:{self.oracle_port}/{self.oracle_service}"

    def require_prod_secrets(self) -> None:
        """Fail loudly outside local if a required secret is missing."""
        if self.env != "local" and not self.oracle_app_password:
            raise RuntimeError("ORACLE_APP_PASSWORD is required when ENV != local")


def get_settings() -> Settings:
    return Settings()
