using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLM;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 避難者の制御を行うクラス
/// </summary>
public class Evacuee : MonoBehaviour {
    
    public enum EnergyLabel
    {
        High,
        Medium,
        Low
    }

    public enum StressLabel
    {
        Calm,
        Alert,
        Panicked
    }

    /// <summary>
    /// 移動手段の種類
    /// </summary>
    public enum TransportMode
    {
        WALKING,    // 徒歩（NavMeshAgent）
        DRIVING,    // 車両（SUMO経由）
    }

    [Header("Transport Mode")]
    public TransportMode CurrentTransportMode = TransportMode.WALKING;
    /// <summary>SUMO上の車両ID（DRIVINGモード時に使用）</summary>
    [HideInInspector] public string SumoVehicleId;
    /// <summary>駐車して徒歩に切り替えたかどうか</summary>
    private bool _hasAbandonedVehicle;
    /// <summary>車両コントローラーへの参照</summary>
    private Traffic.VehicleController _vehicleController;

    [Header("Movement Target")]
    public GameObject Target; // 現在の移動目標
    private NavMeshAgent NavAgent; // NavMeshAgentコンポーネント
    private EnvManager _env; // ShelterEnvManagerの参照
    private bool isEvacuating = false; // 避難処理中のフラグ。当たり判定により発火するため、複数回避難処理が行われるのを防ぐためのフラグ
    private List<string> excludeShelters; //1度避難したタワーのUUIDを格納するリスト
    
    [Header("Movement Settings")]
    public float DefaultSpeed = 3f;
    public float MinSpeed = 1f;
    public float MaxSpeed = 10f;

    [Header("Status")]
    [Range(0f, 1f)]
    public float EnergyLevel = 1.0f;
    public EnergyLabel EnergyState = EnergyLabel.High;
    [Range(0f, 1f)]
    public float StressLevel = 0.0f;
    public StressLabel StressState = StressLabel.Calm;
    [TextArea]
    public string StressReason;
    [Range(0f, 1f)]
    public float StaminaLevel = 1.0f;
    public List<string> InjuryTags = new List<string>();
    [TextArea]
    public string InjuryNotes;

    [Header("Speed Choice")]
    public LLM.SpeedChoice CurrentSpeedChoice = LLM.SpeedChoice.NORMAL;

    // 体力管理の定数
    private const float STAMINA_THRESHOLD_FAST = 0.3f;      // 急ぎ足に必要な最低体力
    private const float STAMINA_THRESHOLD_RUN = 0.5f;       // 走るに必要な最低体力
    private const float STAMINA_DRAIN_FAST = 0.02f;         // 急ぎ足: 2%/秒
    private const float STAMINA_DRAIN_RUN = 0.05f;          // 走る: 5%/秒
    private const float STAMINA_RECOVERY_SLOW = 0.01f;      // ゆっくり: 1%/秒回復
    private const float STAMINA_RECOVERY_STOP = 0.03f;      // 停止中: 3%/秒回復
    private const float STAMINA_DRAIN_ELDERLY_MULT = 1.5f;  // 高齢者消費倍率

    // 速度係数マッピング
    private static readonly Dictionary<LLM.SpeedChoice, float> SpeedChoiceMultipliers = new Dictionary<LLM.SpeedChoice, float>
    {
        { LLM.SpeedChoice.SLOW, 0.6f },
        { LLM.SpeedChoice.NORMAL, 1.0f },
        { LLM.SpeedChoice.FAST, 1.5f },
        { LLM.SpeedChoice.RUN, 2.0f }
    };

    // 会話・連絡のラグ時間（シミュレーション秒）
    // TALK: 発話時間も含めて5秒（往復で10秒程度）
    // CONTACT: 電話/メール送受信で3秒（往復で6秒程度）
    private const float CONVERSATION_MESSAGE_LAG_SEC = 5.0f;
    private const float CONTACT_MESSAGE_LAG_SEC = 3.0f;
    // 注意: タイムアウトはシミュレーション時間で計測される
    // TimeScale=7の場合、シミュレーション70秒 = 実時間10秒
    // LLM応答には実時間で5-10秒程度必要なため、TimeScale考慮して設定
    private const float CONVERSATION_TIMEOUT_SEC = 70.0f;  // 会話応答タイムアウト（TimeScale=7で実時間10秒相当）
    private const float CONTACT_TIMEOUT_SEC = 105.0f;      // 連絡応答タイムアウト（TimeScale=7で実時間15秒相当）

    // 日本語名マッピング
    private static readonly Dictionary<LLM.SpeedChoice, string> SpeedChoiceNames = new Dictionary<LLM.SpeedChoice, string>
    {
        { LLM.SpeedChoice.SLOW, "ゆっくり" },
        { LLM.SpeedChoice.NORMAL, "普通" },
        { LLM.SpeedChoice.FAST, "急ぎ足" },
        { LLM.SpeedChoice.RUN, "走る" }
    };

    [Header("Goal Labeling")]
    [SerializeField] private string GoalLabelOverride;

    [Header("Decision Mode")]
    [Tooltip("意思決定モード: LLM=LLMエージェント, RuleBased=ルールベースエージェント")]
    public bool UseLLMDecision = false;
    [Tooltip("ルールベース意思決定を使用する（UseLLMDecision=falseの場合に適用）")]
    public bool UseRuleBasedDecision = false;
    public LLMDecisionClient DecisionClient;
    public bool FallbackToNearest = true;
    private RuleBasedDecisionMaker _ruleBasedDecisionMaker;
    [Tooltip("LLMの再呼び出し間隔（秒）")]
    public float LLMDecisionInterval = 120f;
    [Tooltip("最小リクエスト間隔（秒）- 連続リクエストを防ぐ")]
    public float MinRequestInterval = 1f;
    private CancellationTokenSource _llmCts;
    private bool _isRequestingLLMDecision;
    private float _lastLLMDecisionTime = 0f;
    private float _lastRequestTime = 0f; // 実際にリクエストを送った時刻
    private string _uniqueId; // 各避難者を区別するための一意のID（生成順序の数字）
    private PersonaData _persona; // この避難者のペルソナデータ

    [Header("Action State")]
    public LLM.ActionType CurrentAction = LLM.ActionType.EVACUATE; // 現在の行動タイプ
    private Vector3 _stayPosition; // 待機位置

    [Header("Contact / Family Actions")]
    private FamilyData _familyData;          // この避難者の家族情報
    private string _lastContactTarget;       // 直近で連絡を試みた家族
    private string _lastContactMessage;      // 直近で送信したメール本文
    private int _consecutiveContactCount = 0; // 連続CONTACT回数
    private const int MAX_CONSECUTIVE_CONTACT = 5; // 連続CONTACTの上限（セッション間）
    private bool _contactCooldown = false; // CONTACT上限後の一時禁止フラグ
    // CONTACT応答状態（家族から連絡を受けた側の状態管理）
    private bool _isRespondingToFamilyContact = false;
    private LLM.ActionType _actionBeforeFamilyContact;
    private string _targetShelterBeforeFamilyContact;

    [Header("Search Family Action")]
    private FamilyMember _searchFamilyTarget;        // 現在探索中の家族メンバー
    private Evacuee _searchFamilyTargetEvacuee;      // 探索対象がシーン内に存在する場合のEvacuee参照
    private float _lastSearchFamilyCheck = 0f;       // 最後に座標更新した時刻
    private const float SEARCH_FAMILY_CHECK_INTERVAL = 10f; // 座標更新間隔（秒）
    private const float FAMILY_REUNION_DISTANCE = 10f; // 合流判定距離（メートル）

    [Header("RuleBased Action Recording")]
    private bool _firstRuleBasedActionRecorded = false; // RuleBasedエージェントの初回行動が記録されたか

    [Header("Follow Action")]
    private Evacuee _followTarget;           // 追従対象の避難者
    private LLM.ActionType _followTargetLastAction; // 追従対象の前回の行動（行動変更検知用）
    private float _followDistance = 5f;      // 追従距離（メートル）- 密集防止のため拡大
    private float _followCheckInterval = 1f; // 追従チェック間隔（秒）
    private float _lastFollowCheck = 0f;     // 最後に追従チェックした時刻
    private const float FOLLOW_SEARCH_RADIUS = 30f; // 周辺避難者検索半径（メートル）
    private List<Evacuee> _followers = new List<Evacuee>(); // この避難者を追従しているFOLLOWERのリスト

    [Header("Information Diffusion")]
    private float _informationDiffusionCheckInterval = 1f; // 情報伝播チェック間隔（秒）
    private float _lastInformationDiffusionCheck = 0f;     // 最後に情報伝播チェックした時刻

    [Header("Talk Action")]
    private List<LLM.ConversationLogEntry> _conversationHistory = new List<LLM.ConversationLogEntry>();
    private int _consecutiveTalkCount = 0;
    private const int MAX_CONSECUTIVE_TALK = 5;         // 連続TALKの上限（セッション間）
    private const int MAX_CONVERSATION_TURNS = 10;      // 1セッション内のターン上限
    private const int MAX_CONVERSATION_HISTORY = 5;
    private TaskCompletionSource<LLM.ConversationResponse> _conversationResponseTCS;
    // TALK応答状態（話しかけられた側の状態管理）
    private bool _isRespondingToConversation = false;
    private LLM.ActionType _actionBeforeConversation;
    private string _targetShelterBeforeConversation;
    // マルチターン会話セッション管理
    private LLM.ConversationSessionContext _conversationSession = null;

    [Header("Hierarchical Decision Making")]
    private LLM.LongTermGoalPayload _currentLongTermGoal;    // 現在の長期目標
    private LLM.MidTermPlanPayload _currentMidTermPlan;      // 現在の中期計画
    private float _lastGoalUpdateTime = 0f;                  // 最後に長期目標を更新した時刻
    private float _lastPlanUpdateTime = 0f;                  // 最後に中期計画を更新した時刻

    [Header("Short-term Memory (Action History)")]
    private List<LLM.ActionHistoryEntry> _actionHistory = new List<LLM.ActionHistoryEntry>();
    private string _summarizedActionHistory = null;          // 要約済み行動履歴
    private int _totalActionCount = 0;                       // 総行動回数
    private const int MAX_ACTION_HISTORY = 5;                // 保持する行動履歴の最大件数

    [Header("Alert / Broadcast State")]
    private bool _hasHeardBroadcast = false;     // 行政無線の放送を聞いたか
    private string _lastBroadcastMessage = "";    // 最後に聞いた放送内容
    private bool _hasReceivedJAlert = false;      // Jアラートを受信したか
    private string _lastJAlertMessage = "";       // 最後に受信したJアラート内容

    [Header("Audio Perception State")]
    private bool _hasHeardRumble = false;         // 地鳴りを聞いたか
    private float _lastRumbleIntensity = 0f;      // 最後に感じた地鳴りの強度
    private bool _hasHeardSiren = false;          // サイレンを聞いたか
    private bool _hasHeardFireTruck = false;      // 消防団の呼びかけを聞いたか
    private string _lastFireTruckMessage = "";    // 最後に聞いた消防団のメッセージ
    
    /// <summary>
    /// スマホを持っているか（ペルソナから取得）
    /// </summary>
    public bool HasSmartphone => _persona != null && _persona.has_smartphone;

    /// <summary>
    /// 車両を所有しているか（ペルソナから取得）
    /// </summary>
    public bool HasVehicle => _persona != null && _persona.has_vehicle;

    /// <summary>
    /// 運転可能か（ペルソナから取得）
    /// </summary>
    public bool CanDrive => _persona != null && _persona.can_drive;

    /// <summary>
    /// 車両で避難中かどうか
    /// </summary>
    public bool IsDriving => CurrentTransportMode == TransportMode.DRIVING && !_hasAbandonedVehicle;

