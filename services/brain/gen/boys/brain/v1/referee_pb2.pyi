from google.protobuf.internal import containers as _containers
from google.protobuf.internal import enum_type_wrapper as _enum_type_wrapper
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Iterable as _Iterable, Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class Verdict(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    VERDICT_UNSPECIFIED: _ClassVar[Verdict]
    VERDICT_ACCEPT: _ClassVar[Verdict]
    VERDICT_REVISE: _ClassVar[Verdict]
    VERDICT_REJECT: _ClassVar[Verdict]

class Verifiability(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    VERIFIABILITY_UNSPECIFIED: _ClassVar[Verifiability]
    VERIFIABILITY_STRONG: _ClassVar[Verifiability]
    VERIFIABILITY_WEAK: _ClassVar[Verifiability]
    VERIFIABILITY_NONE: _ClassVar[Verifiability]
VERDICT_UNSPECIFIED: Verdict
VERDICT_ACCEPT: Verdict
VERDICT_REVISE: Verdict
VERDICT_REJECT: Verdict
VERIFIABILITY_UNSPECIFIED: Verifiability
VERIFIABILITY_STRONG: Verifiability
VERIFIABILITY_WEAK: Verifiability
VERIFIABILITY_NONE: Verifiability

class MilestoneSpec(_message.Message):
    __slots__ = ("ordinal", "description", "target_metric", "due_date")
    ORDINAL_FIELD_NUMBER: _ClassVar[int]
    DESCRIPTION_FIELD_NUMBER: _ClassVar[int]
    TARGET_METRIC_FIELD_NUMBER: _ClassVar[int]
    DUE_DATE_FIELD_NUMBER: _ClassVar[int]
    ordinal: int
    description: str
    target_metric: str
    due_date: str
    def __init__(self, ordinal: _Optional[int] = ..., description: _Optional[str] = ..., target_metric: _Optional[str] = ..., due_date: _Optional[str] = ...) -> None: ...

class ValidateGoalRequest(_message.Message):
    __slots__ = ("goal_text", "deadline", "milestones")
    GOAL_TEXT_FIELD_NUMBER: _ClassVar[int]
    DEADLINE_FIELD_NUMBER: _ClassVar[int]
    MILESTONES_FIELD_NUMBER: _ClassVar[int]
    goal_text: str
    deadline: str
    milestones: _containers.RepeatedCompositeFieldContainer[MilestoneSpec]
    def __init__(self, goal_text: _Optional[str] = ..., deadline: _Optional[str] = ..., milestones: _Optional[_Iterable[_Union[MilestoneSpec, _Mapping]]] = ...) -> None: ...

class GoalVerdict(_message.Message):
    __slots__ = ("verdict", "verifiability", "required_proof_type", "suggested_rewrite", "reasoning")
    VERDICT_FIELD_NUMBER: _ClassVar[int]
    VERIFIABILITY_FIELD_NUMBER: _ClassVar[int]
    REQUIRED_PROOF_TYPE_FIELD_NUMBER: _ClassVar[int]
    SUGGESTED_REWRITE_FIELD_NUMBER: _ClassVar[int]
    REASONING_FIELD_NUMBER: _ClassVar[int]
    verdict: Verdict
    verifiability: Verifiability
    required_proof_type: str
    suggested_rewrite: str
    reasoning: str
    def __init__(self, verdict: _Optional[_Union[Verdict, str]] = ..., verifiability: _Optional[_Union[Verifiability, str]] = ..., required_proof_type: _Optional[str] = ..., suggested_rewrite: _Optional[str] = ..., reasoning: _Optional[str] = ...) -> None: ...

class CheckProofRequest(_message.Message):
    __slots__ = ("milestone_id", "claim", "evidence", "mime")
    MILESTONE_ID_FIELD_NUMBER: _ClassVar[int]
    CLAIM_FIELD_NUMBER: _ClassVar[int]
    EVIDENCE_FIELD_NUMBER: _ClassVar[int]
    MIME_FIELD_NUMBER: _ClassVar[int]
    milestone_id: str
    claim: str
    evidence: bytes
    mime: str
    def __init__(self, milestone_id: _Optional[str] = ..., claim: _Optional[str] = ..., evidence: _Optional[bytes] = ..., mime: _Optional[str] = ...) -> None: ...

class ProofVerdict(_message.Message):
    __slots__ = ("supports_claim", "confidence", "reasoning", "insufficiency_reason")
    SUPPORTS_CLAIM_FIELD_NUMBER: _ClassVar[int]
    CONFIDENCE_FIELD_NUMBER: _ClassVar[int]
    REASONING_FIELD_NUMBER: _ClassVar[int]
    INSUFFICIENCY_REASON_FIELD_NUMBER: _ClassVar[int]
    supports_claim: bool
    confidence: float
    reasoning: str
    insufficiency_reason: str
    def __init__(self, supports_claim: _Optional[bool] = ..., confidence: _Optional[float] = ..., reasoning: _Optional[str] = ..., insufficiency_reason: _Optional[str] = ...) -> None: ...
