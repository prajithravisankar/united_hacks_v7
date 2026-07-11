from boys.common.v1 import money_pb2 as _money_pb2
from google.protobuf.internal import containers as _containers
from google.protobuf.internal import enum_type_wrapper as _enum_type_wrapper
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Iterable as _Iterable, Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class DriveMode(int, metaclass=_enum_type_wrapper.EnumTypeWrapper):
    __slots__ = ()
    DRIVE_MODE_UNSPECIFIED: _ClassVar[DriveMode]
    DRIVE_MODE_AUTO: _ClassVar[DriveMode]
    DRIVE_MODE_USER: _ClassVar[DriveMode]
DRIVE_MODE_UNSPECIFIED: DriveMode
DRIVE_MODE_AUTO: DriveMode
DRIVE_MODE_USER: DriveMode

class NavPoint(_message.Message):
    __slots__ = ("date", "nav", "events")
    DATE_FIELD_NUMBER: _ClassVar[int]
    NAV_FIELD_NUMBER: _ClassVar[int]
    EVENTS_FIELD_NUMBER: _ClassVar[int]
    date: str
    nav: _money_pb2.Money
    events: _containers.RepeatedScalarFieldContainer[str]
    def __init__(self, date: _Optional[str] = ..., nav: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., events: _Optional[_Iterable[str]] = ...) -> None: ...

class GetNavCurveRequest(_message.Message):
    __slots__ = ("commitment_id", "start_date", "end_date", "drive_mode", "principal_cents")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    START_DATE_FIELD_NUMBER: _ClassVar[int]
    END_DATE_FIELD_NUMBER: _ClassVar[int]
    DRIVE_MODE_FIELD_NUMBER: _ClassVar[int]
    PRINCIPAL_CENTS_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    start_date: str
    end_date: str
    drive_mode: DriveMode
    principal_cents: int
    def __init__(self, commitment_id: _Optional[str] = ..., start_date: _Optional[str] = ..., end_date: _Optional[str] = ..., drive_mode: _Optional[_Union[DriveMode, str]] = ..., principal_cents: _Optional[int] = ...) -> None: ...

class NavCurve(_message.Message):
    __slots__ = ("commitment_id", "points")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    POINTS_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    points: _containers.RepeatedCompositeFieldContainer[NavPoint]
    def __init__(self, commitment_id: _Optional[str] = ..., points: _Optional[_Iterable[_Union[NavPoint, _Mapping]]] = ...) -> None: ...

class GetValuationRequest(_message.Message):
    __slots__ = ("commitment_id", "as_of", "drive_mode", "principal_cents", "start_date")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    AS_OF_FIELD_NUMBER: _ClassVar[int]
    DRIVE_MODE_FIELD_NUMBER: _ClassVar[int]
    PRINCIPAL_CENTS_FIELD_NUMBER: _ClassVar[int]
    START_DATE_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    as_of: str
    drive_mode: DriveMode
    principal_cents: int
    start_date: str
    def __init__(self, commitment_id: _Optional[str] = ..., as_of: _Optional[str] = ..., drive_mode: _Optional[_Union[DriveMode, str]] = ..., principal_cents: _Optional[int] = ..., start_date: _Optional[str] = ...) -> None: ...

class Valuation(_message.Message):
    __slots__ = ("nav", "principal", "gain", "carry_preview", "user_take_home")
    NAV_FIELD_NUMBER: _ClassVar[int]
    PRINCIPAL_FIELD_NUMBER: _ClassVar[int]
    GAIN_FIELD_NUMBER: _ClassVar[int]
    CARRY_PREVIEW_FIELD_NUMBER: _ClassVar[int]
    USER_TAKE_HOME_FIELD_NUMBER: _ClassVar[int]
    nav: _money_pb2.Money
    principal: _money_pb2.Money
    gain: _money_pb2.Money
    carry_preview: _money_pb2.Money
    user_take_home: _money_pb2.Money
    def __init__(self, nav: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., principal: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., gain: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., carry_preview: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., user_take_home: _Optional[_Union[_money_pb2.Money, _Mapping]] = ...) -> None: ...

