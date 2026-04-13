using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using EvacSim.Core;
using EvacSim.Evacuees;

namespace EvacSim.Disaster
{
/// <summary>
/// 消防団のポンプ車による巡回・呼びかけを行うエージェント
/// NavMeshを使用して巡回経路を移動し、拡声器で避難を呼びかける
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class FireTruckAgent : MonoBehaviour
{
    [Header("巡回設定")]
    [Tooltip("巡回経路のウェイポイント")]
    public List<Transform> patrolWaypoints = new List<Transform>();

    [Tooltip("巡回ウェイポイントを自動検出するためのタグ")]
    public string waypointTag = "FireTruckWaypoint";

    [Tooltip("移動速度（m/s）")]
    public float moveSpeed = 10f;

    [Header("拡声器設定")]
    [Tooltip("拡声器の可聴範囲（メートル）")]
    public float loudspeakerRange = 200f;

    [Tooltip("呼びかけメッセージ")]
    [TextArea(3, 6)]
    public string announceMessage = "津波が来ます。今すぐ高台に避難してください。";

    [Tooltip("呼びかけ間隔（秒）")]
    public float announceInterval = 10f;

    [Header("動作状態（読み取り専用）")]
    [SerializeField] private bool _isPatrolling = false;
    public bool IsPatrolling => _isPatrolling;

    private NavMeshAgent _navAgent;
    private EnvManager _envManager;
    private int _currentWaypointIndex = 0;
    private float _lastAnnounceTime = 0f;
    private bool _isActive = false;

    void Awake()
    {
        _navAgent = GetComponent<NavMeshAgent>();
        if (_navAgent != null)
        {
            _navAgent.speed = moveSpeed;
        }
    }

    void Start()
    {
        _envManager = FindFirstObjectByType<EnvManager>();

        // ウェイポイントが未設定の場合、タグで自動検出
        if (patrolWaypoints.Count == 0 && !string.IsNullOrEmpty(waypointTag))
        {
            var waypointObjs = GameObject.FindGameObjectsWithTag(waypointTag);
            foreach (var obj in waypointObjs)
            {
                patrolWaypoints.Add(obj.transform);
            }
            if (patrolWaypoints.Count > 0)
            {
                Debug.Log($"[FireTruckAgent] 巡回ウェイポイントを{patrolWaypoints.Count}個自動検出しました");
            }
        }

        // DisasterEventManagerからの通知を受け取る設定
        var eventManager = FindFirstObjectByType<DisasterEventManager>();
        if (eventManager != null)
        {
            eventManager.OnDisasterEvent += OnDisasterEvent;
        }

        // エピソード開始時のリセット
        if (_envManager != null)
        {
            _envManager.OnStartEpisode += OnEpisodeReset;
        }
    }

    void OnDestroy()
    {
        var eventManager = FindFirstObjectByType<DisasterEventManager>();
        if (eventManager != null)
        {
            eventManager.OnDisasterEvent -= OnDisasterEvent;
        }

        if (_envManager != null)
        {
            _envManager.OnStartEpisode -= OnEpisodeReset;
        }
    }

    /// <summary>
    /// DisasterEventManagerからのイベント通知を処理
    /// </summary>
    private void OnDisasterEvent(DisasterEvent evt)
    {
        if (evt.eventType == DisasterEventType.FireTruckPatrol)
        {
            StartPatrol();
        }
    }

    /// <summary>
    /// 巡回を開始する
    /// </summary>
    public void StartPatrol()
    {
        if (patrolWaypoints == null || patrolWaypoints.Count == 0)
        {
            Debug.LogWarning("[FireTruckAgent] 巡回ウェイポイントが設定されていません");
            return;
        }

        _isPatrolling = true;
        _isActive = true;
        _currentWaypointIndex = 0;
        _lastAnnounceTime = _envManager != null ? _envManager.CurrentTimeSec : 0f;

        if (_navAgent != null && patrolWaypoints[0] != null)
        {
            _navAgent.isStopped = false;
            _navAgent.SetDestination(patrolWaypoints[0].position);
        }

        Debug.Log("[FireTruckAgent] 巡回を開始しました");
    }

    /// <summary>
    /// 巡回を停止する
    /// </summary>
    public void StopPatrol()
    {
        _isPatrolling = false;
        _isActive = false;

        if (_navAgent != null)
        {
            _navAgent.isStopped = true;
        }

        Debug.Log("[FireTruckAgent] 巡回を停止しました");
    }

    void Update()
    {
        if (!_isActive || _envManager == null || _navAgent == null) return;

        // ウェイポイントに到達したら次へ
        if (!_navAgent.pathPending && _navAgent.remainingDistance < 2f)
        {
            _currentWaypointIndex = (_currentWaypointIndex + 1) % patrolWaypoints.Count;
            if (patrolWaypoints[_currentWaypointIndex] != null)
            {
                _navAgent.SetDestination(patrolWaypoints[_currentWaypointIndex].position);
            }
        }

        // 定期的に呼びかけ
        float currentTime = _envManager.CurrentTimeSec;
        if (currentTime - _lastAnnounceTime >= announceInterval)
        {
            _lastAnnounceTime = currentTime;
            AnnounceToNearbyEvacuees();
        }
    }

    /// <summary>
    /// 近くの避難者に呼びかけを行う
    /// </summary>
    private void AnnounceToNearbyEvacuees()
    {
        if (_envManager == null || _envManager.Evacuees == null) return;

        int notifiedCount = 0;

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null || !evacueeObj.activeSelf) continue;

            float distance = Vector3.Distance(transform.position, evacueeObj.transform.position);
            if (distance <= loudspeakerRange)
            {
                var evacuee = evacueeObj.GetComponent<Evacuee>();
                if (evacuee != null)
                {
                    evacuee.OnHeardFireTruckAnnounce(announceMessage);
                    notifiedCount++;
                }
            }
        }

        if (notifiedCount > 0)
        {
            Debug.Log($"[FireTruckAgent] {notifiedCount}人の避難者に呼びかけました: {announceMessage}");
        }
    }

    /// <summary>
    /// エピソードリセット時の処理
    /// </summary>
    private void OnEpisodeReset()
    {
        StopPatrol();
        _currentWaypointIndex = 0;
        _lastAnnounceTime = 0f;

        // 初期位置に戻す（最初のウェイポイントがあれば）
        if (patrolWaypoints.Count > 0 && patrolWaypoints[0] != null)
        {
            transform.position = patrolWaypoints[0].position;
        }
    }

    void OnDrawGizmosSelected()
    {
        // 拡声器の可聴範囲を可視化
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f); // オレンジ
        Gizmos.DrawWireSphere(transform.position, loudspeakerRange);

        // 巡回経路を可視化
        if (patrolWaypoints != null && patrolWaypoints.Count > 1)
        {
            Gizmos.color = Color.red;
            for (int i = 0; i < patrolWaypoints.Count; i++)
            {
                if (patrolWaypoints[i] == null) continue;

                int nextIndex = (i + 1) % patrolWaypoints.Count;
                if (patrolWaypoints[nextIndex] != null)
                {
                    Gizmos.DrawLine(patrolWaypoints[i].position, patrolWaypoints[nextIndex].position);
                }

                // ウェイポイントをマーク
                Gizmos.DrawSphere(patrolWaypoints[i].position, 2f);
            }
        }
    }
}
}
