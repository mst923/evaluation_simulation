"""
OSMからいわき市の道路ネットワークを抽出し、GeoJSON + NetworkXグラフとして出力するモジュール。

osmnxを使用して道路ネットワーク（ノード・エッジ・属性）を取得し、
PLATEAU座標系に変換して構造化データとして出力する。

出力するGeoJSON:
  - nodes: 交差点のポイントフィーチャ（id, Unity座標, 接続エッジ数）
  - edges: 道路区間のラインフィーチャ（id, 始点・終点, 車線数, 速度制限, 一方通行, 道路種別, 長さ）
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import networkx as nx
import osmnx as ox

from coordinate_transformer import CoordinateTransformer, PLATEAUOffset


@dataclass
class RoadNode:
    """道路ネットワークのノード（交差点）"""

    id: int
    lon: float
    lat: float
    unity_x: float = 0.0
    unity_y: float = 0.0
    unity_z: float = 0.0
    connected_edge_count: int = 0


@dataclass
class RoadEdge:
    """道路ネットワークのエッジ（道路区間）"""

    id: str
    source_node: int
    target_node: int
    length_meters: float = 0.0
    lanes: int = 1
    speed_limit_kmh: float = 40.0
    oneway: bool = False
    highway_type: str = ""
    name: str = ""
    # Unity座標での道路線形（始点→終点のポイント列）
    geometry_unity: List[Tuple[float, float, float]] = field(default_factory=list)


def _parse_lanes(lanes_attr: Any) -> int:
    """osmnxのlanes属性をintに変換"""
    if lanes_attr is None:
        return 1
    if isinstance(lanes_attr, list):
        # 複数値がある場合は最初を使用
        lanes_attr = lanes_attr[0]
    try:
        return max(1, int(lanes_attr))
    except (ValueError, TypeError):
        return 1


def _parse_speed(speed_attr: Any, highway_type: str) -> float:
    """osmnxのmaxspeed属性をkm/hに変換"""
    if speed_attr is not None:
        if isinstance(speed_attr, list):
            speed_attr = speed_attr[0]
        try:
            # "40 km/h" や "40" のような形式を処理
            speed_str = str(speed_attr).replace("km/h", "").strip()
            return float(speed_str)
        except (ValueError, TypeError):
            pass

    # highway_typeからデフォルト速度を推定
    default_speeds = {
        "motorway": 100.0,
        "motorway_link": 60.0,
        "trunk": 60.0,
        "trunk_link": 40.0,
        "primary": 50.0,
        "primary_link": 30.0,
        "secondary": 40.0,
        "secondary_link": 30.0,
        "tertiary": 30.0,
        "tertiary_link": 20.0,
        "residential": 30.0,
        "living_street": 20.0,
        "unclassified": 30.0,
        "service": 20.0,
    }
    return default_speeds.get(highway_type, 30.0)


def _parse_highway_type(highway_attr: Any) -> str:
    """osmnxのhighway属性を文字列に変換"""
    if highway_attr is None:
        return "unclassified"
    if isinstance(highway_attr, list):
        return str(highway_attr[0])
    return str(highway_attr)


def _parse_oneway(oneway_attr: Any) -> bool:
    """osmnxのoneway属性をboolに変換"""
    if oneway_attr is None:
        return False
    if isinstance(oneway_attr, bool):
        return oneway_attr
    return str(oneway_attr).lower() in ("yes", "true", "1")


def _parse_name(name_attr: Any) -> str:
    """osmnxのname属性を文字列に変換"""
    if name_attr is None:
        return ""
    if isinstance(name_attr, list):
        return str(name_attr[0])
    return str(name_attr)


class OSMNetworkBuilder:
    """OSMから道路ネットワークを構築するクラス"""

    def __init__(
        self,
        transformer: CoordinateTransformer | None = None,
    ):
        self.transformer = transformer or CoordinateTransformer()
        self.graph: Optional[nx.MultiDiGraph] = None
        self.nodes: Dict[int, RoadNode] = {}
        self.edges: Dict[str, RoadEdge] = {}

    def fetch_network(
        self,
        place: str | None = None,
        bbox: Tuple[float, float, float, float] | None = None,
        network_type: str = "drive",
    ) -> nx.MultiDiGraph:
        """
        OSMから道路ネットワークを取得する。

        Args:
            place: 地名（例: "いわき市, 福島県, 日本"）
            bbox: バウンディングボックス (north, south, east, west)
            network_type: ネットワーク種別（"drive", "walk", "all"）

        Returns:
            NetworkXグラフ
        """
        if bbox:
            north, south, east, west = bbox
            self.graph = ox.graph_from_bbox(
                bbox=(west, south, east, north),
                network_type=network_type,
            )
        elif place:
            self.graph = ox.graph_from_place(
                place,
                network_type=network_type,
            )
        else:
            raise ValueError("place or bbox must be specified")

        print(
            f"[OSMNetworkBuilder] ネットワーク取得完了: "
            f"ノード={self.graph.number_of_nodes()}, "
            f"エッジ={self.graph.number_of_edges()}"
        )
        return self.graph

    def build_network(self) -> Tuple[Dict[int, RoadNode], Dict[str, RoadEdge]]:
        """
        取得したグラフからノードとエッジのデータを構築する。
        座標はPLATEAU Unity座標に変換される。

        Returns:
            (nodes, edges) タプル
        """
        if self.graph is None:
            raise RuntimeError("fetch_network() を先に呼び出してください")

        # ノードの構築
        self.nodes = {}
        for node_id, data in self.graph.nodes(data=True):
            lon = data["x"]
            lat = data["y"]
            ux, uy, uz = self.transformer.wgs84_to_unity(lon, lat)

            self.nodes[node_id] = RoadNode(
                id=node_id,
                lon=lon,
                lat=lat,
                unity_x=ux,
                unity_y=uy,
                unity_z=uz,
            )

        # エッジの構築
        self.edges = {}
        for u, v, key, data in self.graph.edges(keys=True, data=True):
            edge_id = f"{u}-{v}-{key}"
            highway_type = _parse_highway_type(data.get("highway"))

            # ジオメトリの変換
            geometry_unity = []
            if "geometry" in data:
                # LineStringジオメトリがある場合
                coords = list(data["geometry"].coords)
                for lon, lat in coords:
                    ux, uy, uz = self.transformer.wgs84_to_unity(lon, lat)
                    geometry_unity.append((ux, uy, uz))
            else:
                # ジオメトリがない場合は始点・終点のみ
                src = self.nodes[u]
                tgt = self.nodes[v]
                geometry_unity = [
                    (src.unity_x, src.unity_y, src.unity_z),
                    (tgt.unity_x, tgt.unity_y, tgt.unity_z),
                ]

            edge = RoadEdge(
                id=edge_id,
                source_node=u,
                target_node=v,
                length_meters=data.get("length", 0.0),
                lanes=_parse_lanes(data.get("lanes")),
                speed_limit_kmh=_parse_speed(data.get("maxspeed"), highway_type),
                oneway=_parse_oneway(data.get("oneway")),
                highway_type=highway_type,
                name=_parse_name(data.get("name")),
                geometry_unity=geometry_unity,
            )
            self.edges[edge_id] = edge

        # ノードの接続エッジ数を計算
        for edge in self.edges.values():
            if edge.source_node in self.nodes:
                self.nodes[edge.source_node].connected_edge_count += 1
            if edge.target_node in self.nodes:
                self.nodes[edge.target_node].connected_edge_count += 1

        print(
            f"[OSMNetworkBuilder] ネットワーク構築完了: "
            f"ノード={len(self.nodes)}, エッジ={len(self.edges)}"
        )
        return self.nodes, self.edges

    def export_geojson(self, output_path: str | Path) -> None:
        """
        ノードとエッジをGeoJSONファイルに出力する。
        座標はUnity座標系で出力される。

        Args:
            output_path: 出力先ファイルパス
        """
        output_path = Path(output_path)
        output_path.parent.mkdir(parents=True, exist_ok=True)

        # ノードのフィーチャ
        node_features = []
        for node in self.nodes.values():
            feature = {
                "type": "Feature",
                "geometry": {
                    "type": "Point",
                    "coordinates": [node.unity_x, node.unity_z],
                },
                "properties": {
                    "type": "node",
                    "id": node.id,
                    "unity_x": round(node.unity_x, 2),
                    "unity_y": round(node.unity_y, 2),
                    "unity_z": round(node.unity_z, 2),
                    "lon": node.lon,
                    "lat": node.lat,
                    "connected_edge_count": node.connected_edge_count,
                },
            }
            node_features.append(feature)

        # エッジのフィーチャ
        edge_features = []
        for edge in self.edges.values():
            coordinates = [[p[0], p[2]] for p in edge.geometry_unity]  # x, z
            feature = {
                "type": "Feature",
                "geometry": {
                    "type": "LineString",
                    "coordinates": coordinates,
                },
                "properties": {
                    "type": "edge",
                    "id": edge.id,
                    "source_node": edge.source_node,
                    "target_node": edge.target_node,
                    "length_meters": round(edge.length_meters, 2),
                    "lanes": edge.lanes,
                    "speed_limit_kmh": edge.speed_limit_kmh,
                    "oneway": edge.oneway,
                    "highway_type": edge.highway_type,
                    "name": edge.name,
                    "geometry_unity": [
                        {"x": round(p[0], 2), "y": round(p[1], 2), "z": round(p[2], 2)}
                        for p in edge.geometry_unity
                    ],
                },
            }
            edge_features.append(feature)

        geojson = {
            "type": "FeatureCollection",
            "features": node_features + edge_features,
            "metadata": {
                "coordinate_system": "PLATEAU_Unity",
                "description": "OSM road network converted to PLATEAU Unity coordinates",
                "node_count": len(self.nodes),
                "edge_count": len(self.edges),
            },
        }

        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(geojson, f, ensure_ascii=False, indent=2)

        print(f"[OSMNetworkBuilder] GeoJSON出力完了: {output_path}")

    def export_network_summary(self) -> Dict[str, Any]:
        """ネットワークの概要統計を返す"""
        if not self.edges:
            return {}

        highway_counts: Dict[str, int] = {}
        total_length = 0.0
        for edge in self.edges.values():
            highway_counts[edge.highway_type] = (
                highway_counts.get(edge.highway_type, 0) + 1
            )
            total_length += edge.length_meters

        return {
            "node_count": len(self.nodes),
            "edge_count": len(self.edges),
            "total_length_km": round(total_length / 1000, 2),
            "highway_type_counts": highway_counts,
        }


def build_iwaki_network(
    output_path: str = "../Assets/Config/iwaki_road_network.geojson",
    offset: PLATEAUOffset | None = None,
    bbox: Tuple[float, float, float, float] | None = None,
) -> OSMNetworkBuilder:
    """
    いわき市沿岸部の道路ネットワークを構築し、GeoJSONを出力する。

    Args:
        output_path: GeoJSON出力先
        offset: PLATEAUオフセット（Noneでデフォルト）
        bbox: カスタムバウンディングボックス (north, south, east, west)

    Returns:
        構築済みのOSMNetworkBuilder
    """
    transformer = CoordinateTransformer(plateau_offset=offset)
    builder = OSMNetworkBuilder(transformer=transformer)

    # 浪江町周辺（シミュレーション対象エリア、PLATEAUのreferencePointに対応）
    if bbox is None:
        bbox = (
            37.54,   # north
            37.50,   # south
            141.055, # east
            140.995, # west
        )

    builder.fetch_network(bbox=bbox, network_type="drive")
    builder.build_network()
    builder.export_geojson(output_path)

    summary = builder.export_network_summary()
    print(f"[OSMNetworkBuilder] サマリー: {json.dumps(summary, indent=2)}")

    return builder


if __name__ == "__main__":
    build_iwaki_network()
