from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from typing import ClassVar as _ClassVar, Optional as _Optional

DESCRIPTOR: _descriptor.FileDescriptor

class StartReplayRequest(_message.Message):
    __slots__ = ("goal_id", "speed")
    GOAL_ID_FIELD_NUMBER: _ClassVar[int]
    SPEED_FIELD_NUMBER: _ClassVar[int]
    goal_id: str
    speed: float
    def __init__(self, goal_id: _Optional[str] = ..., speed: _Optional[float] = ...) -> None: ...

class PauseRequest(_message.Message):
    __slots__ = ("goal_id",)
    GOAL_ID_FIELD_NUMBER: _ClassVar[int]
    goal_id: str
    def __init__(self, goal_id: _Optional[str] = ...) -> None: ...

class SetSpeedRequest(_message.Message):
    __slots__ = ("goal_id", "speed")
    GOAL_ID_FIELD_NUMBER: _ClassVar[int]
    SPEED_FIELD_NUMBER: _ClassVar[int]
    goal_id: str
    speed: float
    def __init__(self, goal_id: _Optional[str] = ..., speed: _Optional[float] = ...) -> None: ...

class GetReplayStateRequest(_message.Message):
    __slots__ = ("goal_id",)
    GOAL_ID_FIELD_NUMBER: _ClassVar[int]
    goal_id: str
    def __init__(self, goal_id: _Optional[str] = ...) -> None: ...

class ReplayState(_message.Message):
    __slots__ = ("goal_id", "position", "speed", "running")
    GOAL_ID_FIELD_NUMBER: _ClassVar[int]
    POSITION_FIELD_NUMBER: _ClassVar[int]
    SPEED_FIELD_NUMBER: _ClassVar[int]
    RUNNING_FIELD_NUMBER: _ClassVar[int]
    goal_id: str
    position: int
    speed: float
    running: bool
    def __init__(self, goal_id: _Optional[str] = ..., position: _Optional[int] = ..., speed: _Optional[float] = ..., running: _Optional[bool] = ...) -> None: ...
