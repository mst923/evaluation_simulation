using System;
using System.Collections.Generic;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// NavMeshベースの車両からエッジごとの渋滞レベルを計算する。
    /// SUMO BPR関数の代替として、エッジ上の車両密度から渋滞度を算出する。
    /// </summary>
    public class NavMeshTrafficCalculator : MonoBehaviour
    {
        private const string Tag = "[NavMeshTrafficCalculator]";

        [Header("更新設定")]
        [Tooltip("渋滞データの更新間隔（秒）")]
        [SerializeField] private float updateInterval = 1.0f;

        [Header("BPR関数パラメータ")]
        [SerializeField] private float bprAlpha = 0.15f;
        [SerializeField] private float bprBeta = 4.0f;

        public static NavMeshTrafficCalculator Instance { get; private set; }

        /// <summary>渋滞データ更新イベント（TrafficStateProviderが購読）</summary>
        public event Action<Dictionary<string, EdgeTrafficUpdate>> OnTrafficStateUpdated;

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

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (Time.time - _lastUpdateTime < updateInterval) return;
            _lastUpdateTime = Time.time;

            CalculateAndPublish();
        }

        /// <summary>
        /// 全アクティブ車両を走査し、エッジごとの渋滞データを計算・発火する
        /// </summary>
        private void CalculateAndPublish()
        {
            var pool = VehiclePoolManager.Instance;
            if (pool == null || pool.ActiveCount == 0)
            {
                // 車両がいない場合は空データを発火
                OnTrafficStateUpdated?.Invoke(new Dictionary<string, EdgeTrafficUpdate>());
                return;
            }

            var loader = RoadNetworkLoader.Instance;
            if (loader == null || !loader.IsLoaded || loader.Network == null) return;

            // エッジごとの車両数と速度合計を集計
            var edgeVehicleCount = new Dictionary<string, int>();
            var edgeSpeedSum = new Dictionary<string, float>();

            foreach (var kvp in pool.ActiveVehicles)
            {
                var vehicle = kvp.Value;
                if (vehicle == null || !vehicle.gameObject.activeSelf) continue;

                string edgeId = vehicle.CurrentEdgeId;
                if (string.IsNullOrEmpty(edgeId)) continue;

                if (!edgeVehicleCount.ContainsKey(edgeId))
                {
                    edgeVehicleCount[edgeId] = 0;
                    edgeSpeedSum[edgeId] = 0f;
                }

                edgeVehicleCount[edgeId]++;
                edgeSpeedSum[edgeId] += vehicle.CurrentSpeed;
            }

            // エッジごとの渋滞データを構築
            var result = new Dictionary<string, EdgeTrafficUpdate>();
            var network = loader.Network;

            foreach (var kvp in edgeVehicleCount)
            {
                string edgeId = kvp.Key;
                int vehicleCount = kvp.Value;
                float averageSpeed = edgeSpeedSum[edgeId] / vehicleCount;

                if (!network.Edges.TryGetValue(edgeId, out var edge)) continue;

                float density = (float)vehicleCount / Mathf.Max(1, edge.EstimatedCapacity);
                float freeFlowSpeed = edge.speedLimitKmh / 3.6f; // km/h → m/s
                float freeFlowTravelTime = edge.lengthMeters / Mathf.Max(0.1f, freeFlowSpeed);

                // BPR関数: travelTime = freeFlowTravelTime × (1 + α × density^β)
                float travelTime = freeFlowTravelTime * (1f + bprAlpha * Mathf.Pow(density, bprBeta));

                int congestionLevel = DensityToCongestionLevel(density);

                result[edgeId] = new EdgeTrafficUpdate
                {
                    edge_id = edgeId,
                    vehicle_count = vehicleCount,
                    average_speed = averageSpeed,
                    occupancy = density,
                    congestion_level = congestionLevel,
                    travel_time = travelTime,
                    free_flow_travel_time = freeFlowTravelTime,
                };

                // RoadEdgeのランタイムプロパティも更新
                edge.congestionLevel = congestionLevel;
                edge.currentVehicleCount = vehicleCount;
                edge.currentAverageSpeed = averageSpeed * 3.6f; // m/s → km/h
            }

            OnTrafficStateUpdated?.Invoke(result);
        }

        /// <summary>
        /// 密度から渋滞レベル（1-5）に変換する
        /// </summary>
        private static int DensityToCongestionLevel(float density)
        {
            if (density < 0.3f) return 1; // 順調
            if (density < 0.5f) return 2; // やや混雑
            if (density < 0.7f) return 3; // 混雑
            if (density < 0.9f) return 4; // 渋滞
            return 5;                      // 大渋滞
        }
    }
}
