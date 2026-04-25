using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace EvacSim.Traffic
{
    /// <summary>
    /// 車両GameObjectのオブジェクトプール管理。
    /// NavMeshVehicleAgentベースの車両を管理し、500台規模に対応する。
    /// </summary>
    public class VehiclePoolManager : MonoBehaviour
    {
        private const string Tag = "[VehiclePoolManager]";

        [Header("プール設定")]
        [SerializeField] private GameObject vehiclePrefab;
        [SerializeField] private int initialPoolSize = 100;
        [SerializeField] private int maxPoolSize = 500;

        public static VehiclePoolManager Instance { get; private set; }

        /// <summary>VehicleIdからNavMeshVehicleAgentへのマップ</summary>
        private Dictionary<string, NavMeshVehicleAgent> _activeVehicles = new Dictionary<string, NavMeshVehicleAgent>();

        /// <summary>非アクティブな車両のプール</summary>
        private Queue<NavMeshVehicleAgent> _pool = new Queue<NavMeshVehicleAgent>();

        /// <summary>アクティブな車両数</summary>
        public int ActiveCount => _activeVehicles.Count;

        /// <summary>プール内の待機車両数</summary>
        public int PoolCount => _pool.Count;

        /// <summary>アクティブな車両一覧（NavMeshTrafficCalculator用）</summary>
        public IReadOnlyDictionary<string, NavMeshVehicleAgent> ActiveVehicles => _activeVehicles;

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
            if (vehiclePrefab == null)
            {
                Debug.LogWarning($"{Tag} vehiclePrefabが未設定のため、デフォルト車両で代用します");
                vehiclePrefab = CreateDefaultVehiclePrefab();
            }

            // プールの事前生成
            for (int i = 0; i < initialPoolSize; i++)
            {
                var vehicle = CreateVehicleInstance();
                vehicle.Deactivate();
                _pool.Enqueue(vehicle);
            }

            Debug.Log($"{Tag} プール初期化完了: {initialPoolSize}台");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 車両をスポーンして目的地へ移動開始する
        /// </summary>
        public NavMeshVehicleAgent SpawnVehicle(string vehicleId, Vector3 origin, Vector3 destination)
        {
            var agent = GetFromPool();
            if (agent == null)
            {
                Debug.LogWarning($"{Tag} 車両スポーン失敗: プールが枯渇 vehicleId={vehicleId}");
                return null;
            }

            bool success = agent.Activate(vehicleId, origin, destination);
            if (!success)
            {
                agent.Deactivate();
                _pool.Enqueue(agent);
                return null;
            }

            _activeVehicles[vehicleId] = agent;
            return agent;
        }

        /// <summary>プールから車両を取得する</summary>
        private NavMeshVehicleAgent GetFromPool()
        {
            if (_pool.Count > 0)
            {
                return _pool.Dequeue();
            }

            // プールが空の場合、最大数に達していなければ新規生成
            if (_activeVehicles.Count + _pool.Count < maxPoolSize)
            {
                return CreateVehicleInstance();
            }

            Debug.LogWarning($"{Tag} プールの最大サイズ ({maxPoolSize}) に達しました");
            return null;
        }

        /// <summary>車両をプールに返却する</summary>
        public void ReturnToPool(string vehicleId)
        {
            if (_activeVehicles.TryGetValue(vehicleId, out var agent))
            {
                agent.Deactivate();
                _pool.Enqueue(agent);
                _activeVehicles.Remove(vehicleId);
            }
        }

        /// <summary>VehicleIdからNavMeshVehicleAgentを取得する</summary>
        public NavMeshVehicleAgent GetVehicle(string vehicleId)
        {
            _activeVehicles.TryGetValue(vehicleId, out var agent);
            return agent;
        }

        /// <summary>全車両をプールに返却する（エピソードリセット用）</summary>
        public void ReturnAllToPool()
        {
            var ids = new List<string>(_activeVehicles.Keys);
            foreach (var id in ids)
            {
                ReturnToPool(id);
            }
        }

        private NavMeshVehicleAgent CreateVehicleInstance()
        {
            var go = Instantiate(vehiclePrefab, transform);

            // NavMeshAgentを確保
            var navAgent = go.GetComponent<NavMeshAgent>();
            if (navAgent == null)
                navAgent = go.AddComponent<NavMeshAgent>();

            // NavMeshVehicleAgentを確保
            var vehicleAgent = go.GetComponent<NavMeshVehicleAgent>();
            if (vehicleAgent == null)
                vehicleAgent = go.AddComponent<NavMeshVehicleAgent>();

            // BoxCollider（物理ブロック用）を確保
            var boxCollider = go.GetComponent<BoxCollider>();
            if (boxCollider == null)
            {
                boxCollider = go.AddComponent<BoxCollider>();
                boxCollider.isTrigger = false;
            }

            // Rigidbody（kinematic: NavMeshが制御するが、Colliderは有効）
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            return vehicleAgent;
        }

        /// <summary>
        /// vehiclePrefabが未設定の場合のデフォルト車両生成
        /// </summary>
        private static GameObject CreateDefaultVehiclePrefab()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "DefaultVehicle";
            // 車両らしいスケール（幅2m × 高さ1.5m × 長さ4.5m）
            cube.transform.localScale = new Vector3(2f, 1.5f, 4.5f);
            // 視認しやすい赤色
            var renderer = cube.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = Color.red;
            }
            // CreatePrimitiveで自動付与されるMeshColliderを削除（BoxColliderを使うため）
            var meshCollider = cube.GetComponent<MeshCollider>();
            if (meshCollider != null)
                DestroyImmediate(meshCollider);
            cube.SetActive(false);
            return cube;
        }
    }
}
