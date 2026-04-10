"""
交通シミュレーションサーバーのデータモデル（Pydantic）
"""

from __future__ import annotations

from enum import Enum
from typing import List, Optional, Tuple

from pydantic import BaseModel, Field


class Vector3(BaseModel):
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0


class VehicleState(BaseModel):
    """SUMOから取得した車両の状態"""

    vehicle_id: str
    position: Vector3
    speed: float = Field(description="現在の速度 (m/s)")
    angle: float = Field(description="進行方向の角度 (度)")
    edge_id: str = Field(default="", description="現在のSUMOエッジID")
    lane_index: int = Field(default=0, description="車線インデックス")
    route_id: str = Field(default="", description="ルートID")
    is_stopped: bool = False


class EdgeTrafficState(BaseModel):
    """道路エッジごとの交通状態"""

    edge_id: str
    vehicle_count: int = 0
    average_speed: float = Field(default=0.0, description="平均速度 (m/s)")
    occupancy: float = Field(default=0.0, description="占有率 (0.0-1.0)")
    congestion_level: int = Field(
        default=1, ge=1, le=5, description="渋滞レベル (1=順調, 5=渋滞)"
    )
    travel_time: float = Field(default=0.0, description="走行時間 (秒)")
    free_flow_travel_time: float = Field(
        default=0.0, description="自由流走行時間 (秒)"
    )


class MessageType(str, Enum):
    """WebSocketメッセージの種類"""

    # クライアント → サーバー
    SPAWN_VEHICLE = "spawn_vehicle"
    REMOVE_VEHICLE = "remove_vehicle"
    SET_ROUTE = "set_route"
    GET_STATE = "get_state"
    GET_TRAFFIC = "get_traffic"
    STEP = "step"

    # サーバー → クライアント
    VEHICLE_UPDATES = "vehicle_updates"
    TRAFFIC_STATE = "traffic_state"
    ERROR = "error"


class SpawnVehicleRequest(BaseModel):
    """車両スポーンリクエスト"""

    vehicle_id: str
    origin_edge: str = Field(default="", description="出発エッジID（SUMO形式、空ならorigin_posから自動解決）")
    destination_edge: str = Field(default="", description="目的エッジID（SUMO形式、空ならdestination_posから自動解決）")
    origin_pos: Optional[Vector3] = Field(default=None, description="出発位置（Unity座標）")
    destination_pos: Optional[Vector3] = Field(default=None, description="目的位置（Unity座標）")
    depart_speed: float = Field(default=0.0, description="出発速度 (m/s)")
    vehicle_type: str = Field(default="passenger", description="車両タイプ")


class RemoveVehicleRequest(BaseModel):
    """車両削除リクエスト"""

    vehicle_id: str


class SetRouteRequest(BaseModel):
    """ルート変更リクエスト"""

    vehicle_id: str
    edge_ids: List[str] = Field(description="新しいルートのエッジIDリスト")


class TrafficRequest(BaseModel):
    """交通サーバーへのリクエスト"""

    message_type: MessageType
    request_id: str = ""
    spawn_vehicle: Optional[SpawnVehicleRequest] = None
    remove_vehicle: Optional[RemoveVehicleRequest] = None
    set_route: Optional[SetRouteRequest] = None
    edge_ids: Optional[List[str]] = None  # GET_TRAFFIC用


class TrafficResponse(BaseModel):
    """交通サーバーからのレスポンス"""

    message_type: MessageType
    request_id: str = ""
    vehicle_states: Optional[List[VehicleState]] = None
    edge_traffic: Optional[List[EdgeTrafficState]] = None
    sim_time: float = 0.0
    error: Optional[str] = None
