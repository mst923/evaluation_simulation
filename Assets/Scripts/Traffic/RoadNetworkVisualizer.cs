using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// デバッグ用：道路ネットワークをGizmoで描画する。
    /// ノード=球、エッジ=線、渋滞度=色で表示。
    /// </summary>
    public class RoadNetworkVisualizer : MonoBehaviour
    {
        [Header("表示設定")]
        [SerializeField] private bool showNodes = true;
        [SerializeField] private bool showEdges = true;
        [SerializeField] private bool showCongestion = true;
        [SerializeField] private bool showLabels;

        [Header("ノード設定")]
        [SerializeField] private float nodeRadius = 3f;
        [SerializeField] private Color nodeColor = Color.cyan;

        [Header("エッジ設定")]
        [SerializeField] private Color edgeColorFree = Color.green;
        [SerializeField] private Color edgeColorCongested = Color.red;

        [Header("フィルタ")]
        [Tooltip("主要道路のみ表示")]
        [SerializeField] private bool majorRoadsOnly;

        private RoadNetworkLoader _loader;

        private void OnDrawGizmos()
        {
            if (_loader == null)
                _loader = FindFirstObjectByType<RoadNetworkLoader>();

            if (_loader == null || !_loader.IsLoaded || _loader.Network == null)
                return;

            var network = _loader.Network;

            // ノードの描画
            if (showNodes)
            {
                Gizmos.color = nodeColor;
                foreach (var node in network.Nodes.Values)
                {
                    Gizmos.DrawSphere(node.position, nodeRadius);
                }
            }

            // エッジの描画
            if (showEdges)
            {
                foreach (var edge in network.Edges.Values)
                {
                    if (majorRoadsOnly && !edge.IsMajorRoad)
                        continue;

                    // 渋滞度に応じた色
                    Color edgeColor;
                    if (showCongestion && edge.congestionLevel > 1)
                    {
                        float t = (edge.congestionLevel - 1f) / 4f; // 1-5 → 0-1
                        edgeColor = Color.Lerp(edgeColorFree, edgeColorCongested, t);
                    }
                    else
                    {
                        edgeColor = edgeColorFree;
                    }

                    // 車線数に応じた線の太さ（Gizmosでは太さ変更不可なので色の濃さで表現）
                    if (edge.lanes >= 3)
                        edgeColor = Color.Lerp(edgeColor, Color.white, 0.2f);

                    Gizmos.color = edgeColor;

                    // 線形の描画
                    for (int i = 0; i < edge.geometry.Count - 1; i++)
                    {
                        Gizmos.DrawLine(edge.geometry[i], edge.geometry[i + 1]);
                    }
                }
            }
        }
    }
}
