using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Traffic
{
    /// <summary>
    /// SUMOからリアルタイムの渋滞データを取得し、BPR関数ベースの渋滞度を計算する。
    /// エッジごとの車両数・平均速度・渋滞レベル(1-5)を提供する。
    /// </summary>
    public class TrafficStateProvider : MonoBehaviour
    {
        private const string Tag = "[TrafficStateProvider]";

        [Header("更新設定")]
        [Tooltip("渋滞情報の更新間隔（秒）")]
        [SerializeField] private float updateInterval = 1f;

        public static TrafficStateProvider Instance { get; private set; }

        /// <summary>エッジIDごとの最新の渋滞データ</summary>
        private Dictionary<string, EdgeTrafficUpdate> _edgeTrafficStates = new Dictionary<string, EdgeTrafficUpdate>();

        /// <summary>最後に更新した時刻</summary>
        private float _lastUpdateTime;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // TrafficClientのイベントを購読
            if (TrafficClient.Instance != null)
            {
                TrafficClient.Instance.OnTrafficStateUpdated += OnTrafficStateUpdated;
            }
        }

        private void OnDestroy()
        {
            if (TrafficClient.Instance != null)
            {
                TrafficClient.Instance.OnTrafficStateUpdated -= OnTrafficStateUpdated;
            }
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (TrafficClient.Instance == null || !TrafficClient.Instance.IsConnected)
                return;

            if (Time.time - _lastUpdateTime >= updateInterval)
            {
                _lastUpdateTime = Time.time;
                _ = TrafficClient.Instance.GetTrafficStateAsync();
            }
        }

        private void OnTrafficStateUpdated(Dictionary<string, EdgeTrafficUpdate> updates)
        {
            _edgeTrafficStates = updates;
        }

        /// <summary>
        /// 指定位置周辺の道路の渋滞情報を取得する。
        /// </summary>
        /// <param name="position">検索中心位置</param>
        /// <param name="radius">検索半径（メートル）</param>
        /// <returns>周辺道路の交通状態リスト（距離順）</returns>
        public List<TrafficCondition> GetTrafficConditions(Vector3 position, float radius = 200f)
        {
            var result = new List<TrafficCondition>();

            // RoadNetworkLoaderから道路ネットワークを取得
            var loader = RoadNetworkLoader.Instance;
            if (loader == null || !loader.IsLoaded || loader.Network == null)
                return result;

            // 周辺のエッジを取得
            var nearbyEdges = loader.Network.GetNearbyEdges(position, radius);

            foreach (var edge in nearbyEdges)
            {
                var condition = new TrafficCondition
                {
                    edgeId = edge.id,
                    roadName = edge.roadName,
                    highwayType = edge.highwayType,
                    lengthMeters = edge.lengthMeters,
                    lanes = edge.lanes,
                    speedLimitKmh = edge.speedLimitKmh,
                    distanceFromPosition = ClosestDistance(position, edge),
                };

                // SUMOからの渋滞データがあれば適用
                if (_edgeTrafficStates.TryGetValue(edge.id, out var trafficState))
                {
                    condition.vehicleCount = trafficState.vehicle_count;
                    condition.averageSpeedKmh = trafficState.average_speed * 3.6f; // m/s → km/h
                    condition.congestionLevel = trafficState.congestion_level;
                    condition.travelTime = trafficState.travel_time;
                    condition.freeFlowTravelTime = trafficState.free_flow_travel_time;
                }
                else
                {
                    // データがない場合は自由流と仮定
                    condition.congestionLevel = 1;
                    condition.averageSpeedKmh = edge.speedLimitKmh;
                    condition.freeFlowTravelTime = edge.lengthMeters / (edge.speedLimitKmh / 3.6f);
                    condition.travelTime = condition.freeFlowTravelTime;
                }

                condition.congestionLabel = TransportModeDecider.GetCongestionLabel(condition.congestionLevel);
                result.Add(condition);
            }

            // 距離順にソート
            result.Sort((a, b) => a.distanceFromPosition.CompareTo(b.distanceFromPosition));
            return result;
        }

        /// <summary>
        /// 全体的な渋滞度を0-1のスケールで取得する。
        /// </summary>
        /// <param name="position">検索中心位置</param>
        /// <param name="radius">検索半径</param>
        /// <returns>全体的な渋滞度（0=順調, 1=大渋滞）</returns>
        public float GetOverallCongestion(Vector3 position, float radius = 300f)
        {
            var conditions = GetTrafficConditions(position, radius);
            if (conditions.Count == 0) return 0f;

            float totalWeight = 0f;
            float weightedCongestion = 0f;

            foreach (var c in conditions)
            {
                // 距離による重み付け（近いほど重要）
                float weight = 1f / Mathf.Max(1f, c.distanceFromPosition);
                // 主要道路は重み2倍
                if (c.highwayType is "primary" or "trunk" or "motorway")
                    weight *= 2f;

                weightedCongestion += (c.congestionLevel - 1f) / 4f * weight;
                totalWeight += weight;
            }

            return totalWeight > 0 ? Mathf.Clamp01(weightedCongestion / totalWeight) : 0f;
        }

        /// <summary>
        /// 渋滞情報の自然言語サマリーを生成する。
        /// </summary>
        public string GetTrafficSummary(Vector3 position, float radius = 300f)
        {
            var conditions = GetTrafficConditions(position, radius);
            if (conditions.Count == 0)
                return "周辺の交通情報はありません";

            var congested = conditions.Where(c => c.congestionLevel >= 3).ToList();
            if (congested.Count == 0)
                return "周辺の道路は概ね順調です";

            var parts = new List<string>();
            foreach (var c in congested.Take(3))
            {
                string name = string.IsNullOrEmpty(c.roadName) ? c.highwayType : c.roadName;
                float ratio = c.freeFlowTravelTime > 0 ? c.travelTime / c.freeFlowTravelTime : 1f;
                parts.Add($"{name}は{c.congestionLabel}（推定所要時間: 通常の{ratio:F1}倍）");
            }

            return string.Join("。", parts);
        }

        private static float ClosestDistance(Vector3 position, RoadEdge edge)
        {
            float min = float.MaxValue;
            foreach (var pt in edge.geometry)
            {
                float d = Vector3.Distance(position, pt);
                if (d < min) min = d;
            }
            return min;
        }
    }

    /// <summary>
    /// 道路の交通状態データ
    /// </summary>
    public class TrafficCondition
    {
        public string edgeId;
        public string roadName;
        public string highwayType;
        public float lengthMeters;
        public int lanes;
        public float speedLimitKmh;
        public float distanceFromPosition;

        // リアルタイムデータ
        public int vehicleCount;
        public float averageSpeedKmh;
        public int congestionLevel;       // 1=順調, 5=大渋滞
        public string congestionLabel;    // 日本語ラベル
        public float travelTime;          // 走行時間（秒）
        public float freeFlowTravelTime;  // 自由流走行時間（秒）
    }
}
