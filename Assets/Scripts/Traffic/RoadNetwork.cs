using System;
using System.Collections.Generic;
using UnityEngine;

namespace Traffic
{
    /// <summary>
    /// 道路ネットワークのノード（交差点）
    /// </summary>
    [Serializable]
    public class RoadNode
    {
        public long id;
        public Vector3 position; // Unity座標
        public float lon;        // WGS84経度（デバッグ用）
        public float lat;        // WGS84緯度（デバッグ用）
        public List<string> connectedEdgeIds = new List<string>();
    }

    /// <summary>
    /// 道路ネットワークのエッジ（道路区間）
    /// </summary>
    [Serializable]
    public class RoadEdge
    {
        public string id;
        public long sourceNodeId;
        public long targetNodeId;
        public float lengthMeters;
        public int lanes = 1;
        public float speedLimitKmh = 40f;
        public bool oneway;
        public string highwayType = "";
        public string roadName = "";

        /// <summary>道路の線形（Unity座標のポイント列）</summary>
        public List<Vector3> geometry = new List<Vector3>();

        /// <summary>現在の渋滞レベル（1=順調 ～ 5=渋滞）</summary>
        [NonSerialized] public int congestionLevel = 1;

        /// <summary>現在のエッジ上の車両数</summary>
        [NonSerialized] public int currentVehicleCount;

        /// <summary>現在の平均速度（km/h）</summary>
        [NonSerialized] public float currentAverageSpeed;

        /// <summary>
        /// 道路の容量（最大車両数）を概算する。
        /// 1台あたり7.5m（車長5m + 車間2.5m）と仮定。
        /// </summary>
        public int EstimatedCapacity => Mathf.Max(1, Mathf.FloorToInt(lengthMeters * lanes / 7.5f));

        /// <summary>
        /// 主要道路（国道以上）かどうか
        /// </summary>
        public bool IsMajorRoad =>
            highwayType is "motorway" or "motorway_link" or "trunk" or "trunk_link"
                or "primary" or "primary_link";
    }

    /// <summary>
    /// 道路ネットワーク全体を管理するクラス
    /// </summary>
    public class RoadNetwork
    {
        public Dictionary<long, RoadNode> Nodes { get; } = new Dictionary<long, RoadNode>();
        public Dictionary<string, RoadEdge> Edges { get; } = new Dictionary<string, RoadEdge>();

        /// <summary>ノード間の隣接リスト（グラフ探索用）</summary>
        private Dictionary<long, List<string>> _adjacency = new Dictionary<long, List<string>>();

        /// <summary>空間インデックス用グリッド（エッジの高速検索）</summary>
        private Dictionary<Vector2Int, List<string>> _edgeSpatialGrid = new Dictionary<Vector2Int, List<string>>();
        private const float GridCellSize = 100f; // 100mグリッド

        public int NodeCount => Nodes.Count;
        public int EdgeCount => Edges.Count;

        /// <summary>
        /// ノードを追加する
        /// </summary>
        public void AddNode(RoadNode node)
        {
            Nodes[node.id] = node;
            if (!_adjacency.ContainsKey(node.id))
            {
                _adjacency[node.id] = new List<string>();
            }
        }

        /// <summary>
        /// エッジを追加する
        /// </summary>
        public void AddEdge(RoadEdge edge)
        {
            Edges[edge.id] = edge;

            // 隣接リストの更新
            if (!_adjacency.ContainsKey(edge.sourceNodeId))
                _adjacency[edge.sourceNodeId] = new List<string>();
            _adjacency[edge.sourceNodeId].Add(edge.id);

            // ノードの接続エッジリスト更新
            if (Nodes.TryGetValue(edge.sourceNodeId, out var srcNode))
                srcNode.connectedEdgeIds.Add(edge.id);
            if (Nodes.TryGetValue(edge.targetNodeId, out var tgtNode))
                tgtNode.connectedEdgeIds.Add(edge.id);

            // 空間インデックスに登録
            RegisterEdgeInSpatialGrid(edge);
        }

