"""
WGS84 (OSM) → JGD2011 → PLATEAU Unity座標への変換モジュール。

座標系変換パイプライン:
  1. WGS84 (EPSG:4326) - OSMの緯度経度
  2. JGD2011 / Japan Plane Rectangular CS IX (EPSG:6677) - いわき市は9系
  3. PLATEAU Unity座標 - PLATEAUのオフセットを適用

PLATEAUのUnity座標系:
  - X軸: 東方向（正）
  - Y軸: 上方向（正）
  - Z軸: 北方向（正）
  - 原点: PLATEAUインポート時のオフセット（通常は区域の中心付近）
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Tuple

from pyproj import Transformer


@dataclass
class PLATEAUOffset:
    """PLATEAUインポート時の座標オフセット（Unity座標系の原点）"""

    x: float  # 東方向オフセット（メートル）
    y: float  # 上方向オフセット（メートル）
    z: float  # 北方向オフセット（メートル）


# いわき市（浪江）のPLATEAUオフセット（namie.unityシーンのreferencePoint）
# PLATEAUインポート時のオフセット: Unity座標 = JGD2011平面直角 - referencePoint
DEFAULT_IWAKI_OFFSET = PLATEAUOffset(
    x=105341.68,
    y=0.0,
    z=168959.62,
)


class CoordinateTransformer:
    """WGS84からPLATEAU Unity座標系への変換器"""

    def __init__(
        self,
        plateau_offset: PLATEAUOffset | None = None,
        zone: int = 9,
    ):
        """
        Args:
            plateau_offset: PLATEAUのオフセット値。Noneの場合はデフォルト（いわき市）を使用。
            zone: 日本測地系の系番号（いわき市は9系）
        """
        self.offset = plateau_offset or DEFAULT_IWAKI_OFFSET

        # EPSG:6669 (1系) ~ EPSG:6687 (19系)
        epsg_code = 6669 + (zone - 1)

        # WGS84 → JGD2011 平面直角座標
        self._wgs84_to_jpr = Transformer.from_crs(
            "EPSG:4326",
            f"EPSG:{epsg_code}",
            always_xy=True,
        )

    def wgs84_to_unity(
        self, lon: float, lat: float, alt: float = 0.0
    ) -> Tuple[float, float, float]:
        """
        WGS84緯度経度をPLATEAU Unity座標に変換する。

        Args:
            lon: 経度（度）
            lat: 緯度（度）
            alt: 標高（メートル）

        Returns:
            (x, y, z) Unity座標 - x:東, y:上, z:北
        """
        # WGS84 → JGD2011平面直角座標（always_xy=Trueなので lon, lat 順）
        easting, northing = self._wgs84_to_jpr.transform(lon, lat)

        # JGD2011 → Unity座標（PLATEAUオフセットを適用）
        # PLATEAUの変換式: Unity座標 = JGD2011平面直角 - referencePoint
        unity_x = easting - self.offset.x  # 東方向
        unity_y = alt - self.offset.y  # 上方向
        unity_z = northing - self.offset.z  # 北方向

        return (unity_x, unity_y, unity_z)

    def wgs84_to_unity_2d(self, lon: float, lat: float) -> Tuple[float, float]:
        """
        WGS84緯度経度をPLATEAU Unity座標のXZ平面に変換（Y=0）。

        Returns:
            (x, z) Unity座標
        """
        x, _, z = self.wgs84_to_unity(lon, lat, 0.0)
        return (x, z)


if __name__ == "__main__":
    # テスト: 浪江町の座標で変換テスト
    transformer = CoordinateTransformer()

    # 浪江町中心部付近: 37.4953°N, 141.0011°E
    test_lat, test_lon = 37.4953, 141.0011
    x, y, z = transformer.wgs84_to_unity(test_lon, test_lat)
    print(f"Input: lat={test_lat}, lon={test_lon}")
    print(f"Unity: x={x:.2f}, y={y:.2f}, z={z:.2f}")
