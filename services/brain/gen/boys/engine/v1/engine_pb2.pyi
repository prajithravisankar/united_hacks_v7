from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from typing import ClassVar as _ClassVar, Optional as _Optional

DESCRIPTOR: _descriptor.FileDescriptor

class StartReplayRequest(_message.Message):
    __slots__ = ("commitment_id", "speed")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    SPEED_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    speed: float
    def __init__(self, commitment_id: _Optional[str] = ..., speed: _Optional[float] = ...) -> None: ...

class PauseRequest(_message.Message):
    __slots__ = ("commitment_id",)
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    def __init__(self, commitment_id: _Optional[str] = ...) -> None: ...

class SetSpeedRequest(_message.Message):
    __slots__ = ("commitment_id", "speed")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    SPEED_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    speed: float
    def __init__(self, commitment_id: _Optional[str] = ..., speed: _Optional[float] = ...) -> None: ...

class GetReplayStateRequest(_message.Message):
    __slots__ = ("commitment_id",)
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    def __init__(self, commitment_id: _Optional[str] = ...) -> None: ...

class ReplayState(_message.Message):
    __slots__ = ("commitment_id", "position", "speed", "running", "current_sim_date")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    POSITION_FIELD_NUMBER: _ClassVar[int]
    SPEED_FIELD_NUMBER: _ClassVar[int]
    RUNNING_FIELD_NUMBER: _ClassVar[int]
    CURRENT_SIM_DATE_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    position: int
    speed: float
    running: bool
    current_sim_date: str
    def __init__(self, commitment_id: _Optional[str] = ..., position: _Optional[int] = ..., speed: _Optional[float] = ..., running: _Optional[bool] = ..., current_sim_date: _Optional[str] = ...) -> None: ...
