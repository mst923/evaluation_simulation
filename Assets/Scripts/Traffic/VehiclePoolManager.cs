using System.Collections.Generic;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// 車両GameObjectのオブジェクトプール管理。
    /// 500台規模に対応するため、生成と破棄を最小限にする。
    /// </summary>
    public class VehiclePoolManager : MonoBehaviour
    {
        private const string Tag = "[VehiclePoolManager]";

        [Header("プール設定")]
        [SerializeField] private GameObject vehiclePrefab;
        [SerializeField] private int initialPoolSize = 100;
        [SerializeField] private int maxPoolSize = 500;

        public static VehiclePoolManager Instance { get; private set; }

        /// <summary>VehicleIdからControllerへのマップ</summary>
        private Dictionary<string, VehicleController> _activeVehicles = new Dictionary<string, VehicleController>();

        /// <summary>非アクティブな車両のプール</summary>
        private Queue<VehicleController> _pool = new Queue<VehicleController>();

        /// <summary>アクティブな車両数</summary>
        public int ActiveCount => _activeVehicles.Count;

        /// <summary>プール内の待機車両数</summary>
        public int PoolCount => _pool.Count;

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
                Debug.LogWarning($"{Tag} vehiclePrefabが未設定のため、Cubeで代用します");
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

            // TrafficClientのイベントを購読
            if (TrafficClient.Instance != null)
            {
                TrafficClient.Instance.OnVehicleStatesUpdated += OnVehicleStatesUpdated;
            }
        }

        private void OnDestroy()
        {
            if (TrafficClient.Instance != null)
            {
                TrafficClient.Instance.OnVehicleStatesUpdated -= OnVehicleStatesUpdated;
            }
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 車両状態の更新を処理する。
        /// 新規車両はプールから取得し、消えた車両はプールに返却する。
        /// </summary>
        private void OnVehicleStatesUpdated(List<VehicleUpdate> updates)
        {
            var updatedIds = new HashSet<string>();

            foreach (var update in updates)
            {
                updatedIds.Add(update.vehicle_id);

                if (_activeVehicles.TryGetValue(update.vehicle_id, out var controller))
                {
                    // 既存車両の更新
                    controller.ApplyUpdate(update);
                }
                else
                {
                    // 新規車両の取得
                    controller = GetFromPool();
                    if (controller != null)
                    {
                        controller.VehicleId = update.vehicle_id;
                        controller.gameObject.SetActive(true);
                        controller.ApplyUpdate(update);
                        _activeVehicles[update.vehicle_id] = controller;
                    }
                }
            }

            // 更新に含まれなかった車両はプールに返却
            var toRemove = new List<string>();
            foreach (var kvp in _activeVehicles)
            {
                if (!updatedIds.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                ReturnToPool(id);
            }
        }

        /// <summary>プールから車両を取得する</summary>
        public VehicleController GetFromPool()
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
            if (_activeVehicles.TryGetValue(vehicleId, out var controller))
            {
                controller.Deactivate();
                _pool.Enqueue(controller);
                _activeVehicles.Remove(vehicleId);
            }
        }

        /// <summary>VehicleIdからControllerを取得する</summary>
        public VehicleController GetVehicle(string vehicleId)
        {
            _activeVehicles.TryGetValue(vehicleId, out var controller);
            return controller;
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

        private VehicleController CreateVehicleInstance()
        {
            var go = Instantiate(vehiclePrefab, transform);
            var controller = go.GetComponent<VehicleController>();
            if (controller == null)
                controller = go.AddComponent<VehicleController>();
            return controller;
        }

        /// <summary>
        /// vehiclePrefabが未設定の場合に、ランタイムでCubeベースの代替プレハブを生成する。
        /// </summary>
        private static GameObject CreateDefaultVehiclePrefab()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "DefaultVehicle";
            // 車両らしいスケール（幅4m × 高さ3m × 長さ8m）- 視認性重視で大きめ
            cube.transform.localScale = new Vector3(4f, 3f, 8f);
            // 視認しやすい赤色
            var renderer = cube.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = Color.red;
            }
            cube.SetActive(false);
            return cube;
        }
    }
}