        /// <summary>
        /// エッジを空間インデックスに登録
        /// </summary>
        private void RegisterEdgeInSpatialGrid(RoadEdge edge)
        {
            foreach (var point in edge.geometry)
            {
                var cell = WorldToGridCell(point);
                if (!_edgeSpatialGrid.ContainsKey(cell))
                    _edgeSpatialGrid[cell] = new List<string>();

                if (!_edgeSpatialGrid[cell].Contains(edge.id))
                    _edgeSpatialGrid[cell].Add(edge.id);
            }
        }

        private static Vector2Int WorldToGridCell(Vector3 worldPos)
        {
            return new Vector2Int(
                Mathf.FloorToInt(worldPos.x / GridCellSize),
                Mathf.FloorToInt(worldPos.z / GridCellSize)
            );
        }

        /// <summary>
        /// 指定位置から半径内のエッジを検索する
        /// </summary>
        public List<RoadEdge> GetNearbyEdges(Vector3 position, float radius)
        {
            var result = new List<RoadEdge>();
            var found = new HashSet<string>();
            int cellRadius = Mathf.CeilToInt(radius / GridCellSize);
            var centerCell = WorldToGridCell(position);

            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            {
                for (int dz = -cellRadius; dz <= cellRadius; dz++)
                {
                    var cell = new Vector2Int(centerCell.x + dx, centerCell.y + dz);
                    if (!_edgeSpatialGrid.TryGetValue(cell, out var edgeIds))
                        continue;

                    foreach (var edgeId in edgeIds)
                    {
                        if (found.Contains(edgeId)) continue;

                        if (Edges.TryGetValue(edgeId, out var edge))
                        {
                            // エッジ上の最近点までの距離を概算
                            float minDist = float.MaxValue;
                            foreach (var pt in edge.geometry)
                            {
                                float dist = Vector3.Distance(position, pt);
                                if (dist < minDist) minDist = dist;
                            }

                            if (minDist <= radius)
                            {
                                result.Add(edge);
                                found.Add(edgeId);
                            }
                        }
                    }
                }
            }

            result.Sort((a, b) =>
            {
                float distA = ClosestDistanceToEdge(position, a);
                float distB = ClosestDistanceToEdge(position, b);
                return distA.CompareTo(distB);
            });

            return result;
        }

        /// <summary>
        /// 指定位置に最も近いエッジを返す
        /// </summary>
        public RoadEdge GetNearestEdge(Vector3 position, float maxRadius = 200f)
        {
            var edges = GetNearbyEdges(position, maxRadius);
            return edges.Count > 0 ? edges[0] : null;
        }

        /// <summary>
        /// ノードIDから出発するエッジ一覧を取得
        /// </summary>
        public List<RoadEdge> GetOutgoingEdges(long nodeId)
        {
            var result = new List<RoadEdge>();
            if (_adjacency.TryGetValue(nodeId, out var edgeIds))
            {
                foreach (var edgeId in edgeIds)
                {
                    if (Edges.TryGetValue(edgeId, out var edge))
                        result.Add(edge);
                }
            }
            return result;
        }

        /// <summary>
        /// 位置からエッジ上の最短距離を計算（概算）
        /// </summary>
        private static float ClosestDistanceToEdge(Vector3 position, RoadEdge edge)
        {
            float minDist = float.MaxValue;
            for (int i = 0; i < edge.geometry.Count - 1; i++)
            {
                var a = edge.geometry[i];
                var b = edge.geometry[i + 1];
                float dist = DistanceToSegment(position, a, b);
                if (dist < minDist) minDist = dist;
            }
            if (edge.geometry.Count == 1)
            {
                minDist = Vector3.Distance(position, edge.geometry[0]);
            }
            return minDist;
        }

        /// <summary>
        /// 点から線分への最短距離
        /// </summary>
        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var ap = point - a;
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / Vector3.Dot(ab, ab));
            var closest = a + t * ab;
            return Vector3.Distance(point, closest);
        }
    }
}