    /// <summary>
    /// 車両を放棄して徒歩に切り替える（渋滞で車を捨てて歩く等）
    /// </summary>
    public async void SwitchToWalking()
    {
        if (CurrentTransportMode != TransportMode.DRIVING) return;

        Debug.Log($"[Evacuee] {gameObject.name}: 車両を放棄して徒歩に切り替えます");

        // SUMOから車両を削除
        if (!string.IsNullOrEmpty(SumoVehicleId) && Traffic.TrafficClient.Instance != null)
        {
            await Traffic.TrafficClient.Instance.RemoveVehicleAsync(SumoVehicleId);
        }

        // 車両プールから返却
        if (Traffic.VehiclePoolManager.Instance != null && !string.IsNullOrEmpty(SumoVehicleId))
        {
            Traffic.VehiclePoolManager.Instance.ReturnToPool(SumoVehicleId);
        }

        _hasAbandonedVehicle = true;
        CurrentTransportMode = TransportMode.WALKING;
        _vehicleController = null;

        // NavMeshAgentを有効化（現在位置でNavMesh上に補正）
        if (NavAgent != null)
        {
            NavAgent.enabled = true;
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(transform.position, out hit, 50f, UnityEngine.AI.NavMesh.AllAreas))
            {
                NavAgent.Warp(hit.position);
            }
        }
    }

    /// <summary>
    /// 車両モードに切り替える（出発時）
    /// </summary>
    public async void SwitchToDriving(string originEdge, string destinationEdge)
    {
        if (!HasVehicle || !CanDrive || _hasAbandonedVehicle) return;

        CurrentTransportMode = TransportMode.DRIVING;
        SumoVehicleId = $"veh_{_uniqueId}";

        // NavMeshAgentを無効化
        if (NavAgent != null)
        {
            NavAgent.isStopped = true;
            NavAgent.enabled = false;
        }

        // SUMOに車両を追加
        if (Traffic.TrafficClient.Instance != null)
        {
            bool success = await Traffic.TrafficClient.Instance.SpawnVehicleAsync(
                SumoVehicleId, originEdge, destinationEdge);
            if (!success)
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 車両スポーンに失敗。徒歩に戻ります");
                SwitchToWalking();
            }
        }
    }

    /// <summary>
    /// 避難者のID（TALK行動で使用）
    /// </summary>
    public string EvacueeId => _uniqueId;

    /// <summary>
    /// 避難者のペルソナ名（TALK行動で使用）
    /// </summary>
    public string PersonaName => _persona?.name ?? gameObject.name;

    /// <summary>
    /// 避難者の役割（TALK行動で使用）
    /// </summary>
    public string PersonaRole => _persona?.role ?? "";

    /// <summary>
    /// 現在の目標避難所ID（TALK行動で使用）
    /// UnityのオブジェクトはC#のnull条件演算子と異なる挙動をするため、明示的にnullチェック
    /// </summary>
    public string CurrentTargetShelterId => (Target != null && Target) ? Target.name : "";

    /// <summary>
    /// 現在の追従対象（FOLLOW循環検出で使用）
    /// </summary>
    public Evacuee FollowTarget => _followTarget;


    /// <summary>
    /// 避難者のIDを設定する（EnvManagerから呼び出される）
    /// </summary>
    /// <param name="id">避難者の生成順序（1から始まる）</param>
    public void SetEvacueeId(int id)
    {
        _uniqueId = id.ToString();
        
        // ペルソナデータを読み込んで設定
        _persona = PersonaManager.GetPersona(id);
        if (_persona != null)
        {
            // 速度倍率を適用（NavAgentが初期化されていない場合は後で適用）
            if (NavAgent != null)
            {
                NavAgent.speed = DefaultSpeed * _persona.speed_multiplier;
            }
        }
        else
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: ペルソナデータが見つかりません (agent_id: {id})");
        }

        // 家族データを読み込んで設定
        _familyData = FamilyManager.GetFamily(id);

        // GameObject名を更新してHierarchyで識別可能にする
        string personaName = _persona?.name ?? "Unknown";
        gameObject.name = $"Evacuee_{_uniqueId}_{personaName}";

        // 交通シミュレーション: ペルソナと実験設定に基づいて移動モードを判定
        InitializeTransportMode();
    }

    /// <summary>
    /// 交通シミュレーションの実験設定とペルソナに基づき移動モードを決定する。
    /// DRIVINGと判定された場合はTargetが設定され次第、SUMOに車両をスポーンする
    /// （実際のスポーンはTick内でTryBeginPendingDrive()が処理する）。
    /// </summary>
    private void InitializeTransportMode()
    {
        if (_persona == null)
        {
            Debug.LogWarning($"[TransportDiag] {gameObject.name}: _persona==null スキップ");
            return;
        }

        bool hasEC = ExperimentConfig.Instance != null;
        float usageRate = hasEC ? ExperimentConfig.Instance.VehicleUsageRate : 0.6f;
        bool enableTraffic = hasEC && ExperimentConfig.Instance.EnableTrafficSimulation;

        CurrentTransportMode = Traffic.TransportModeDecider.Decide(_persona, usageRate, enableTraffic);

        Debug.Log($"[TransportDiag] {gameObject.name}: hasEC={hasEC} enableTraffic={enableTraffic} usageRate={usageRate} has_vehicle={_persona.has_vehicle} can_drive={_persona.can_drive} physical={_persona.physical_condition} -> mode={CurrentTransportMode}");

        if (CurrentTransportMode == TransportMode.DRIVING)
        {
            // Targetが確定した後で実際のスポーンを行う
            _pendingDriveStart = true;
            Debug.Log($"[Evacuee] {gameObject.name}: DRIVINGモードで初期化（車両スポーンはTarget確定後）");
        }
    }

    /// <summary>車両スポーンを待機中（Targetが決まり次第SUMOに追加する）</summary>
    private bool _pendingDriveStart = false;
    /// <summary>最後にShouldAbandonVehicleを評価した時刻</summary>
    private float _lastAbandonCheckTime = 0f;
    /// <summary>放棄判定の評価間隔（秒）</summary>
    private const float ABANDON_CHECK_INTERVAL_SEC = 5f;
    /// <summary>現在DRIVING中に向かっているTargetの参照（変更検出用）</summary>
    private GameObject _lastDrivenTarget = null;

    /// <summary>
    /// 車両スポーンが保留されている場合、現在位置とTargetから最寄りのSUMOエッジを算出し
    /// SwitchToDrivingを呼び出す。TrafficClientが未接続なら何もしない。
    /// </summary>
    private void TryBeginPendingDrive()
    {
        if (!_pendingDriveStart) return;
        if (_hasAbandonedVehicle) { _pendingDriveStart = false; return; }
        if (Target == null) return;
        if (Traffic.TrafficClient.Instance == null || !Traffic.TrafficClient.Instance.IsConnected) return;

        _pendingDriveStart = false;
        _lastDrivenTarget = Target;

        // edge IDは空で送信し、Unity座標をサーバーに渡してSUMO edge自動解決させる
        SwitchToDrivingByPosition(transform.position, Target.transform.position);
    }

    /// <summary>
    /// Unity座標ベースで車両モードを開始する。
    /// edge IDの解決はPython (traffic_server) 側で行う。
    /// </summary>
    private async void SwitchToDrivingByPosition(Vector3 originPos, Vector3 destPos)
    {
        if (!HasVehicle || !CanDrive || _hasAbandonedVehicle) return;

        CurrentTransportMode = TransportMode.DRIVING;
        SumoVehicleId = $"veh_{_uniqueId}";

        // NavMeshAgentを無効化
        if (NavAgent != null)
        {
            NavAgent.isStopped = true;
            NavAgent.enabled = false;
        }

        // SUMOに車両を追加（座標ベース: edge解決はサーバー側）
        if (Traffic.TrafficClient.Instance != null)
        {
            bool success = await Traffic.TrafficClient.Instance.SpawnVehicleByPositionAsync(
                SumoVehicleId, originPos, destPos);
            if (!success)
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 車両スポーンに失敗。徒歩に戻ります");
                SwitchToWalking();
            }
        }
    }

    /// <summary>
    /// DRIVING中、VehiclePoolManagerが保持する対応車両GameObjectの位置/回転を
    /// Evacuee本体にコピーし、見た目の位置を一致させる。
    /// 初回はキャッシュ（_vehicleController）を解決し、OwnerEvacueeIdも書き込む。
    /// </summary>
    private void SyncPositionWithVehicle()
    {
        if (string.IsNullOrEmpty(SumoVehicleId)) return;
        if (_vehicleController == null)
        {
            var pool = Traffic.VehiclePoolManager.Instance;
            if (pool == null) return;
            _vehicleController = pool.GetVehicle(SumoVehicleId);
            if (_vehicleController == null) return; // 初回更新前
            _vehicleController.OwnerEvacueeId = _uniqueId;
        }

        transform.position = _vehicleController.transform.position;
        transform.rotation = _vehicleController.transform.rotation;
    }

    /// <summary>
    /// 走行中に渋滞状況を評価し、ShouldAbandonVehicle()で放棄すべきと判定されたら徒歩に切り替える。
    /// ABANDON_CHECK_INTERVAL_SEC 間隔でスロットル。
    /// </summary>
    private void EvaluateAbandonVehicle()
    {
        if (Time.time - _lastAbandonCheckTime < ABANDON_CHECK_INTERVAL_SEC) return;
        _lastAbandonCheckTime = Time.time;

        if (Target == null) return;
        var provider = Traffic.TrafficStateProvider.Instance;
        if (provider == null) return;

        var conditions = provider.GetTrafficConditions(transform.position, 100f);
        if (conditions == null || conditions.Count == 0) return;

        // 現在位置に最も近いエッジの渋滞レベル
        int congestionLevel = conditions[0].congestionLevel;
        float remainingDist = Vector3.Distance(transform.position, Target.transform.position);
        // 徒歩所要時間（分）: DefaultSpeed m/s 前提で概算
        float walkingMinutes = remainingDist / Mathf.Max(0.1f, DefaultSpeed) / 60f;

        if (Traffic.TransportModeDecider.ShouldAbandonVehicle(congestionLevel, remainingDist, walkingMinutes))
        {
            Debug.Log($"[Evacuee] {gameObject.name}: 渋滞評価により車両を放棄 (level={congestionLevel}, remaining={remainingDist:F0}m, walk={walkingMinutes:F1}min)");
            SwitchToWalking();
        }
    }

    /// <summary>
    /// DRIVING中にTarget(避難先)が変わっていれば、既存車両を削除して新しい目的地で再スポーンを保留する。
    /// SUMO TraCIのsetRouteは完全なエッジパスを要するため、Phase Bではシンプルに再スポーンで対応する。
    /// </summary>
    private void CheckDrivingTargetChange()
    {
        if (Target == null) return;
        if (_lastDrivenTarget == Target) return;

        Debug.Log($"[Evacuee] {gameObject.name}: DRIVING中の目的地変更を検出。車両を再スポーンします");

        // 既存車両をSUMOから削除（非同期だが待機不要）
        if (!string.IsNullOrEmpty(SumoVehicleId) && Traffic.TrafficClient.Instance != null)
        {
            _ = Traffic.TrafficClient.Instance.RemoveVehicleAsync(SumoVehicleId);
        }
        if (Traffic.VehiclePoolManager.Instance != null && !string.IsNullOrEmpty(SumoVehicleId))
        {
            Traffic.VehiclePoolManager.Instance.ReturnToPool(SumoVehicleId);
        }

        // 保留フラグを立て、次回Update内のTryBeginPendingDriveで新Targetに対してスポーン
        CurrentTransportMode = TransportMode.WALKING; // 一時的にWALKING
        SumoVehicleId = null;
        _vehicleController = null;
        _lastDrivenTarget = null;
        _pendingDriveStart = true;
    }
    
    /// <summary>
    /// 家族データを取得
    /// </summary>
    public FamilyData GetFamilyData()
    {
        return _familyData;
    }

    /// <summary>
    /// ペルソナデータを取得
    /// </summary>
    public PersonaData GetPersona()
    {
        return _persona;
    }

    /// <summary>
    /// 行政無線の放送を聞いたことを記録（AlertManagerから呼ばれる）
    /// </summary>
    public void OnHeardBroadcast(string message)
    {
        if (!_hasHeardBroadcast || _lastBroadcastMessage != message)
        {
            _hasHeardBroadcast = true;
            _lastBroadcastMessage = message;
            Debug.Log($"[Evacuee] {gameObject.name}: 行政無線の放送を聞きました - {message}");

            // ルールベースDecisionMakerにも通知
            if (_ruleBasedDecisionMaker != null)
            {
                _ruleBasedDecisionMaker.OnHeardBroadcast();
            }
        }
    }

    /// <summary>
    /// Jアラートを受信したことを記録（AlertManagerから呼ばれる）
    /// </summary>
    public void OnReceivedJAlert(string message)
    {
        if (!_hasReceivedJAlert || _lastJAlertMessage != message)
        {
            _hasReceivedJAlert = true;
            _lastJAlertMessage = message;
            // ログはAlertManager側で一括出力するため、ここでは出力しない

            // ルールベースDecisionMakerにも通知
            if (_ruleBasedDecisionMaker != null)
            {
                _ruleBasedDecisionMaker.OnReceivedJAlert();
            }
        }
    }

    /// <summary>
    /// 消防団の呼びかけを聞いたことを記録（FireTruckAgentから呼ばれる）
    /// </summary>
    public void OnHeardFireTruckAnnounce(string message)
    {
        if (!_hasHeardFireTruck || _lastFireTruckMessage != message)
        {
            _hasHeardFireTruck = true;
            _lastFireTruckMessage = message;
            Debug.Log($"[Evacuee] {gameObject.name}: 消防団の呼びかけを聞きました - {message}");

            // ルールベースDecisionMakerにも通知
            if (_ruleBasedDecisionMaker != null)
            {
                _ruleBasedDecisionMaker.OnHeardFireTruck();
            }
        }
    }

    /// <summary>
    /// 聴覚情報の更新（AudioEventManagerから定期的に呼ばれる、または自身で更新）
    /// </summary>
    public void UpdateAudioPerception()
    {
        var audioManager = FindFirstObjectByType<AudioEventManager>();
        if (audioManager == null) return;

        // 地鳴り
        float rumbleIntensity = audioManager.GetRumbleIntensityAt(transform.position);
        if (rumbleIntensity > 0.3f && !_hasHeardRumble)
        {
            _hasHeardRumble = true;
            _lastRumbleIntensity = rumbleIntensity;
            Debug.Log($"[Evacuee] {gameObject.name}: 地鳴りを感じました（強度: {rumbleIntensity:F2}）");
        }
        else if (rumbleIntensity > _lastRumbleIntensity)
        {
            _lastRumbleIntensity = rumbleIntensity;
        }

        // サイレン
        if (!_hasHeardSiren && audioManager.CanHearSiren(transform.position))
        {
            _hasHeardSiren = true;
            Debug.Log($"[Evacuee] {gameObject.name}: サイレンが聞こえます");
        }
    }

    /// <summary>
    /// 知覚情報をLLMリクエスト用のPayloadとして取得
    /// </summary>
    public LLM.PerceptionStatePayload BuildPerceptionPayload()
    {
        return new LLM.PerceptionStatePayload
        {
            // 聴覚情報
            has_heard_rumble = _hasHeardRumble,
            rumble_intensity = _lastRumbleIntensity,
            has_heard_siren = _hasHeardSiren,
            has_heard_fire_truck = _hasHeardFireTruck,
            last_fire_truck_message = _lastFireTruckMessage,

            // 既存の放送・Jアラート情報
            has_heard_broadcast = _hasHeardBroadcast,
            last_broadcast_message = _lastBroadcastMessage,
            has_received_j_alert = _hasReceivedJAlert,
            last_j_alert_message = _lastJAlertMessage
        };
    }

    /// <summary>
    /// エピソード開始時に放送・Jアラートの状態をリセット
    /// </summary>
    public void ResetAlertState()
    {
        _hasHeardBroadcast = false;
        _lastBroadcastMessage = "";
        _hasReceivedJAlert = false;
        _lastJAlertMessage = "";

        // 聴覚状態もリセット
        _hasHeardRumble = false;
        _lastRumbleIntensity = 0f;
        _hasHeardSiren = false;
        _hasHeardFireTruck = false;
        _lastFireTruckMessage = "";

        // ルールベースDecisionMakerもリセット
        if (_ruleBasedDecisionMaker != null)
        {
            _ruleBasedDecisionMaker.Reset();
        }
    }

    /// <summary>
    /// 新しいエピソード開始時に全ての動的状態をリセット
    /// ResetAlertState()を内包し、さらにLLM関連の状態もリセットする
    /// </summary>
    public void ResetForNewEpisode()
    {
        // 交通モードのリセット
        if (IsDriving && !string.IsNullOrEmpty(SumoVehicleId))
        {
            if (Traffic.TrafficClient.Instance != null)
                _ = Traffic.TrafficClient.Instance.RemoveVehicleAsync(SumoVehicleId);
            if (Traffic.VehiclePoolManager.Instance != null)
                Traffic.VehiclePoolManager.Instance.ReturnToPool(SumoVehicleId);
        }
        CurrentTransportMode = TransportMode.WALKING;
        SumoVehicleId = null;
        _hasAbandonedVehicle = false;
        _vehicleController = null;
        if (NavAgent != null) NavAgent.enabled = true;

        // 警報・放送状態のリセット
        ResetAlertState();

        // 行動状態のリセット
        isEvacuating = false;
        excludeShelters = new List<string>();
        CurrentAction = LLM.ActionType.EVACUATE;
        _stayPosition = Vector3.zero;

        // 連絡・連続行動のリセット
        _lastContactTarget = null;
        _lastContactMessage = null;
        _consecutiveContactCount = 0;
        _contactCooldown = false;
        _isRespondingToFamilyContact = false;
        _actionBeforeFamilyContact = LLM.ActionType.EVACUATE;
        _targetShelterBeforeFamilyContact = null;

        // 家族探索のリセット
        _searchFamilyTarget = null;
        _searchFamilyTargetEvacuee = null;
        _lastSearchFamilyCheck = 0f;

        // RuleBased行動記録のリセット
        _firstRuleBasedActionRecorded = false;

        // 追従のリセット
        _followTarget = null;
        _followers = new List<Evacuee>();
        _lastFollowCheck = 0f;

        // 会話のリセット
        _conversationHistory = new List<LLM.ConversationLogEntry>();
        _consecutiveTalkCount = 0;
        _conversationResponseTCS = null;
        _isRespondingToConversation = false;
        _actionBeforeConversation = LLM.ActionType.EVACUATE;
        _targetShelterBeforeConversation = null;
        _conversationSession = null;

        // 階層的意思決定のリセット
        _currentLongTermGoal = default;
        _currentMidTermPlan = default;
        _lastGoalUpdateTime = 0f;
        _lastPlanUpdateTime = 0f;

        // 行動履歴のリセット
        _actionHistory = new List<LLM.ActionHistoryEntry>();
        _summarizedActionHistory = null;
        _totalActionCount = 0;

        // LLM関連のリセット
        _lastLLMDecisionTime = 0f;
        _lastRequestTime = 0f;
        _isRequestingLLMDecision = false;

        // 移動目標のリセット
        Target = null;
    }

    void Awake() {
        // IDはEnvManagerから設定されるため、ここでは初期化しない
        NavAgent = GetComponent<NavMeshAgent>();
        if (NavAgent != null)
        {
            NavAgent.speed = DefaultSpeed;
        }
        excludeShelters = new List<string>();

        _env = GetComponentInParent<EnvManager>();

        // ExperimentConfigから設定を取得
        if (ExperimentConfig.Instance != null)
        {
            UseLLMDecision = ExperimentConfig.ShouldUseLLM();
            UseRuleBasedDecision = !UseLLMDecision;
            Debug.Log($"[Evacuee] {gameObject.name}: ExperimentConfigから設定を取得 - UseLLM={UseLLMDecision}, UseRuleBased={UseRuleBasedDecision}");
        }
        else
        {
            // ExperimentConfigが見つからない場合のフォールバック
            // デフォルトではLLMを使用（従来の動作を維持）
            UseLLMDecision = true;
            UseRuleBasedDecision = false;
            Debug.LogWarning($"[Evacuee] {gameObject.name}: ExperimentConfig.Instanceがnullです。デフォルト設定を使用: UseLLM={UseLLMDecision}");
        }

        if (UseLLMDecision && DecisionClient == null)
        {
            DecisionClient = FindFirstObjectByType<LLMDecisionClient>();
        }

        // ルールベース意思決定の初期化
        if (UseRuleBasedDecision)
        {
            _ruleBasedDecisionMaker = gameObject.AddComponent<RuleBasedDecisionMaker>();
            _ruleBasedDecisionMaker.Initialize(this, _env, NavAgent);

            // Level 2: 家族情報を渡す
            if (_familyData != null)
            {
                _ruleBasedDecisionMaker.InitializeExtended(_familyData);
            }
        }

        _env.Agent.OnDidActioned += () => {
            // エージェントが建物を選択したことを検知して最短距離の避難所を探す
            if(this != null && this.gameObject.activeSelf) {
                if (UseLLMDecision && DecisionClient != null)
                {
                    RequestLLMDecision();
                }
                else if (UseRuleBasedDecision && _ruleBasedDecisionMaker != null)
                {
                    RequestRuleBasedDecision();
                }
                else
                {
                    MoveToNearestShelter();
                }
            }
        };
    }

    void Start()
    {
        // NavAgentが初期化された後にペルソナの速度倍率を適用
        if (_persona != null && NavAgent != null)
        {
            NavAgent.speed = DefaultSpeed * _persona.speed_multiplier;
        }

        // NavMeshAgent衝突回避設定の改善
        if (NavAgent != null)
        {
            NavAgent.stoppingDistance = 0.5f;  // 停止距離を設ける
            NavAgent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            NavAgent.avoidancePriority = 50;   // 初期値は中間
        }
    }

    private void OnDestroy()
    {
        // FOLLOWERから自分を削除
        UnregisterFromFollowTarget();
        
        // 自分を追従しているFOLLOWERに通知
        NotifyFollowersOfTargetLost();
        
        _llmCts?.Cancel();
        _llmCts?.Dispose();
        _llmCts = null;
    }

    void Update()
    {
        // 体力の更新（毎フレーム）
        UpdateStamina();

        // 交通シミュレーション: DRIVING保留中ならスポーン試行
        if (_pendingDriveStart)
        {
            TryBeginPendingDrive();
        }
        // 交通シミュレーション: 走行中の定期評価（放棄判定 & 目的地変更検出）
        if (IsDriving)
        {
            SyncPositionWithVehicle();
            EvaluateAbandonVehicle();
            CheckDrivingTargetChange();
        }

        // FOLLOW行動の更新処理（位置追跡のみ、行動変更の検知は通知で行う）
        if (CurrentAction == LLM.ActionType.FOLLOW && _followTarget != null)
        {
            UpdateFollowAction();
        }

        // SEARCH_FAMILY行動の更新処理（シーン内家族の座標を定期的に更新）
        if (CurrentAction == LLM.ActionType.SEARCH_FAMILY && _searchFamilyTargetEvacuee != null)
        {
            UpdateSearchFamilyAction();
        }

        // ルールベースモードの定期評価
        if (UseRuleBasedDecision && _ruleBasedDecisionMaker != null && gameObject.activeSelf && _env != null)
        {
            // 情報伝播チェック（待機中のエージェントが近くの避難者から情報を受け取る）
            CheckInformationDiffusion();

            // ルールベースは常に再評価可能（内部で間隔制御）
            RequestRuleBasedDecision();
            return;
        }

        // LLMを使用している場合、定期的に再呼び出しをチェック
        if (!UseLLMDecision || DecisionClient == null || !gameObject.activeSelf || _env == null)
        {
            return;
        }

        // 最後にLLMを呼び出した時刻が記録されている場合（初期呼び出し後）
        if (_lastLLMDecisionTime <= 0f)
        {
            return;
        }

        // シミュレーション内の経過時間を使用
        float currentSimTime = _env.CurrentTimeSec;
        float elapsedSinceLastDecision = currentSimTime - _lastLLMDecisionTime;
        if (elapsedSinceLastDecision >= LLMDecisionInterval)
        {
            // 既にリクエスト中でない場合のみ再呼び出し
            if (!_isRequestingLLMDecision)
            {
                Debug.Log($"[Evacuee] {gameObject.name}: 定期LLM再呼び出し（シミュレーション経過時間: {elapsedSinceLastDecision:F2}秒）");
                RequestLLMDecision();
            }
        }
    }

    /// <summary>
    /// ルールベース意思決定を実行（Level 2: SEARCH_FAMILY/CONTACT対応）
    /// </summary>
    private void RequestRuleBasedDecision()
    {
        if (_ruleBasedDecisionMaker == null)
        {
            return;
        }

        var (actionType, targetShelter, reasoning, familyTarget) = _ruleBasedDecisionMaker.MakeDecisionExtended();

        // 行動が変更された場合、または初回の行動記録が未実行の場合に処理
        bool actionChanged = CurrentAction != actionType;
        bool targetChanged = actionType == LLM.ActionType.EVACUATE && Target != targetShelter;
        bool familyTargetChanged = actionType == LLM.ActionType.SEARCH_FAMILY &&
            (_searchFamilyTarget == null || (familyTarget != null && _searchFamilyTarget.agent_id != familyTarget.agent_id));
        bool needsRecording = !_firstRuleBasedActionRecorded;

        if (actionChanged || targetChanged || familyTargetChanged || needsRecording)
        {
            CurrentAction = actionType;

            // SimulationMetricsに記録（シングルトンインスタンスを優先）
            var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();

            if (metrics != null)
            {
                string targetName = targetShelter?.name ?? familyTarget?.name ?? "";
                metrics.RecordAction(_uniqueId ?? gameObject.name, actionType, targetName, reasoning, 1.0f);
                _firstRuleBasedActionRecorded = true;  // 初回記録完了をマーク
            }
            else
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: SimulationMetricsが見つかりません。アクション記録をスキップします。");
            }

            switch (actionType)
            {
                case LLM.ActionType.EVACUATE:
                    if (targetShelter != null)
                    {
                        Target = targetShelter;
                        SafeStopAgent(false);
                        SafeSetDestination(Target.transform.position);
                        Debug.Log($"[RuleBased] {gameObject.name}: EVACUATE - {targetShelter.name}に向かいます ({reasoning})");
                    }
                    break;

                case LLM.ActionType.STAY:
                    _stayPosition = transform.position;
                    SafeStopAgent(true);
                    SafeResetPath();
                    Debug.Log($"[RuleBased] {gameObject.name}: STAY - 待機します ({reasoning})");
                    break;

                case LLM.ActionType.SEARCH_FAMILY:
                    ExecuteRuleBasedSearchFamily(familyTarget, reasoning);
                    break;

                case LLM.ActionType.CONTACT:
                    ExecuteRuleBasedContact(familyTarget, reasoning);
                    break;

                case LLM.ActionType.FOLLOW:
                    ExecuteRuleBasedFollow(reasoning);
                    break;
            }
        }
    }

    /// <summary>
    /// ルールベースでSEARCH_FAMILYを実行
    /// </summary>
    private void ExecuteRuleBasedSearchFamily(FamilyMember target, string reasoning)
    {
        if (target == null) return;

        _searchFamilyTarget = target;

        // シーン内家族のEvacuee参照を取得
        var allEvacuees = FindObjectsByType<Evacuee>(FindObjectsSortMode.None);
        _searchFamilyTargetEvacuee = allEvacuees.FirstOrDefault(e =>
            e.EvacueeId == target.agent_id.ToString());

        Vector3 destination = _searchFamilyTargetEvacuee != null
            ? _searchFamilyTargetEvacuee.transform.position
            : target.search_position;

        SafeStopAgent(false);
        SafeSetDestination(destination);

        string trackingMode = _searchFamilyTargetEvacuee != null ? "リアルタイム追跡" : "予測位置";
        Debug.Log($"[RuleBased] {gameObject.name}: SEARCH_FAMILY - {target.relation} {target.name} を探しに向かいます ({trackingMode}) ({reasoning})");
    }

    /// <summary>
    /// ルールベースでCONTACTを実行
    /// </summary>
    private void ExecuteRuleBasedContact(FamilyMember target, string reasoning)
    {
        if (target == null) return;

        // 簡易実装: ログ出力と短時間の待機後にEVACUATEに移行
        Debug.Log($"[RuleBased] {gameObject.name}: CONTACT - {target.relation} {target.name} に連絡を取りました ({reasoning})");

        // 立ち止まって連絡
        SafeStopAgent(true);
        SafeResetPath();

        // CONTACT完了後はルール再評価で次の行動が決まる
        // （_hasContactedFamily=trueになっているので、次はEVACUATEかSTAYが選ばれる）
        StartCoroutine(TransitionAfterContact());
    }

    /// <summary>
    /// ルールベースでFOLLOWを実行（同調バイアスによる追従行動）
    /// </summary>
    private void ExecuteRuleBasedFollow(string reasoning)
    {
        // RuleBasedDecisionMakerから追従対象を取得
        if (_ruleBasedDecisionMaker == null)
        {
            Debug.LogWarning($"[RuleBased] {gameObject.name}: FOLLOW - RuleBasedDecisionMakerがありません。STAYにフォールバックします。");
            _stayPosition = transform.position;
            SafeStopAgent(true);
            SafeResetPath();
            return;
        }

        var targetEvacuee = _ruleBasedDecisionMaker.GetFollowTargetEvacuee();
        if (targetEvacuee == null)
        {
            Debug.LogWarning($"[RuleBased] {gameObject.name}: FOLLOW - 追従対象がありません。STAYにフォールバックします。");
            _stayPosition = transform.position;
            SafeStopAgent(true);
            SafeResetPath();
            return;
        }

        // 循環チェック
        if (WouldCreateFollowCycle(targetEvacuee))
        {
            Debug.LogWarning($"[RuleBased] {gameObject.name}: FOLLOW - {targetEvacuee.PersonaName}をフォローすると循環が発生するため、STAYにフォールバックします。");
            _stayPosition = transform.position;
            SafeStopAgent(true);
            SafeResetPath();
            return;
        }

        // 追従対象を設定
        _followTarget = targetEvacuee;

        // 追従対象に自分をフォロワーとして登録
        _followTarget.RegisterFollower(this);

        // 追従者はNavMesh優先度を低く設定（リーダーに避けさせない）
        if (NavAgent != null)
        {
            NavAgent.avoidancePriority = 80; // 高い値 = 低優先度（自分が避ける）
        }

        // 追従開始
        _lastFollowCheck = Time.time;
        _followTargetLastAction = _followTarget.CurrentAction;

        // 相手の行動に合わせた初期処理
        if (_followTargetLastAction == LLM.ActionType.EVACUATE)
        {
            // 相手がEVACUATEを選択していた場合 → 同じ避難所を目指す
            if (_followTarget.Target != null)
            {
                Target = _followTarget.Target;
                SafeStopAgent(false);
                SafeSetDestination(Target.transform.position);
                Debug.Log($"[RuleBased] {gameObject.name}: FOLLOW - {_followTarget.PersonaName}について行きます（相手は避難所に向かっているため、同じ避難所を目指します）({reasoning})");
            }
            else
            {
                // 相手の目標が設定されていない場合は位置を追従
                SafeStopAgent(false);
                SafeSetDestination(_followTarget.transform.position);
                Debug.Log($"[RuleBased] {gameObject.name}: FOLLOW - {_followTarget.PersonaName}について行きます（相手の位置を追従）({reasoning})");
            }
        }
        else if (_followTargetLastAction == LLM.ActionType.STAY)
        {
            // 相手がSTAYだった場合 → 同じようにSTAYする
            _stayPosition = transform.position;
            SafeStopAgent(true);
            SafeResetPath();
            Debug.Log($"[RuleBased] {gameObject.name}: FOLLOW - {_followTarget.PersonaName}について行きます（相手は待機中なので、同じく待機します）({reasoning})");
        }
        else
        {
            // その他の行動の場合は位置を追従
            SafeStopAgent(false);
            SafeSetDestination(_followTarget.transform.position);
            Debug.Log($"[RuleBased] {gameObject.name}: FOLLOW - {_followTarget.PersonaName}について行きます ({reasoning})");
        }
    }

    /// <summary>
    /// 情報伝播チェック（Pseudo-TALK）
    /// 待機中のルールベースエージェントが、近くの避難中エージェントから情報を受け取る
    /// SIRモデルを応用：避難中エージェント（Infected）→ 待機中エージェント（Susceptible）
    /// </summary>
    private void CheckInformationDiffusion()
    {
        // ルールベースエージェントのみ対象
        if (!UseRuleBasedDecision || _ruleBasedDecisionMaker == null)
            return;

        // 待機中のエージェントのみが情報を受け取る
        if (CurrentAction != LLM.ActionType.STAY)
            return;

        // 既に情報を受け取っている場合はスキップ
        if (_ruleBasedDecisionMaker.HasHeardFromNearbyAgent)
            return;

        // チェック間隔制限
        float currentTime = _env != null ? _env.CurrentTimeSec : Time.time;
        if (currentTime - _lastInformationDiffusionCheck < _informationDiffusionCheckInterval)
            return;
        _lastInformationDiffusionCheck = currentTime;

        // 近くの避難中エージェントを検索
        float proximityRadius = _ruleBasedDecisionMaker.ProximityRadius;
        var allEvacuees = FindObjectsByType<Evacuee>(FindObjectsSortMode.None);

        foreach (var nearby in allEvacuees)
        {
            if (nearby == this || nearby == null || !nearby.gameObject.activeSelf)
                continue;

            // 距離チェック
            float distance = Vector3.Distance(transform.position, nearby.transform.position);
            if (distance > proximityRadius)
                continue;

            // 相手が情報源として機能できるか確認
            bool canSpread = false;
            if (nearby.UseRuleBasedDecision && nearby._ruleBasedDecisionMaker != null)
            {
                canSpread = nearby._ruleBasedDecisionMaker.CanSpreadInformation();
            }
            else if (nearby.UseLLMDecision)
            {
                // LLMエージェントは避難中/追従中なら情報源
                canSpread = nearby.CurrentAction == LLM.ActionType.EVACUATE ||
                            nearby.CurrentAction == LLM.ActionType.FOLLOW;
            }

            if (!canSpread) continue;

            // 確率的に情報伝播
            float probability = _ruleBasedDecisionMaker.InformationDiffusionProbability;
            if (UnityEngine.Random.value < probability)
            {
                string sourceName = nearby.PersonaName ?? nearby.EvacueeId;
                _ruleBasedDecisionMaker.OnHeardFromNearbyAgent(sourceName);

                // メトリクス記録（オプション）
                var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
                // 情報伝播の記録は既存のRecordActionを流用
                metrics?.RecordAction(
                    EvacueeId,
                    LLM.ActionType.STAY,
                    null,
                    $"[情報伝播] {sourceName}から避難情報を受け取った",
                    1.0f
                );

                break; // 1回の伝播で終了
            }
        }
    }

    /// <summary>
    /// CONTACT完了後に次の判断を行う
    /// </summary>
    private System.Collections.IEnumerator TransitionAfterContact()
    {
        yield return new WaitForSeconds(3f);  // 連絡に3秒
        RequestRuleBasedDecision();
    }

    
    /// <summary>
    /// タグ名から避難所を検索する。フィールドに存在する全てのタワーを検索し、距離別にソートして返す
    /// </summary>
    /// <param name="excludeTowerUUIDs">除外するタワーのUUID.未指定の場合はnull</param>
    /// <returns>localField内のTowerオブジェクトのリスト</returns>
    private List<GameObject> SearchShelters(List<string> excludeTowerUUIDs = null) {
        // タグ名から避難所を検索する
        GameObject[] towers = GameObject.FindGameObjectsWithTag("Shelter");
        GameObject[] constShelters = GameObject.FindGameObjectsWithTag("ConstShelter");
        List<GameObject> Iterates = new List<GameObject>();
        foreach (var shelter in towers) {
            Iterates.Add(shelter);
        }
        foreach (var shelter in constShelters) {
            Iterates.Add(shelter);
        }
        // Schoolタグも避難所として検索対象に追加
        try {
            GameObject[] schools = GameObject.FindGameObjectsWithTag("School");
            foreach (var school in schools) {
                Iterates.Add(school);
            }
        } catch (UnityException) {
            // Schoolタグが存在しない場合は無視
        }
        // 訪れたことのない避難所を探す
        List<GameObject> sortedTowerPoints = new List<GameObject>();
        foreach (var tower in Iterates) {
            if(excludeTowerUUIDs != null && excludeTowerUUIDs.Contains(tower.GetComponent<Shelter>().uuid)) {
                continue;
            }
            // 子オブジェクトがある場合はそれを使用、ない場合は親オブジェクト自体を使用
            GameObject point = tower.transform.childCount > 0
                ? tower.transform.GetChild(0).gameObject
                : tower;
            sortedTowerPoints.Add(point);
        }
        // NOTE: エピソード更新時にgameObjectがnullになることがあるので、nullチェックを行う
        if(this != null) {
            // 距離別にソート
            sortedTowerPoints.Sort((a, b) => Vector3.Distance(a.transform.position, transform.position).CompareTo(Vector3.Distance(b.transform.position, transform.position))); 
        }
        return sortedTowerPoints;
    }

    /// <summary>
    /// 避難を行う処理
    /// 避難所のオブジェクトにアタッチされ、当たり判定により呼び出される 
    /// </summary>
    public void Evacuation(Shelter shelter) {
        if(isEvacuating) {
            return;
        }
        isEvacuating = true;
        // キャパシティーがある場合、避難処理を行う
        if(shelter.currentCapacity > 0) {
            shelter.NowAccCount++;
            // 避難成功時にSimulationMetricsに記録
            RecordEvacuationToMetrics(shelter.displayName, shelter.gameObject.name);
            gameObject.SetActive(false);
        } else { //キャパシティがいっぱいの場合、次の避難所を探す
            excludeShelters.Add(shelter.uuid);
            if (UseLLMDecision && DecisionClient != null)
            {
                RequestLLMDecision();
            }
            else
            {
                List<GameObject> shelters = SearchShelters(excludeShelters);
                if(shelters.Count > 0) {
                    Target = shelters[0]; //最短距離のタワーを目標に設定
                    SafeSetDestination(Target.transform.position);
                    ResetMovementSpeed();
                }
            }
        }
        isEvacuating = false;
    }

    /// <summary>
    /// 津波避難地域への避難完了処理
    /// </summary>
    /// <param name="area">到達した津波避難地域</param>
    public void EvacuationToArea(TsunamiEvacuationArea area) {
        if(isEvacuating) {
            return;
        }
        isEvacuating = true;

        // 津波避難地域は収容制限がないため、常に避難完了
        Debug.Log($"[Evacuee] {gameObject.name}: 津波避難地域 {area.displayName} に到達しました！");
        // 避難成功時にSimulationMetricsに記録
        RecordEvacuationToMetrics(area.displayName, area.gameObject.name);
        gameObject.SetActive(false);

        isEvacuating = false;
    }

    /// <summary>
    /// SimulationMetricsに避難完了を記録
    /// 避難が実際に成功した時点（SetActive(false)の直前）で呼び出す
    /// </summary>
    /// <param name="locationDisplayName">避難先の表示名</param>
    /// <param name="locationGameObjectName">避難先のGameObject名（フォールバック用）</param>
    private void RecordEvacuationToMetrics(string locationDisplayName, string locationGameObjectName)
    {
        // シングルトンインスタンスを優先、フォールバックでFindFirstObjectByType
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        if (metrics == null)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: SimulationMetricsが見つかりません。避難記録をスキップします。");
            return;
        }

        // _envがnullの場合はFindFirstObjectByTypeでフォールバック
        var env = _env ?? FindFirstObjectByType<EnvManager>();
        if (env == null)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: EnvManagerが見つかりません。避難記録をスキップします。");
            return;
        }

        string agentId = EvacueeId ?? gameObject.name;
        float evacuationTime = env.CurrentTimeSec;
        string locationName = !string.IsNullOrEmpty(locationDisplayName) ? locationDisplayName : locationGameObjectName;
        string agentType = UseLLMDecision ? "LLM" : "RuleBased";

        metrics.RecordEvacuation(agentId, evacuationTime, locationName, agentType);
    }

    private void RequestLLMDecision()
    {
        // 既にリクエスト中なら拒否
        if (_isRequestingLLMDecision)
        {
            return;
        }

        // 非同期で実行（最小リクエスト間隔の待機を含む）
        _ = RequestLLMDecisionAsync();
    }

    /// <summary>
    /// LLM意思決定リクエストを非同期で実行（最小リクエスト間隔を待機）
    /// </summary>
    private async Task RequestLLMDecisionAsync()
    {
        // ★ 最小リクエスト間隔のチェックと待機
        float currentTime = _env != null ? _env.CurrentTimeSec : Time.time;
        float elapsedSinceLastRequest = currentTime - _lastRequestTime;

        if (_lastRequestTime > 0f && elapsedSinceLastRequest < MinRequestInterval)
        {
            float waitTime = MinRequestInterval - elapsedSinceLastRequest;
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 最小リクエスト間隔({MinRequestInterval}秒)を満たしていません。"
                      + $"前回から{elapsedSinceLastRequest:F2}秒経過。{waitTime:F2}秒待機します。");
            await WaitSimulationTimeAsync(waitTime);
        }

        // リクエストを許可
        _lastRequestTime = _env != null ? _env.CurrentTimeSec : Time.time;
        await DecideAndMoveAsync();
    }

    /// <summary>
    /// シミュレーション時間で指定秒数待機する
    /// Task.Delay()と異なり、TimeScaleの影響を受けてシミュレーション時間基準で待機する
    /// </summary>
    private async Task WaitSimulationTimeAsync(float seconds)
    {
        float startTime = _env != null ? _env.CurrentTimeSec : Time.time;
        float targetTime = startTime + seconds;

        while (true)
        {
            await Task.Yield(); // 次のフレームまで待機
            float currentTime = _env != null ? _env.CurrentTimeSec : Time.time;
            if (currentTime >= targetTime)
            {
                break;
            }
        }
    }

    private async Task DecideAndMoveAsync()
    {
        _isRequestingLLMDecision = true;
        _llmCts?.Cancel();
        _llmCts = new CancellationTokenSource();

        try
        {
            if (DecisionClient == null)
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: DecisionClient が見つかりません。フォールバックします。");
                if (FallbackToNearest) MoveToNearestShelter();
                return;
            }
            var request = BuildEvacDecisionRequest();
            var response = await DecisionClient.RequestEvacueeDecisionAsync(request, _llmCts.Token);
            if (!ApplyLLMDecision(response))
            {
                if (FallbackToNearest)
                {
                    Debug.Log($"[Evacuee] {gameObject.name}: LLM応答なし／無効な応答のため最寄り避難所へフォールバックします。");
                    MoveToNearestShelter();
                }
            }
            // LLMの応答を受けた時刻を記録（成功・失敗に関わらず、シミュレーション内の経過時間を使用）
            _lastLLMDecisionTime = _env != null ? _env.CurrentTimeSec : Time.time;
        }
        catch (OperationCanceledException)
        {
            // タイムアウトなどでキャンセルされた場合も最寄り避難所へフォールバック
            Debug.LogWarning($"[Evacuee] {gameObject.name}: LLM decision canceled (timeout?). Fallback to nearest shelter.");
            if (FallbackToNearest)
            {
                MoveToNearestShelter();
            }
            _lastLLMDecisionTime = _env != null ? _env.CurrentTimeSec : Time.time;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: LLM decision failed - {ex.Message}");
            if (FallbackToNearest)
            {
                MoveToNearestShelter();
            }
            // エラーが発生した場合も時刻を記録（次回の再呼び出しタイミングをリセット、シミュレーション内の経過時間を使用）
            _lastLLMDecisionTime = _env != null ? _env.CurrentTimeSec : Time.time;
        }
        finally
        {
            _isRequestingLLMDecision = false;
        }
    }

    private bool ApplyLLMDecision(LLMEvacDecisionResponse response)
    {
        // 既に避難完了等で非アクティブな場合はスキップ
        if (!gameObject.activeInHierarchy)
        {
            return false;
        }

        if (response == null)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: LLMレスポンスがありません");
            return false;
        }

        // LLMエラー情報がある場合は赤色警告で出力
        if (response.llm_error != null && !string.IsNullOrEmpty(response.llm_error.error_type))
        {
            var errorInfo = response.llm_error;
            // OpenAI APIからのエラーメッセージをそのまま表示
            Debug.LogError($"[LLM {errorInfo.error_type}] {gameObject.name}\nOpenAI API Error: {errorInfo.error_message}");
        }

        // action_typeを解析（デフォルトはEVACUATE）
        LLM.ActionType actionType = LLM.ActionType.EVACUATE;
        if (!string.IsNullOrEmpty(response.action_type))
        {
            if (System.Enum.TryParse(response.action_type, true, out LLM.ActionType parsedAction))
            {
                actionType = parsedAction;
            }
            else
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 不明なaction_type '{response.action_type}'、EVACUATEとして処理");
            }
        }

        // 行動が変更されたかどうかを確認
        bool actionChanged = CurrentAction != actionType;
        
        // FOLLOW行動をやめる場合、追従対象から自分を削除
        if (CurrentAction == LLM.ActionType.FOLLOW && actionType != LLM.ActionType.FOLLOW)
        {
            UnregisterFromFollowTarget();
        }
        
        CurrentAction = actionType;

        // CONTACT以外の行動を選択した場合は連続CONTACTカウントとクールダウンをリセット
        if (actionType != LLM.ActionType.CONTACT)
        {
            _consecutiveContactCount = 0;
            _contactCooldown = false;
        }

        // TALK以外の行動を選択した場合は連続TALKカウントをリセット
        if (actionType != LLM.ActionType.TALK)
        {
            _consecutiveTalkCount = 0;
        }

        // 行動タイプに応じた処理
        bool result = false;
        switch (actionType)
        {
            case LLM.ActionType.STAY:
                result = ExecuteStayAction(response);
                break;

            case LLM.ActionType.EVACUATE:
                result = ExecuteEvacuateAction(response);
                break;

            case LLM.ActionType.SEARCH_FAMILY:
                result = ExecuteSearchFamilyAction(response);
                break;

            case LLM.ActionType.CONTACT:
                result = ExecuteContactAction(response);
                break;

            case LLM.ActionType.FOLLOW:
                result = ExecuteFollowAction(response);
                break;

            case LLM.ActionType.TALK:
                result = ExecuteTalkAction(response);
                break;

            default:
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 未知の行動タイプ {actionType}");
                return false;
        }

        // 長期目標の更新
        if (response.should_update_goal && response.long_term_goal != null)
        {
            _currentLongTermGoal = response.long_term_goal;
            _lastGoalUpdateTime = _env != null ? _env.CurrentTimeSec : Time.time;
            Debug.Log($"[Evacuee] {gameObject.name}: 長期目標を更新 - Primary Goal: {_currentLongTermGoal.primary_goal}");
        }

        // 中期計画の更新
        if (response.should_update_plan && response.mid_term_plan != null)
        {
            _currentMidTermPlan = response.mid_term_plan;
            _lastPlanUpdateTime = _env != null ? _env.CurrentTimeSec : Time.time;
            if (_currentMidTermPlan.steps != null && _currentMidTermPlan.steps.Length > 0)
            {
                Debug.Log($"[Evacuee] {gameObject.name}: 中期計画を更新 - Steps: {string.Join(", ", _currentMidTermPlan.steps)}");
            }
        }

        // 行動履歴に記録（短期記憶）
        if (result)
        {
            string target = actionType switch
            {
                LLM.ActionType.EVACUATE => response.selected_shelter_id,
                LLM.ActionType.SEARCH_FAMILY => response.target_family_member,
                LLM.ActionType.CONTACT => response.contact_target,
                LLM.ActionType.FOLLOW => response.target_evacuee_id,
                LLM.ActionType.TALK => response.talk_target_id,
                _ => null
            };
            AddActionToHistory(actionType, target, response.reasoning);
        }

        // サーバーから返された行動履歴の要約を適用
        if (!string.IsNullOrEmpty(response.summarized_action_history))
        {
            ApplySummarizedActionHistory(response.summarized_action_history);
        }

        // 行動が変更された場合、FOLLOWERに通知
        if (actionChanged && result)
        {
            NotifyFollowersOfActionChange();
        }

        // 交通: LLMから推奨される移動手段を反映
        ApplyRecommendedTransportMode(response);

        return result;
    }

    /// <summary>
    /// LLM応答の recommended_transport_mode を反映する。
    /// - "ABANDON_VEHICLE": 車両放棄（SwitchToWalking）
    /// - "WALKING": 車両モード中なら放棄
    /// - "DRIVING": 徒歩中かつ車両保有＆未放棄なら車両スポーンを保留
    /// </summary>
    private void ApplyRecommendedTransportMode(LLMEvacDecisionResponse response)
    {
        if (response == null || string.IsNullOrEmpty(response.recommended_transport_mode)) return;
        if (ExperimentConfig.Instance == null || !ExperimentConfig.Instance.EnableTrafficSimulation) return;

        switch (response.recommended_transport_mode.ToUpperInvariant())
        {
            case "ABANDON_VEHICLE":
            case "WALKING":
                if (IsDriving)
                {
                    Debug.Log($"[Evacuee] {gameObject.name}: LLM推奨により車両を放棄します ({response.recommended_transport_mode})");
                    SwitchToWalking();
                }
                break;
            case "DRIVING":
                if (!IsDriving && HasVehicle && CanDrive && !_hasAbandonedVehicle)
                {
                    Debug.Log($"[Evacuee] {gameObject.name}: LLM推奨により車両モードに切替（保留）");
                    _pendingDriveStart = true;
                }
                break;
        }
    }

    /// <summary>
    /// STAY行動を実行（その場で待機）
    /// </summary>
    private bool ExecuteStayAction(LLMEvacDecisionResponse response)
    {
        _stayPosition = transform.position;

        // NavMeshAgentを停止
        SafeStopAgent(true);
        SafeResetPath();

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.STAY, null, response.reasoning, response.confidence);

        Debug.Log($"[Evacuee] {gameObject.name}: STAY行動を選択 - その場で待機します " +
                 $"(reason='{response.reasoning}', confidence={response.confidence:F2})");

        return true;
    }

    /// <summary>
    /// SEARCH_FAMILY 行動を実行（特定の家族の位置を目標に移動）
    /// シーン内に存在する家族の場合は現在座標を追跡、存在しない家族は予測位置を使用
    /// </summary>
    private bool ExecuteSearchFamilyAction(LLMEvacDecisionResponse response)
    {
        if (_familyData == null || _familyData.members == null || _familyData.members.Count == 0)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族情報がないためSEARCH_FAMILYを実行できません。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        // シーン内に存在する家族のみを対象とする
        var searchableMembers = _familyData.members.Where(m => m.exists_in_scene && m.agent_id > 0).ToList();
        if (searchableMembers.Count == 0)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: シーン内に探索可能な家族がいないためSEARCH_FAMILYを実行できません。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        string targetKey = response.target_family_member;
        FamilyMember target = null;

        if (!string.IsNullOrEmpty(targetKey))
        {
            // まず名前で一致を試みる（シーン内家族のみ）
            target = searchableMembers.FirstOrDefault(m => m.name == targetKey);
            // 見つからなければ続柄（「息子」「娘」「妻」など）で一致を試みる
            if (target == null)
            {
                target = searchableMembers.FirstOrDefault(m => m.relation == targetKey);
            }
        }

        // LLMがtarget_family_memberを指定しなかった、または見つからなかった場合は
        // 簡単なヒューリスティックでターゲットを選ぶ（子供優先 → 配偶者 → その他）
        if (target == null)
        {
            target = searchableMembers
                .FirstOrDefault(m => m.relation.Contains("子") || m.relation.Contains("息子") || m.relation.Contains("娘"))
                ?? searchableMembers.FirstOrDefault(m => m.relation.Contains("妻") || m.relation.Contains("夫"))
                ?? searchableMembers.First();
        }

        // 探索対象を保存
        _searchFamilyTarget = target;
        _searchFamilyTargetEvacuee = null;
        _lastSearchFamilyCheck = Time.time;

        // 目標位置を決定（シーン内家族のみなので必ずEvacueeが存在するはず）
        Vector3 destination;
        var targetEvacuee = _env?.GetEvacueeById(target.agent_id.ToString());
        if (targetEvacuee != null)
        {
            _searchFamilyTargetEvacuee = targetEvacuee;
            destination = targetEvacuee.transform.position;
            Debug.Log($"[Evacuee] {gameObject.name}: シーン内家族 {target.name} (agent_id={target.agent_id}) の現在座標を追跡します");
        }
        else
        {
            // Evacueeが見つからない場合はフォールバック
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族 {target.name} (agent_id={target.agent_id}) のEvacueeが見つかりません。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        // NavMeshAgentで探索地点へ移動
        SafeStopAgent(false);
        SafeSetDestination(destination);

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.SEARCH_FAMILY, null, response.reasoning, response.confidence);

        string trackingMode = _searchFamilyTargetEvacuee != null ? "リアルタイム追跡" : "予測位置";
        Debug.Log($"[Evacuee] {gameObject.name}: SEARCH_FAMILY行動を選択 - {target.relation} {target.name} を探しに {destination} に向かいます ({trackingMode}) "
                  + $"(reason='{response.reasoning}', confidence={response.confidence:F2})");

        return true;
    }

    /// <summary>
    /// CONTACT 行動を実行（家族にメールで連絡）
    /// TALKと同様に、家族がシーン内に存在する場合はその避難者のLLMを呼び出す
    /// 存在しない場合はサーバーに直接家族としての応答を要求する
    /// </summary>
    private bool ExecuteContactAction(LLMEvacDecisionResponse response)
    {
        if (_familyData == null || _familyData.members == null || _familyData.members.Count == 0)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族情報がないためCONTACTを実行できません。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        // ★ 無限ループ防止: 連続CONTACT制限
        _consecutiveContactCount++;
        if (_consecutiveContactCount >= MAX_CONSECUTIVE_CONTACT)
        {
            Debug.Log($"[Evacuee] {gameObject.name}: 連続CONTACTが上限({MAX_CONSECUTIVE_CONTACT}回)に達しました。LLMに次の行動を決定させます。");
            _consecutiveContactCount = 0;
            _contactCooldown = true; // 次の意思決定でCONTACTを除外
            // LLMに次の行動を決定させる（CONTACTを除外した選択肢で）
            // 注意: この時点では_isRequestingLLMDecisionがtrueのため、
            // フラグをリセットしてから呼び出す必要がある
            if (UseLLMDecision && DecisionClient != null)
            {
                _isRequestingLLMDecision = false; // フラグをリセットして新しいリクエストを許可
                RequestLLMDecision();
            }
            return true; // 現在の行動は終了、LLMが次を決定
        }

        string targetKey = response.contact_target;
        FamilyMember target = null;

        if (!string.IsNullOrEmpty(targetKey))
        {
            // まず名前で一致を試みる
            target = _familyData.members.FirstOrDefault(m => m.name == targetKey);
            // 見つからなければ続柄で一致を試みる
            if (target == null)
            {
                target = _familyData.members.FirstOrDefault(m => m.relation == targetKey);
            }
        }

        // それでも見つからなければ、「連絡手段ありの家族」を優先して一人選ぶ
        if (target == null)
        {
            target = _familyData.members
                .Where(m => m.has_phone)
                .FirstOrDefault() ?? _familyData.members.First();
        }

        _lastContactTarget = target.name;

        // 連絡中は一旦立ち止まるイメージにする
        SafeStopAgent(true);
        SafeResetPath();

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.CONTACT, null, response.reasoning, response.confidence);

        Debug.Log($"[Evacuee] {gameObject.name}: CONTACT行動を選択 - {target.relation} {target.name} に連絡します " +
                  $"(reason='{response.reasoning}', confidence={response.confidence:F2})");

        // 非同期で連絡を開始（家族のLLMを呼び出す）
        _ = InitiateFamilyContactAsync(target, response);

        return true;
    }

    /// <summary>
    /// 家族への連絡を開始し、相手からの返信を待機（非同期）- マルチターン会話対応
    /// </summary>
    private async Task InitiateFamilyContactAsync(FamilyMember target, LLMEvacDecisionResponse response)
    {
        await InitiateFamilyContactAsync(target, $"大丈夫？今どこにいる？避難できそう？");
    }

    /// <summary>
    /// 家族への連絡を開始し、相手からの返信を待機（非同期）- マルチターン会話対応
    /// </summary>
    private async Task InitiateFamilyContactAsync(FamilyMember target, string message)
    {
        try
        {
            // セッション初期化（新規連絡の場合）
            if (_conversationSession == null || _conversationSession.partner_id != target.agent_id.ToString())
            {
                _conversationSession = new LLM.ConversationSessionContext
                {
                    partner_id = target.agent_id.ToString(),
                    partner_name = target.name,
                    turn_count = 1,
                    current_topic = "family_contact",
                    partner_wants_to_continue = true,
                    session_start_time = _env?.CurrentTimeSec ?? Time.time,
                    is_family_contact = true
                };
            }
            else
            {
                _conversationSession.turn_count++;
            }

            // メッセージ送信前のラグ（シミュレーション時間で3秒 - 連絡開始時間を表現）
            await WaitSimulationTimeAsync(CONTACT_MESSAGE_LAG_SEC);

            // 連絡リクエストを作成
            var contactRequest = new LLM.FamilyContactRequest
            {
                sender_id = EvacueeId,
                sender_name = PersonaName,
                sender_relation = GetMyRelationTo(target), // 相手から見た自分の続柄
                message = message,
                sender_action = CurrentAction.ToString(),
                sender_target = CurrentTargetShelterId,
                sender_location = GetCurrentLocationDescription()
            };

            LLM.FamilyContactResponse contactResponse;

            // 家族がシーン内に存在するかチェック
            if (target.exists_in_scene && target.agent_id > 0)
            {
                // シーン内の避難者を検索してそのLLMを呼び出す
                Evacuee familyEvacuee = FindEvacueeById(target.agent_id.ToString());
                if (familyEvacuee != null)
                {
                    Debug.Log($"[Evacuee] {gameObject.name}: 家族 {target.name} (agent_id: {target.agent_id}) がシーン内に存在 - 双方向通信開始");

                    // 家族エージェントに連絡を送信し、応答を待機
                    _conversationResponseTCS = new TaskCompletionSource<LLM.ConversationResponse>();
                    familyEvacuee.OnFamilyContactReceived(contactRequest, this);

                    // シミュレーション時間ベースのタイムアウト待機
                    float startTime = _env != null ? _env.CurrentTimeSec : Time.time;

                    while (!_conversationResponseTCS.Task.IsCompleted)
                    {
                        await Task.Yield();
                        float currentTime = _env != null ? _env.CurrentTimeSec : Time.time;
                        if (currentTime - startTime >= CONTACT_TIMEOUT_SEC)
                        {
                            break;
                        }
                    }

                    if (!_conversationResponseTCS.Task.IsCompleted)
                    {
                        Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族連絡応答タイムアウト（シミュレーション時間{CONTACT_TIMEOUT_SEC}秒） - デフォルト応答を使用");
                        contactResponse = new LLM.FamilyContactResponse
                        {
                            responder_id = target.agent_id.ToString(),
                            responder_name = target.name,
                            response_message = "ごめん、今バタバタしてて。無事だから心配しないで。",
                            current_status = "無事",
                            current_location = "不明",
                            planned_action = "",
                            want_to_continue = false
                        };
                    }
                    else
                    {
                        // ConversationResponseをFamilyContactResponseに変換
                        var convResponse = _conversationResponseTCS.Task.Result;
                        contactResponse = new LLM.FamilyContactResponse
                        {
                            responder_id = convResponse.responder_id,
                            responder_name = convResponse.responder_name,
                            response_message = convResponse.response_message,
                            current_status = "不明",
                            current_location = "",
                            planned_action = "",
                            want_to_continue = convResponse.want_to_continue
                        };
                    }
                }
                else
                {
                    Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族 {target.name} (agent_id: {target.agent_id}) がシーン内に見つかりません - サーバー経由で応答生成");
                    contactResponse = await RequestFamilyContactFromServerAsync(contactRequest, target);
                }
            }
            else
            {
                // シーン内に存在しない場合はサーバーに直接リクエスト
                Debug.Log($"[Evacuee] {gameObject.name}: 家族 {target.name} はシーン外 - サーバー経由で応答生成");
                contactResponse = await RequestFamilyContactFromServerAsync(contactRequest, target);
            }

            // 返信内容を保存
            _lastContactMessage = contactResponse.response_message;

            // await中にセッションが終了されている場合は早期リターン
            if (_conversationSession == null)
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族連絡の応答待ち中にセッションが終了されました - 処理をスキップ");
                return;
            }

            Debug.Log($"[Evacuee] {gameObject.name}: 家族連絡ターン{_conversationSession.turn_count}完了 - {target.name}からの返信: 「{contactResponse.response_message}」(継続希望: {contactResponse.want_to_continue})\n" +
                      $"  状況: {contactResponse.current_status}, 場所: {contactResponse.current_location}, 予定: {contactResponse.planned_action}");

            // マルチターン継続判定
            if (contactResponse.want_to_continue && _conversationSession.turn_count < MAX_CONVERSATION_TURNS)
            {
                // 相手が継続希望 → 自分も継続するか判断
                _conversationSession.partner_wants_to_continue = true;
                await ContinueFamilyContactAsync(target, contactResponse.response_message);
            }
            else
            {
                // 連絡終了（相手が終了希望 or ターン上限）
                EndConversationSession();

                // 次の意思決定のためにLLMを呼び出し
                if (UseLLMDecision && DecisionClient != null)
                {
                    RequestLLMDecision();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Evacuee] {gameObject.name}: 家族連絡中にエラー: {ex.Message}");
            EndConversationSession();

            // エラー時もデフォルトの返信を設定して続行
            _lastContactMessage = $"{target.name}からの返信: 「連絡が取れませんでした...」";

            if (UseLLMDecision && DecisionClient != null)
            {
                RequestLLMDecision();
            }
        }
        finally
        {
            _conversationResponseTCS = null;
        }
    }

    /// <summary>
    /// 家族連絡を継続する（自分のターンで次のメッセージを生成）
    /// </summary>
    private async Task ContinueFamilyContactAsync(FamilyMember target, string partnerLastMessage)
    {
        try
        {
            // 自分が継続したいか判断（LLMで生成）
            var continuationResponse = await RequestConversationContinuationAsync(partnerLastMessage);

            // デフォルト応答チェック（reasoningにLLM失敗の兆候がある場合）
            if (!string.IsNullOrEmpty(continuationResponse?.reasoning) &&
                (continuationResponse.reasoning.Contains("LLM unavailable") ||
                 continuationResponse.reasoning.Contains("LLM call failed") ||
                 continuationResponse.reasoning.Contains("Exception")))
            {
                LogDefaultResponseWarning("CONTACT継続判断", continuationResponse.reasoning);
            }

            if (!continuationResponse.want_to_continue)
            {
                // 自分は終了したい → 会話終了
                Debug.Log($"[Evacuee] {gameObject.name}: 家族連絡を終了します - 「{continuationResponse.message}」");
                EndConversationSession();

                // 次の意思決定のためにLLMを呼び出し
                if (UseLLMDecision && DecisionClient != null)
                {
                    RequestLLMDecision();
                }
                return;
            }

            // await中にセッションが終了されている場合は早期リターン
            if (_conversationSession == null)
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 家族連絡継続の応答待ち中にセッションが終了されました - 処理をスキップ");
                return;
            }

            // 継続 → 次のメッセージを送信
            _conversationSession.turn_count++;
            Debug.Log($"[Evacuee] {gameObject.name}: 家族連絡継続 - ターン{_conversationSession.turn_count}: 「{continuationResponse.message}」");

            // 再帰的にInitiateFamilyContactAsyncを呼び出し（セッションは維持）
            await InitiateFamilyContactAsync(target, continuationResponse.message);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Evacuee] {gameObject.name}: 家族連絡継続中にエラー: {ex.Message}");
            EndConversationSession();

            if (UseLLMDecision && DecisionClient != null)
            {
                RequestLLMDecision();
            }
        }
    }

    /// <summary>
    /// サーバーに家族としての応答を生成させる（シーン外の家族用）
    /// </summary>
    private async Task<LLM.FamilyContactResponse> RequestFamilyContactFromServerAsync(LLM.FamilyContactRequest contactRequest, FamilyMember target)
    {
        if (DecisionClient == null)
        {
            LogDefaultResponseWarning("CONTACT(家族)", "LLMが無効");
            return new LLM.FamilyContactResponse
            {
                responder_id = target.agent_id.ToString(),
                responder_name = target.name,
                response_message = "無事だよ。今は自宅付近にいる。様子を見ている。",
                current_status = "無事",
                current_location = "自宅付近",
                planned_action = "様子を見て避難する",
                want_to_continue = false
            };
        }

        // 実験IDを取得
        string experimentId = !string.IsNullOrEmpty(_env?.recordID)
            ? _env.recordID.Replace("_", "").Replace("-", "_")
            : DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // LLMリクエストを構築
        var llmRequest = new LLM.LLMFamilyContactResponseRequest
        {
            request_id = $"fam-{Guid.NewGuid():N}",
            request_type = "family_contact_response",
            persona = BuildFamilyPersonaPayload(target),
            environment = BuildEnvironmentPayload(),
            incoming_contact = contactRequest,
            current_action = "STAY", // シーン外の家族はデフォルトでSTAY
            current_target = "",
            family_relationship = $"{PersonaName}の{target.relation}",
            // ログ用メタデータ
            evacuee_id = target.name, // 家族の名前をIDとして使用
            experiment_id = experimentId,
            episode_id = _env?.currentEpisodeId ?? 0,
            episode_elapsed_time = _env?.CurrentTimeSec ?? 0f
        };

        var llmResponse = await DecisionClient.RequestFamilyContactResponseAsync(llmRequest);

        // デフォルト応答チェック（reasoningにLLM失敗の兆候がある場合）
        if (!string.IsNullOrEmpty(llmResponse?.reasoning) &&
            (llmResponse.reasoning.Contains("LLM unavailable") ||
             llmResponse.reasoning.Contains("LLM call failed") ||
             llmResponse.reasoning.Contains("Exception")))
        {
            LogDefaultResponseWarning("CONTACT(家族)", llmResponse.reasoning);
        }

        return new LLM.FamilyContactResponse
        {
            responder_id = target.agent_id.ToString(),
            responder_name = target.name,
            response_message = llmResponse?.response_message ?? "無事だよ。今は自宅付近にいる。様子を見ている。",
            current_status = llmResponse?.current_status ?? "無事",
            current_location = llmResponse?.current_location ?? "自宅付近",
            planned_action = llmResponse?.planned_action ?? "様子を見て避難する",
            want_to_continue = llmResponse?.want_to_continue ?? false
        };
    }

    /// <summary>
    /// 家族メンバーからLLM用のPersonaPayloadを構築
    /// </summary>
    private LLM.PersonaPayload BuildFamilyPersonaPayload(FamilyMember target)
    {
        return new LLM.PersonaPayload
        {
            agent_id = target.agent_id,
            name = target.name,
            role = target.relation, // 続柄を役割として使用
            age_group = InferAgeGroupFromRelation(target.relation),
            speed_multiplier = 1.0f,
            mental_state = "普通",
            priority = "安全確保",
            system_prompt_context = $"{PersonaName}の{target.relation}です。"
        };
    }

    /// <summary>
    /// 続柄から年齢層を推定
    /// </summary>
    private string InferAgeGroupFromRelation(string relation)
    {
        if (string.IsNullOrEmpty(relation)) return "成人";

        if (relation.Contains("息子") || relation.Contains("娘") || relation.Contains("子"))
        {
            return "子ども";
        }
        if (relation.Contains("父") || relation.Contains("母") || relation.Contains("祖父") || relation.Contains("祖母"))
        {
            return "高齢者";
        }
        return "成人";
    }

    /// <summary>
    /// 現在位置の説明を取得
    /// </summary>
    private string GetCurrentLocationDescription()
    {
        // TODO: 周辺の建物情報などを使って詳細な位置説明を生成
        // UnityのオブジェクトはC#のnull条件演算子と異なる挙動をするため、明示的にnullチェック
        if (Target != null && Target)
        {
            return $"{Target.name}付近";
        }
        return "移動中";
    }

    /// <summary>
    /// 相手から見た自分の続柄を取得
    /// </summary>
    private string GetMyRelationTo(FamilyMember target)
    {
        // 相手の続柄から自分の続柄を推定
        string theirRelation = target.relation;
        if (theirRelation == "妻") return "夫";
        if (theirRelation == "夫") return "妻";
        if (theirRelation == "息子" || theirRelation == "娘") return "親";
        if (theirRelation == "父" || theirRelation == "母") return "子";
        if (theirRelation == "兄" || theirRelation == "弟") return "兄弟";
        if (theirRelation == "姉" || theirRelation == "妹") return "姉妹";
        return "家族";
    }

    /// <summary>
    /// 環境情報ペイロードを構築（会話/連絡応答用）
    /// </summary>
    private LLM.EnvironmentPayload BuildEnvironmentPayload()
    {
        return new LLM.EnvironmentPayload
        {
            scenario_id = "shindo_7_tsunami", // TODO: 実際のシナリオIDを取得
            has_heard_broadcast = _hasHeardBroadcast,
            last_broadcast_message = _lastBroadcastMessage,
            has_received_j_alert = _hasReceivedJAlert,
            last_j_alert_message = _lastJAlertMessage
        };
    }

    /// <summary>
    /// 他のエージェントから家族連絡を受信（シーン内の家族として）
    /// </summary>
    public void OnFamilyContactReceived(LLM.FamilyContactRequest request, Evacuee sender)
    {
        Debug.Log($"[Evacuee] {gameObject.name}: 家族 {request.sender_name} からメール受信: 「{request.message}」");

        // 応答拒否判定: TALK中は即座に拒否応答を返す
        if (CurrentAction == LLM.ActionType.TALK || _isRespondingToConversation)
        {
            Debug.Log($"[Evacuee] {gameObject.name}: 現在会話中のため家族連絡に簡易応答を返します");
            sender.OnFamilyContactResponseReceived(new LLM.ConversationResponse
            {
                responder_id = EvacueeId,
                responder_name = PersonaName,
                response_message = "ごめん、今話し中。後で連絡する。",
                willing_to_share = true
            });
            return;
        }

        // 応答拒否判定: CONTACT中は即座に拒否応答を返す
        if (CurrentAction == LLM.ActionType.CONTACT || _isRespondingToFamilyContact)
        {
            LogDefaultResponseWarning("CONTACT", "相手が連絡中", isBusyRejection: true);
            sender.OnFamilyContactResponseReceived(new LLM.ConversationResponse
            {
                responder_id = EvacueeId,
                responder_name = PersonaName,
                response_message = "ごめん、今電話中。すぐ折り返す。",
                willing_to_share = true
            });
            return;
        }

        // 連絡前の状態を保存
        _actionBeforeFamilyContact = CurrentAction;
        _targetShelterBeforeFamilyContact = CurrentTargetShelterId;

        // 応答者も立ち止まる
        _isRespondingToFamilyContact = true;
        SafeStopAgent(true);
        SafeResetPath();

        // LLMに応答生成をリクエスト
        _ = RequestFamilyContactResponseAsync(request, sender);
    }

    /// <summary>
    /// LLMに家族連絡への応答を生成させ、送信者に返す
    /// </summary>
    private async Task RequestFamilyContactResponseAsync(LLM.FamilyContactRequest request, Evacuee sender)
    {
        try
        {
            LLM.ConversationResponse response;

            if (UseLLMDecision && DecisionClient != null)
            {
                // 実験IDを取得
                string experimentId = !string.IsNullOrEmpty(_env?.recordID)
                    ? _env.recordID.Replace("_", "").Replace("-", "_")
                    : DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // LLMに応答生成をリクエスト
                var llmRequest = new LLM.LLMFamilyContactResponseRequest
                {
                    request_id = $"fam-{Guid.NewGuid():N}",
                    request_type = "family_contact_response",
                    persona = new LLM.PersonaPayload
                    {
                        agent_id = int.TryParse(_uniqueId, out var id) ? id : 0,
                        name = PersonaName,
                        role = _persona?.role ?? "",
                        age_group = _persona?.age_group ?? "成人",
                        speed_multiplier = _persona?.speed_multiplier ?? 1.0f,
                        mental_state = _persona?.mental_state ?? "普通",
                        priority = _persona?.priority ?? "安全確保",
                        system_prompt_context = _persona?.system_prompt_context ?? ""
                    },
                    environment = BuildEnvironmentPayload(),
                    incoming_contact = request,
                    current_action = _actionBeforeFamilyContact.ToString(), // 連絡前の行動を送信
                    current_target = _targetShelterBeforeFamilyContact,
                    family_relationship = $"{request.sender_name}の{request.sender_relation}の家族",
                    // ログ用メタデータ
                    evacuee_id = EvacueeId,
                    experiment_id = experimentId,
                    episode_id = _env?.currentEpisodeId ?? 0,
                    episode_elapsed_time = _env?.CurrentTimeSec ?? 0f
                };

                var llmResponse = await DecisionClient.RequestFamilyContactResponseAsync(llmRequest);

                // デフォルト応答チェック（reasoningにLLM失敗の兆候がある場合）
                if (!string.IsNullOrEmpty(llmResponse?.reasoning) &&
                    (llmResponse.reasoning.Contains("LLM unavailable") ||
                     llmResponse.reasoning.Contains("LLM call failed") ||
                     llmResponse.reasoning.Contains("Exception")))
                {
                    LogDefaultResponseWarning("CONTACT", llmResponse.reasoning);
                }

                response = new LLM.ConversationResponse
                {
                    responder_id = EvacueeId,
                    responder_name = PersonaName,
                    response_message = llmResponse?.response_message ?? "無事だよ。今避難中。そっちも気をつけて。",
                    willing_to_share = true
                };
            }
            else
            {
                // LLMが利用できない場合はデフォルト応答
                LogDefaultResponseWarning("CONTACT", "LLMが無効");
                response = new LLM.ConversationResponse
                {
                    responder_id = EvacueeId,
                    responder_name = PersonaName,
                    response_message = "無事だよ。今避難中。そっちも気をつけて。",
                    willing_to_share = true
                };
            }

            // 応答送信前のラグ（シミュレーション時間で3秒）
            await WaitSimulationTimeAsync(CONTACT_MESSAGE_LAG_SEC);

            // 送信者に応答を返す
            sender.OnFamilyContactResponseReceived(response);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Evacuee] {gameObject.name}: 家族連絡応答生成中にエラー: {ex.Message}");

            // エラー時もデフォルト応答を返す
            sender.OnFamilyContactResponseReceived(new LLM.ConversationResponse
            {
                responder_id = EvacueeId,
                responder_name = PersonaName,
                response_message = "ごめん、今バタバタしてる。無事だから。",
                willing_to_share = true
            });
        }
        finally
        {
            // 連絡終了後の処理
            _isRespondingToFamilyContact = false;

            // LLMに次の行動を決定させる（連絡の結果を踏まえて行動が変わる可能性あり）
            if (UseLLMDecision && DecisionClient != null)
            {
                Debug.Log($"[Evacuee] {gameObject.name}: 家族連絡終了後、次の行動を決定します");
                RequestLLMDecision();
            }
            else
            {
                // LLMが無効な場合は以前の行動を再開
                Debug.Log($"[Evacuee] {gameObject.name}: 家族連絡終了後、以前の行動({_actionBeforeFamilyContact})を再開します");
                CurrentAction = _actionBeforeFamilyContact;
                SafeStopAgent(false);
            }
        }
    }

    /// <summary>
    /// 家族からの応答を受信（連絡した側）
    /// </summary>
    public void OnFamilyContactResponseReceived(LLM.ConversationResponse response)
    {
        _conversationResponseTCS?.TrySetResult(response);
    }

    /// <summary>
    /// FOLLOW 行動を実行（周囲の避難者について行く）
    /// </summary>
    private bool ExecuteFollowAction(LLMEvacDecisionResponse response)
    {
        // 対象の避難者を検索
        _followTarget = null;

        if (!string.IsNullOrEmpty(response.target_evacuee_id))
        {
            // Step 1: 数字かどうかを判定
            bool isNumericId = int.TryParse(response.target_evacuee_id, out _);

            if (isNumericId)
            {
                // 数字の場合: IDで検索
                _followTarget = FindEvacueeById(response.target_evacuee_id);
            }
            else
            {
                // 数字でない場合: 名前として検索
                _followTarget = FindEvacueeByName(response.target_evacuee_id);
                if (_followTarget != null)
                {
                    Debug.Log($"[Evacuee] {gameObject.name}: 名前 '{response.target_evacuee_id}' で避難者を見つけました。");
                }
            }

            // 循環チェック: 相互FOLLOWを防止
            if (_followTarget != null && WouldCreateFollowCycle(_followTarget))
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: {response.target_evacuee_id} をフォローすると循環が発生するため、別の対象を探します。");
                _followTarget = null;
            }
        }

        // Step 2: IDでも名前でも見つからない、または循環が発生する場合、循環しない最寄りの避難者にフォールバック
        if (_followTarget == null)
        {
            _followTarget = FindNearestNonCyclicFollowTarget();
            if (_followTarget != null)
            {
                string fallbackId = !string.IsNullOrEmpty(_followTarget._uniqueId) ? _followTarget._uniqueId : _followTarget.gameObject.name;
                string fallbackName = _followTarget._persona?.name ?? fallbackId;
                Debug.Log($"[Evacuee] {gameObject.name}: 指定 '{response.target_evacuee_id}' が見つからない/循環するため、最寄りの避難者 '{fallbackName}' (ID: {fallbackId}) を追従対象にします。");
            }
        }

        // Step 3: それでも見つからない場合はSTAYにフォールバック
        if (_followTarget == null)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 有効な追従対象がいません（循環防止のため）。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        // 追従開始：追従対象に自分をFOLLOWERとして登録
        _followTarget.RegisterFollower(this);

        // 追従者はNavMesh優先度を低く設定（リーダーに避けさせない）
        if (NavAgent != null)
        {
            NavAgent.avoidancePriority = 80; // 高い値 = 低優先度（自分が避ける）
        }
        
        // 追従開始
        _lastFollowCheck = Time.time;
        _followTargetLastAction = _followTarget.CurrentAction; // 相手の現在の行動を記録
        
        // 相手の行動に合わせた初期処理
        if (_followTargetLastAction == LLM.ActionType.EVACUATE)
        {
            // 相手がEVACUATEを選択していた場合 → 同じ避難所を目指す
            if (_followTarget.Target != null)
            {
                Target = _followTarget.Target;
                SafeStopAgent(false);
                SafeSetDestination(Target.transform.position);
                Debug.Log($"[Evacuee] {gameObject.name}: FOLLOW行動を選択 - {_followTarget.PersonaName}(ID:{response.target_evacuee_id})について行きます（相手は避難所に向かっているため、同じ避難所を目指します）");
            }
            else
            {
                // 相手の目標が設定されていない場合は位置を追従
                SafeStopAgent(false);
                SafeSetDestination(_followTarget.transform.position);
                Debug.Log($"[Evacuee] {gameObject.name}: FOLLOW行動を選択 - {_followTarget.PersonaName}(ID:{response.target_evacuee_id})について行きます（相手の位置を追従）");
            }
        }
        else if (_followTargetLastAction == LLM.ActionType.STAY)
        {
            // 相手がSTAYだった場合 → 同じようにSTAYする
            CurrentAction = LLM.ActionType.STAY;
            _stayPosition = transform.position;
            SafeStopAgent(true);
            SafeResetPath();
            Debug.Log($"[Evacuee] {gameObject.name}: FOLLOW行動を選択 - {_followTarget.PersonaName}(ID:{response.target_evacuee_id})について行きます（相手は待機中なので、同じく待機します）");
        }
        else
        {
            // その他の行動（SEARCH_FAMILY, CONTACT, FOLLOW）の場合は位置を追従
            SafeStopAgent(false);
            SafeSetDestination(_followTarget.transform.position);
            Debug.Log($"[Evacuee] {gameObject.name}: FOLLOW行動を選択 - {_followTarget.PersonaName}(ID:{response.target_evacuee_id})について行きます " +
                      $"(reason='{response.reasoning}', confidence={response.confidence:F2})");
        }

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.FOLLOW, null, response.reasoning, response.confidence);

        return true;
    }

    /// <summary>
    /// TALK 行動を実行（周辺の避難者と会話）
    /// </summary>
    private bool ExecuteTalkAction(LLMEvacDecisionResponse response)
    {
        if (string.IsNullOrEmpty(response.talk_target_id))
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: TALK行動においてtalk_target_idが確認できません。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        // ★ 無限ループ防止: 連続TALK制限
        _consecutiveTalkCount++;
        if (_consecutiveTalkCount >= MAX_CONSECUTIVE_TALK)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 連続TALKが上限({MAX_CONSECUTIVE_TALK}回)に達しました。STAYに切り替えます。");
            _consecutiveTalkCount = 0;
            return ExecuteStayAction(response);
        }

        // 対象の避難者を検索
        Evacuee talkTarget = FindEvacueeById(response.talk_target_id);
        if (talkTarget == null)
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: TALK対象 {response.talk_target_id} が見つかりません。STAYにフォールバックします。");
            return ExecuteStayAction(response);
        }

        // 会話中は立ち止まる
        SafeStopAgent(true);
        SafeResetPath();

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.TALK, null, response.reasoning, response.confidence);

        Debug.Log($"[Evacuee] {gameObject.name}: TALK行動を選択 - {talkTarget.PersonaName}(ID:{talkTarget.EvacueeId})に話しかけます " +
                  $"(topic='{response.talk_topic}', message='{response.talk_message}', reason='{response.reasoning}')");

        // 非同期で会話を開始
        _ = InitiateConversationAsync(talkTarget, response.talk_topic, response.talk_message);

        return true;
    }

    /// <summary>
    /// 会話を開始し、相手からの応答を待機（非同期）- マルチターン会話対応
    /// </summary>
    private async Task InitiateConversationAsync(Evacuee target, string topic, string message)
    {
        try
        {
            // セッション初期化（新規会話の場合）
            if (_conversationSession == null || _conversationSession.partner_id != target.EvacueeId)
            {
                _conversationSession = new LLM.ConversationSessionContext
                {
                    partner_id = target.EvacueeId,
                    partner_name = target.PersonaName,
                    turn_count = 1,
                    current_topic = topic ?? "General",
                    partner_wants_to_continue = true,
                    session_start_time = _env?.CurrentTimeSec ?? Time.time,
                    is_family_contact = false
                };
            }
            else
            {
                _conversationSession.turn_count++;
            }

            _conversationResponseTCS = new TaskCompletionSource<LLM.ConversationResponse>();

            // メッセージ送信前のラグ（シミュレーション時間で5秒 - 発話時間を表現）
            await WaitSimulationTimeAsync(CONVERSATION_MESSAGE_LAG_SEC);

            // 相手に会話リクエストを送信
            var request = new LLM.ConversationRequest
            {
                initiator_id = EvacueeId,
                initiator_name = PersonaName,
                topic = topic ?? "General",
                message = message,
                initiator_action = CurrentAction.ToString(),
                initiator_target = CurrentTargetShelterId
            };

            target.OnConversationReceived(request);

            // シミュレーション時間ベースのタイムアウト待機
            float startTime = _env != null ? _env.CurrentTimeSec : Time.time;

            while (!_conversationResponseTCS.Task.IsCompleted)
            {
                await Task.Yield();
                float currentTime = _env != null ? _env.CurrentTimeSec : Time.time;
                if (currentTime - startTime >= CONVERSATION_TIMEOUT_SEC)
                {
                    break;
                }
            }

            LLM.ConversationResponse response;
            if (!_conversationResponseTCS.Task.IsCompleted)
            {
                // タイムアウト - デフォルト応答を使用
                Debug.LogWarning($"[Evacuee] {gameObject.name}: 会話応答タイムアウト（シミュレーション時間{CONVERSATION_TIMEOUT_SEC}秒） - デフォルト応答を使用");
                response = new LLM.ConversationResponse
                {
                    responder_id = target.EvacueeId,
                    responder_name = target.PersonaName,
                    response_message = "すみません、今は急いでいるので...",
                    willing_to_share = false,
                    want_to_continue = false
                };
            }
            else
            {
                response = _conversationResponseTCS.Task.Result;
            }

            // 会話履歴に追加
            AddConversationToHistory(target.EvacueeId, target.PersonaName, topic, message, response.response_message, true);

            Debug.Log($"[Evacuee] {gameObject.name}: 会話ターン{_conversationSession.turn_count}完了 - {target.PersonaName}(ID:{target.EvacueeId})の返答: 「{response.response_message}」(継続希望: {response.want_to_continue})");

            // マルチターン継続判定
            if (response.want_to_continue && _conversationSession.turn_count < MAX_CONVERSATION_TURNS)
            {
                // 相手が継続希望 → 自分も継続するか判断
                _conversationSession.partner_wants_to_continue = true;
                await ContinueConversationAsync(target, topic, response.response_message);
            }
            else
            {
                // 会話終了（相手が終了希望 or ターン上限）
                EndConversationSession();

                // 次の意思決定のためにLLMを呼び出し
                if (UseLLMDecision && DecisionClient != null)
                {
                    RequestLLMDecision();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Evacuee] {gameObject.name}: 会話中にエラー: {ex.Message}");
            EndConversationSession();
        }
        finally
        {
            _conversationResponseTCS = null;
        }
    }

    /// <summary>
    /// 会話を継続する（自分のターンで次のメッセージを生成）
    /// </summary>
    private async Task ContinueConversationAsync(Evacuee target, string topic, string partnerLastMessage)
    {
        try
        {
            // 自分が継続したいか判断（LLMで生成）
            var continuationResponse = await RequestConversationContinuationAsync(partnerLastMessage);

            // デフォルト応答チェック（reasoningにLLM失敗の兆候がある場合）
            if (!string.IsNullOrEmpty(continuationResponse?.reasoning) &&
                (continuationResponse.reasoning.Contains("LLM unavailable") ||
                 continuationResponse.reasoning.Contains("LLM call failed") ||
                 continuationResponse.reasoning.Contains("Exception")))
            {
                LogDefaultResponseWarning("TALK継続判断", continuationResponse.reasoning);
            }

            if (!continuationResponse.want_to_continue)
            {
                // 自分は終了したい → 終了メッセージを送って会話終了
                Debug.Log($"[Evacuee] {gameObject.name}: 会話を終了します - 「{continuationResponse.message}」");

                // 最後のメッセージを送信（終了の挨拶等）
                _conversationSession.turn_count++;
                await WaitSimulationTimeAsync(CONVERSATION_MESSAGE_LAG_SEC);

                var finalRequest = new LLM.ConversationRequest
                {
                    initiator_id = EvacueeId,
                    initiator_name = PersonaName,
                    topic = topic ?? "General",
                    message = continuationResponse.message,
                    initiator_action = CurrentAction.ToString(),
                    initiator_target = CurrentTargetShelterId
                };

                // 終了メッセージを相手に通知（応答は待たない）
                target.OnFinalConversationMessageReceived(finalRequest);

                // 会話履歴に追加
                AddConversationToHistory(target.EvacueeId, target.PersonaName, topic, continuationResponse.message, "(会話終了)", true);

                EndConversationSession();

                // 次の意思決定のためにLLMを呼び出し
                if (UseLLMDecision && DecisionClient != null)
                {
                    RequestLLMDecision();
                }
                return;
            }

            // 継続 → 次のメッセージを送信
            _conversationSession.turn_count++;
            Debug.Log($"[Evacuee] {gameObject.name}: 会話継続 - ターン{_conversationSession.turn_count}: 「{continuationResponse.message}」");

            // 再帰的にInitiateConversationAsyncを呼び出し（セッションは維持）
            await InitiateConversationAsync(target, topic, continuationResponse.message);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Evacuee] {gameObject.name}: 会話継続中にエラー: {ex.Message}");
            EndConversationSession();

            if (UseLLMDecision && DecisionClient != null)
            {
                RequestLLMDecision();
            }
        }
    }

    /// <summary>
    /// 会話継続判断をLLMにリクエスト
    /// </summary>
    private async Task<LLM.LLMConversationContinuationResponse> RequestConversationContinuationAsync(string partnerLastMessage)
    {
        if (DecisionClient == null)
        {
            // LLMが利用できない場合はデフォルトで終了
            return new LLM.LLMConversationContinuationResponse
            {
                want_to_continue = false,
                message = "じゃあ、気をつけて。",
                reasoning = "LLM unavailable"
            };
        }

        // 実験IDを取得
        string experimentId = !string.IsNullOrEmpty(_env?.recordID)
            ? _env.recordID.Replace("_", "").Replace("-", "_")
            : DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var request = new LLM.LLMConversationContinuationRequest
        {
            request_id = $"cont-{Guid.NewGuid():N}",
            request_type = "conversation_continuation",
            persona = BuildPersonaPayload(),
            session = _conversationSession,
            partner_last_message = partnerLastMessage,
            // ログ用メタデータ
            evacuee_id = EvacueeId,
            experiment_id = experimentId,
            episode_id = _env?.currentEpisodeId ?? 0,
            episode_elapsed_time = _env?.CurrentTimeSec ?? 0f
        };

        return await DecisionClient.RequestConversationContinuationAsync(request);
    }

    /// <summary>
    /// 会話セッションを終了
    /// </summary>
    private void EndConversationSession()
    {
        _conversationSession = null;
    }

    /// <summary>
    /// 会話終了メッセージを受信（応答は返さない）
    /// </summary>
    public void OnFinalConversationMessageReceived(LLM.ConversationRequest request)
    {
        Debug.Log($"[Evacuee] {gameObject.name}: {request.initiator_name}からの会話終了メッセージ: 「{request.message}」");

        // 自分側のセッションも終了
        EndConversationSession();

        // 応答状態をリセット
        if (_isRespondingToConversation)
        {
            _isRespondingToConversation = false;
            // 移動再開
            SafeStopAgent(false);

            // 次の意思決定
            if (UseLLMDecision && DecisionClient != null)
            {
                RequestLLMDecision();
            }
        }
    }

    /// <summary>
    /// 他のエージェントから会話リクエストを受信（話しかけられた側）
    /// </summary>
    public void OnConversationReceived(LLM.ConversationRequest request)
    {
        Debug.Log($"[Evacuee] {gameObject.name}: {request.initiator_name}から話しかけられました: 「{request.message}」");

        // 応答拒否判定: TALK中は即座に拒否応答を返す
        if (CurrentAction == LLM.ActionType.TALK || _isRespondingToConversation)
        {
            LogDefaultResponseWarning("TALK", "相手が会話中", isBusyRejection: true);
            var initiator = FindEvacueeById(request.initiator_id);
            if (initiator != null)
            {
                initiator.OnConversationResponseReceived(new LLM.ConversationResponse
                {
                    responder_id = EvacueeId,
                    responder_name = PersonaName,
                    response_message = "すみません、今ちょっと話し中で...",
                    willing_to_share = false,
                    want_to_continue = false
                });
            }
            return;
        }

        // 応答拒否判定: CONTACT中は即座に拒否応答を返す
        if (CurrentAction == LLM.ActionType.CONTACT || _isRespondingToFamilyContact)
        {
            LogDefaultResponseWarning("TALK", "相手が連絡中", isBusyRejection: true);
            var initiator = FindEvacueeById(request.initiator_id);
            if (initiator != null)
            {
                initiator.OnConversationResponseReceived(new LLM.ConversationResponse
                {
                    responder_id = EvacueeId,
                    responder_name = PersonaName,
                    response_message = "すみません、今電話中で...",
                    willing_to_share = false,
                    want_to_continue = false
                });
            }
            return;
        }

        // 会話前の状態を保存
        _actionBeforeConversation = CurrentAction;
        _targetShelterBeforeConversation = CurrentTargetShelterId;

        // 応答者も立ち止まる
        _isRespondingToConversation = true;
        SafeStopAgent(true);
        SafeResetPath();

        // LLMに応答生成をリクエスト
        _ = RequestConversationResponseAsync(request);
    }

    /// <summary>
    /// LLMに会話応答を生成させ、話しかけてきた相手に返す
    /// </summary>
    private async Task RequestConversationResponseAsync(LLM.ConversationRequest request)
    {
        try
        {
            LLM.ConversationResponse response;

            if (UseLLMDecision && DecisionClient != null)
            {
                // 実験IDを取得
                string experimentId = !string.IsNullOrEmpty(_env?.recordID)
                    ? _env.recordID.Replace("_", "").Replace("-", "_")
                    : DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // LLMに応答生成をリクエスト
                var llmRequest = new LLM.LLMConversationResponseRequest
                {
                    request_id = $"conv-{Guid.NewGuid()}",
                    request_type = "conversation_response",
                    persona = BuildPersonaPayload(),
                    environment = BuildEnvironmentPayload(),
                    incoming_conversation = request,
                    current_action = _actionBeforeConversation.ToString(), // 会話前の行動を送信
                    current_target = _targetShelterBeforeConversation,
                    // ログ用メタデータ
                    evacuee_id = EvacueeId,
                    experiment_id = experimentId,
                    episode_id = _env?.currentEpisodeId ?? 0,
                    episode_elapsed_time = _env?.CurrentTimeSec ?? 0f
                };

                var llmResponse = await DecisionClient.RequestConversationResponseAsync(llmRequest);

                if (llmResponse != null)
                {
                    // デフォルト応答チェック（reasoningにLLM失敗の兆候がある場合）
                    if (!string.IsNullOrEmpty(llmResponse.reasoning) &&
                        (llmResponse.reasoning.Contains("LLM unavailable") ||
                         llmResponse.reasoning.Contains("LLM call failed") ||
                         llmResponse.reasoning.Contains("Exception")))
                    {
                        LogDefaultResponseWarning("TALK", llmResponse.reasoning);
                    }

                    response = new LLM.ConversationResponse
                    {
                        responder_id = EvacueeId,
                        responder_name = PersonaName,
                        response_message = llmResponse.response_message,
                        willing_to_share = llmResponse.willing_to_share,
                        want_to_continue = llmResponse.want_to_continue
                    };
                }
                else
                {
                    // LLMが失敗した場合はデフォルト応答
                    LogDefaultResponseWarning("TALK", "LLM応答がnull");
                    response = GenerateDefaultConversationResponse(request);
                }
            }
            else
            {
                // LLMが無効な場合はデフォルト応答
                LogDefaultResponseWarning("TALK", "LLMが無効");
                response = GenerateDefaultConversationResponse(request);
            }

            // 応答送信前のラグ（シミュレーション時間で5秒）
            await WaitSimulationTimeAsync(CONVERSATION_MESSAGE_LAG_SEC);

            // 話しかけてきたエージェントに応答を送信
            var initiator = FindEvacueeById(request.initiator_id);
            if (initiator != null)
            {
                initiator.OnConversationResponseReceived(response);
            }

            // 自分の会話履歴にも追加（話しかけられた側として）
            AddConversationToHistory(request.initiator_id, request.initiator_name, request.topic,
                                      request.message, response.response_message, false);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Evacuee] {gameObject.name}: 会話応答生成中にエラー: {ex.Message}");

            // エラー時はデフォルト応答を送信
            var initiator = FindEvacueeById(request.initiator_id);
            if (initiator != null)
            {
                initiator.OnConversationResponseReceived(GenerateDefaultConversationResponse(request));
            }
        }
        finally
        {
            // 会話終了後の処理
            _isRespondingToConversation = false;

            // LLMに次の行動を決定させる（会話の結果を踏まえて行動が変わる可能性あり）
            if (UseLLMDecision && DecisionClient != null)
            {
                Debug.Log($"[Evacuee] {gameObject.name}: 会話終了後、次の行動を決定します");
                RequestLLMDecision();
            }
            else
            {
                // LLMが無効な場合は以前の行動を再開
                Debug.Log($"[Evacuee] {gameObject.name}: 会話終了後、以前の行動({_actionBeforeConversation})を再開します");
                CurrentAction = _actionBeforeConversation;
                SafeStopAgent(false);
            }
        }
    }

    /// <summary>
    /// 相手からの会話応答を受信（話しかける側）
    /// </summary>
    public void OnConversationResponseReceived(LLM.ConversationResponse response)
    {
        if (_conversationResponseTCS != null && !_conversationResponseTCS.Task.IsCompleted)
        {
            _conversationResponseTCS.SetResult(response);
        }
    }

    /// <summary>
    /// デフォルトの会話応答を生成
    /// </summary>
    private LLM.ConversationResponse GenerateDefaultConversationResponse(LLM.ConversationRequest request)
    {
        string responseMessage;
        bool willingToShare = true;

        switch (request.topic)
        {
            case "ShelterInfo":
                if (!string.IsNullOrEmpty(CurrentTargetShelterId))
                {
                    responseMessage = $"私は{CurrentTargetShelterId}に向かっています。そこに行けば安全だと思いますよ。";
                }
                else
                {
                    responseMessage = "すみません、私もまだどこに避難するか決めかねているんです...";
                }
                break;

            case "CurrentSituation":
                responseMessage = "今は避難を急いでいます。お互い気をつけましょう。";
                break;

            case "ActionAdvice":
                responseMessage = "とりあえず高台に向かうのがいいと思います。";
                break;

            case "SafetyConfirm":
                responseMessage = "大丈夫ですよ。一緒に避難しましょう。";
                break;

            default:
                responseMessage = "はい、わかりました。";
                break;
        }

        return new LLM.ConversationResponse
        {
            responder_id = EvacueeId,
            responder_name = PersonaName,
            response_message = responseMessage,
            willing_to_share = willingToShare,
            want_to_continue = false  // デフォルトでは会話を終了
        };
    }

    /// <summary>
    /// 会話履歴に追加
    /// </summary>
    private void AddConversationToHistory(string partnerId, string partnerName, string topic,
                                           string myMessage, string partnerResponse, bool iInitiated)
    {
        var entry = new LLM.ConversationLogEntry
        {
            timestamp = _env != null ? _env.CurrentTimeSec : Time.time,
            partner_id = partnerId,
            partner_name = partnerName,
            topic = topic,
            my_message = iInitiated ? myMessage : partnerResponse,
            partner_response = iInitiated ? partnerResponse : myMessage,
            i_initiated = iInitiated
        };

        _conversationHistory.Add(entry);

        // 履歴が上限を超えたら古いものから削除
        while (_conversationHistory.Count > MAX_CONVERSATION_HISTORY)
        {
            _conversationHistory.RemoveAt(0);
        }
    }

    /// <summary>
    /// 会話履歴ペイロードを構築
    /// </summary>
    private LLM.ConversationHistoryPayload BuildConversationHistoryPayload()
    {
        if (_conversationHistory == null || _conversationHistory.Count == 0)
        {
            return null;
        }

        // 直近3件を返す
        var recentConversations = _conversationHistory
            .OrderByDescending(c => c.timestamp)
            .Take(3)
            .Reverse()
            .ToArray();

        return new LLM.ConversationHistoryPayload
        {
            recent_conversations = recentConversations,
            total_conversation_count = _conversationHistory.Count
        };
    }

    /// <summary>
    /// 行動履歴に新しいエントリを追加
    /// </summary>
    /// <param name="actionType">行動タイプ</param>
    /// <param name="target">対象（避難所名、家族名など）</param>
    /// <param name="reasoning">LLMの判断理由</param>
    /// <param name="result">結果（completed, failed, interrupted）</param>
    private void AddActionToHistory(LLM.ActionType actionType, string target, string reasoning, string result = "completed")
    {
        var entry = new LLM.ActionHistoryEntry
        {
            timestamp = _env != null ? _env.CurrentTimeSec : Time.time,
            action_type = actionType.ToString(),
            target = target ?? "",
            reasoning = reasoning ?? "",
            result = result
        };

        _actionHistory.Add(entry);
        _totalActionCount++;

        // 履歴が上限を超えたら古いものから削除（要約はサーバー側で行う）
        while (_actionHistory.Count > MAX_ACTION_HISTORY)
        {
            _actionHistory.RemoveAt(0);
        }
    }

    /// <summary>
    /// 行動履歴ペイロードを構築
    /// </summary>
    private LLM.ActionHistoryPayload BuildActionHistoryPayload()
    {
        return new LLM.ActionHistoryPayload
        {
            recent_actions = _actionHistory.ToArray(),
            summarized_history = _summarizedActionHistory,
            total_action_count = _totalActionCount
        };
    }

    /// <summary>
    /// サーバーから返された要約を適用
    /// </summary>
    /// <param name="summarizedHistory">要約済み行動履歴</param>
    private void ApplySummarizedActionHistory(string summarizedHistory)
    {
        if (!string.IsNullOrEmpty(summarizedHistory))
        {
            _summarizedActionHistory = summarizedHistory;
            // 要約が返されたら履歴をクリア（要約に含まれているため）
            _actionHistory.Clear();
            Debug.Log($"[Evacuee] {gameObject.name}: 行動履歴を要約しました: {summarizedHistory}");
        }
    }

    /// <summary>
    /// ペルソナ情報ペイロードを構築（会話応答用）
    /// </summary>
    private LLM.PersonaPayload BuildPersonaPayload()
    {
        PersonaData persona = _persona;
        if (persona == null && !string.IsNullOrEmpty(_uniqueId))
        {
            if (int.TryParse(_uniqueId, out int agentId))
            {
                persona = PersonaManager.GetPersona(agentId);
            }
        }

        if (persona != null)
        {
            return new LLM.PersonaPayload
            {
                agent_id = persona.agent_id,
                name = persona.name,
                role = persona.role,
                age_group = persona.age_group,
                speed_multiplier = persona.speed_multiplier,
                mental_state = persona.mental_state,
                priority = persona.priority,
                system_prompt_context = persona.system_prompt_context
            };
        }

        // ペルソナがない場合はデフォルト値を返す
        return new LLM.PersonaPayload
        {
            agent_id = 0,
            name = gameObject.name,
            role = "避難者",
            age_group = "成人",
            speed_multiplier = 1.0f,
            mental_state = "平常",
            priority = "自分の安全",
            system_prompt_context = ""
        };
    }

    /// <summary>
    /// FOLLOW行動の更新処理（追従対象の位置を追跡）
    /// 注意: 行動変更の検知は追従対象からの通知で行うため、ここでは位置追跡のみ
    /// </summary>
    private void UpdateFollowAction()
    {
        if (_followTarget == null || !_followTarget.gameObject.activeSelf)
        {
            // 追従対象が消えた場合はEVACUATEに切り替え
            Debug.LogWarning($"[Evacuee] {gameObject.name}: 追従対象が消えました。EVACUATEに切り替えます。");
            UnregisterFromFollowTarget();
            CurrentAction = LLM.ActionType.EVACUATE;
            if (UseLLMDecision && DecisionClient != null)
            {
                RequestLLMDecision();
            }
            else
            {
                MoveToNearestShelter();
            }
            return;
        }

        // 相手の現在の行動に応じた位置追跡のみ（行動変更の検知は通知で行う）
        LLM.ActionType currentTargetAction = _followTarget.CurrentAction;

        // 定期的に追従先の位置を更新
        float currentTime = Time.time;
        if (currentTime - _lastFollowCheck >= _followCheckInterval)
        {
            _lastFollowCheck = currentTime;

            // 相手の現在の行動に応じた処理
            if (currentTargetAction == LLM.ActionType.EVACUATE)
            {
                // 相手がEVACUATEの場合 → 同じ避難所を目指す
                if (_followTarget.Target != null)
                {
                    Target = _followTarget.Target;
                    Vector3 targetPos = Target.transform.position;
                    float distance = Vector3.Distance(transform.position, targetPos);

                    if (distance > _followDistance * 2)
                    {
                        SafeSetDestination(targetPos);
                    }
                    else if (distance < _followDistance)
                    {
                        SafeStopAgent(true);
                    }
                    else
                    {
                        SafeStopAgent(false);
                        SafeSetDestination(targetPos);
                    }
                }
                else
                {
                    // 相手の目標が設定されていない場合は位置を追従
                    Vector3 targetPos = _followTarget.transform.position;
                    SafeSetDestination(targetPos);
                }
            }
            else if (currentTargetAction == LLM.ActionType.STAY)
            {
                // 相手がSTAYの場合 → 同じようにSTAYする
                if (CurrentAction != LLM.ActionType.STAY)
                {
                    CurrentAction = LLM.ActionType.STAY;
                    _stayPosition = transform.position;
                }
                SafeStopAgent(true);
                SafeResetPath();
            }
            else
            {
                // その他の行動（SEARCH_FAMILY, CONTACT, FOLLOW）の場合は位置を追従
                Vector3 targetPos = _followTarget.transform.position;
                float distance = Vector3.Distance(transform.position, targetPos);

                if (distance > _followDistance * 2)
                {
                    SafeSetDestination(targetPos);
                }
                else if (distance < _followDistance)
                {
                    SafeStopAgent(true);
                }
                else
                {
                    SafeStopAgent(false);
                    SafeSetDestination(targetPos);
                }
            }
        }

        // 追従対象が避難所に到達したら、自分も同じ避難所に向かう
        if (_followTarget.isEvacuating && _followTarget.Target != null)
        {
            Target = _followTarget.Target;
            UnregisterFromFollowTarget(); // 追従解除
            CurrentAction = LLM.ActionType.EVACUATE;
            Debug.Log($"[Evacuee] {gameObject.name}: 追従対象が避難所に到達しました。同じ避難所に向かいます。");
        }
    }

    /// <summary>
    /// SEARCH_FAMILY行動の更新処理（シーン内家族の座標を定期的に更新）
    /// </summary>
    private void UpdateSearchFamilyAction()
    {
        // 探索対象が無効になった場合
        if (_searchFamilyTargetEvacuee == null || !_searchFamilyTargetEvacuee.gameObject.activeSelf)
        {
            Debug.Log($"[Evacuee] {gameObject.name}: 探索対象の家族がシーンから消えました。予測位置に切り替えます。");
            _searchFamilyTargetEvacuee = null;

            // 予測位置にフォールバック
            if (_searchFamilyTarget != null)
            {
                Vector3 fallbackPosition = _searchFamilyTarget.search_position != Vector3.zero
                    ? _searchFamilyTarget.search_position
                    : _searchFamilyTarget.spawn_position;

                if (fallbackPosition != Vector3.zero)
                {
                    SafeSetDestination(fallbackPosition);
                }
            }
            return;
        }

        // 定期的に座標を更新
        float currentTime = Time.time;
        if (currentTime - _lastSearchFamilyCheck >= SEARCH_FAMILY_CHECK_INTERVAL)
        {
            _lastSearchFamilyCheck = currentTime;

            Vector3 targetPos = _searchFamilyTargetEvacuee.transform.position;
            float distance = Vector3.Distance(transform.position, targetPos);

            // 合流判定: 10m以内なら合流処理
            if (distance <= FAMILY_REUNION_DISTANCE)
            {
                HandleFamilyReunion();
                return;
            }

            SafeSetDestination(targetPos);

            Debug.Log($"[Evacuee] {gameObject.name}: SEARCH_FAMILY座標更新 - {_searchFamilyTarget?.name}の現在位置 {targetPos} (距離: {distance:F1}m)");
        }
    }

    /// <summary>
    /// 家族との合流処理 - 探索者が家族をFOLLOWする連帯行動を開始
    /// </summary>
    private void HandleFamilyReunion()
    {
        string familyName = _searchFamilyTarget?.name ?? "不明";
        int familyAgentId = _searchFamilyTarget?.agent_id ?? -1;
        Debug.Log($"[Evacuee] {gameObject.name}: 家族 {familyName} と合流しました（距離10m以内）。FOLLOWに切り替えます。");

        // ★ 双方の合流フラグを設定
        // 1. 自分の家族データで相手を「合流済み」にマーク
        if (_searchFamilyTarget != null)
        {
            _searchFamilyTarget.is_reunited = true;
        }

        // 2. 相手の家族データで自分を「合流済み」にマーク
        MarkReunitedOnOtherSide(_searchFamilyTargetEvacuee);

        // 探索者が家族をFOLLOWする
        _followTarget = _searchFamilyTargetEvacuee;
        _followTarget.RegisterFollower(this);

        _lastFollowCheck = Time.time;
        _followTargetLastAction = _followTarget.CurrentAction;

        // 行動をFOLLOWに変更
        CurrentAction = LLM.ActionType.FOLLOW;

        // 探索状態をクリア
        _searchFamilyTarget = null;
        _searchFamilyTargetEvacuee = null;

        // 相手の行動に合わせた初期処理
        if (_followTargetLastAction == LLM.ActionType.EVACUATE && _followTarget.Target != null)
        {
            Target = _followTarget.Target;
            SafeStopAgent(false);
            SafeSetDestination(Target.transform.position);
        }
        else
        {
            // 相手の位置を追従
            SafeStopAgent(false);
            SafeSetDestination(_followTarget.transform.position);
        }

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.FOLLOW, null, $"家族({familyName})と合流したため連帯行動を開始", 1.0f);
    }

    /// <summary>
    /// 相手側の家族データで自分を「合流済み」にマーク
    /// </summary>
    private void MarkReunitedOnOtherSide(Evacuee otherEvacuee)
    {
        if (otherEvacuee == null) return;

        // 自分のagent_idを取得
        int myAgentId = -1;
        if (!string.IsNullOrEmpty(_uniqueId) && int.TryParse(_uniqueId, out int parsedId))
        {
            myAgentId = parsedId;
        }
        if (myAgentId <= 0) return;

        // 相手の家族データを取得
        var otherFamilyData = otherEvacuee.GetFamilyData();
        if (otherFamilyData == null || otherFamilyData.members == null) return;

        // 相手の家族リストから自分を探して合流済みにマーク
        foreach (var member in otherFamilyData.members)
        {
            if (member.agent_id == myAgentId)
            {
                member.is_reunited = true;
                Debug.Log($"[Evacuee] {otherEvacuee.gameObject.name}: 家族 {member.name} との合流を認識しました");
                break;
            }
        }
    }

    /// <summary>
    /// 追従対象に自分をFOLLOWERとして登録
    /// </summary>
    public void RegisterFollower(Evacuee follower)
    {
        if (follower == null) return;

        if (!_followers.Contains(follower))
        {
            _followers.Add(follower);
            Debug.Log($"[Evacuee] {gameObject.name}: {follower.gameObject.name}がFOLLOWERとして登録されました（現在{_followers.Count}人）");

            // リーダーは高優先度に設定（フォロワーに避けさせる）
            if (NavAgent != null && _followers.Count == 1)
            {
                NavAgent.avoidancePriority = 20; // 低い値 = 高優先度（他が避ける）
            }
        }
    }

    /// <summary>
    /// 追従対象から自分をFOLLOWERとして削除
    /// </summary>
    public void UnregisterFollower(Evacuee follower)
    {
        if (follower == null) return;

        if (_followers.Remove(follower))
        {
            Debug.Log($"[Evacuee] {gameObject.name}: {follower.gameObject.name}がFOLLOWERから削除されました（現在{_followers.Count}人）");

            // フォロワーがいなくなったら通常優先度に戻す
            if (NavAgent != null && _followers.Count == 0)
            {
                NavAgent.avoidancePriority = 50; // 通常優先度
            }
        }
    }

    /// <summary>
    /// 追従対象から自分を削除（内部用）
    /// </summary>
    private void UnregisterFromFollowTarget()
    {
        if (_followTarget != null)
        {
            _followTarget.UnregisterFollower(this);
            _followTarget = null;
        }
    }

    /// <summary>
    /// FOLLOW連鎖に循環がないかチェック
    /// A→B→A や A→B→C→A のような循環を検出
    /// </summary>
    private bool WouldCreateFollowCycle(Evacuee target)
    {
        if (target == null) return false;

        HashSet<Evacuee> visited = new HashSet<Evacuee> { this };
        Evacuee current = target;

        while (current != null)
        {
            if (visited.Contains(current))
                return true; // 循環検出

            visited.Add(current);

            // 相手がFOLLOW中でなければ連鎖終了
            if (current.CurrentAction != LLM.ActionType.FOLLOW)
                break;

            current = current._followTarget;
        }

        return false;
    }

    /// <summary>
    /// 循環を発生させない最寄りの追従対象を検索
    /// </summary>
    private Evacuee FindNearestNonCyclicFollowTarget()
    {
        var nearby = Physics.OverlapSphere(transform.position, FOLLOW_SEARCH_RADIUS);
        Evacuee nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (var col in nearby)
        {
            var evacuee = col.GetComponent<Evacuee>();
            if (evacuee == null || evacuee == this) continue;
            if (!evacuee.gameObject.activeSelf) continue;
            if (WouldCreateFollowCycle(evacuee)) continue; // 循環チェック

            float distance = Vector3.Distance(transform.position, evacuee.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = evacuee;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 行動が変更された際にFOLLOWERに通知
    /// </summary>
    private void NotifyFollowersOfActionChange()
    {
        if (_followers == null || _followers.Count == 0) return;

        Debug.Log($"[Evacuee] {gameObject.name}: 行動が変更されました（{CurrentAction}）。FOLLOWER {_followers.Count}人に通知します。");

        // FOLLOWERのリストをコピー（通知中にリストが変更される可能性があるため）
        var followersCopy = new List<Evacuee>(_followers);
        
        foreach (var follower in followersCopy)
        {
            if (follower == null || !follower.gameObject.activeSelf) continue;

            // FOLLOWERの行動変更を検知してLLM呼び出し
            follower.OnFollowTargetActionChanged(this, CurrentAction);
        }
    }

    /// <summary>
    /// 追従対象が消えた際にFOLLOWERに通知
    /// </summary>
    private void NotifyFollowersOfTargetLost()
    {
        if (_followers == null || _followers.Count == 0) return;

        var followersCopy = new List<Evacuee>(_followers);
        _followers.Clear();

        foreach (var follower in followersCopy)
        {
            if (follower == null || !follower.gameObject.activeSelf) continue;
            follower.OnFollowTargetLost();
        }
    }

    /// <summary>
    /// 追従対象の行動が変更された際に呼ばれる（追従対象から通知される）
    /// </summary>
    private void OnFollowTargetActionChanged(Evacuee target, LLM.ActionType newAction)
    {
        if (_followTarget != target) return; // 通知元が自分の追従対象か確認

        Debug.Log($"[Evacuee] {gameObject.name}: 追従対象の行動が変更されました（{_followTargetLastAction} → {newAction}）。LLMに判断を問い合わせます。");
        _followTargetLastAction = newAction;

        if (UseLLMDecision && DecisionClient != null)
        {
            RequestLLMDecision();
        }
        else
        {
            // LLMが無効な場合は自動的に同期
            SyncWithFollowTargetAction(newAction);
        }
    }

    /// <summary>
    /// 追従対象が消えた際に呼ばれる（追従対象から通知される）
    /// </summary>
    private void OnFollowTargetLost()
    {
        Debug.LogWarning($"[Evacuee] {gameObject.name}: 追従対象が消えました。EVACUATEに切り替えます。");
        _followTarget = null;
        CurrentAction = LLM.ActionType.EVACUATE;
        if (UseLLMDecision && DecisionClient != null)
        {
            RequestLLMDecision();
        }
        else
        {
            MoveToNearestShelter();
        }
    }

    /// <summary>
    /// 追従対象の行動に合わせて自身の行動を同期（LLMが無効な場合のフォールバック）
    /// </summary>
    private void SyncWithFollowTargetAction(LLM.ActionType targetAction)
    {
        if (_followTarget == null) return;

        if (targetAction == LLM.ActionType.EVACUATE)
        {
            // 相手がEVACUATE → 同じ避難所を目指す
            if (_followTarget.Target != null)
            {
                Target = _followTarget.Target;
                CurrentAction = LLM.ActionType.EVACUATE;
                SafeStopAgent(false);
                SafeSetDestination(Target.transform.position);
                Debug.Log($"[Evacuee] {gameObject.name}: 追従対象がEVACUATEに変更したため、同じ避難所を目指します。");
            }
        }
        else if (targetAction == LLM.ActionType.STAY)
        {
            // 相手がSTAY → 同じようにSTAYする
            CurrentAction = LLM.ActionType.STAY;
            _stayPosition = transform.position;
            SafeStopAgent(true);
            SafeResetPath();
            Debug.Log($"[Evacuee] {gameObject.name}: 追従対象がSTAYに変更したため、同じく待機します。");
        }
        // その他の行動（SEARCH_FAMILY, CONTACT, FOLLOW）の場合は位置を追従し続ける
    }

    /// <summary>
    /// EVACUATE行動を実行（避難所に向かう）- 既存のロジック
    /// </summary>
    private bool ExecuteEvacuateAction(LLMEvacDecisionResponse response)
    {
        if (string.IsNullOrEmpty(response.selected_shelter_id))
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: EVACUATE行動においてselected_shelter_idが確認できません。最寄りの避難所にフォールバックします。");

            // 最寄りの避難所を選択
            var nearestShelter = FindNearestShelter();
            if (nearestShelter != null)
            {
                response.selected_shelter_id = GetShelterDisplayName(nearestShelter);
                Debug.Log($"[Evacuee] {gameObject.name}: 最寄りの避難所 '{response.selected_shelter_id}' を選択しました。");
            }
            else
            {
                Debug.LogError($"[Evacuee] {gameObject.name}: 避難所が見つかりません。");
                return false;
            }
        }

        GameObject selectedShelter = null;
        
        // まずdisplayNameでマッチング
        foreach (var shelterObj in _env.Shelters)
        {
            if (shelterObj == null) continue;
            
            var shelterComp = shelterObj.GetComponent<Shelter>();
            if (shelterComp != null && !string.IsNullOrEmpty(shelterComp.displayName))
            {
                if (shelterComp.displayName == response.selected_shelter_id)
                {
                    selectedShelter = shelterObj;
                    break;
                }
            }
        }
        
        // displayNameで見つからなければGameObject.nameでフォールバック
        if (selectedShelter == null)
        {
            foreach (var shelterObj in _env.Shelters)
            {
                if (shelterObj != null && shelterObj.name == response.selected_shelter_id)
                {
                    selectedShelter = shelterObj;
                    break;
                }
            }
        }
        
        // Shelterで見つからなければ、津波避難地域を検索（displayNameでマッチング）
        if (selectedShelter == null && _env.TsunamiEvacuationAreas != null)
        {
            foreach (var areaObj in _env.TsunamiEvacuationAreas)
            {
                if (areaObj == null) continue;

                var areaComp = areaObj.GetComponent<TsunamiEvacuationArea>();
                if (areaComp != null && !string.IsNullOrEmpty(areaComp.displayName))
                {
                    if (areaComp.displayName == response.selected_shelter_id)
                    {
                        selectedShelter = areaObj;
                        break;
                    }
                }
            }
        }

        // 津波避難地域でもdisplayNameで見つからなければGameObject.nameでフォールバック
        if (selectedShelter == null && _env.TsunamiEvacuationAreas != null)
        {
            foreach (var areaObj in _env.TsunamiEvacuationAreas)
            {
                if (areaObj != null && areaObj.name == response.selected_shelter_id)
                {
                    selectedShelter = areaObj;
                    break;
                }
            }
        }

        // それでも見つからなければGameObject.Findで検索
        if (selectedShelter == null)
        {
            selectedShelter = GameObject.Find(response.selected_shelter_id);
        }

        if (selectedShelter == null)
        {
            // 避難所の名前リスト
            var availableShelters = _env.Shelters
                .Where(s => s != null)
                .Select(s => {
                    var sc = s.GetComponent<Shelter>();
                    return sc != null && !string.IsNullOrEmpty(sc.displayName)
                        ? sc.displayName
                        : s.name;
                })
                .ToList();

            // 津波避難地域の名前リスト（別リストとして管理）
            var availableAreas = new List<string>();
            if (_env.TsunamiEvacuationAreas != null)
            {
                availableAreas = _env.TsunamiEvacuationAreas
                    .Where(a => a != null)
                    .Select(a => {
                        var ac = a.GetComponent<TsunamiEvacuationArea>();
                        return ac != null && !string.IsNullOrEmpty(ac.displayName)
                            ? ac.displayName
                            : a.name;
                    })
                    .ToList();
            }

            // 区別してエラーメッセージを表示
            string shelterList = availableShelters.Count > 0
                ? $"避難所: {string.Join(", ", availableShelters)}"
                : "避難所: なし";
            string areaList = availableAreas.Count > 0
                ? $"津波避難地域: {string.Join(", ", availableAreas)}"
                : "津波避難地域: なし";

            Debug.LogWarning($"[Evacuee] {gameObject.name}: LLMが選択した避難先が見つかりません: '{response.selected_shelter_id}' " +
                           $"(利用可能な {shelterList} / {areaList})");
            return false;
        }

        var point = selectedShelter.transform.childCount > 0 ? selectedShelter.transform.GetChild(0).gameObject : selectedShelter;
        Target = point;

        // NavMeshAgentを再開（STAY状態から遷移した場合）
        SafeStopAgent(false);
        SafeSetDestination(Target.transform.position);
        
        ApplySpeedFromLLMResponse(response.desired_speed);
        
        // 表示名と種別を取得（Shelter または TsunamiEvacuationArea のdisplayNameを使用）
        var selectedShelterComp = selectedShelter.GetComponent<Shelter>();
        var selectedAreaComp = selectedShelter.GetComponent<TsunamiEvacuationArea>();
        string displayName = selectedShelter.name;
        string destinationType = "避難所";  // デフォルト

        if (selectedShelterComp != null && !string.IsNullOrEmpty(selectedShelterComp.displayName))
        {
            // Shelterの場合
            // displayNameが「bldg_」で始まる場合は簡略表示、それ以外はそのまま
            if (selectedShelterComp.displayName.StartsWith("bldg_"))
            {
                // bldg_の後の最初の8文字を表示（例: bldg_37ad7316）
                displayName = selectedShelterComp.displayName.Length > 13
                    ? selectedShelterComp.displayName.Substring(0, 13) + "..."
                    : selectedShelterComp.displayName;
            }
            else
            {
                displayName = selectedShelterComp.displayName;
            }
            destinationType = "避難所";
        }
        else if (selectedAreaComp != null && !string.IsNullOrEmpty(selectedAreaComp.displayName))
        {
            // 津波避難地域の場合
            displayName = selectedAreaComp.displayName;
            destinationType = "津波避難地域";
        }

        // メトリクス記録
        var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
        metrics?.RecordAction(_uniqueId ?? gameObject.name, LLM.ActionType.EVACUATE, response.selected_shelter_id, response.reasoning, response.confidence);

        Debug.Log($"[Evacuee] {gameObject.name}: EVACUATE - 【{destinationType}】{displayName}に向かいます "
              + $"(pos: {Target.transform.position}), "
              + $"reason='{response.reasoning}', confidence={response.confidence:F2}, "
              + $"speed={NavAgent.speed:F2} m/s（{SpeedChoiceNames[CurrentSpeedChoice]}）");
        return true;
    }

    private LLMEvacDecisionRequest BuildEvacDecisionRequest()
    {
        var shelterPayloads = new List<ShelterCandidatePayload>();
        foreach (var shelterObj in _env.Shelters)
        {
            if (shelterObj == null)
            {
                continue;
            }
            var shelter = shelterObj.GetComponent<Shelter>();
            var pointTransform = shelterObj.transform.childCount > 0
                ? shelterObj.transform.GetChild(0)
                : shelterObj.transform;
            var pointPosition = pointTransform.position;

            // 最新のcurrentCapacityを取得（プロパティなので常に最新値が返される）
            int currentCapacity = shelter != null ? shelter.currentCapacity : 0;
            int maxCapacity = shelter != null ? shelter.MaxCapacity : 0;
            
            // 避難所の表示名と説明を取得
            string displayName = shelter != null && !string.IsNullOrEmpty(shelter.displayName) 
                ? shelter.displayName 
                : shelterObj.name;
            string description = shelter != null ? shelter.description : "";
            
            // NavMeshでの経路距離と徒歩時間を計算
            float distanceMeters = 0f;
            float walkingTimeMinutes = 0f;
            
            // パフォーマンス最適化: ShelterのNavMesh位置キャッシュを使用
            Vector3 targetPos = pointPosition;
            try
            {
                Vector3? cachedNavMeshPos = shelter != null ? shelter.GetNavMeshPosition() : null;
                if (cachedNavMeshPos.HasValue)
                {
                    targetPos = cachedNavMeshPos.Value;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: GetNavMeshPosition failed for {shelterObj.name}, using default position - {ex.Message}");
            }
            
            CalculateNavMeshDistance(transform.position, targetPos, out distanceMeters, out walkingTimeMinutes);
            
            // 避難所の海抜を取得
            float elevationMeters = shelter != null ? shelter.GetElevation() : 0f;

            shelterPayloads.Add(new ShelterCandidatePayload
            {
                id = shelterObj.name,
                display_name = displayName,
                description = description,
                position = new Vector3Payload(pointPosition),
                current_capacity = currentCapacity,
                max_capacity = maxCapacity,
                distance_meters = distanceMeters,
                walking_time_minutes = walkingTimeMinutes,
                destination_type = "shelter",
                elevation_meters = elevationMeters
            });
        }

        // 津波避難地域を候補に追加
        if (_env.TsunamiEvacuationAreas != null)
        {
            foreach (var areaObj in _env.TsunamiEvacuationAreas)
            {
                if (areaObj == null)
                {
                    continue;
                }
                var area = areaObj.GetComponent<TsunamiEvacuationArea>();
                var pointTransform = areaObj.transform.childCount > 0
                    ? areaObj.transform.GetChild(0)
                    : areaObj.transform;
                var pointPosition = pointTransform.position;

                string displayName = area != null && !string.IsNullOrEmpty(area.displayName)
                    ? area.displayName
                    : areaObj.name;
                string description = area != null ? area.description : "";
                float elevationMeters = area != null ? area.elevationMeters : 0f;

                // NavMeshでの経路距離と徒歩時間を計算
                float distanceMeters = 0f;
                float walkingTimeMinutes = 0f;

                Vector3 targetPos = pointPosition;
                try
                {
                    Vector3? cachedNavMeshPos = area != null ? area.GetNavMeshPosition() : null;
                    if (cachedNavMeshPos.HasValue)
                    {
                        targetPos = cachedNavMeshPos.Value;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Evacuee] {gameObject.name}: GetNavMeshPosition failed for {areaObj.name}, using default position - {ex.Message}");
                }

                CalculateNavMeshDistance(transform.position, targetPos, out distanceMeters, out walkingTimeMinutes);

                shelterPayloads.Add(new ShelterCandidatePayload
                {
                    id = areaObj.name,
                    display_name = displayName,
                    description = description,
                    position = new Vector3Payload(pointPosition),
                    current_capacity = int.MaxValue,  // 収容制限なし
                    max_capacity = int.MaxValue,
                    distance_meters = distanceMeters,
                    walking_time_minutes = walkingTimeMinutes,
                    destination_type = "tsunami_evacuation_area",
                    elevation_meters = elevationMeters
                });
            }
        }

        // ペルソナ情報をペイロードに変換
        PersonaPayload personaPayload = null;
        
        // _personaがnullの場合は、_uniqueIdからエージェントIDを取得して再取得を試みる
        PersonaData persona = _persona;
        if (persona == null && !string.IsNullOrEmpty(_uniqueId))
        {
            if (int.TryParse(_uniqueId, out int agentId))
            {
                persona = PersonaManager.GetPersona(agentId);
                if (persona != null)
                {
                    _persona = persona; // キャッシュしておく
                    Debug.Log($"[Evacuee] {gameObject.name}: ペルソナを再取得しました - {persona.name} (agent_id: {agentId})");
                }
            }
        }
        
        if (persona != null)
        {
            personaPayload = new PersonaPayload
            {
                agent_id = persona.agent_id,
                name = persona.name,
                role = persona.role,
                age_group = persona.age_group,
                speed_multiplier = persona.speed_multiplier,
                mental_state = persona.mental_state,
                priority = persona.priority,
                system_prompt_context = persona.system_prompt_context,
                // 拡張フィールド: 生活背景情報
                home_location_category = persona.home_location_category,
                home_elevation = persona.home_elevation,
                home_structure = persona.home_structure,
                residence_years = persona.residence_years,
                local_knowledge_level = persona.local_knowledge_level,
                current_location_reason = persona.current_location_reason,
                past_disaster_experience = persona.past_disaster_experience,
                physical_condition = persona.physical_condition
            };
        }
        else
        {
            Debug.LogWarning($"[Evacuee] {gameObject.name}: ペルソナ情報が見つかりません (_uniqueId: {_uniqueId})");
        }

        // 環境コンテキストを取得
        EnvironmentalContextPayload envPayload = BuildEnvironmentalContextPayload();

        // 家族情報を取得
        FamilyMemberPayload[] familyPayloads = BuildFamilyPayload();

        // 周辺避難者情報を取得（詳細情報と統計情報）
        int nearbyEvacueesCount = 0;
        float nearbyEvacueesDensity = 0f;
        NearbyEvacueePayload[] nearbyEvacuees = GetNearbyEvacuees(FOLLOW_SEARCH_RADIUS, out nearbyEvacueesCount, out nearbyEvacueesDensity);

        // 環境状態を取得（DisasterEventManagerから）
        EnvironmentStatePayload envStatePayload = null;
        var disasterEventManager = FindFirstObjectByType<DisasterEventManager>();
        if (disasterEventManager != null)
        {
            envStatePayload = disasterEventManager.GetEnvironmentStatePayload();
        }

        // 利用可能なアクションを構築（条件に基づいて動的に設定）
        var availableActions = BuildAvailableActions(familyPayloads, nearbyEvacuees);

        // 実験IDを取得（EnvManagerのrecordIDから統一的に生成）
        // 形式: yyyy_MM_dd-HH_mm_ss → yyyyMMdd_HHmmss
        string experimentId = !string.IsNullOrEmpty(_env?.recordID)
            ? _env.recordID.Replace("_", "").Replace("-", "_")
            : DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // 実験2-2: 認知バイアス条件を取得
        string biasCondition = "none";
        bool overrideBias = false;
        if (ExperimentConfig.Instance != null)
        {
            overrideBias = ExperimentConfig.ShouldOverrideBias();
            if (overrideBias)
            {
                biasCondition = ExperimentConfig.GetBiasCondition() switch
                {
                    ExperimentConfig.BiasCondition.NormalcyBias => "normalcy_bias",
                    ExperimentConfig.BiasCondition.ConformityBias => "conformity_bias",
                    ExperimentConfig.BiasCondition.Combined => "combined",
                    _ => "none"
                };
            }
        }

        // 実験3: 行政情報を構築
        string informationStrategyName = "standard";
        AdministrativeGuidancePayload adminGuidance = null;
        if (ExperimentConfig.Instance != null)
        {
            informationStrategyName = ExperimentConfig.GetInformationStrategy() switch
            {
                ExperimentConfig.InformationStrategy.Standard => "standard",
                ExperimentConfig.InformationStrategy.Urgent => "urgent",
                ExperimentConfig.InformationStrategy.Detailed => "detailed",
                ExperimentConfig.InformationStrategy.DetailedUrgent => "detailed_urgent",
                _ => "standard"
            };
            adminGuidance = BuildAdministrativeGuidancePayload();
        }

        return new LLMEvacDecisionRequest
        {
            request_id = $"{_env.currentEpisodeId}-{gameObject.name}-{Guid.NewGuid()}",
            experiment_id = experimentId,
            episode_id = _env.currentEpisodeId,
            episode_elapsed_time = _env.CurrentTimeSec,
            timestamp = Time.time,
            evacuee = new EvacueePayload
            {
                id = !string.IsNullOrEmpty(_uniqueId) ? _uniqueId : gameObject.name, // 生成順序のIDを使用
                position = new Vector3Payload(transform.position)
            },
            shelter_candidates = shelterPayloads.ToArray(),
            self_state = BuildSelfStatePayload(),
            temporal_context = BuildTemporalContextPayload(),
            persona = personaPayload,
            environmental_context = envPayload,
            family_members = familyPayloads,
            nearby_evacuees = nearbyEvacuees,
            nearby_evacuees_count = nearbyEvacueesCount,
            nearby_evacuees_density = nearbyEvacueesDensity,
            last_contact_message = _lastContactMessage,
            scenario_id = _env != null ? _env.GetScenarioId() : "shindo_2",
            has_heard_broadcast = _hasHeardBroadcast,
            last_broadcast_message = _lastBroadcastMessage,
            has_received_j_alert = _hasReceivedJAlert,
            last_j_alert_message = _lastJAlertMessage,
            current_long_term_goal = _currentLongTermGoal,  // 現在の長期目標（更新判断用）
            current_mid_term_plan = _currentMidTermPlan,   // 現在の中期計画（更新判断用）
            conversation_history = BuildConversationHistoryPayload(),  // 会話履歴（TALK行動用）
            action_history = BuildActionHistoryPayload(),  // 行動履歴（短期記憶）
            environment_state = envStatePayload,  // 環境状態（災害フェーズ等）
            available_actions = availableActions,  // 利用可能なアクション一覧
            // 実験2-2: 認知バイアス条件
            bias_condition = biasCondition,
            override_persona_bias = overrideBias,
            // 実験3: 情報提供戦略
            information_strategy = informationStrategyName,
            administrative_guidance = adminGuidance,
            // 交通シミュレーション: 現在の移動手段と周辺交通状況
            transport_mode = CurrentTransportMode == TransportMode.DRIVING ? "DRIVING" : "WALKING",
            nearby_traffic = BuildNearbyTrafficPayload()
        };
    }

    /// <summary>
    /// 周辺の交通状況ペイロードを構築する（LLM意思決定用）。
    /// EnvironmentalContextProvider 経由で TrafficStateProvider から取得する。
    /// 交通シミュレーションが無効、または情報が得られない場合は null を返す。
    /// </summary>
    private NearbyTrafficPayload BuildNearbyTrafficPayload()
    {
        if (ExperimentConfig.Instance == null || !ExperimentConfig.Instance.EnableTrafficSimulation)
            return null;

        var envContext = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (envContext == null) return null;

        Vector3 pos = transform.position;
        var conditions = envContext.GetTrafficConditions(pos, 300f);
        if (conditions == null || conditions.Count == 0) return null;

        // 上位5件のみLLMに渡す（コンテキスト長を抑制）
        var roads = conditions.Take(5).Select(c => new RoadTrafficPayload
        {
            edge_id = c.edgeId,
            road_name = c.roadName,
            congestion_level = c.congestionLevel,
            congestion_label = c.congestionLabel,
            average_speed_kmh = c.averageSpeedKmh,
            estimated_travel_time = c.travelTime,
            free_flow_travel_time = c.freeFlowTravelTime,
            distance_meters = c.distanceFromPosition
        }).ToArray();

        return new NearbyTrafficPayload
        {
            nearby_roads = roads,
            overall_congestion = envContext.GetOverallCongestion(pos, 300f),
            summary = envContext.GetTrafficSummary(pos, 300f)
        };
    }

    /// <summary>
    /// 利用可能なアクション一覧を構築（条件に基づいて動的に設定）
    /// </summary>
    private string[] BuildAvailableActions(FamilyMemberPayload[] familyPayloads, NearbyEvacueePayload[] nearbyEvacuees)
    {
        var actions = new List<string>();

        // 基本アクション（常に利用可能）
        actions.Add("EVACUATE");
        actions.Add("STAY");

        // 家族関連アクション（家族がいる場合のみ）
        bool hasFamily = _familyData != null && _familyData.members != null && _familyData.members.Count > 0;
        if (hasFamily)
        {
            // SEARCH_FAMILY: シーン内に存在する家族がいる場合のみ（シーン外家族は座標取得不可）
            // ExecuteSearchFamilyActionと同じ条件: exists_in_scene && agent_id > 0
            bool hasInSceneFamily = _familyData.members.Any(m => m.exists_in_scene && m.agent_id > 0);
            if (hasInSceneFamily)
            {
                actions.Add("SEARCH_FAMILY");
            }

            // CONTACT: スマホを持っていて、連絡可能な家族がいる場合（クールダウン中は除外）
            bool canContact = HasSmartphone && _familyData.members.Any(m => m.has_phone) && !_contactCooldown;
            if (canContact)
            {
                actions.Add("CONTACT");
            }
        }

        // FOLLOW: 周辺に他の避難者がいる場合
        bool hasNearbyEvacuees = nearbyEvacuees != null && nearbyEvacuees.Length > 0;
        if (hasNearbyEvacuees)
        {
            actions.Add("FOLLOW");
        }

        // TALK: 周辺に他の避難者がいる場合
        if (hasNearbyEvacuees)
        {
            actions.Add("TALK");
        }

        return actions.ToArray();
    }

    /// <summary>
    /// 行政からの情報提供ガイダンスを構築（実験3用）
    /// ExperimentConfigの情報提供戦略に基づいて、避難所の推奨・非推奨を決定
    /// </summary>
    private AdministrativeGuidancePayload BuildAdministrativeGuidancePayload()
    {
        if (ExperimentConfig.Instance == null)
        {
            return null;
        }

        var strategy = ExperimentConfig.GetInformationStrategy();
        float tsunamiHeight = ExperimentConfig.GetTsunamiHeight();
        float safeElevation = tsunamiHeight * 2f; // 安全ライン = 津波高さ × 2

        // 情報提供戦略に応じたパラメータ設定
        string strategyName = strategy switch
        {
            ExperimentConfig.InformationStrategy.Standard => "standard",
            ExperimentConfig.InformationStrategy.Urgent => "urgent",
            ExperimentConfig.InformationStrategy.Detailed => "detailed",
            ExperimentConfig.InformationStrategy.DetailedUrgent => "detailed_urgent",
            _ => "standard"
        };

        bool isDetailed = ExperimentConfig.IsDetailedStrategy();
        bool isUrgent = ExperimentConfig.IsUrgentStrategy();

        string urgencyLevel = isUrgent ? "critical" : "normal";

        // 推奨・非推奨避難所の分類（詳細情報提供の場合のみ）
        var recommendedShelters = new List<string>();
        var unsafeShelters = new List<string>();

        if (isDetailed && _env != null)
        {
            // 避難所を海抜に基づいて分類
            if (_env.Shelters != null)
            {
                foreach (var shelterObj in _env.Shelters)
                {
                    if (shelterObj == null) continue;
                    var shelter = shelterObj.GetComponent<Shelter>();
                    if (shelter == null) continue;

                    float elevation = shelter.GetElevation();
                    string displayName = !string.IsNullOrEmpty(shelter.displayName)
                        ? shelter.displayName
                        : shelterObj.name;

                    if (elevation >= safeElevation)
                    {
                        recommendedShelters.Add($"{displayName}（海抜{elevation:F0}m）");
                    }
                    else
                    {
                        unsafeShelters.Add($"{displayName}（海抜{elevation:F0}m - 想定津波高を下回る可能性）");
                    }
                }
            }

            // 津波避難地域も確認
            if (_env.TsunamiEvacuationAreas != null)
            {
                foreach (var areaObj in _env.TsunamiEvacuationAreas)
                {
                    if (areaObj == null) continue;
                    var area = areaObj.GetComponent<TsunamiEvacuationArea>();
                    if (area == null) continue;

                    float elevation = area.elevationMeters;
                    string displayName = !string.IsNullOrEmpty(area.displayName)
                        ? area.displayName
                        : areaObj.name;

                    if (elevation >= safeElevation)
                    {
                        recommendedShelters.Add($"{displayName}（海抜{elevation:F0}m）");
                    }
                }
            }
        }

        // 避難ガイダンス文の生成
        string evacuationGuidance = strategy switch
        {
            ExperimentConfig.InformationStrategy.Standard =>
                "大津波警報が発令されています。指定避難所へ避難してください。",
            ExperimentConfig.InformationStrategy.Urgent =>
                "【緊急】津波が来ます！今すぐ高台へ逃げてください！指定避難所を目指してください！",
            ExperimentConfig.InformationStrategy.Detailed =>
                $"津波予想高さ{tsunamiHeight}mに対し、海抜{safeElevation:F0}m以上の避難所を推奨します。" +
                (recommendedShelters.Count > 0 ? $"推奨避難先: {string.Join("、", recommendedShelters)}" : ""),
            ExperimentConfig.InformationStrategy.DetailedUrgent =>
                $"【緊急警告】想定を超える津波の恐れ！海抜{safeElevation:F0}m以上へ今すぐ避難！" +
                (recommendedShelters.Count > 0 ? $" 推奨: {string.Join("、", recommendedShelters)}" : ""),
            _ => "避難してください。"
        };

        // 追加警告メッセージ（切迫感が高い場合）
        string additionalWarning = isUrgent
            ? "あなたの命がかかっています！想定を過信せず、より高い場所を目指してください。"
            : null;

        return new AdministrativeGuidancePayload
        {
            information_strategy = strategyName,
            initial_tsunami_height = 3f,  // 初報の予想値
            updated_tsunami_height = tsunamiHeight,  // 更新後の予想値
            exceeds_assumption = tsunamiHeight > 5f,  // ハザードマップ想定を超えるか
            urgency_level = urgencyLevel,
            evacuation_guidance = evacuationGuidance,
            recommended_shelters = recommendedShelters.ToArray(),
            unsafe_shelters = unsafeShelters.ToArray(),
            additional_warning = additionalWarning
        };
    }

    /// <summary>
    /// 周辺の避難者を検出してペイロードに変換（FOLLOW行動用）
    /// </summary>
    /// <param name="radius">検索半径（メートル）</param>
    /// <param name="totalCount">検出された総人数（出力パラメータ）</param>
    /// <param name="density">混雑度（人数/100m²）（出力パラメータ）</param>
    /// <returns>詳細情報（最大5人、距離順）</returns>
    private NearbyEvacueePayload[] GetNearbyEvacuees(float radius, out int totalCount, out float density)
    {
        totalCount = 0;
        density = 0f;

        if (_env == null || _env.Evacuees == null)
        {
            return new NearbyEvacueePayload[0];
        }

        var nearbyList = new List<NearbyEvacueePayload>();
        Vector3 myPosition = transform.position;

        foreach (var evacueeObj in _env.Evacuees)
        {
            if (evacueeObj == null || evacueeObj == this.gameObject || !evacueeObj.activeSelf)
            {
                continue;
            }

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null)
            {
                continue;
            }

            float distance = Vector3.Distance(myPosition, evacueeObj.transform.position);
            if (distance <= radius)
            {
                totalCount++; // 総人数をカウント

                // この人をフォローすると循環が発生する場合はリストから除外（相互FOLLOW防止）
                // 混雑度計算には含めるが、FOLLOW/TALK対象としては表示しない
                if (WouldCreateFollowCycle(evacuee))
                {
                    continue;
                }

                // 目標避難所のIDを取得
                string targetShelterId = null;
                if (evacuee.Target != null)
                {
                    var shelter = evacuee.Target.GetComponentInParent<Shelter>();
                    if (shelter != null && !string.IsNullOrEmpty(shelter.displayName))
                    {
                        targetShelterId = shelter.displayName;
                    }
                    else
                    {
                        targetShelterId = evacuee.Target.name;
                    }
                }

                string evacueeId = !string.IsNullOrEmpty(evacuee._uniqueId) ? evacuee._uniqueId : evacueeObj.name;

                // ペルソナ情報を取得
                string evacueeName = evacuee._persona?.name ?? evacueeObj.name;
                string evacueeRole = evacuee._persona?.role ?? "";
                string evacueeAgeGroup = evacuee._persona?.age_group ?? "";

                // 現在の行動を判定（応答中のフラグも考慮）
                string currentActionStr;
                if (evacuee._isRespondingToConversation)
                {
                    // 会話の応答者として対応中の場合はTALKとして表示
                    currentActionStr = LLM.ActionType.TALK.ToString();
                }
                else if (evacuee._isRespondingToFamilyContact)
                {
                    // 家族連絡の応答者として対応中の場合はCONTACTとして表示
                    currentActionStr = LLM.ActionType.CONTACT.ToString();
                }
                else
                {
                    currentActionStr = evacuee.CurrentAction.ToString();
                }

                nearbyList.Add(new NearbyEvacueePayload
                {
                    id = evacueeId,
                    position = new Vector3Payload(evacueeObj.transform.position),
                    distance_meters = distance,
                    current_action = currentActionStr,
                    target_shelter_id = targetShelterId,
                    name = evacueeName,
                    role = evacueeRole,
                    age_group = evacueeAgeGroup
                });
            }
        }

        // 混雑度を計算（人数/100m²）
        // 検索範囲の面積 = π * radius²
        float searchArea = Mathf.PI * radius * radius;
        density = totalCount > 0 ? (totalCount / searchArea) * 100f : 0f; // 100m²あたりの人数

        // 距離順にソートして最大5人まで返す（詳細情報）
        return nearbyList
            .OrderBy(e => e.distance_meters)
            .Take(5)
            .ToArray();
    }
    
    /// <summary>
    /// 家族情報をLLMペイロードに変換
    /// </summary>
    private FamilyMemberPayload[] BuildFamilyPayload()
    {
        if (_familyData == null || _familyData.members == null || _familyData.members.Count == 0)
        {
            return new FamilyMemberPayload[0];
        }
        
        var payloads = new List<FamilyMemberPayload>();
        
        foreach (var member in _familyData.members)
        {
            // 距離を計算
            float distance = Vector3.Distance(
                new Vector3(transform.position.x, 0, transform.position.z),
                new Vector3(member.search_position.x, 0, member.search_position.z)
            );
            
            payloads.Add(new FamilyMemberPayload
            {
                name = member.name,
                relation = member.relation,
                likely_location = member.likely_location,
                search_position = new Vector3Payload(member.search_position),
                distance_meters = distance,
                has_phone = member.has_phone,
                exists_in_scene = member.exists_in_scene,
                is_reunited = member.is_reunited
            });
        }
        
        return payloads.ToArray();
    }
    
    private EnvironmentalContextPayload BuildEnvironmentalContextPayload()
    {
        // EnvironmentalContextProviderを検索
        var envContext = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (envContext == null)
        {
            // プロバイダーが見つからない場合はnullを返す
            Debug.LogWarning($"[Evacuee] {gameObject.name}: EnvironmentalContextProviderが見つかりません。環境情報は含まれません。");
            return null;
        }

        Vector3 currentPosition = transform.position;

        // 周辺建物を検索
        float searchRadius = 30f;
        var nearbyBuildings = envContext.GetNearbyBuildings(currentPosition, searchRadius);

        // LLMのコンテキスト長を考慮して、最大10件に制限
        var limitedBuildings = nearbyBuildings.Take(10).ToList();

        // ペイロードに変換
        var buildingPayloads = limitedBuildings.Select(b => new BuildingContextPayload
        {
            name = b.name,
            position = new Vector3Payload(b.position),
            distance = b.distance,
            usage = b.usage,
            major_usage = b.majorUsage ?? "",
            height = b.height,
            floors = b.floors,
            structure_type = b.structureType ?? "",
            land_use_type = b.landUseType ?? ""
        }).ToArray();

        // 津波リスク情報を取得
        var tsunamiRisk = envContext.GetTsunamiRiskAtPosition(currentPosition);
        bool isInTsunamiZone = tsunamiRisk != null;
        string tsunamiRiskRank = tsunamiRisk?.rank ?? "";
        float tsunamiEstimatedDepth = tsunamiRisk?.estimatedDepth ?? 0f;

        // 土砂災害リスク情報を取得
        var landslideRisk = envContext.GetLandslideRiskAtPosition(currentPosition);
        bool isInLandslideZone = landslideRisk != null;
        bool isInSpecialLandslideZone = landslideRisk?.isSpecialZone ?? false;
        string landslideType = landslideRisk?.descriptionName ?? "";

        // 標高情報を取得
        float currentElevation = envContext.GetCurrentElevation(currentPosition);

        // 最寄りの主要道路情報を取得
        var nearestRoad = envContext.GetNearestMajorRoad(currentPosition);
        string nearestMajorRoad = nearestRoad?.name ?? "";
        string nearestRoadType = nearestRoad?.functionName ?? "";

        // 土地利用情報を取得
        var landUse = envContext.GetLandUseAtPosition(currentPosition);
        string currentLandUse = landUse?.landUseClassName ?? "";

        return new EnvironmentalContextPayload
        {
            nearby_buildings = buildingPayloads,
            search_radius = searchRadius,
            total_buildings_in_area = nearbyBuildings.Count,

            // 災害リスク情報
            is_in_tsunami_zone = isInTsunamiZone,
            tsunami_risk_rank = tsunamiRiskRank,
            tsunami_estimated_depth = tsunamiEstimatedDepth,
            is_in_landslide_zone = isInLandslideZone,
            is_in_special_landslide_zone = isInSpecialLandslideZone,
            landslide_type = landslideType,

            // 地形情報
            current_elevation = currentElevation,

            // 道路情報
            nearest_major_road = nearestMajorRoad,
            nearest_road_type = nearestRoadType,

            // 土地利用情報
            current_land_use = currentLandUse
        };
    }

    private void MoveToNearestShelter()
    {
        List<GameObject> towers = SearchShelters(excludeShelters);
        if(towers.Count > 0) {
            Target = towers[0]; //最短距離のタワーを目標に設定
            Vector3 destination = Target.transform.position;
            SafeSetDestination(destination);
            ResetMovementSpeed();

            // CurrentActionを更新
            CurrentAction = LLM.ActionType.EVACUATE;

            // SimulationMetricsにアクションを記録（フォールバック時も記録漏れを防ぐ）
            var metrics = SimulationMetrics.Instance ?? FindFirstObjectByType<SimulationMetrics>();
            if (metrics != null)
            {
                string shelterName = Target?.transform.parent?.name ?? Target?.name ?? "";
                metrics.RecordAction(
                    _uniqueId ?? gameObject.name,
                    LLM.ActionType.EVACUATE,
                    shelterName,
                    "最寄り避難所へフォールバック移動",
                    1.0f
                );
            }
            else
            {
                Debug.LogWarning($"[Evacuee] {gameObject.name}: SimulationMetricsが見つかりません。フォールバックアクション記録をスキップします。");
            }

            Debug.Log($"[Evacuee] {gameObject.name}: 目的地を設定しました - 座標: ({destination.x:F2}, {destination.y:F2}, {destination.z:F2}), 避難所: {Target.transform.parent?.name}, speed={NavAgent?.speed:F2}");
        }
    }

    private SelfStatePayload BuildSelfStatePayload()
    {
        var velocity = NavAgent != null ? NavAgent.velocity : Vector3.zero;
        var availableChoices = GetAvailableSpeedChoices();
        return new SelfStatePayload
        {
            position = new Vector3Payload(transform.position),
            velocity = new Vector3Payload(velocity),
            energy_level = Mathf.Clamp01(EnergyLevel),
            energy_label = EnergyState.ToString().ToLowerInvariant(),
            stress_level = Mathf.Clamp01(StressLevel),
            stress_label = StressState.ToString().ToLowerInvariant(),
            stress_reason = string.IsNullOrWhiteSpace(StressReason) ? null : StressReason,
            current_goal = ResolveCurrentGoalLabel(),
            stamina = Mathf.Clamp01(StaminaLevel),
            stamina_label = GetStaminaLabel(),
            current_speed_choice = CurrentSpeedChoice.ToString(),
            available_speed_choices = availableChoices.Select(c => SpeedChoiceNames[c]).ToArray(),
            injuries = InjuryTags?.ToArray() ?? Array.Empty<string>(),
            injury_notes = string.IsNullOrWhiteSpace(InjuryNotes) ? null : InjuryNotes
        };
    }

    private TemporalContextPayload BuildTemporalContextPayload()
    {
        if (_env == null)
        {
            return new TemporalContextPayload
            {
                elapsed_time = Time.time,
                has_time_limit = false,
                time_limit = 0f
            };
        }

        var hasManualLimit = _env.EnableTemporalOverrides && _env.ManualTimeLimitSeconds > 0f;
        return new TemporalContextPayload
        {
            elapsed_time = _env.CurrentTimeSec,
            has_time_limit = hasManualLimit,
            time_limit = hasManualLimit ? _env.ManualTimeLimitSeconds : 0f
        };
    }

    private string ResolveCurrentGoalLabel()
    {
        if (!string.IsNullOrWhiteSpace(GoalLabelOverride))
        {
            return GoalLabelOverride;
        }

        if (Target == null)
        {
            return null;
        }

        var parentName = Target.transform.parent != null ? Target.transform.parent.name : null;
        return parentName ?? Target.name;
    }

    private void ResetMovementSpeed()
    {
        if (NavAgent != null)
        {
            NavAgent.speed = DefaultSpeed;
        }
    }

    /// <summary>
    /// 高齢者かどうかを判定
    /// </summary>
    private bool IsElderly()
    {
        if (_persona == null) return false;
        return _persona.age_group.Contains("70s") ||
               _persona.age_group.Contains("80s") ||
               _persona.speed_multiplier < 0.7f;
    }

    /// <summary>
    /// 体力ラベルを取得
    /// </summary>
    private string GetStaminaLabel()
    {
        if (StaminaLevel >= 0.7f) return "十分";
        if (StaminaLevel >= 0.5f) return "普通";
        if (StaminaLevel >= 0.3f) return "低下";
        return "疲労";
    }

    /// <summary>
    /// 利用可能な速度選択肢を取得
    /// </summary>
    public List<LLM.SpeedChoice> GetAvailableSpeedChoices()
    {
        var choices = new List<LLM.SpeedChoice> { LLM.SpeedChoice.SLOW, LLM.SpeedChoice.NORMAL };
        if (StaminaLevel >= STAMINA_THRESHOLD_FAST)
        {
            choices.Add(LLM.SpeedChoice.FAST);
        }
        if (StaminaLevel >= STAMINA_THRESHOLD_RUN)
        {
            choices.Add(LLM.SpeedChoice.RUN);
        }
        return choices;
    }

    /// <summary>
    /// 体力を更新する（毎フレームUpdateから呼び出し）
    /// </summary>
    private void UpdateStamina()
    {
        float dt = Time.deltaTime;
        float drainMult = IsElderly() ? STAMINA_DRAIN_ELDERLY_MULT : 1.0f;

        switch (CurrentSpeedChoice)
        {
            case LLM.SpeedChoice.RUN:
                StaminaLevel -= STAMINA_DRAIN_RUN * drainMult * dt;
                break;
            case LLM.SpeedChoice.FAST:
                StaminaLevel -= STAMINA_DRAIN_FAST * drainMult * dt;
                break;
            case LLM.SpeedChoice.SLOW:
                StaminaLevel += STAMINA_RECOVERY_SLOW * dt;
                break;
            case LLM.SpeedChoice.NORMAL:
                // 変化なし
                break;
        }

        // 停止中は追加回復
        if (NavAgent != null && NavAgent.velocity.magnitude < 0.1f)
        {
            StaminaLevel += STAMINA_RECOVERY_STOP * dt;
        }

        // クランプ
        StaminaLevel = Mathf.Clamp01(StaminaLevel);

        // 体力不足時の自動ダウングレード
        if (CurrentSpeedChoice == LLM.SpeedChoice.RUN && StaminaLevel < STAMINA_THRESHOLD_RUN)
        {
            ApplySpeedChoice(LLM.SpeedChoice.FAST);
        }
        if (CurrentSpeedChoice == LLM.SpeedChoice.FAST && StaminaLevel < STAMINA_THRESHOLD_FAST)
        {
            ApplySpeedChoice(LLM.SpeedChoice.NORMAL);
        }
    }

    /// <summary>
    /// 速度選択肢を適用する
    /// </summary>
    public void ApplySpeedChoice(LLM.SpeedChoice choice)
    {
        // 体力チェック＆ダウングレード
        if (choice == LLM.SpeedChoice.RUN && StaminaLevel < STAMINA_THRESHOLD_RUN)
        {
            choice = LLM.SpeedChoice.FAST;
        }
        if (choice == LLM.SpeedChoice.FAST && StaminaLevel < STAMINA_THRESHOLD_FAST)
        {
            choice = LLM.SpeedChoice.NORMAL;
        }

        CurrentSpeedChoice = choice;

        if (NavAgent != null)
        {
            float personaMult = _persona?.speed_multiplier ?? 1.0f;
            NavAgent.speed = DefaultSpeed * personaMult * SpeedChoiceMultipliers[choice];
        }
    }

    /// <summary>
    /// LLMレスポンスから速度選択肢を適用（string -> SpeedChoice変換）
    /// </summary>
    private void ApplySpeedFromLLMResponse(string desiredSpeed)
    {
        if (string.IsNullOrEmpty(desiredSpeed))
        {
            ApplySpeedChoice(LLM.SpeedChoice.NORMAL);
            return;
        }

        var upperSpeed = desiredSpeed.ToUpperInvariant();
        LLM.SpeedChoice choice;

        if (upperSpeed == "SLOW" || desiredSpeed == "ゆっくり")
        {
            choice = LLM.SpeedChoice.SLOW;
        }
        else if (upperSpeed == "FAST" || desiredSpeed == "急ぎ足")
        {
            choice = LLM.SpeedChoice.FAST;
        }
        else if (upperSpeed == "RUN" || desiredSpeed == "走る")
        {
            choice = LLM.SpeedChoice.RUN;
        }
        else
        {
            choice = LLM.SpeedChoice.NORMAL;
        }

        ApplySpeedChoice(choice);
    }

    // 後方互換性のため残す（float版）
    private void ApplySpeedFromLLM(float desiredSpeed)
    {
        // 旧形式（float）が渡された場合は値に応じて選択肢を決定
        if (desiredSpeed <= 0f)
        {
            ApplySpeedChoice(LLM.SpeedChoice.NORMAL);
        }
        else if (desiredSpeed <= 3f)
        {
            ApplySpeedChoice(LLM.SpeedChoice.SLOW);
        }
        else if (desiredSpeed <= 6f)
        {
            ApplySpeedChoice(LLM.SpeedChoice.NORMAL);
        }
        else if (desiredSpeed <= 8f)
        {
            ApplySpeedChoice(LLM.SpeedChoice.FAST);
        }
        else
        {
            ApplySpeedChoice(LLM.SpeedChoice.RUN);
        }
    }

    private static bool _navMeshWarningShown = false; // 警告を一度だけ表示するためのフラグ
    
    /// <summary>
    /// NavMeshを使用して2点間の経路距離と徒歩所要時間を計算
    /// 注: endは既にNavMesh上の点に補正されていることを想定（Shelter.GetNavMeshPosition()で取得）
    /// </summary>
    /// <param name="start">開始位置</param>
    /// <param name="end">終了位置（NavMesh上の点）</param>
    /// <param name="distanceMeters">経路距離（メートル）</param>
    /// <param name="walkingTimeMinutes">徒歩所要時間（分）</param>
    private void CalculateNavMeshDistance(Vector3 start, Vector3 end, out float distanceMeters, out float walkingTimeMinutes)
    {
        // Googleマップのデフォルト徒歩速度: 4.8km/h = 80m/分
        const float WALKING_SPEED_M_PER_MIN = 80f;
        
        // 開始位置がNavMesh上にあることを確認（避難者は基本的にNavMesh上にいる）
        Vector3 navMeshStart = start;
        NavMeshHit startHit;
        if (NavMesh.SamplePosition(start, out startHit, 5f, NavMesh.AllAreas))
        {
            navMeshStart = startHit.position;
        }
        
        NavMeshPath path = new NavMeshPath();
        
        // 経路を計算（endは既にNavMesh上の点）
        if (NavMesh.CalculatePath(navMeshStart, end, NavMesh.AllAreas, path))
        {
            // 経路が完全か部分的に成功した場合
            if (path.status == NavMeshPathStatus.PathComplete || path.status == NavMeshPathStatus.PathPartial)
            {
                // 経路の総距離を計算
                distanceMeters = 0f;
                for (int i = 0; i < path.corners.Length - 1; i++)
                {
                    distanceMeters += Vector3.Distance(path.corners[i], path.corners[i + 1]);
                }
                
                // 徒歩所要時間を計算（分）
                walkingTimeMinutes = distanceMeters / WALKING_SPEED_M_PER_MIN;
                return; // 成功したので終了
            }
        }
        
        // フォールバック: 経路計算が失敗した場合は直線距離を使用
        distanceMeters = Vector3.Distance(start, end);
        walkingTimeMinutes = distanceMeters / WALKING_SPEED_M_PER_MIN;
        
        // 警告は初回のみ表示
        if (!_navMeshWarningShown)
        {
            Debug.LogWarning($"[Evacuee] NavMesh経路計算が失敗しました。直線距離を使用します（以降この警告は表示されません）");
            _navMeshWarningShown = true;
        }
    }

    /// <summary>
    /// 最寄りの避難所を検索
    /// </summary>
    private GameObject FindNearestShelter()
    {
        if (_env == null || _env.Shelters == null || _env.Shelters.Count == 0)
            return null;

        GameObject nearest = null;
        float nearestDistance = float.MaxValue;
        Vector3 currentPos = transform.position;

        foreach (var shelterObj in _env.Shelters)
        {
            if (shelterObj == null) continue;

            float distance = Vector3.Distance(currentPos, shelterObj.transform.position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = shelterObj;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 最寄りの避難者を検索（FOLLOW/TALKフォールバック用）
    /// </summary>
    private Evacuee FindNearestEvacuee()
    {
        if (_env == null || _env.Evacuees == null || _env.Evacuees.Count == 0)
            return null;

        Evacuee nearest = null;
        float nearestDistance = float.MaxValue;
        Vector3 currentPos = transform.position;

        foreach (var evacueeObj in _env.Evacuees)
        {
            if (evacueeObj == null || evacueeObj == this.gameObject || !evacueeObj.activeSelf)
                continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null)
                continue;

            float distance = Vector3.Distance(currentPos, evacueeObj.transform.position);

            // FOLLOW検索範囲内（30m）のみ対象
            if (distance <= FOLLOW_SEARCH_RADIUS && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = evacuee;
            }
        }

        return nearest;
    }

    /// <summary>
    /// IDで避難者を検索（FOLLOW/TALK用）
    /// EnvManagerのインデックスを使用した O(1) 検索
    /// </summary>
    private Evacuee FindEvacueeById(string targetId)
    {
        if (_env == null || string.IsNullOrEmpty(targetId))
            return null;

        var found = _env.GetEvacueeById(targetId);
        // 自分自身を除外
        return (found != null && found != this) ? found : null;
    }

    /// <summary>
    /// 名前で避難者を検索（FOLLOW/TALK用、部分一致対応）
    /// EnvManagerのインデックスを使用
    /// </summary>
    private Evacuee FindEvacueeByName(string targetName)
    {
        if (_env == null || string.IsNullOrEmpty(targetName))
            return null;

        var found = _env.GetEvacueeByNamePartial(targetName);
        // 自分自身を除外
        return (found != null && found != this) ? found : null;
    }

    /// <summary>
    /// 避難所の表示名を取得（displayName優先、なければname）
    /// </summary>
    private string GetShelterDisplayName(GameObject shelterObj)
    {
        if (shelterObj == null) return "";

        var shelterComp = shelterObj.GetComponent<Shelter>();
        if (shelterComp != null && !string.IsNullOrEmpty(shelterComp.displayName))
        {
            return shelterComp.displayName;
        }

        var areaComp = shelterObj.GetComponent<TsunamiEvacuationArea>();
        if (areaComp != null && !string.IsNullOrEmpty(areaComp.displayName))
        {
            return areaComp.displayName;
        }

        return shelterObj.name;
    }

    #region NavMeshAgent Safe Wrappers

    /// <summary>
    /// NavMeshAgentの状態を確認してから安全にSetDestinationを呼び出す
    /// </summary>
    /// <returns>SetDestinationが成功したかどうか</returns>
    private bool SafeSetDestination(Vector3 destination, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        string agentName = gameObject?.name ?? "Unknown";
        string destStr = $"({destination.x:F2}, {destination.y:F2}, {destination.z:F2})";
        string targetName = Target != null ? Target.name : "null";

        if (NavAgent == null)
        {
            Debug.LogWarning($"[SafeSetDestination] {agentName}: FAILED - NavAgent is null. " +
                           $"Destination={destStr}, Target={targetName}, Caller={callerName}");
            return false;
        }

        if (!gameObject.activeSelf)
        {
            Debug.LogWarning($"[SafeSetDestination] {agentName}: FAILED - GameObject is inactive. " +
                           $"Destination={destStr}, Target={targetName}, Caller={callerName}");
            return false;
        }

        if (!NavAgent.enabled)
        {
            Debug.LogWarning($"[SafeSetDestination] {agentName}: FAILED - NavAgent is disabled. " +
                           $"Destination={destStr}, Target={targetName}, Caller={callerName}");
            return false;
        }

        if (!NavAgent.isOnNavMesh)
        {
            Vector3 agentPos = transform.position;
            Debug.LogWarning($"[SafeSetDestination] {agentName}: FAILED - Agent is NOT on NavMesh. " +
                           $"AgentPos=({agentPos.x:F2}, {agentPos.y:F2}, {agentPos.z:F2}), " +
                           $"Destination={destStr}, Target={targetName}, Caller={callerName}");
            return false;
        }

        // SetDestinationを実行
        bool setDestResult = NavAgent.SetDestination(destination);

        // 経路計算の結果を確認（1フレーム後に確定するが、即時チェックも有用）
        StartCoroutine(CheckPathAfterSetDestination(agentName, destination, targetName, callerName));

        if (!setDestResult)
        {
            Debug.LogWarning($"[SafeSetDestination] {agentName}: SetDestination returned FALSE. " +
                           $"Destination={destStr}, Target={targetName}, Caller={callerName}");
        }

        return setDestResult;
    }

    /// <summary>
    /// SetDestination後に経路が正しく計算されたかを確認するコルーチン
    /// </summary>
    private System.Collections.IEnumerator CheckPathAfterSetDestination(string agentName, Vector3 destination, string targetName, string callerName)
    {
        // 1フレーム待機して経路計算を待つ
        yield return null;

        if (NavAgent == null || !gameObject.activeSelf) yield break;

        string destStr = $"({destination.x:F2}, {destination.y:F2}, {destination.z:F2})";

        // pathPendingがtrueの場合はさらに待機
        int waitFrames = 0;
        while (NavAgent.pathPending && waitFrames < 10)
        {
            yield return null;
            waitFrames++;
        }

    }

    /// <summary>
    /// TALK/CONTACTのデフォルト応答時に警告を出力する
    /// - 技術的問題（LLM利用不可、呼び出し失敗、例外）: 赤色警告
    /// - 相手が会話中/連絡中（正常な拒否応答）: 黄色警告
    /// </summary>
    /// <param name="actionType">行動タイプ（TALK/CONTACT）</param>
    /// <param name="reasoning">LLMからの理由フィールド</param>
    /// <param name="isBusyRejection">相手が会話中/連絡中のための拒否かどうか</param>
    private void LogDefaultResponseWarning(string actionType, string reasoning, bool isBusyRejection = false)
    {
        string agentName = gameObject?.name ?? "Unknown";

        if (isBusyRejection)
        {
            // 黄色警告: 相手が会話中/連絡中（正常な動作）
            Debug.LogWarning($"<color=yellow>[{actionType} DEFAULT] {agentName}: 相手が会話中/連絡中のためデフォルト応答</color>");
        }
        else
        {
            // 赤色警告: 技術的問題
            string reason = string.IsNullOrEmpty(reasoning) ? "不明" : reasoning;
            Debug.LogError($"<color=red>[{actionType} DEFAULT] {agentName}: デフォルト応答が返されました - 理由: {reason}</color>");
        }
    }

    /// <summary>
    /// NavMeshAgentの状態を確認してから安全にisStopped/Resumeを操作する
    /// </summary>
    /// <returns>操作が成功したかどうか</returns>
    private bool SafeStopAgent(bool stop, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        string agentName = gameObject?.name ?? "Unknown";
        string action = stop ? "STOP" : "RESUME";

        if (NavAgent == null)
        {
            Debug.LogWarning($"[SafeStopAgent] {agentName}: FAILED to {action} - NavAgent is null. Caller={callerName}");
            return false;
        }

        if (!gameObject.activeSelf)
        {
            Debug.LogWarning($"[SafeStopAgent] {agentName}: FAILED to {action} - GameObject is inactive. Caller={callerName}");
            return false;
        }

        if (!NavAgent.enabled)
        {
            Debug.LogWarning($"[SafeStopAgent] {agentName}: FAILED to {action} - NavAgent is disabled. Caller={callerName}");
            return false;
        }

        if (!NavAgent.isOnNavMesh)
        {
            Vector3 agentPos = transform.position;
            Debug.LogWarning($"[SafeStopAgent] {agentName}: FAILED to {action} - Agent is NOT on NavMesh. " +
                           $"AgentPos=({agentPos.x:F2}, {agentPos.y:F2}, {agentPos.z:F2}), Caller={callerName}");
            return false;
        }

        bool previousState = NavAgent.isStopped;
        NavAgent.isStopped = stop;

        return true;
    }

    /// <summary>
    /// NavMeshAgentの状態を確認してから安全にResetPathを操作する
    /// </summary>
    /// <returns>操作が成功したかどうか</returns>
    private bool SafeResetPath()
    {
        if (NavAgent == null || !gameObject.activeSelf || !NavAgent.enabled || !NavAgent.isOnNavMesh)
        {
            return false;
        }

        NavAgent.ResetPath();
        return true;
    }

    #endregion

#if UNITY_EDITOR
    /// <summary>
    /// シーンビューでFOLLOW関係を矢印で表示
    /// </summary>
    private void OnDrawGizmos()
    {
        // FOLLOW中で追従対象がいる場合のみ描画
        if (CurrentAction != LLM.ActionType.FOLLOW || _followTarget == null)
            return;

        if (!_followTarget.gameObject.activeSelf)
            return;

        Vector3 start = transform.position + Vector3.up * 2f; // 少し上に
        Vector3 end = _followTarget.transform.position + Vector3.up * 2f;

        Gizmos.color = Color.cyan; // FOLLOW関係は水色で表示

        // 線を描画
        Gizmos.DrawLine(start, end);

        // 矢印の先端を描画
        Vector3 direction = (end - start).normalized;
        Vector3 right = Quaternion.Euler(0, 30, 0) * -direction;
        Vector3 left = Quaternion.Euler(0, -30, 0) * -direction;

        float arrowHeadLength = 1.5f;
        Gizmos.DrawLine(end, end + right * arrowHeadLength);
        Gizmos.DrawLine(end, end + left * arrowHeadLength);
    }
#endif

}
