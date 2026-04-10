"""
交通シミュレーション WebSocketサーバー。
SUMO TraCI経由で車両の追加・ルート設定・位置取得を行い、
Unity側に車両状態を送信する。
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import websockets
from websockets.asyncio.server import ServerConnection

from models import (
    EdgeTrafficState,
    MessageType,
    TrafficRequest,
    TrafficResponse,
    VehicleState,
    Vector3,
)
from coordinate_transformer import CoordinateTransformer

# SUMO関連のインポート（オプショナル）
try:
    import traci
    import sumolib

    SUMO_AVAILABLE = True
except ImportError:
    SUMO_AVAILABLE = False
    print("[TrafficServer] SUMO (traci/sumolib) がインストールされていません")

SERVER_HOST = os.getenv("TRAFFIC_SERVER_HOST", "127.0.0.1")
SERVER_PORT = int(os.getenv("TRAFFIC_SERVER_PORT", "8766"))
SUMO_CONFIG = os.getenv("SUMO_CONFIG", "")
SUMO_BINARY = os.getenv("SUMO_BINARY", "sumo")  # sumo or sumo-gui
SUMO_STEP_LENGTH = float(os.getenv("SUMO_STEP_LENGTH", "0.1"))

# PLATEAU座標変換
_transformer = CoordinateTransformer()


class SUMOBridge:
    """SUMO TraCIとのブリッジ"""

    def __init__(self, config_path: str, binary: str = "sumo"):
        self.config_path = config_path
        self.binary = binary
        self.is_running = False
        self._net: Optional[Any] = None

    def start(self) -> None:
        """SUMOシミュレーションを開始する"""
        if not SUMO_AVAILABLE:
            print("[SUMOBridge] SUMOが利用できないため、スキップします")
            return

        if not self.config_path or not Path(self.config_path).exists():
            print(f"[SUMOBridge] SUMO設定ファイルが見つかりません: {self.config_path}")
            return

        cmd = [
            self.binary,
            "-c",
            self.config_path,
            "--step-length",
            str(SUMO_STEP_LENGTH),
            "--no-warnings",
            "true",
        ]

        traci.start(cmd)
        self.is_running = True

        # ネットワークファイルの読み込み（座標変換用）
        net_file = Path(self.config_path).parent / "network.net.xml"
        if net_file.exists():
            self._net = sumolib.net.readNet(str(net_file))

        print("[SUMOBridge] SUMO開始")

    def stop(self) -> None:
        """SUMOシミュレーションを停止する"""
        if self.is_running and SUMO_AVAILABLE:
            try:
                traci.close()
            except Exception:
                pass
            self.is_running = False
            print("[SUMOBridge] SUMO停止")

    def step(self) -> None:
        """1ステップ進める"""
        if self.is_running:
            traci.simulationStep()

    def get_sim_time(self) -> float:
        """シミュレーション時間を取得"""
        if self.is_running:
            return traci.simulation.getTime()
        return 0.0

    def find_nearest_edge(self, unity_x: float, unity_z: float) -> Optional[str]:
        """Unity座標からSUMOの最寄りエッジIDを返す"""
        if self._net is None:
            return None

        # Unity座標→WGS84の逆変換（CoordinateTransformerを使用）
        # Unity: x=東, z=北 → JGD2011: easting=x+offset.x, northing=z+offset.z → WGS84
        easting = unity_x + _transformer.offset.x
        northing = unity_z + _transformer.offset.z

        # JGD2011平面直角 → WGS84（pyproj逆変換）
        lon, lat = _transformer._wgs84_to_jpr.transform(
            easting, northing, direction="INVERSE"
        )

        # sumolib: WGS84 → SUMO座標系（network.net.xmlが持つ座標に変換）
        sumo_x, sumo_y = self._net.convertLonLat2XY(lon, lat)

        # 最寄りエッジを検索（内部エッジを除外）
        edges = self._net.getNeighboringEdges(sumo_x, sumo_y, r=500)
        if not edges:
            print(f"[SUMOBridge] 最寄りエッジなし: unity=({unity_x:.1f}, {unity_z:.1f}) wgs84=({lon:.6f}, {lat:.6f})")
            return None

        # 距離でソート、内部エッジ（:で始まる）を除外
        edges_sorted = sorted(edges, key=lambda e: e[1])
        for edge, dist in edges_sorted:
            eid = edge.getID()
            if not eid.startswith(":"):
                return eid

        return None

    def spawn_vehicle(
        self,
        vehicle_id: str,
        origin_edge: str,
        destination_edge: str,
        depart_speed: float = 0.0,
        vehicle_type: str = "passenger",
    ) -> bool:
        """車両を追加する。ルートが見つからない場合は近傍edgeで再試行する。"""
        if not self.is_running:
            return False

        # 同一edgeの場合、隣接edgeを目的地にする
        if origin_edge == destination_edge:
            alt = self._find_alternative_dest(origin_edge) if self._net else None
            if alt:
                destination_edge = alt
            else:
                print(f"[SUMOBridge] 同一edge＆代替なし ({vehicle_id}): {origin_edge}")
                return False

        # ルートの存在を事前確認、なければ近傍edgeで再試行
        route_edges = self._find_valid_route(origin_edge, destination_edge)
        if not route_edges:
            print(f"[SUMOBridge] ルートなし ({vehicle_id}): {origin_edge} → {destination_edge}")
            return False

        try:
            route_id = f"route_{vehicle_id}"
            traci.route.add(route_id, route_edges)
            traci.vehicle.add(
                vehicle_id,
                routeID=route_id,
                typeID=vehicle_type,
                departSpeed=str(depart_speed) if depart_speed > 0 else "max",
            )
            print(f"[SUMOBridge] 車両追加成功: {vehicle_id} ({route_edges[0]} → {route_edges[-1]}, hops={len(route_edges)})")
            return True
        except traci.exceptions.TraCIException as e:
            print(f"[SUMOBridge] 車両追加エラー ({vehicle_id}): {e}")
            return False

    def _find_valid_route(
        self, origin: str, destination: str
    ) -> Optional[list]:
        """origin→destination のルートをSUMO findRouteで検索。
        見つからなければ destination の近傍edgeで最大5回再試行する。"""
        try:
            route = traci.simulation.findRoute(origin, destination)
            if route.edges and len(route.edges) >= 2:
                return list(route.edges)
        except Exception:
            pass

        # 近傍edgeで再試行
        if self._net:
            try:
                dest_edge = self._net.getEdge(destination)
                dest_x, dest_y = dest_edge.getFromNode().getCoord()
                nearby = self._net.getNeighboringEdges(dest_x, dest_y, r=1000)
                nearby_sorted = sorted(nearby, key=lambda e: e[1])
                for alt_edge, _ in nearby_sorted[:10]:
                    alt_id = alt_edge.getID()
                    if alt_id.startswith(":") or alt_id == origin:
                        continue
                    try:
                        route = traci.simulation.findRoute(origin, alt_id)
                        if route.edges and len(route.edges) >= 2:
                            return list(route.edges)
                    except Exception:
                        continue
            except Exception:
                pass

        return None

    def _find_alternative_dest(self, edge_id: str) -> Optional[str]:
        """同一edgeを回避するため、接続先のedgeを返す"""
        try:
            edge = self._net.getEdge(edge_id)
            to_node = edge.getToNode()
            for out_edge in to_node.getOutgoing():
                eid = out_edge.getID()
                if eid != edge_id and not eid.startswith(":"):
                    return eid
            # outgoing がなければ incoming を試す
            from_node = edge.getFromNode()
            for in_edge in from_node.getIncoming():
                eid = in_edge.getID()
                if eid != edge_id and not eid.startswith(":"):
                    return eid
        except Exception:
            pass
        return None

    def remove_vehicle(self, vehicle_id: str) -> bool:
        """車両を削除する"""
        if not self.is_running:
            return False

        try:
            traci.vehicle.remove(vehicle_id)
            return True
        except traci.exceptions.TraCIException as e:
            print(f"[SUMOBridge] 車両削除エラー ({vehicle_id}): {e}")
            return False

    def set_route(self, vehicle_id: str, edge_ids: List[str]) -> bool:
        """車両のルートを変更する"""
        if not self.is_running:
            return False

        try:
            traci.vehicle.setRoute(vehicle_id, edge_ids)
            return True
        except traci.exceptions.TraCIException as e:
            print(f"[SUMOBridge] ルート変更エラー ({vehicle_id}): {e}")
            return False

    def get_all_vehicle_states(self) -> List[VehicleState]:
        """全車両の状態を取得する"""
        if not self.is_running:
            return []

        states = []
        for vid in traci.vehicle.getIDList():
            try:
                pos = traci.vehicle.getPosition(vid)  # (x, y) SUMO座標
                speed = traci.vehicle.getSpeed(vid)
                angle = traci.vehicle.getAngle(vid)
                edge_id = traci.vehicle.getRoadID(vid)
                lane_idx = traci.vehicle.getLaneIndex(vid)
                route_id = traci.vehicle.getRouteID(vid)
                is_stopped = traci.vehicle.isStopped(vid)

                # SUMO座標(x, y) → WGS84(lon, lat) → Unity座標
                unity_pos = self._sumo_to_unity(pos[0], pos[1])

                states.append(
                    VehicleState(
                        vehicle_id=vid,
                        position=Vector3(
                            x=unity_pos[0], y=unity_pos[1], z=unity_pos[2]
                        ),
                        speed=speed,
                        angle=angle,
                        edge_id=edge_id,
                        lane_index=lane_idx,
                        route_id=route_id,
                        is_stopped=is_stopped,
                    )
                )
            except traci.exceptions.TraCIException:
                continue

        return states

    def get_edge_traffic(
        self, edge_ids: Optional[List[str]] = None
    ) -> List[EdgeTrafficState]:
        """エッジごとの交通状態を取得する"""
        if not self.is_running:
            return []

        if edge_ids is None:
            edge_ids = list(traci.edge.getIDList())

        states = []
        for eid in edge_ids:
            try:
                vehicle_count = traci.edge.getLastStepVehicleNumber(eid)
                mean_speed = traci.edge.getLastStepMeanSpeed(eid)
                occupancy = traci.edge.getLastStepOccupancy(eid) / 100.0
                travel_time = traci.edge.getTraveltime(eid)

                # BPR関数ベースの渋滞レベル計算
                free_flow_speed = traci.edge.getMaxSpeed(eid) if self._net else 13.89  # 50km/h
                free_flow_time = (
                    traci.edge.getLength(eid) / free_flow_speed
                    if free_flow_speed > 0
                    else travel_time
                )

                congestion_level = self._calc_congestion_level(
                    travel_time, free_flow_time
                )

                states.append(
                    EdgeTrafficState(
                        edge_id=eid,
                        vehicle_count=vehicle_count,
                        average_speed=mean_speed,
                        occupancy=occupancy,
                        congestion_level=congestion_level,
                        travel_time=travel_time,
                        free_flow_travel_time=free_flow_time,
                    )
                )
            except traci.exceptions.TraCIException:
                continue

        return states

    def _sumo_to_unity(self, sumo_x: float, sumo_y: float) -> tuple[float, float, float]:
        """SUMO座標をUnity座標に変換"""
        if self._net is not None:
            try:
                lon, lat = self._net.convertXY2LonLat(sumo_x, sumo_y)
                return _transformer.wgs84_to_unity(lon, lat)
            except Exception:
                pass
        # フォールバック: SUMO座標をそのまま使用
        return (sumo_x, 0.0, sumo_y)

    @staticmethod
    def _calc_congestion_level(travel_time: float, free_flow_time: float) -> int:
        """
        BPR関数ベースで渋滞レベルを計算。
        travel_time / free_flow_time の比率で1-5段階に分類。
        """
        if free_flow_time <= 0:
            return 1

        ratio = travel_time / free_flow_time
        if ratio < 1.2:
            return 1  # 順調
        elif ratio < 1.5:
            return 2  # やや混雑
        elif ratio < 2.0:
            return 3  # 混雑
        elif ratio < 3.0:
            return 4  # 渋滞
        else:
            return 5  # 大渋滞


# グローバルSUMOブリッジ
_sumo_bridge: Optional[SUMOBridge] = None


async def handle_message(
    websocket: ServerConnection,
) -> None:
    """WebSocket接続を処理する"""
    print(f"[TrafficServer] クライアント接続: {websocket.remote_address}")

    try:
        async for raw_message in websocket:
            try:
                data = json.loads(raw_message)
                request = TrafficRequest(**data)
                response = process_request(request)
                await websocket.send(response.model_dump_json())
            except Exception as e:
                error_resp = TrafficResponse(
                    message_type=MessageType.ERROR,
                    error=str(e),
                )
                await websocket.send(error_resp.model_dump_json())
    except websockets.exceptions.ConnectionClosed:
        print(f"[TrafficServer] クライアント切断")


def process_request(request: TrafficRequest) -> TrafficResponse:
    """リクエストを処理してレスポンスを返す"""
    global _sumo_bridge

    if _sumo_bridge is None or not _sumo_bridge.is_running:
        return TrafficResponse(
            message_type=MessageType.ERROR,
            request_id=request.request_id,
            error="SUMO is not running",
        )

    msg_type = request.message_type

    if msg_type == MessageType.STEP:
        _sumo_bridge.step()
        vehicle_states = _sumo_bridge.get_all_vehicle_states()
        return TrafficResponse(
            message_type=MessageType.VEHICLE_UPDATES,
            request_id=request.request_id,
            vehicle_states=vehicle_states,
            sim_time=_sumo_bridge.get_sim_time(),
        )

    elif msg_type == MessageType.SPAWN_VEHICLE:
        if request.spawn_vehicle:
            sv = request.spawn_vehicle
            origin = sv.origin_edge
            dest = sv.destination_edge

            # Unity座標が渡された場合、座標からSUMO edge IDを自動解決
            if (not origin or origin == "") and sv.origin_pos:
                resolved = _sumo_bridge.find_nearest_edge(sv.origin_pos.x, sv.origin_pos.z)
                if resolved:
                    origin = resolved
                    print(f"[TrafficServer] origin edge resolved: ({sv.origin_pos.x:.1f}, {sv.origin_pos.z:.1f}) → {origin}")
                else:
                    return TrafficResponse(
                        message_type=MessageType.ERROR,
                        request_id=request.request_id,
                        error=f"Cannot resolve origin edge from pos ({sv.origin_pos.x}, {sv.origin_pos.z})",
                    )
            if (not dest or dest == "") and sv.destination_pos:
                resolved = _sumo_bridge.find_nearest_edge(sv.destination_pos.x, sv.destination_pos.z)
                if resolved:
                    dest = resolved
                    print(f"[TrafficServer] dest edge resolved: ({sv.destination_pos.x:.1f}, {sv.destination_pos.z:.1f}) → {dest}")
                else:
                    return TrafficResponse(
                        message_type=MessageType.ERROR,
                        request_id=request.request_id,
                        error=f"Cannot resolve dest edge from pos ({sv.destination_pos.x}, {sv.destination_pos.z})",
                    )

            success = _sumo_bridge.spawn_vehicle(
                sv.vehicle_id,
                origin,
                dest,
                sv.depart_speed,
                sv.vehicle_type,
            )
            if not success:
                return TrafficResponse(
                    message_type=MessageType.ERROR,
                    request_id=request.request_id,
                    error=f"Failed to spawn vehicle {sv.vehicle_id}",
                )
        return TrafficResponse(
            message_type=MessageType.VEHICLE_UPDATES,
            request_id=request.request_id,
            sim_time=_sumo_bridge.get_sim_time(),
        )

    elif msg_type == MessageType.REMOVE_VEHICLE:
        if request.remove_vehicle:
            _sumo_bridge.remove_vehicle(request.remove_vehicle.vehicle_id)
        return TrafficResponse(
            message_type=MessageType.VEHICLE_UPDATES,
            request_id=request.request_id,
            sim_time=_sumo_bridge.get_sim_time(),
        )

    elif msg_type == MessageType.SET_ROUTE:
        if request.set_route:
            sr = request.set_route
            _sumo_bridge.set_route(sr.vehicle_id, sr.edge_ids)
        return TrafficResponse(
            message_type=MessageType.VEHICLE_UPDATES,
            request_id=request.request_id,
            sim_time=_sumo_bridge.get_sim_time(),
        )

    elif msg_type == MessageType.GET_STATE:
        vehicle_states = _sumo_bridge.get_all_vehicle_states()
        return TrafficResponse(
            message_type=MessageType.VEHICLE_UPDATES,
            request_id=request.request_id,
            vehicle_states=vehicle_states,
            sim_time=_sumo_bridge.get_sim_time(),
        )

    elif msg_type == MessageType.GET_TRAFFIC:
        edge_traffic = _sumo_bridge.get_edge_traffic(request.edge_ids)
        return TrafficResponse(
            message_type=MessageType.TRAFFIC_STATE,
            request_id=request.request_id,
            edge_traffic=edge_traffic,
            sim_time=_sumo_bridge.get_sim_time(),
        )

    return TrafficResponse(
        message_type=MessageType.ERROR,
        request_id=request.request_id,
        error=f"Unknown message type: {msg_type}",
    )


async def main() -> None:
    """サーバーのメインエントリポイント"""
    global _sumo_bridge

    # SUMO初期化
    if SUMO_AVAILABLE and SUMO_CONFIG:
        _sumo_bridge = SUMOBridge(SUMO_CONFIG, SUMO_BINARY)
        _sumo_bridge.start()
    else:
        print("[TrafficServer] SUMOなしで起動（モックモード）")
        _sumo_bridge = None

    print(f"[TrafficServer] WebSocketサーバー起動: ws://{SERVER_HOST}:{SERVER_PORT}")

    try:
        async with websockets.serve(
            handle_message,
            SERVER_HOST,
            SERVER_PORT,
        ):
            await asyncio.Future()  # 永久に待機
    finally:
        if _sumo_bridge:
            _sumo_bridge.stop()


if __name__ == "__main__":
    asyncio.run(main())
