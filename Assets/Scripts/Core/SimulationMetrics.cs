using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using EvacSim.Decision.LLM;
using EvacSim.Evacuees;
using EvacSim.Shelters;

namespace EvacSim.Core
{
/// <summary>
/// シミュレーションの評価指標を計測・記録するクラス
/// 実験1（LLM vs ルールベース比較）のための指標収集
/// </summary>
public class SimulationMetrics : MonoBehaviour
{
    /// <summary>
    /// シングルトンインスタンス
    /// Evacueeなど他のコンポーネントからアクセスするために使用
    /// </summary>
    public static SimulationMetrics Instance { get; private set; }

    [Header("Settings")]
    [Tooltip("指標の記録を有効にする")]
    public bool EnableMetrics = true;

    [Tooltip("ログ出力先ディレクトリ（Assets/Logsからの相対パス）")]
    public string OutputDirectory = "experiment_results";

    [Tooltip("避難完了判定の制限時間（秒）- paper.mdでは30分=1800秒")]
    public float EvacuationTimeLimit = 1800f;

    private EnvManager _envManager;
    private string _experimentId;
    private float _episodeStartTime;
    private float _capturedElapsedTime;  // エピソード終了時にキャプチャした経過時間
    private bool _episodeInProgress = false;  // エピソード進行中フラグ
    private bool _partialLogsSaved = false;   // 部分ログ保存済みフラグ（二重保存防止）
    private string _capturedConditionId = "";  // エピソード開始時にキャプチャした条件ID（ログファイル名用）
    private string _capturedAgentType = "";   // エピソード開始時にキャプチャしたエージェントタイプ
    private bool _conditionCaptured = false;  // 条件ID・エージェントタイプがキャプチャ済みか

    // 次のエピソード用に事前設定された条件ID・エージェントタイプ・試行番号
    // ExperimentConfigから条件切り替え前に設定される
    private string _pendingConditionId = "";
    private string _pendingAgentType = "";
    private int _pendingTrialNumber = 1;  // 試行番号（1始まり）
    private int _capturedTrialNumber = 1;  // キャプチャした試行番号

    /// <summary>
    /// 現在の実験IDを取得（LLMサーバーとのログ統合用）
    /// </summary>
    public string ExperimentId => _experimentId;

    // エピソードごとの行動ログ
    private List<ActionLogEntry> _actionLogs = new List<ActionLogEntry>();

    // エピソードごとの避難完了記録
    private List<EvacuationRecord> _evacuationRecords = new List<EvacuationRecord>();

    // 行動タイプごとのカウント
    private Dictionary<ActionType, int> _actionCounts = new Dictionary<ActionType, int>();

    // 避難所ごとの選択カウント
    private Dictionary<string, int> _shelterSelectionCounts = new Dictionary<string, int>();

    // エージェント別の評価指標
    private Dictionary<string, AgentMetrics> _agentMetrics = new Dictionary<string, AgentMetrics>();

    // エージェント別のSTAY開始時刻（STAY継続時間計算用）
    private Dictionary<string, float> _stayStartTimes = new Dictionary<string, float>();

    // 交通関連メトリクス
    private List<TrafficSnapshot> _trafficSnapshots = new List<TrafficSnapshot>();
    private int _vehicleEvacueeCount;          // 車両避難者数
    private int _vehicleEvacueeCompleted;      // 車両避難完了数
    private int _vehicleAbandonedCount;        // 車両放棄数
    private Dictionary<string, float> _edgePeakCongestion = new Dictionary<string, float>(); // エッジごとの最大渋滞度

    /// <summary>
    /// 行動ログエントリ
    /// </summary>
    [Serializable]
    public class ActionLogEntry
    {
        public float timestamp;
        public string agentId;
        public string actionType;
        public string targetShelter;
        public string reasoning;
        public float confidence;
        // 階層的意思決定用フィールド
        public string primaryGoal;      // 長期目標
        public string planSteps;        // 中期計画のステップ（カンマ区切り）
        public bool goalUpdated;        // 目標更新フラグ
        public bool planUpdated;        // 計画更新フラグ
    }

    /// <summary>
    /// 交通状態のスナップショット（時系列記録用）
    /// </summary>
    [Serializable]
    public class TrafficSnapshot
    {
        public float timestamp;
        public float averageCongestion;         // 全体の平均渋滞度 (0-1)
        public int activeVehicleCount;          // アクティブな車両数
        public int congestedEdgeCount;          // 渋滞エッジ数（レベル3以上）
        public string worstBottleneck;          // 最悪のボトルネック道路名
        public int worstCongestionLevel;        // 最悪の渋滞レベル
    }

    /// <summary>
    /// エージェント別の評価指標
    /// </summary>
    [Serializable]
    public class AgentMetrics
    {
        public string agentId;
        public string personaName;
        public string mentalState;          // バイアス条件（mental_state）
        public float firstEvacuateTime;     // 最初のEVACUATE時刻（-1なら未避難）
        public float totalStayDuration;     // STAY継続時間合計
        public int followCount;             // FOLLOW選択回数
        public int talkCount;               // TALK選択回数
        public int contactCount;            // CONTACT選択回数
        public int searchFamilyCount;       // SEARCH_FAMILY選択回数
        public int goalUpdateCount;         // 長期目標更新回数
        public int planUpdateCount;         // 中期計画更新回数
        public bool evacuationCompleted;    // 避難完了したか
        public float evacuationTime;        // 避難完了時間（-1なら未完了）
        public string finalShelter;         // 到達した避難所
        // 交通シミュレーション用フィールド
        public string transportMode;        // 移動手段: "WALKING" or "DRIVING"
        public bool vehicleAbandoned;       // 車両を放棄したか
        public float vehicleAbandonTime;    // 車両放棄時刻（-1なら未放棄）
        // 実験3: 生存判定用フィールド
        public bool survived;               // 津波到達時に生存したか
        public float finalElevation;        // 最終地点の海抜（m）
        public string survivalLocation;     // 生存判定時の場所（避難所名 or "field"）
    }

