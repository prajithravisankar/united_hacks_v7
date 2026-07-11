"""RefereeService — orchestrates structural checks + an LLM provider with a seeded
fallback, so goal/proof flows NEVER block even when the LLM is down."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from typing import TypeVar

from app.referee.models import GoalVerdict, ProofVerdict, Verdict, Verifiability
from app.referee.providers import (
    GeminiProvider,
    LLMProvider,
    MalformedResponse,
    SeededProvider,
)

_T = TypeVar("_T")

MAX_GOAL_CHARS = 2000
MAX_EVIDENCE_BYTES = 5_000_000


class InvalidGoal(Exception):
    pass


class OversizedEvidence(Exception):
    pass


@dataclass(frozen=True)
class MilestoneSpec:
    ordinal: int
    description: str
    target_metric: str
    due_date: str  # ISO


class RefereeService:
    def __init__(self, provider: LLMProvider, fallback: LLMProvider):
        self._provider = provider
        self._fallback = fallback

    def validate_goal(
        self, goal_text: str, deadline: str, milestones: list[MilestoneSpec]
    ) -> GoalVerdict:
        if not goal_text or not goal_text.strip():
            raise InvalidGoal("goal text is empty")
        goal_text = goal_text[:MAX_GOAL_CHARS]  # truncate oversized input

        # structural: a milestone can't be due after the deadline
        if deadline and any(m.due_date and m.due_date > deadline for m in milestones):
            return GoalVerdict(
                verdict=Verdict.REJECT,
                verifiability=Verifiability.NONE,
                reasoning="A milestone is due after the deadline.",
            )

        try:
            return self._with_retry(lambda: self._provider.validate_goal(goal_text))
        except Exception:  # noqa: BLE001 - any provider failure -> deterministic fallback
            return self._fallback.validate_goal(goal_text)

    def check_proof(
        self, milestone_id: str, claim: str, evidence: bytes, mime: str
    ) -> ProofVerdict:
        if len(evidence) > MAX_EVIDENCE_BYTES:
            raise OversizedEvidence("evidence exceeds the size limit")
        try:
            return self._with_retry(lambda: self._provider.check_proof(claim, evidence, mime))
        except Exception:  # noqa: BLE001
            return self._fallback.check_proof(claim, evidence, mime)

    @staticmethod
    def _with_retry(call: Callable[[], _T]) -> _T:
        """Retry once on a malformed response; other errors (e.g. timeout) propagate."""
        try:
            return call()
        except MalformedResponse:
            return call()


def make_referee_service(ai_mode: str, gemini_api_key: str) -> RefereeService:
    seeded = SeededProvider()
    if ai_mode == "live" and gemini_api_key:
        return RefereeService(provider=GeminiProvider(gemini_api_key), fallback=seeded)
    return RefereeService(provider=seeded, fallback=seeded)
