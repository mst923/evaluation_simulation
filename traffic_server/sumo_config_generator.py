"""
OSMデータからSUMO設定ファイル群を生成するモジュール。

生成されるファイル:
  - network.net.xml: SUMO道路ネットワーク
  - routes.rou.xml: 車両ルート定義
  - simulation.sumocfg: SUMO設定ファイル
"""

from __future__ import annotations

import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Optional


class SUMOConfigGenerator:
    """OSMデータからSUMO設定ファイルを生成する"""

    def __init__(self, output_dir: str | Path):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)

    def generate_from_osm(
        self,
        osm_file: str | Path,
        bbox: tuple[float, float, float, float] | None = None,
    ) -> Path:
        """
        OSMファイルからSUMOネットワークを生成する。

        Args:
            osm_file: OSMファイルパス
            bbox: バウンディングボックス (west, south, east, north)

        Returns:
            生成された.sumocfgファイルのパス
        """
        net_file = self.output_dir / "network.net.xml"
        rou_file = self.output_dir / "routes.rou.xml"
        cfg_file = self.output_dir / "simulation.sumocfg"

        # 1. netconvertでOSM → SUMO Network
        self._run_netconvert(osm_file, net_file, bbox)

        # 2. デフォルトのルートファイルを生成
        self._create_default_routes(rou_file)

        # 3. SUMOcfgファイルを生成
        self._create_sumocfg(cfg_file, net_file, rou_file)

        print(f"[SUMOConfigGenerator] 設定ファイル生成完了: {cfg_file}")
        return cfg_file

    def generate_from_bbox(
        self,
        bbox: tuple[float, float, float, float],
    ) -> Path:
        """
        バウンディングボックスからOSMデータを取得し、SUMO設定を生成する。

        Args:
            bbox: (west, south, east, north)

        Returns:
            生成された.sumocfgファイルのパス
        """
        import osmnx as ox

        osm_file = self.output_dir / "map.osm"

        # osmnxでOSMデータを取得してファイルに保存
        # OSM XML出力には非簡略化グラフとall_oneway=Trueが必要
        ox.settings.all_oneway = True
        west, south, east, north = bbox
        G = ox.graph_from_bbox(
            bbox=(west, south, east, north),
            network_type="drive",
            simplify=False,
        )
        ox.save_graph_xml(G, filepath=str(osm_file))

        return self.generate_from_osm(osm_file, bbox)

    def _run_netconvert(
        self,
        osm_file: str | Path,
        net_file: Path,
        bbox: tuple[float, float, float, float] | None = None,
    ) -> None:
        """netconvertを実行してSUMOネットワークを生成"""
        cmd = [
            "netconvert",
            "--osm-files",
            str(osm_file),
            "--output-file",
            str(net_file),
            "--geometry.remove",
            "--ramps.guess",
            "--junctions.join",
            "--tls.guess-signals",
            "--tls.discard-simple",
            "--tls.join",
            "--tls.default-type",
            "actuated",
            "--no-turnarounds",
            "true",
        ]

        if bbox:
            west, south, east, north = bbox
            cmd.extend(
                [
                    "--proj.utm",
                    "--edges.join-tram-dist",
                    "1.6",
                ]
            )

        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                check=True,
            )
            print(f"[SUMOConfigGenerator] netconvert完了: {net_file}")
        except FileNotFoundError:
            print(
                "[SUMOConfigGenerator] netconvertが見つかりません。"
                "SUMOがインストールされているか確認してください。"
            )
            raise
        except subprocess.CalledProcessError as e:
            print(f"[SUMOConfigGenerator] netconvertエラー: {e.stderr}")
            raise

    def _create_default_routes(self, rou_file: Path) -> None:
        """デフォルトのルートファイル（空のルート + 車両タイプ定義）を生成"""
        routes = ET.Element("routes")

        # 車両タイプ定義
        vtype = ET.SubElement(routes, "vType")
        vtype.set("id", "passenger")
        vtype.set("vClass", "passenger")
        vtype.set("length", "5")
        vtype.set("minGap", "2.5")
        vtype.set("maxSpeed", "50")  # m/s ≈ 180km/h
        vtype.set("accel", "2.6")
        vtype.set("decel", "4.5")
        vtype.set("sigma", "0.5")
        vtype.set("color", "0.8,0.8,0.2")

        # 緊急車両タイプ
        emergency = ET.SubElement(routes, "vType")
        emergency.set("id", "emergency")
        emergency.set("vClass", "emergency")
        emergency.set("length", "7")
        emergency.set("minGap", "3")
        emergency.set("maxSpeed", "40")
        emergency.set("accel", "2.0")
        emergency.set("decel", "5.0")
        emergency.set("color", "1,0,0")

        tree = ET.ElementTree(routes)
        ET.indent(tree, space="    ")
        tree.write(str(rou_file), encoding="unicode", xml_declaration=True)

        print(f"[SUMOConfigGenerator] ルートファイル生成: {rou_file}")

    def _create_sumocfg(
        self, cfg_file: Path, net_file: Path, rou_file: Path
    ) -> None:
        """SUMO設定ファイルを生成"""
        configuration = ET.Element("configuration")

        # 入力ファイル
        input_elem = ET.SubElement(configuration, "input")
        net = ET.SubElement(input_elem, "net-file")
        net.set("value", net_file.name)
        rou = ET.SubElement(input_elem, "route-files")
        rou.set("value", rou_file.name)

        # 時間設定
        time_elem = ET.SubElement(configuration, "time")
        begin = ET.SubElement(time_elem, "begin")
        begin.set("value", "0")
        end = ET.SubElement(time_elem, "end")
        end.set("value", "3600")  # 1時間
        step = ET.SubElement(time_elem, "step-length")
        step.set("value", "0.1")

        # 処理設定
        processing = ET.SubElement(configuration, "processing")
        lateral = ET.SubElement(processing, "lateral-resolution")
        lateral.set("value", "0.8")

        tree = ET.ElementTree(configuration)
        ET.indent(tree, space="    ")
        tree.write(str(cfg_file), encoding="unicode", xml_declaration=True)

        print(f"[SUMOConfigGenerator] 設定ファイル生成: {cfg_file}")


def generate_iwaki_config(
    output_dir: str = "sumo_data",
) -> Path:
    """
    いわき市のSUMO設定を生成する。

    Returns:
        生成された.sumocfgファイルのパス
    """
    generator = SUMOConfigGenerator(output_dir)

    # 浪江町周辺のバウンディングボックス
    bbox = (140.995, 37.50, 141.055, 37.54)  # (west, south, east, north)

    return generator.generate_from_bbox(bbox)


if __name__ == "__main__":
    generate_iwaki_config()
