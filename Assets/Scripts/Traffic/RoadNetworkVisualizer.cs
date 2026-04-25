using System.Collections.Generic;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// 道路ネットワークを可視化するコンポーネント。
    /// エッジのgeometryと車線数から道路幅を持った平面メッシュを動的生成し、
    /// Gameビューでも道路らしく表示する。Gizmo描画も引き続きサポート。
    /// </summary>
    public class RoadNetworkVisualizer : MonoBehaviour
    {
        [Header("メッシュ表示設定")]
        [Tooltip("道路メッシュを生成して表示する")]
        [SerializeField] private bool generateRoadMesh = true;
        [Tooltip("1車線あたりの幅（メートル）")]
        [SerializeField] private float laneWidth = 3.0f;
        [Tooltip("道路メッシュの色")]
        [SerializeField] private Color roadMeshColor = new Color(0.25f, 0.25f, 0.27f, 0.9f);
        [Tooltip("道路メッシュの地面からのオフセット（めり込み防止）")]
        [SerializeField] private float heightOffset = 0.15f;

        [Header("Gizmo表示設定")]
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
        private GameObject _roadMeshRoot;
        private bool _meshGenerated;

        private void Start()
        {
            if (generateRoadMesh)
                TryGenerateRoadMesh();
        }

        private void Update()
        {
            if (generateRoadMesh && !_meshGenerated)
                TryGenerateRoadMesh();
        }

        private void OnDestroy()
        {
            if (_roadMeshRoot != null)
                Destroy(_roadMeshRoot);
        }

        /// <summary>
        /// 道路ネットワークが読み込まれていればメッシュを生成する
        /// </summary>
        private void TryGenerateRoadMesh()
        {
            if (_loader == null)
                _loader = FindFirstObjectByType<RoadNetworkLoader>();
            if (_loader == null || !_loader.IsLoaded || _loader.Network == null)
                return;

            GenerateRoadMesh(_loader.Network);
            _meshGenerated = true;
        }

        /// <summary>
        /// 全エッジに対して道路幅を持った平面メッシュを生成する
        /// </summary>
        private void GenerateRoadMesh(RoadNetwork network)
        {
            if (_roadMeshRoot != null)
                Destroy(_roadMeshRoot);

            _roadMeshRoot = new GameObject("RoadMeshRoot");
            _roadMeshRoot.transform.SetParent(transform);

            // マテリアルを1つ作って共有
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = roadMeshColor;

            // エッジをバッチ化（Unity Meshの頂点数上限 65535 を考慮して分割）
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var colors = new List<Color>();
            int batchIndex = 0;

            foreach (var edge in network.Edges.Values)
            {
                if (majorRoadsOnly && !edge.IsMajorRoad)
                    continue;
                if (edge.geometry.Count < 2)
                    continue;

                float halfWidth = edge.lanes * laneWidth * 0.5f;
                int geomCount = edge.geometry.Count;

                for (int i = 0; i < geomCount - 1; i++)
                {
                    // 頂点数上限に近づいたらメッシュを確定して新バッチ開始
                    if (vertices.Count + 4 > 65000)
                    {
                        CreateMeshObject(vertices, triangles, colors, mat, batchIndex++);
                        vertices.Clear();
                        triangles.Clear();
                        colors.Clear();
                    }

                    Vector3 p0 = edge.geometry[i];
                    Vector3 p1 = edge.geometry[i + 1];

                    // 進行方向に直交する水平方向を算出
                    Vector3 forward = p1 - p0;
                    forward.y = 0;
                    if (forward.sqrMagnitude < 0.001f) continue;

                    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized * halfWidth;

                    // 4頂点の四角形（地面から少し浮かせる）
                    Vector3 offset = Vector3.up * heightOffset;
                    int baseIdx = vertices.Count;

                    vertices.Add(p0 - right + offset);
                    vertices.Add(p0 + right + offset);
                    vertices.Add(p1 + right + offset);
                    vertices.Add(p1 - right + offset);

                    colors.Add(roadMeshColor);
                    colors.Add(roadMeshColor);
                    colors.Add(roadMeshColor);
                    colors.Add(roadMeshColor);

                    // 両面表示のため表裏2つの三角形ペア
                    triangles.Add(baseIdx);
                    triangles.Add(baseIdx + 1);
                    triangles.Add(baseIdx + 2);
                    triangles.Add(baseIdx);
                    triangles.Add(baseIdx + 2);
                    triangles.Add(baseIdx + 3);
                }
            }

            // 残りのメッシュを確定
            if (vertices.Count > 0)
                CreateMeshObject(vertices, triangles, colors, mat, batchIndex);

            Debug.Log($"[RoadNetworkVisualizer] 道路メッシュ生成完了: {batchIndex + 1}バッチ");
        }

        /// <summary>
        /// 頂点・三角形データからMeshオブジェクトを生成してシーンに配置する
        /// </summary>
        private void CreateMeshObject(List<Vector3> vertices, List<int> triangles, List<Color> colors, Material mat, int index)
        {
            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetColors(colors);
            mesh.RecalculateNormals();

            var go = new GameObject($"RoadMesh_{index}");
            go.transform.SetParent(_roadMeshRoot.transform);
            var mf = go.AddComponent<MeshFilter>();
            mf.mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

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
