using UnityEngine;
using UnityEngine.AI;

namespace EvacSim.Traffic
{
    /// <summary>
    /// NavMeshAgentベースの車両コントローラー。
    /// RVO回避を無効化し、BoxCollider + kinematic Rigidbodyで車両同士をブロックさせることで
    /// 渋滞を自然に発生させる。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NavMeshVehicleAgent : MonoBehaviour
    {
        private const string Tag = "[NavMeshVehicleAgent]";

        [Header("車両パラメータ")]
        [SerializeField] private float defaultSpeed = 11.1f; // 40km/h
        [SerializeField] private float vehicleRadius = 2.0f;
        [SerializeField] private float vehicleAcceleration = 3.0f;
        [SerializeField] private float vehicleAngularSpeed = 120f;

        /// <summary>この車両のID</summary>
        public string VehicleId { get; private set; }

        /// <summary>現在の道路エッジID</summary>
        public string CurrentEdgeId { get; private set; }

        /// <summary>現在の速度（m/s）</summary>
        public float CurrentSpeed => _agent != null ? _agent.velocity.magnitude : 0f;

        /// <summary>停車中かどうか</summary>
        public bool IsStopped => CurrentSpeed < 0.1f;

        /// <summary>対応するEvacueeのID</summary>
        public string OwnerEvacueeId { get; set; }

        private NavMeshAgent _agent;
        private float _lastEdgeUpdateTime;
        private const float EDGE_UPDATE_INTERVAL = 1.0f;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            ConfigureAgent();
        }

        /// <summary>
        /// NavMeshAgentを車両用に設定する
        /// </summary>
        private void ConfigureAgent()
        {
            if (_agent == null) return;

            _agent.speed = defaultSpeed;
            _agent.radius = vehicleRadius;
            _agent.acceleration = vehicleAcceleration;
            _agent.angularSpeed = vehicleAngularSpeed;
            _agent.autoBraking = true;
            _agent.stoppingDistance = 1.0f;

            // RVO回避を無効化 → 他車を避けない → Colliderで物理的にブロック
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
        }

        /// <summary>
        /// 車両をアクティブ化して目的地へ移動開始する
        /// </summary>
        public bool Activate(string vehicleId, Vector3 origin, Vector3 destination)
        {
            VehicleId = vehicleId;
            gameObject.SetActive(true);

            if (_agent == null)
                _agent = GetComponent<NavMeshAgent>();

            ConfigureAgent();
            _agent.enabled = true;

            // NavMesh上の最寄り位置にスナップ
            NavMeshHit hit;
            if (NavMesh.SamplePosition(origin, out hit, 50f, NavMesh.AllAreas))
            {
                _agent.Warp(hit.position);
            }
            else
            {
                Debug.LogWarning($"{Tag} {vehicleId}: NavMesh上にスナップできません origin={origin}");
                Deactivate();
                return false;
            }

            // 目的地を設定
            if (!_agent.isOnNavMesh)
            {
                Debug.LogWarning($"{Tag} {vehicleId}: Warp後もNavMesh上にいません");
                Deactivate();
                return false;
            }

            _agent.SetDestination(destination);
            _lastEdgeUpdateTime = Time.time;

            // 初回のエッジID更新
            UpdateCurrentEdgeId();

            Debug.Log($"{Tag} {vehicleId}: アクティブ化 origin={hit.position} dest={destination}");
            return true;
        }

        /// <summary>
        /// 目的地を変更する
        /// </summary>
        public void UpdateDestination(Vector3 destination)
        {
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            {
                _agent.SetDestination(destination);
            }
        }

        /// <summary>
        /// 車両を非アクティブにする（プールに返却前）
        /// </summary>
        public void Deactivate()
        {
            if (_agent != null)
            {
                if (_agent.isOnNavMesh)
                    _agent.ResetPath();
                _agent.enabled = false;
            }

            VehicleId = null;
            OwnerEvacueeId = null;
            CurrentEdgeId = null;
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!_agent || !_agent.enabled) return;

            // 定期的にCurrentEdgeIdを更新
            if (Time.time - _lastEdgeUpdateTime >= EDGE_UPDATE_INTERVAL)
            {
                _lastEdgeUpdateTime = Time.time;
                UpdateCurrentEdgeId();
            }
        }

        /// <summary>
        /// 現在位置から最寄りのRoadEdgeを特定してCurrentEdgeIdを更新する
        /// </summary>
        private void UpdateCurrentEdgeId()
        {
            var loader = RoadNetworkLoader.Instance;
            if (loader == null || !loader.IsLoaded || loader.Network == null) return;

            var edge = loader.Network.GetNearestEdge(transform.position, 30f);
            if (edge != null)
            {
                CurrentEdgeId = edge.id;
            }
        }
    }
}