    /// <summary>
    /// 避難完了記録
    /// </summary>
    [Serializable]
    public class EvacuationRecord
    {
        public string agentId;
        public float evacuationTime;  // 発災からの経過時間（秒）
        public bool completedInTime;  // 制限時間内に完了したか
        public string finalShelter;   // 到達した避難所
        public string agentType;      // "LLM" or "RuleBased"
    }

    /// <summary>
    /// エピソードサマリ
    /// </summary>
    [Serializable]
    public class EpisodeSummary
    {
        public int episodeId;       // グローバルエピソードID
        public int trialNumber;     // 条件内での試行番号（1始まり）
        public string conditionId;  // 条件ID
        public string agentType;
        public float evacuationRate;
        public float averageEvacuationTime;
        public float medianEvacuationTime;
        public float maxEvacuationTime;
        public int totalAgents;
        public int evacuatedAgents;
        public Dictionary<string, float> actionDistribution;
        public Dictionary<string, int> shelterDistribution;
        // 場所タイプ別避難率
        public float evacuationRateToShelter;    // 避難所への避難完了率
        public float evacuationRateToArea;       // 津波避難地域への避難完了率
        public Dictionary<string, int> evacuationCountByLocation;  // 実際の避難完了場所別カウント
        // 実験3: 生存判定結果
        public float survivalRate;          // 生存率
        public int survivedAgents;          // 生存したエージェント数
        public float tsunamiHeight;         // 判定に使用した津波高さ（m）
        public string informationStrategy;  // 情報提供戦略
    }

    void Awake()
    {
        // シングルトンパターン
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SimulationMetrics] 複数のインスタンスが存在します。このインスタンスを破棄します。");
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _envManager = FindFirstObjectByType<EnvManager>();
        if (_envManager == null)
        {
            Debug.LogError("[SimulationMetrics] EnvManagerが見つかりません");
            enabled = false;
            return;
        }

        // イベントハンドラを登録
        _envManager.OnStartEpisode += OnEpisodeStart;
        _envManager.OnEndEpisode += OnEpisodeEnd;

        InitializeActionCounts();
    }

    void Start()
    {
        // EnvManagerのrecordIDを実験IDとして使用（EnvManager.Start()で生成される）
        // 形式を統一: yyyy_MM_dd-HH_mm_ss → yyyyMMdd_HHmmss
        if (_envManager != null && !string.IsNullOrEmpty(_envManager.recordID))
        {
            _experimentId = _envManager.recordID.Replace("_", "").Replace("-", "_");
        }
        else
        {
            _experimentId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }
        Debug.Log($"[SimulationMetrics] 実験ID: {_experimentId}");
    }

    void OnDestroy()
    {
        // 途中停止時に部分ログを保存
        SavePartialLogsIfNeeded();

        if (_envManager != null)
        {
            _envManager.OnStartEpisode -= OnEpisodeStart;
            _envManager.OnEndEpisode -= OnEpisodeEnd;
        }
    }

    /// <summary>
    /// アプリケーション終了時に部分ログを保存
    /// </summary>
    void OnApplicationQuit()
    {
        SavePartialLogsIfNeeded();
    }

    /// <summary>
    /// エピソード進行中に停止された場合、部分ログを保存
    /// </summary>
    private void SavePartialLogsIfNeeded()
    {
        if (!EnableMetrics) return;
        if (!_episodeInProgress) return;
        if (_partialLogsSaved) return;  // 二重保存を防止

        _partialLogsSaved = true;

        Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} が途中で停止されました - 部分ログを保存します");

        // 避難完了記録を収集
        CollectEvacuationRecords();

        // 未完了エージェントのSTAY継続時間を確定
        FinalizeAgentMetrics();

        // サマリを生成（部分データであることをマーク）
        var summary = GenerateEpisodeSummary();

        // 部分ログを出力（ファイル名に_PARTIALを付与）
        SaveEpisodeLogs(isPartial: true);
        SaveEpisodeSummary(summary, isPartial: true);
        SaveAgentMetrics(isPartial: true);

