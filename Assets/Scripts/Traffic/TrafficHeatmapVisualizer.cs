using System.Collections.Generic;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// 道路の渋滞ヒートマップ可視化。
    /// 道路メッシュの色を渋滞度に応じて変更（緑→黄→赤）。
    /// PLATEAU道路オブジェクト (tran_) のマテリアルを動的に更新する。
    /// </summary>
    public class TrafficHeatmapVisualizer : MonoBehaviour
    {
        private const string Tag = "[TrafficHeatmap]";

        [Header("表示設定")]
        [SerializeField] private bool heatmapEnabled = true;

        [Tooltip("ヒートマップの更新間隔（秒）")]
        [SerializeField] private float updateInterval = 2f;

        [Header("色設定")]
        [SerializeField] private Color freeFlowColor = new Color(0.2f, 0.8f, 0.2f, 0.7f);  // 緑
        [SerializeField] private Color moderateColor = new Color(0.9f, 0.9f, 0.2f, 0.7f);   // 黄
        [SerializeField] private Color congestedColor = new Color(0.9f, 0.2f, 0.2f, 0.7f);  // 赤

        [Header("Gizmo描画設定")]
        [Tooltip("Gizmoで道路線を太く描画")]
        [SerializeField] private bool useGizmoOverlay = true;
        [SerializeField] private float lineWidth = 3f;

        /// <summary>エッジIDごとの渋滞レベルキャッシュ</summary>
        private Dictionary<string, int> _congestionCache = new Dictionary<string, int>();
        private float _lastUpdateTime;

        /// <summary>PLATEAU道路オブジェクトのマテリアルキャッシュ</summary>
        private Dictionary<string, Material> _originalMaterials = new Dictionary<string, Material>();
        private Dictionary<string, Renderer> _roadRenderers = new Dictionary<string, Renderer>();
        private bool _initialized;

        private void Start()
        {
            if (!heatmapEnabled) return;

            // NavMeshTrafficCalculatorの渋滞データ更新イベントを購読
            if (NavMeshTrafficCalculator.Instance != null)
            {
                NavMeshTrafficCalculator.Instance.OnTrafficStateUpdated += OnTrafficStateUpdated;
            }
        }

        private void OnDestroy()
        {
            if (NavMeshTrafficCalculator.Instance != null)
            {
                NavMeshTrafficCalculator.Instance.OnTrafficStateUpdated -= OnTrafficStateUpdated;
            }

            // マテリアルを元に戻す
            RestoreOriginalMaterials();
        }

        private void OnTrafficStateUpdated(Dictionary<string, EdgeTrafficUpdate> updates)
        {
            if (!heatmapEnabled) return;

            foreach (var kvp in updates)
            {
                _congestionCache[kvp.Key] = kvp.Value.congestion_level;
            }
        }

        private void Update()
        {
            if (!heatmapEnabled) return;
            if (Time.time - _lastUpdateTime < updateInterval) return;
            _lastUpdateTime = Time.time;

            // RoadNetworkのエッジ渋滞レベルを更新
            var loader = RoadNetworkLoader.Instance;
            if (loader != null && loader.IsLoaded && loader.Network != null)
            {
                foreach (var kvp in _congestionCache)
                {
                    if (loader.Network.Edges.TryGetValue(kvp.Key, out var edge))
                    {
                        edge.congestionLevel = kvp.Value;
                    }
                }
            }

            // PLATEAUの道路オブジェクトの色を更新
            UpdateRoadMaterials();
        }

        /// <summary>
        /// PLATEAU道路オブジェクトのマテリアルを渋滞度に応じて更新する
        /// </summary>
        private void UpdateRoadMaterials()
        {
            if (!_initialized)
            {
                InitializeRoadRenderers();
                _initialized = true;
            }

            var loader = RoadNetworkLoader.Instance;
            if (loader == null || !loader.IsLoaded) return;

            foreach (var kvp in _roadRenderers)
            {
                var renderer = kvp.Value;
                if (renderer == null) continue;

                // このPLATEAU道路に最も近いネットワークエッジを探す
                var nearestEdge = loader.Network.GetNearestEdge(renderer.bounds.center, 50f);
                if (nearestEdge == null) continue;

                int congestionLevel = nearestEdge.congestionLevel;
                Color color = GetCongestionColor(congestionLevel);

                // マテリアルの色を変更
                if (renderer.material != null)
                {
                    renderer.material.color = color;
                }
            }
        }

        /// <summary>
        /// PLATEAU道路オブジェクト(tran_)のRendererをキャッシュする
        /// </summary>
        private void InitializeRoadRenderers()
        {
            var allObjects = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            foreach (var renderer in allObjects)
            {
                if (renderer.gameObject.name.StartsWith("tran_"))
                {
                    string name = renderer.gameObject.name;
                    _roadRenderers[name] = renderer;

                    // 元のマテリアルを保存
                    if (renderer.sharedMaterial != null)
                    {
                        _originalMaterials[name] = new Material(renderer.sharedMaterial);
                    }
                }
            }

            if (_roadRenderers.Count > 0)
            {
                Debug.Log($"{Tag} 道路オブジェクト {_roadRenderers.Count}件をキャッシュしました");
            }
        }

        /// <summary>元のマテリアルに戻す</summary>
        private void RestoreOriginalMaterials()
        {
            foreach (var kvp in _originalMaterials)
            {
                if (_roadRenderers.TryGetValue(kvp.Key, out var renderer) && renderer != null)
                {
                    renderer.material = kvp.Value;
                }
            }
        }

        /// <summary>
        /// 渋滞レベルに応じた色を返す
        /// </summary>
        private Color GetCongestionColor(int level)
        {
            return level switch
            {
                1 => freeFlowColor,
                2 => Color.Lerp(freeFlowColor, moderateColor, 0.5f),
                3 => moderateColor,
                4 => Color.Lerp(moderateColor, congestedColor, 0.5f),
                5 => congestedColor,
                _ => freeFlowColor,
            };
        }

        /// <summary>
        /// Gizmoで道路ネットワーク上にヒートマップをオーバーレイ描画
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!heatmapEnabled || !useGizmoOverlay) return;

            var loader = RoadNetworkLoader.Instance;
            if (loader == null || !loader.IsLoaded || loader.Network == null) return;

            foreach (var edge in loader.Network.Edges.Values)
            {
                if (edge.congestionLevel <= 1) continue; // 順調な道路は描画しない

                Gizmos.color = GetCongestionColor(edge.congestionLevel);

                for (int i = 0; i < edge.geometry.Count - 1; i++)
                {
                    var from = edge.geometry[i] + Vector3.up * 2f; // 少し浮かせる
                    var to = edge.geometry[i + 1] + Vector3.up * 2f;
                    Gizmos.DrawLine(from, to);
                }
            }
        }
    }
}