class ProjectOutcomesRequest(_message.Message):
    __slots__ = ("commitment_id", "drive_mode", "principal_cents", "start_date", "as_of")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    DRIVE_MODE_FIELD_NUMBER: _ClassVar[int]
    PRINCIPAL_CENTS_FIELD_NUMBER: _ClassVar[int]
    START_DATE_FIELD_NUMBER: _ClassVar[int]
    AS_OF_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    drive_mode: DriveMode
    principal_cents: int
    start_date: str
    as_of: str
    def __init__(self, commitment_id: _Optional[str] = ..., drive_mode: _Optional[_Union[DriveMode, str]] = ..., principal_cents: _Optional[int] = ..., start_date: _Optional[str] = ..., as_of: _Optional[str] = ...) -> None: ...

class Projection(_message.Message):
    __slots__ = ("cash_now", "ride_p10", "ride_p50", "ride_p90")
    CASH_NOW_FIELD_NUMBER: _ClassVar[int]
    RIDE_P10_FIELD_NUMBER: _ClassVar[int]
    RIDE_P50_FIELD_NUMBER: _ClassVar[int]
    RIDE_P90_FIELD_NUMBER: _ClassVar[int]
    cash_now: _money_pb2.Money
    ride_p10: _money_pb2.Money
    ride_p50: _money_pb2.Money
    ride_p90: _money_pb2.Money
    def __init__(self, cash_now: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., ride_p10: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., ride_p50: _Optional[_Union[_money_pb2.Money, _Mapping]] = ..., ride_p90: _Optional[_Union[_money_pb2.Money, _Mapping]] = ...) -> None: ...

class ListOpenMarketsRequest(_message.Message):
    __slots__ = ("as_of",)
    AS_OF_FIELD_NUMBER: _ClassVar[int]
    as_of: str
    def __init__(self, as_of: _Optional[str] = ...) -> None: ...

class Market(_message.Message):
    __slots__ = ("market_id", "description", "implied_prob", "model_prob")
    MARKET_ID_FIELD_NUMBER: _ClassVar[int]
    DESCRIPTION_FIELD_NUMBER: _ClassVar[int]
    IMPLIED_PROB_FIELD_NUMBER: _ClassVar[int]
    MODEL_PROB_FIELD_NUMBER: _ClassVar[int]
    market_id: str
    description: str
    implied_prob: float
    model_prob: float
    def __init__(self, market_id: _Optional[str] = ..., description: _Optional[str] = ..., implied_prob: _Optional[float] = ..., model_prob: _Optional[float] = ...) -> None: ...

class OpenMarkets(_message.Message):
    __slots__ = ("markets",)
    MARKETS_FIELD_NUMBER: _ClassVar[int]
    markets: _containers.RepeatedCompositeFieldContainer[Market]
    def __init__(self, markets: _Optional[_Iterable[_Union[Market, _Mapping]]] = ...) -> None: ...

class PlaceUserBetRequest(_message.Message):
    __slots__ = ("commitment_id", "market_id", "side", "stake")
    COMMITMENT_ID_FIELD_NUMBER: _ClassVar[int]
    MARKET_ID_FIELD_NUMBER: _ClassVar[int]
    SIDE_FIELD_NUMBER: _ClassVar[int]
    STAKE_FIELD_NUMBER: _ClassVar[int]
    commitment_id: str
    market_id: str
    side: str
    stake: _money_pb2.Money
    def __init__(self, commitment_id: _Optional[str] = ..., market_id: _Optional[str] = ..., side: _Optional[str] = ..., stake: _Optional[_Union[_money_pb2.Money, _Mapping]] = ...) -> None: ...

class BetAck(_message.Message):
    __slots__ = ("accepted", "reason")
    ACCEPTED_FIELD_NUMBER: _ClassVar[int]
    REASON_FIELD_NUMBER: _ClassVar[int]
    accepted: bool
    reason: str
    def __init__(self, accepted: _Optional[bool] = ..., reason: _Optional[str] = ...) -> None: ...