        Debug.Log($"[SimulationMetrics] 部分ログを保存完了 - " +
                  $"エピソード: {_envManager.currentEpisodeId}, " +
                  $"経過時間: {_envManager.CurrentTimeSec:F1}秒, " +
                  $"避難完了率: {summary.evacuationRate:P1}");
    }

    private void InitializeActionCounts()
    {
        _actionCounts.Clear();
        foreach (ActionType action in Enum.GetValues(typeof(ActionType)))
        {
            _actionCounts[action] = 0;
        }
    }

    private void OnEpisodeStart()
    {
        if (!EnableMetrics) return;

        _episodeStartTime = Time.time;
        _episodeInProgress = true;

        // ★重要: _pendingConditionIdを最優先でチェック
        // ExperimentConfig.OnConditionComplete()で次のエピソード用に事前設定された条件IDがあれば、それを使用
        // これにより、条件切り替え後にOnEpisodeStartが呼ばれても正しい条件IDが使われる
        string conditionIdToUse = "";
        string agentTypeToUse = "";
        int trialNumberToUse = 1;
        bool usePendingCondition = false;

        if (!string.IsNullOrEmpty(_pendingConditionId))
        {
            // 事前設定された条件IDを使用
            conditionIdToUse = _pendingConditionId;
            agentTypeToUse = _pendingAgentType;
            trialNumberToUse = _pendingTrialNumber;
            usePendingCondition = true;
            // ★注意: ここではクリアしない
            // 実際にキャプチャが行われる分岐内でクリアする
            // （OnEpisodeStartが2回呼ばれる場合、1回目でクリアすると2回目で現在値が使われてしまう）
        }
        else
        {
            // 事前設定がない場合は現在値を使用
            conditionIdToUse = CaptureCurrentConditionId();
            agentTypeToUse = CaptureCurrentAgentType();
            trialNumberToUse = CaptureCurrentTrialNumber();
        }

        // ★重要: クリア処理はFinalizeAndSaveEpisodeLogs()でのみ行う
        // OnEpisodeStartでは、FinalizeAndSaveEpisodeLogs()でクリア済み（_fullLogsSaved=true）の場合のみ
        // フラグをリセットしてエージェントメトリクスを初期化する
        if (_fullLogsSaved)
        {
            // 前のエピソードのログ保存が完了している場合のみフラグをリセット
            _partialLogsSaved = false;
            _fullLogsSaved = false;
            _conditionCaptured = false;  // キャプチャフラグもリセット

            // ★条件ID・エージェントタイプ・試行番号を設定（上で決定した値を使用）
            _capturedConditionId = conditionIdToUse;
            _capturedAgentType = agentTypeToUse;
            _capturedTrialNumber = trialNumberToUse;
            _conditionCaptured = true;

            // ★実際にキャプチャしたので、_pendingConditionIdをクリア
            if (usePendingCondition)
            {
                _pendingConditionId = "";
                _pendingAgentType = "";
                _pendingTrialNumber = 1;
            }

            // エージェント別メトリクスを初期化（Evacueeリストが更新されている可能性があるため）
            InitializeAgentMetrics();

            Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} 開始 - conditionId={_capturedConditionId}, trial={_capturedTrialNumber}（{(usePendingCondition ? "事前設定値" : "現在値")}）");
        }
        else if (_evacuationRecords.Count == 0 && !_conditionCaptured)
        {
            // 初回エピソード（まだログ保存が一度も行われていない）かつ未キャプチャ
            _partialLogsSaved = false;
            _fullLogsSaved = false;

            // ★条件ID・エージェントタイプ・試行番号を設定（上で決定した値を使用）
            // 初回は_pendingConditionIdが設定されていないはずなので、通常はconditionIdToUseは現在値
            _capturedConditionId = conditionIdToUse;
            _capturedAgentType = agentTypeToUse;
            _capturedTrialNumber = trialNumberToUse;
            _conditionCaptured = true;

            // ★実際にキャプチャしたので、_pendingConditionIdをクリア
            if (usePendingCondition)
            {
                _pendingConditionId = "";
                _pendingAgentType = "";
                _pendingTrialNumber = 1;
            }

            _actionLogs.Clear();
            InitializeActionCounts();
            _shelterSelectionCounts.Clear();
            _agentMetrics.Clear();
            _stayStartTimes.Clear();

            // エージェント別メトリクスを初期化
            InitializeAgentMetrics();

            Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} 開始（初回） - conditionId={_capturedConditionId}, trial={_capturedTrialNumber}（{(usePendingCondition ? "事前設定値" : "現在値")}）");
        }
        else if (_evacuationRecords.Count == 0 && _conditionCaptured)
        {
            // 既にキャプチャ済み（OnEpisodeStartが2回呼ばれた場合）
            // ただし、_pendingConditionIdが設定されている場合は新しい条件なのでキャプチャし直す
            if (usePendingCondition && _capturedConditionId != conditionIdToUse)
            {
                _capturedConditionId = conditionIdToUse;
                _capturedAgentType = agentTypeToUse;
                _capturedTrialNumber = trialNumberToUse;
                _pendingConditionId = "";
                _pendingAgentType = "";
                _pendingTrialNumber = 1;
                Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} - 新しい条件をキャプチャ（conditionId={_capturedConditionId}, trial={_capturedTrialNumber}）");
            }
            else
            {
                Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} - 既にキャプチャ済み（conditionId={_capturedConditionId}, trial={_capturedTrialNumber}）");
            }
        }
        else if (_evacuationRecords.Count > 0 && usePendingCondition)
        {
            // 前のエピソードのレコードが残っているが、新しい条件が事前設定されている
            // ★重要: ログ保存が完了していない場合は、現在のエピソードのデータを守る必要がある
            // _fullLogsSaved=falseの場合はキャプチャしない（前のエピソードのログ保存を待つ）
            // この状態は、OnConditionComplete()でFinalizeMLAgentEpisode()が呼ばれた時に発生する
            // FinalizeAndSaveEpisodeLogs()はOnTrialEnd()内で先に呼ばれているはずなので、
            // ここに到達するということはまだ保存が完了していない
            Debug.LogWarning($"[SimulationMetrics] OnEpisodeStart: レコード残存({_evacuationRecords.Count})、ログ保存待ち - _pendingConditionIdは保持（{conditionIdToUse}）");
            // ★_pendingConditionIdはクリアしない（次のOnEpisodeStart()で使用される）
        }
        else
        {
            // 前のエピソードのログ保存がまだ完了していない
            // ★ここではキャプチャしない（前のエピソードの値を保持）
            Debug.LogWarning($"[SimulationMetrics] OnEpisodeStart: 前エピソードのログ保存待ち（_evacuationRecords.Count={_evacuationRecords.Count}）");
        }
    }

    /// <summary>
    /// 現在の条件IDをキャプチャする（OnEpisodeEndで呼び出し）
    /// </summary>
    private string CaptureCurrentConditionId()
    {
        if (ExperimentConfig.Instance != null && ExperimentConfig.Instance.IsBatchMode)
        {
            string conditionId = ExperimentConfig.Instance.ExperimentName;
            if (!string.IsNullOrEmpty(conditionId))
            {
                return conditionId;
            }
        }
        return "";
    }

    /// <summary>
    /// 現在のエージェントタイプをキャプチャする（OnEpisodeEndで呼び出し）
    /// </summary>
    private string CaptureCurrentAgentType()
    {
        return ExperimentConfig.GetAgentType().ToString();
    }

    /// <summary>
    /// 現在の試行番号をキャプチャする
    /// </summary>
    private int CaptureCurrentTrialNumber()
    {
        if (ExperimentConfig.Instance != null)
        {
            return ExperimentConfig.Instance.CurrentTrialNumber;
        }
        return 1;
    }

    /// <summary>
    /// 次のエピソード用に条件ID・エージェントタイプ・試行番号を事前設定する
    /// ExperimentConfigから条件切り替え前に呼び出される
    /// これにより、OnEpisodeStart時点で条件が既に切り替わっていても正しい値を使用できる
    /// </summary>
    /// <param name="conditionId">次の条件ID</param>
    /// <param name="agentType">次のエージェントタイプ</param>
    /// <param name="trialNumber">試行番号（1始まり、省略時は1）</param>
    public void SetPendingCondition(string conditionId, string agentType, int trialNumber = 1)
    {
        _pendingConditionId = conditionId;
        _pendingAgentType = agentType;
        _pendingTrialNumber = trialNumber;
    }

    private void InitializeAgentMetrics()
    {
        Debug.Log($"[SimulationMetrics] InitializeAgentMetrics: Evacuees count = {_envManager.Evacuees.Count}");

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null) continue;

            // EvacueeIdを使用（RecordAction/RecordEvacuationと同じIDを使用して一致させる）
            string agentId = evacuee.EvacueeId;

            Debug.Log($"[SimulationMetrics] InitializeAgentMetrics: agentId={agentId}, objName={evacueeObj.name}");

            // ペルソナ情報を取得（EvacueeIdからintに変換）
            PersonaData persona = null;
            if (int.TryParse(agentId, out int personaId))
            {
                persona = PersonaManager.GetPersona(personaId);
            }

            var metrics = new AgentMetrics
            {
                agentId = agentId,
                personaName = persona?.name ?? evacuee.PersonaName ?? "Unknown",
                mentalState = persona?.mental_state ?? "",
                firstEvacuateTime = -1f,
                totalStayDuration = 0f,
                followCount = 0,
                talkCount = 0,
                contactCount = 0,
                searchFamilyCount = 0,
                goalUpdateCount = 0,
                planUpdateCount = 0,
                evacuationCompleted = false,
                evacuationTime = -1f,
                finalShelter = ""
            };

            _agentMetrics[agentId] = metrics;
        }

        Debug.Log($"[SimulationMetrics] InitializeAgentMetrics complete: _agentMetrics count = {_agentMetrics.Count}");
    }

    // 完全ログ保存済みフラグ（二重保存防止）
    private bool _fullLogsSaved = false;

    private void OnEpisodeEnd(float evacuationRate, float elapsedTime)
    {
        // ★経過時間のみキャプチャ（条件ID・エージェントタイプはOnEpisodeStartでキャプチャ済み）
        _capturedElapsedTime = elapsedTime;

        // ★重要: ExperimentConfigがある場合はOnTrialEnd()からFinalizeAndSaveEpisodeLogsが呼ばれる
        // そちらで条件切り替え前にログ保存が行われるため、ここでは呼ばない
        // ExperimentConfigがない場合（非バッチモード等）のみフォールバックとして呼び出す
        if (ExperimentConfig.Instance == null)
        {
            FinalizeAndSaveEpisodeLogs(elapsedTime);
        }
        else
        {
            Debug.Log($"[SimulationMetrics] OnEpisodeEnd: ExperimentConfigが存在するため、OnTrialEnd()でのログ保存を待機");
        }
    }

    /// <summary>
    /// エピソード終了時のログ保存を実行する
    /// ExperimentConfig.OnTrialEnd内でOnConditionComplete前に明示的に呼び出される
    /// または、OnEpisodeEndイベントからフォールバックとして呼び出される
    /// 二重保存防止のため、1エピソードにつき1回のみ実行される
    /// </summary>
    /// <param name="elapsedTime">エピソードの経過時間</param>
    public void FinalizeAndSaveEpisodeLogs(float elapsedTime)
    {
        if (!EnableMetrics) return;

        // 既に完全ログが保存されている場合はスキップ（二重保存防止）
        if (_fullLogsSaved)
        {
            return;
        }

        // ★条件ID・エージェントタイプはOnEpisodeStartでキャプチャ済み
        // ここでは経過時間のみキャプチャ
        _episodeInProgress = false;  // エピソード終了をマーク
        _capturedElapsedTime = elapsedTime;  // Dispose()でリセットされる前にキャプチャされた経過時間を保存

        // 既に部分ログが保存されている場合はスキップ（二重保存防止）
        if (_partialLogsSaved)
        {
            Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} - 部分ログが既に保存済みのためスキップ");
            return;
        }

        // 避難完了記録を収集
        CollectEvacuationRecords();

        // 未完了エージェントのSTAY継続時間を確定
        FinalizeAgentMetrics();

        // 実験3: 生存判定を実行
        EvaluateSurvival();

        // サマリを生成
        var summary = GenerateEpisodeSummary();

        // ログを出力（完全なログ）
        SaveEpisodeLogs(isPartial: false);
        SaveEpisodeSummary(summary, isPartial: false);
        SaveAgentMetrics(isPartial: false);

        // 完全ログ保存済みフラグを立てる
        _fullLogsSaved = true;

        Debug.Log($"[SimulationMetrics] エピソード {_envManager.currentEpisodeId} 終了 - " +
                  $"避難完了率: {summary.evacuationRate:P1}, " +
                  $"生存率: {summary.survivalRate:P1}, " +
                  $"平均避難時間: {summary.averageEvacuationTime:F1}秒 (条件: {_capturedConditionId})");

        // ★ログ保存完了後、次のエピソードのためにレコードをクリア
        // OnEpisodeStartより先にFinalizeAndSaveEpisodeLogsが呼ばれる保証がないため、
        // ここで明示的にクリアして次のエピソードに備える
        _actionLogs.Clear();
        _evacuationRecords.Clear();
        InitializeActionCounts();
        _shelterSelectionCounts.Clear();
        _agentMetrics.Clear();
        _stayStartTimes.Clear();
    }

    /// <summary>
    /// エージェント別メトリクスを確定（エピソード終了時）
    /// </summary>
    private void FinalizeAgentMetrics()
    {
        float endTime = _envManager.CurrentTimeSec;

        foreach (var kvp in _agentMetrics)
        {
            var metrics = kvp.Value;

            // 残っているSTAY継続時間があれば加算
            if (_stayStartTimes.TryGetValue(kvp.Key, out float stayStart))
            {
                metrics.totalStayDuration += endTime - stayStart;
            }
        }

        _stayStartTimes.Clear();
    }

    /// <summary>
    /// 全エージェントの生存判定を実行（実験3用）
    /// 判定基準: エージェントの現在位置の海抜 ≥ 津波高さ × 2
    /// </summary>
    private void EvaluateSurvival()
    {
        // ExperimentConfigから津波高さを取得
        float tsunamiHeight = 8f;  // デフォルト値
        if (ExperimentConfig.Instance != null)
        {
            tsunamiHeight = ExperimentConfig.GetTsunamiHeight();
        }

        float safeElevation = tsunamiHeight * 2f;  // 安全ライン

        Debug.Log($"[SimulationMetrics] 生存判定開始 - 津波高さ: {tsunamiHeight}m, 安全ライン: {safeElevation}m以上");

        int survivedCount = 0;
        int totalCount = 0;

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null) continue;

            // EvacueeIdを使用（InitializeAgentMetricsと同じIDを使用して一致させる）
            string agentId = evacuee.EvacueeId;
            totalCount++;

            if (!_agentMetrics.TryGetValue(agentId, out var metrics))
            {
                continue;
            }

            // エージェントの最終位置の海抜を判定
            float currentElevation = 0f;
            string survivalLocation = "field";

            if (metrics.evacuationCompleted && !string.IsNullOrEmpty(metrics.finalShelter))
            {
                // 避難所に到達している場合、避難所の海抜を使用
                currentElevation = GetShelterElevation(metrics.finalShelter);
                survivalLocation = metrics.finalShelter;
            }
            else
            {
                // 避難所に到達していない場合、現在位置のY座標を使用
                // （簡易的に地形の高さとして扱う）
                currentElevation = evacueeObj.transform.position.y;
                survivalLocation = "field";
            }

            // 生存判定: 海抜 ≥ 津波高さ × 2
            bool survived = currentElevation >= safeElevation;

            metrics.survived = survived;
            metrics.finalElevation = currentElevation;
            metrics.survivalLocation = survivalLocation;

            if (survived)
            {
                survivedCount++;
            }

            Debug.Log($"[SimulationMetrics] {agentId}: 海抜{currentElevation:F1}m @ {survivalLocation} → {(survived ? "生存" : "死亡")}");
        }

        float survivalRate = totalCount > 0 ? (float)survivedCount / totalCount : 0f;
        Debug.Log($"[SimulationMetrics] 生存判定完了 - 生存率: {survivalRate:P1} ({survivedCount}/{totalCount})");
    }

    /// <summary>
    /// 避難所の海抜を取得
    /// </summary>
    private float GetShelterElevation(string shelterName)
    {
        // 避難所（Shelter）から検索
        if (_envManager.Shelters != null)
        {
            foreach (var shelterObj in _envManager.Shelters)
            {
                if (shelterObj == null) continue;

                var shelter = shelterObj.GetComponent<Shelter>();
                if (shelter == null) continue;

                string displayName = !string.IsNullOrEmpty(shelter.displayName)
                    ? shelter.displayName
                    : shelterObj.name;

                if (displayName == shelterName || shelterObj.name == shelterName)
                {
                    return shelter.GetElevation();
                }
            }
        }

        // 津波避難地域（TsunamiEvacuationArea）から検索
        if (_envManager.TsunamiEvacuationAreas != null)
        {
            foreach (var areaObj in _envManager.TsunamiEvacuationAreas)
            {
                if (areaObj == null) continue;

                var area = areaObj.GetComponent<TsunamiEvacuationArea>();
                if (area == null) continue;

                string displayName = !string.IsNullOrEmpty(area.displayName)
                    ? area.displayName
                    : areaObj.name;

                if (displayName == shelterName || areaObj.name == shelterName)
                {
                    return area.elevationMeters;
                }
            }
        }

        // 見つからない場合はデフォルト値（0m）を返す
        Debug.LogWarning($"[SimulationMetrics] 避難所 '{shelterName}' の海抜情報が見つかりません");
        return 0f;
    }

    /// <summary>
    /// 全避難所の名前を取得
    /// </summary>
    private HashSet<string> GetShelterNames()
    {
        var names = new HashSet<string>();
        if (_envManager.Shelters != null)
        {
            foreach (var obj in _envManager.Shelters)
            {
                if (obj == null) continue;
                var shelter = obj.GetComponent<Shelter>();
                if (shelter != null)
                    names.Add(!string.IsNullOrEmpty(shelter.displayName) ? shelter.displayName : obj.name);
            }
        }
        return names;
    }

    /// <summary>
    /// 全津波避難地域の名前を取得
    /// </summary>
    private HashSet<string> GetAreaNames()
    {
        var names = new HashSet<string>();
        if (_envManager.TsunamiEvacuationAreas != null)
        {
            foreach (var obj in _envManager.TsunamiEvacuationAreas)
            {
                if (obj == null) continue;
                var area = obj.GetComponent<TsunamiEvacuationArea>();
                if (area != null)
                    names.Add(!string.IsNullOrEmpty(area.displayName) ? area.displayName : obj.name);
            }
        }
        return names;
    }

    /// <summary>
    /// エージェントの行動を記録（Evacueeから呼び出される）
    /// </summary>
    public void RecordAction(string agentId, ActionType actionType, string targetShelter = null,
                             string reasoning = null, float confidence = 0f)
    {
        RecordActionWithHierarchy(agentId, actionType, targetShelter, reasoning, confidence, null, null, false, false);
    }

    /// <summary>
    /// エージェントの行動を記録（階層的意思決定情報を含む拡張版）
    /// </summary>
    public void RecordActionWithHierarchy(string agentId, ActionType actionType, string targetShelter,
                                          string reasoning, float confidence,
                                          string primaryGoal, string[] planSteps,
                                          bool goalUpdated, bool planUpdated)
    {
        if (!EnableMetrics) return;

        float currentTime = _envManager.CurrentTimeSec;

        var entry = new ActionLogEntry
        {
            timestamp = currentTime,
            agentId = agentId,
            actionType = actionType.ToString(),
            targetShelter = targetShelter ?? "",
            reasoning = reasoning ?? "",
            confidence = confidence,
            primaryGoal = primaryGoal ?? "",
            planSteps = planSteps != null ? string.Join("|", planSteps) : "",
            goalUpdated = goalUpdated,
            planUpdated = planUpdated
        };

        _actionLogs.Add(entry);

        // 行動カウントを更新
        if (_actionCounts.ContainsKey(actionType))
        {
            _actionCounts[actionType]++;
        }

        // 避難所選択カウントを更新
        if (actionType == ActionType.EVACUATE && !string.IsNullOrEmpty(targetShelter))
        {
            if (!_shelterSelectionCounts.ContainsKey(targetShelter))
            {
                _shelterSelectionCounts[targetShelter] = 0;
            }
            _shelterSelectionCounts[targetShelter]++;
        }

        // エージェント別メトリクスを更新
        UpdateAgentMetrics(agentId, actionType, currentTime, goalUpdated, planUpdated);
    }

    /// <summary>
    /// エージェント別メトリクスを更新
    /// </summary>
    private void UpdateAgentMetrics(string agentId, ActionType actionType, float currentTime, bool goalUpdated, bool planUpdated)
    {
        if (!_agentMetrics.TryGetValue(agentId, out var metrics))
        {
            // まだ初期化されていないエージェントの場合は新規作成
            metrics = new AgentMetrics
            {
                agentId = agentId,
                personaName = "Unknown",
                mentalState = "",
                firstEvacuateTime = -1f,
                totalStayDuration = 0f,
                followCount = 0,
                talkCount = 0,
                contactCount = 0,
                searchFamilyCount = 0,
                goalUpdateCount = 0,
                planUpdateCount = 0,
                evacuationCompleted = false,
                evacuationTime = -1f,
                finalShelter = ""
            };
            _agentMetrics[agentId] = metrics;
        }

        // STAYからの遷移時にSTAY継続時間を加算
        if (_stayStartTimes.TryGetValue(agentId, out float stayStart) && actionType != ActionType.STAY)
        {
            metrics.totalStayDuration += currentTime - stayStart;
            _stayStartTimes.Remove(agentId);
        }

        // 行動タイプ別の処理
        switch (actionType)
        {
            case ActionType.EVACUATE:
                if (metrics.firstEvacuateTime < 0)
                {
                    metrics.firstEvacuateTime = currentTime;
                }
                break;
            case ActionType.STAY:
                if (!_stayStartTimes.ContainsKey(agentId))
                {
                    _stayStartTimes[agentId] = currentTime;
                }
                break;
            case ActionType.FOLLOW:
                metrics.followCount++;
                break;
            case ActionType.TALK:
                metrics.talkCount++;
                break;
            case ActionType.CONTACT:
                metrics.contactCount++;
                break;
            case ActionType.SEARCH_FAMILY:
                metrics.searchFamilyCount++;
                break;
        }

        // 目標・計画更新カウント
        if (goalUpdated) metrics.goalUpdateCount++;
        if (planUpdated) metrics.planUpdateCount++;
    }

    /// <summary>
    /// エージェントの避難完了を記録（Shelterから呼び出される）
    /// </summary>
    public void RecordEvacuation(string agentId, float evacuationTime, string shelterName, string agentType)
    {
        if (!EnableMetrics) return;

        var record = new EvacuationRecord
        {
            agentId = agentId,
            evacuationTime = evacuationTime,
            completedInTime = true,  // RecordEvacuationは避難成功時（SetActive(false)直前）にのみ呼ばれるため常にtrue
            finalShelter = shelterName,
            agentType = agentType
        };

        _evacuationRecords.Add(record);

        // エージェント別メトリクスを更新
        if (_agentMetrics.TryGetValue(agentId, out var metrics))
        {
            metrics.evacuationCompleted = true;
            metrics.evacuationTime = evacuationTime;
            metrics.finalShelter = shelterName;

            // 残っているSTAY継続時間があれば加算
            if (_stayStartTimes.TryGetValue(agentId, out float stayStart))
            {
                metrics.totalStayDuration += evacuationTime - stayStart;
                _stayStartTimes.Remove(agentId);
            }
        }
    }

    private void CollectEvacuationRecords()
    {
        // 既に記録されていない避難者について、最終状態を収集
        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null) continue;

            // EvacueeIdを使用（RecordEvacuationと同じIDを使用して一致させる）
            string agentId = evacuee.EvacueeId;

            // 既に記録済みかチェック
            if (_evacuationRecords.Any(r => r.agentId == agentId)) continue;

            // 避難完了していない場合は記録
            if (evacueeObj.activeSelf)
            {
                var record = new EvacuationRecord
                {
                    agentId = agentId,
                    evacuationTime = _envManager.CurrentTimeSec,
                    completedInTime = false,
                    finalShelter = "",
                    agentType = evacuee.UseLLMDecision ? "LLM" : "RuleBased"
                };
                _evacuationRecords.Add(record);
            }
        }
    }

    private EpisodeSummary GenerateEpisodeSummary()
    {
        var completedRecords = _evacuationRecords.Where(r => r.completedInTime).ToList();

        var evacuationTimes = completedRecords.Select(r => r.evacuationTime).ToList();

        // 行動分布を計算
        int totalActions = _actionCounts.Values.Sum();
        var actionDistribution = new Dictionary<string, float>();
        foreach (var kvp in _actionCounts)
        {
            actionDistribution[kvp.Key.ToString()] = totalActions > 0
                ? (float)kvp.Value / totalActions
                : 0f;
        }

        // 場所タイプ別の避難完了数をカウント
        var shelterNames = GetShelterNames();
        var areaNames = GetAreaNames();

        int evacuatedToShelter = 0;
        int evacuatedToArea = 0;
        var evacuationCountByLocation = new Dictionary<string, int>();

        foreach (var record in completedRecords)
        {
            string location = record.finalShelter;
            if (string.IsNullOrEmpty(location)) continue;

            // 場所別カウント
            if (!evacuationCountByLocation.ContainsKey(location))
                evacuationCountByLocation[location] = 0;
            evacuationCountByLocation[location]++;

            // タイプ別カウント
            if (shelterNames.Contains(location))
                evacuatedToShelter++;
            else if (areaNames.Contains(location))
                evacuatedToArea++;
        }

        int totalAgents = _envManager.Evacuees.Count;
        float evacuationRateToShelter = totalAgents > 0 ? (float)evacuatedToShelter / totalAgents : 0f;
        float evacuationRateToArea = totalAgents > 0 ? (float)evacuatedToArea / totalAgents : 0f;

        // 実験3: 生存率を計算
        int survivedCount = _agentMetrics.Values.Count(m => m.survived);
        int totalAgentsCount = _agentMetrics.Count;
        float survivalRate = totalAgentsCount > 0 ? (float)survivedCount / totalAgentsCount : 0f;

        // ExperimentConfigから実験3の設定を取得
        float tsunamiHeight = 8f;
        string informationStrategy = "standard";
        if (ExperimentConfig.Instance != null)
        {
            tsunamiHeight = ExperimentConfig.GetTsunamiHeight();
            informationStrategy = ExperimentConfig.GetInformationStrategy() switch
            {
                ExperimentConfig.InformationStrategy.Standard => "standard",
                ExperimentConfig.InformationStrategy.Urgent => "urgent",
                ExperimentConfig.InformationStrategy.Detailed => "detailed",
                ExperimentConfig.InformationStrategy.DetailedUrgent => "detailed_urgent",
                _ => "standard"
            };
        }

        return new EpisodeSummary
        {
            episodeId = _envManager.currentEpisodeId,
            trialNumber = _capturedTrialNumber,
            conditionId = _capturedConditionId,
            agentType = _capturedAgentType,  // OnEpisodeEndでキャプチャした値を使用
            evacuationRate = _envManager.EvacuationRate,
            averageEvacuationTime = evacuationTimes.Count > 0 ? evacuationTimes.Average() : 0f,
            medianEvacuationTime = evacuationTimes.Count > 0 ? GetMedian(evacuationTimes) : 0f,
            maxEvacuationTime = evacuationTimes.Count > 0 ? evacuationTimes.Max() : 0f,
            totalAgents = totalAgents,
            evacuatedAgents = completedRecords.Count,
            actionDistribution = actionDistribution,
            shelterDistribution = new Dictionary<string, int>(_shelterSelectionCounts),
            // 場所タイプ別避難率
            evacuationRateToShelter = evacuationRateToShelter,
            evacuationRateToArea = evacuationRateToArea,
            evacuationCountByLocation = evacuationCountByLocation,
            // 実験3: 生存判定結果
            survivalRate = survivalRate,
            survivedAgents = survivedCount,
            tsunamiHeight = tsunamiHeight,
            informationStrategy = informationStrategy
        };
    }

    private float GetMedian(List<float> values)
    {
        if (values.Count == 0) return 0f;

        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;

        if (sorted.Count % 2 == 0)
        {
            return (sorted[mid - 1] + sorted[mid]) / 2f;
        }
        return sorted[mid];
    }

    private void SaveEpisodeLogs(bool isPartial = false)
    {
        string directory = GetOutputDirectory();
        string partialSuffix = isPartial ? "_PARTIAL" : "";
        string conditionPrefix = GetConditionPrefix();
        // ファイル名に試行番号を使用（条件内での何回目かがわかるように）
        string filename = $"{conditionPrefix}trial_{_capturedTrialNumber}_actions{partialSuffix}.csv";
        string filepath = Path.Combine(directory, filename);

        using (var writer = new StreamWriter(filepath))
        {
            // 部分ログの場合はヘッダーにメタ情報を追加
            if (isPartial)
            {
                writer.WriteLine($"# PARTIAL LOG - シミュレーション途中停止時のデータ");
                writer.WriteLine($"# 停止時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"# 経過時間: {_envManager.CurrentTimeSec:F2}秒");
                writer.WriteLine($"# 記録された行動数: {_actionLogs.Count}");
            }

            // ヘッダー（階層的意思決定フィールドを追加）
            writer.WriteLine("timestamp,agent_id,action_type,target_shelter,reasoning,confidence,primary_goal,plan_steps,goal_updated,plan_updated");

            // データ
            foreach (var log in _actionLogs)
            {
                string reasoning = log.reasoning.Replace(",", ";").Replace("\n", " ");
                string primaryGoal = (log.primaryGoal ?? "").Replace(",", ";").Replace("\n", " ");
                string planSteps = (log.planSteps ?? "").Replace(",", ";").Replace("\n", " ");
                writer.WriteLine($"{log.timestamp:F2},{log.agentId},{log.actionType}," +
                               $"{log.targetShelter},{reasoning},{log.confidence:F2}," +
                               $"{primaryGoal},{planSteps},{log.goalUpdated},{log.planUpdated}");
            }
        }

        string logType = isPartial ? "部分行動ログ" : "行動ログ";
        Debug.Log($"[SimulationMetrics] {logType}を保存: {filepath}");
    }

    private void SaveEpisodeSummary(EpisodeSummary summary, bool isPartial = false)
    {
        string directory = GetOutputDirectory();
        string partialSuffix = isPartial ? "_PARTIAL" : "";
        string conditionPrefix = GetConditionPrefix();
        // ファイル名に試行番号を使用（条件内での何回目かがわかるように）
        string filename = $"{conditionPrefix}trial_{_capturedTrialNumber}_summary{partialSuffix}.csv";
        string filepath = Path.Combine(directory, filename);

        using (var writer = new StreamWriter(filepath))
        {
            // 部分ログの場合はヘッダーにメタ情報を追加
            // 経過時間は完全ログの場合は_capturedElapsedTime、部分ログの場合は現在時刻を使用
            float elapsedTime = isPartial ? _envManager.CurrentTimeSec : _capturedElapsedTime;
            if (isPartial)
            {
                writer.WriteLine($"# PARTIAL LOG - シミュレーション途中停止時のデータ");
                writer.WriteLine($"# 停止時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"# 経過時間: {elapsedTime:F2}秒");
                writer.WriteLine($"# 注意: このデータは不完全です。避難率・避難時間は停止時点の値です。");
            }

            writer.WriteLine("metric,value");
            writer.WriteLine($"episode_id,{summary.episodeId}");
            writer.WriteLine($"trial_number,{summary.trialNumber}");
            writer.WriteLine($"condition_id,{summary.conditionId}");
            writer.WriteLine($"agent_type,{summary.agentType}");
            writer.WriteLine($"is_partial,{isPartial}");  // 部分ログフラグを追加
            writer.WriteLine($"elapsed_time,{elapsedTime:F2}");  // 経過時間を追加（キャプチャ済みの値を使用）
            writer.WriteLine($"evacuation_rate,{summary.evacuationRate:F4}");
            writer.WriteLine($"average_evacuation_time,{summary.averageEvacuationTime:F2}");
            writer.WriteLine($"median_evacuation_time,{summary.medianEvacuationTime:F2}");
            writer.WriteLine($"max_evacuation_time,{summary.maxEvacuationTime:F2}");
            writer.WriteLine($"total_agents,{summary.totalAgents}");
            writer.WriteLine($"evacuated_agents,{summary.evacuatedAgents}");

            // 行動分布
            writer.WriteLine("");
            writer.WriteLine("action_type,ratio");
            foreach (var kvp in summary.actionDistribution)
            {
                writer.WriteLine($"{kvp.Key},{kvp.Value:F4}");
            }

            // 避難所分布（EVACUATEアクション選択時）
            writer.WriteLine("");
            writer.WriteLine("shelter_selection,count");
            foreach (var kvp in summary.shelterDistribution)
            {
                writer.WriteLine($"{kvp.Key},{kvp.Value}");
            }

            // 場所タイプ別避難率
            writer.WriteLine("");
            writer.WriteLine("evacuation_rate_to_shelter,{0:F4}", summary.evacuationRateToShelter);
            writer.WriteLine("evacuation_rate_to_area,{0:F4}", summary.evacuationRateToArea);

            // 場所別避難完了人数（実際の避難完了）
            writer.WriteLine("");
            writer.WriteLine("evacuation_location,count");
            if (summary.evacuationCountByLocation != null)
            {
                foreach (var kvp in summary.evacuationCountByLocation.OrderByDescending(x => x.Value))
                {
                    writer.WriteLine($"{kvp.Key},{kvp.Value}");
                }
            }

            // 生存判定結果（実験3用）
            writer.WriteLine("");
            writer.WriteLine($"survival_rate,{summary.survivalRate:F4}");
            writer.WriteLine($"survived_agents,{summary.survivedAgents}");
            writer.WriteLine($"tsunami_height,{summary.tsunamiHeight:F2}");
            writer.WriteLine($"information_strategy,{summary.informationStrategy}");
        }

        string logType = isPartial ? "部分サマリ" : "サマリ";
        Debug.Log($"[SimulationMetrics] {logType}を保存: {filepath}");
    }

    private string GetOutputDirectory()
    {
        // プロジェクトルートのLogs/ディレクトリに出力（Assets/外）
        string basePath = Path.Combine(Application.dataPath, "..", "Logs", OutputDirectory, _experimentId);

        if (!Directory.Exists(basePath))
        {
            Directory.CreateDirectory(basePath);
        }

        return basePath;
    }

    /// <summary>
    /// 現在の実験条件IDを取得（エピソード開始時にキャプチャした値を使用）
    /// バッチ実験時、OnEndEpisodeイベントのハンドラ実行順序により
    /// ExperimentConfig.OnConditionCompleteが先に実行され、ExperimentNameが次の条件に
    /// 更新された後にログ保存が行われる問題を回避するため、
    /// エピソード開始時にキャプチャした条件IDを使用する
    /// </summary>
    private string GetConditionPrefix()
    {
        if (!string.IsNullOrEmpty(_capturedConditionId))
        {
            return _capturedConditionId + "_";
        }
        return "";
    }

    /// <summary>
    /// エージェント別メトリクスをCSVに保存
    /// </summary>
    private void SaveAgentMetrics(bool isPartial = false)
    {
        string directory = GetOutputDirectory();
        string partialSuffix = isPartial ? "_PARTIAL" : "";
        string conditionPrefix = GetConditionPrefix();
        // ファイル名に試行番号を使用（条件内での何回目かがわかるように）
        string filename = $"{conditionPrefix}trial_{_capturedTrialNumber}_agents{partialSuffix}.csv";
        string filepath = Path.Combine(directory, filename);

        using (var writer = new StreamWriter(filepath))
        {
            // 部分ログの場合はヘッダーにメタ情報を追加
            if (isPartial)
            {
                writer.WriteLine($"# PARTIAL LOG - シミュレーション途中停止時のデータ");
                writer.WriteLine($"# 停止時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"# 経過時間: {_envManager.CurrentTimeSec:F2}秒");
                writer.WriteLine($"# 注意: evacuation_completed=falseのエージェントは避難完了前に停止された可能性があります");
            }

            // ヘッダー
            writer.WriteLine("agent_id,persona_name,mental_state,first_evacuate_time,total_stay_duration," +
                           "follow_count,talk_count,contact_count,search_family_count," +
                           "goal_update_count,plan_update_count,evacuation_completed,evacuation_time,final_shelter," +
                           "survived,final_elevation,survival_location");

            // データ
            foreach (var kvp in _agentMetrics)
            {
                var m = kvp.Value;
                string personaName = (m.personaName ?? "").Replace(",", ";");
                string mentalState = (m.mentalState ?? "").Replace(",", ";");
                string finalShelter = (m.finalShelter ?? "").Replace(",", ";");
                string survivalLocation = (m.survivalLocation ?? "").Replace(",", ";");

                writer.WriteLine($"{m.agentId},{personaName},{mentalState},{m.firstEvacuateTime:F2},{m.totalStayDuration:F2}," +
                               $"{m.followCount},{m.talkCount},{m.contactCount},{m.searchFamilyCount}," +
                               $"{m.goalUpdateCount},{m.planUpdateCount},{m.evacuationCompleted},{m.evacuationTime:F2},{finalShelter}," +
                               $"{m.survived},{m.finalElevation:F2},{survivalLocation}");
            }
        }

        string logType = isPartial ? "部分エージェントメトリクス" : "エージェント別メトリクス";
        Debug.Log($"[SimulationMetrics] {logType}を保存: {filepath}");
    }

    /// <summary>
    /// 現在のエピソードの指標を取得（デバッグ用）
    /// </summary>
    public EpisodeSummary GetCurrentMetrics()
    {
        return GenerateEpisodeSummary();
    }
}
}
