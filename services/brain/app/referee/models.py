"""Schema-validated verdicts (pydantic). Providers must return these — never free text."""

from __future__ import annotations

from enum import Enum

from pydantic import BaseModel, Field


class Verdict(str, Enum):
    ACCEPT = "ACCEPT"
    REVISE = "REVISE"
    REJECT = "REJECT"


class Verifiability(str, Enum):
    STRONG = "STRONG"
    WEAK = "WEAK"
    NONE = "NONE"


class GoalVerdict(BaseModel):
    verdict: Verdict
    verifiability: Verifiability
    required_proof_type: str = ""
    suggested_rewrite: str = ""
    reasoning: str = ""


class ProofVerdict(BaseModel):
    supports_claim: bool
    confidence: float = Field(ge=0.0, le=1.0)
    reasoning: str = ""
    insufficiency_reason: str = ""
