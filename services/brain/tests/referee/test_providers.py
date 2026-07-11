"""B10/R3: GeminiProvider parsing (mocked httpx, no network) + proof-check fallback."""

from __future__ import annotations

import httpx
import pytest

from app.referee.models import Verdict
from app.referee.providers import GeminiProvider, MalformedResponse, SeededProvider
from app.referee.service import RefereeService

_GOOD = (
    '{"verdict":"ACCEPT","verifiability":"STRONG","required_proof_type":"grade_screenshot",'
    '"suggested_rewrite":"","reasoning":"ok"}'
)


class _FakeResp:
    def __init__(self, text: str):
        self._text = text

    def raise_for_status(self) -> None:
        pass

    def json(self) -> dict:  # type: ignore[type-arg]
        return {"candidates": [{"content": {"parts": [{"text": self._text}]}}]}


def test_gemini_parses_valid_json(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(httpx, "post", lambda *a, **k: _FakeResp(_GOOD))
    v = GeminiProvider("key").validate_goal("Score 90% in History")
    assert v.verdict == Verdict.ACCEPT
    assert v.required_proof_type == "grade_screenshot"


def test_gemini_raises_malformed_on_junk(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(httpx, "post", lambda *a, **k: _FakeResp("sorry, no json here"))
    with pytest.raises(MalformedResponse):
        GeminiProvider("key").validate_goal("Score 90%")


class _FailingProvider:
    def validate_goal(self, goal_text: str):  # type: ignore[no-untyped-def]
        raise TimeoutError("hang")

    def check_proof(self, claim: str, evidence: bytes, mime: str):  # type: ignore[no-untyped-def]
        raise TimeoutError("hang")


def test_check_proof_falls_back_on_provider_failure() -> None:
    svc = RefereeService(provider=_FailingProvider(), fallback=SeededProvider())
    verdict = svc.check_proof("m1", "Scored 87", b"a full grade screenshot", "image/png")
    assert verdict.supports_claim is True  # seeded fallback answered
