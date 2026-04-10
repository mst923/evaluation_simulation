using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 試行結果を保持する構造体
/// HotReloadの互換性のためクラス外に定義
/// </summary>
[System.Serializable]
public struct ExperimentTrialResult
{
    public int trialIndex;
    public float evacuationRate;
    public float elapsedTime;
    public string timestamp;
}

/// <summary>
/// 実験条件を定義する構造体（バッチ実験用）
/// </summary>
[System.Serializable]
public struct ExperimentCondition
{
    public string conditionId;
    public ExperimentConfig.AgentType agentType;
    public ExperimentConfig.BiasCondition biasCondition;
    public ExperimentConfig.InformationStrategy informationStrategy;
    public bool overrideBias;
}

/// <summary>
/// バッチ実験の条件別結果を保持する構造体
/// </summary>
[System.Serializable]
public struct BatchConditionResult
{
    public string conditionId;
    public List<ExperimentTrialResult> trialResults;
    public float averageEvacuationRate;
    public float stdDev;
}

/// <summary>
/// 実験条件の設定を管理するクラス
/// UnityのInspectorから設定を変更可能
/// N回の試行後に自動的に実験を終了し、結果をまとめて出力する
/// </summary>
[DefaultExecutionOrder(-100)] // 他のスクリプトより先に初期化（Instanceの早期設定）
public class ExperimentConfig : MonoBehaviour
{
    /// <summary>
    /// エージェントタイプの選択
    /// </summary>
    public enum AgentType
    {
        LLM,        // LLMエージェント
        RuleBased   // ルールベースエージェント
    }

    /// <summary>
    /// 実験終了時のアクション
    /// </summary>
    public enum ExperimentEndAction
    {
        PauseSimulation,    // シミュレーションを一時停止
        StopSimulation,     // シミュレーションを完全停止
        QuitApplication     // アプリケーションを終了
    }

    /// <summary>
    /// 認知バイアス条件（実験2-2用）
    /// </summary>
    public enum BiasCondition
    {
        None,              // ベースライン（既存ペルソナのmental_stateを使用）
        NormalcyBias,      // 正常性バイアス（全員に強制適用）
        ConformityBias,    // 同調バイアス（全員に強制適用）
        Combined           // 複合バイアス（正常性 + 同調）
    }

    /// <summary>
    /// 情報提供戦略（実験3用）
    /// 「情報の具体性」×「表現の切迫感」の2軸で4条件を設定
    /// </summary>
    public enum InformationStrategy
    {
        /// <summary>
        /// 条件A: 標準警報のみ（具体性:低 × 切迫感:低）
        /// 気象庁発表をそのまま伝達する定型的対応
        /// </summary>
        Standard,

        /// <summary>
        /// 条件B: 強い警告表現（具体性:低 × 切迫感:高）
        /// NHKアナウンサー調の危機感を強調した表現
        /// </summary>
        Urgent,

        /// <summary>
        /// 条件C: 詳細な避難先指示（具体性:高 × 切迫感:低）
        /// 具体的な避難所名と海抜情報を提供
        /// </summary>
        Detailed,

        /// <summary>
        /// 条件D: 詳細＋強い切迫感（具体性:高 × 切迫感:高）
        /// 最大限の情報提供と強い切迫感の両方
        /// </summary>
        DetailedUrgent
    }

    /// <summary>
    /// バッチ実験のプリセット
    /// </summary>
    public enum BatchExperimentPreset
    {
        /// <summary>バッチモード無効（従来どおり単一条件）</summary>
        None,

        // === 実験1: エージェントタイプ比較 ===
        /// <summary>実験1-A: LLMエージェント</summary>
        Exp1_A_LLM,
        /// <summary>実験1-B: ルールベースエージェント</summary>
        Exp1_B_RuleBased,
        /// <summary>実験1全体（2条件）</summary>
        Experiment1,

        // === 実験2: 認知バイアスの影響 ===
        /// <summary>実験2-1: ベースライン（既存ペルソナ）</summary>
        Exp2_1_Baseline,
        /// <summary>実験2-2: 正常性バイアス</summary>
        Exp2_2_NormalcyBias,
        /// <summary>実験2-3: 同調バイアス</summary>
        Exp2_3_ConformityBias,
        /// <summary>実験2-4: 複合バイアス</summary>
        Exp2_4_CombinedBias,
        /// <summary>実験2全体（4条件）</summary>
        Experiment2,

        // === 実験3: 情報提供戦略 ===
        /// <summary>実験3-A: 標準（具体性:低 × 切迫感:低）</summary>
        Exp3_A_Standard,
        /// <summary>実験3-B: 切迫感強調（具体性:低 × 切迫感:高）</summary>
        Exp3_B_Urgent,
        /// <summary>実験3-C: 詳細情報（具体性:高 × 切迫感:低）</summary>
        Exp3_C_Detailed,
        /// <summary>実験3-D: 詳細+切迫感（具体性:高 × 切迫感:高）</summary>
        Exp3_D_DetailedUrgent,
        /// <summary>実験3全体（4条件）</summary>
        Experiment3,

        // === 全実験 ===
        /// <summary>全実験（8条件、重複統合）</summary>
        AllExperiments
    }

    [Header("Experiment Settings")]
    [Tooltip("使用するエージェントタイプ")]
    public AgentType SelectedAgentType = AgentType.LLM;

    [Tooltip("ランダムシード（-1で毎回異なるシード）")]
    public int RandomSeed = -1;

    [Tooltip("試行回数（1試行 = 1エピソード）")]
    public int NumberOfTrials = 10;

    [Tooltip("試行回数に達したら自動的に実験を終了する")]
    public bool AutoStopOnComplete = true;

    [Tooltip("実験終了時のアクション")]
    public ExperimentEndAction EndAction = ExperimentEndAction.PauseSimulation;

    [Tooltip("シミュレーション開始時に自動的に実験を開始する")]
    public bool AutoStartOnPlay = true;

    [Header("Experiment 2-2: Cognitive Bias Settings")]
    [Tooltip("認知バイアス実験条件")]
    public BiasCondition SelectedBiasCondition = BiasCondition.None;

    [Tooltip("バイアス条件を全エージェントに一律適用（false=ペルソナのmental_stateを使用）")]
    public bool OverrideBiasForAllAgents = false;

    [Header("Experiment 3: Information Strategy Settings")]
    [Tooltip("情報提供戦略（具体性×切迫感の4条件）")]
    public InformationStrategy SelectedInformationStrategy = InformationStrategy.Standard;

    [Tooltip("津波の実際の高さ（メートル）- 生存判定に使用")]
    public float TsunamiHeight = 8f;

    [Tooltip("津波到達時刻（秒）- 生存判定のタイミング")]
    public float TsunamiArrivalTime = 1500f; // 25分 = 1500秒

    [Header("Traffic Simulation Settings")]
    [Tooltip("車両利用率（0.0-1.0）。0で全員徒歩、1.0で全員車両")]
    [Range(0f, 1f)]
    public float VehicleUsageRate = 0.6f;

    [Tooltip("交通シミュレーションを有効にする")]
    public bool EnableTrafficSimulation = false;

    [Header("Output Settings")]
    [Tooltip("結果をCSVに出力する")]
    public bool ExportResults = true;

    [Tooltip("実験名（出力ファイルの識別用）")]
    public string ExperimentName = "experiment_1";

    [Tooltip("サマリーレポートを生成する")]
    public bool GenerateSummaryReport = true;

    [Header("Batch Experiment Settings")]
    [Tooltip("バッチ実験プリセット（Noneで従来の単一実験モード）")]
    public BatchExperimentPreset BatchPreset = BatchExperimentPreset.None;

    [Tooltip("各条件の試行回数")]
    public int TrialsPerCondition = 10;

    private EnvManager _envManager;
    private SimulationMetrics _simulationMetrics;
    private int _currentTrialIndex = 0;
    private bool _experimentStarted = false;
    private bool _experimentCompleted = false;

    // 各試行の結果を保存
    private List<ExperimentTrialResult> _trialResults = new List<ExperimentTrialResult>();

    // バッチ実験用内部状態
    private List<ExperimentCondition> _batchConditions;
    private int _currentConditionIndex = 0;
    private List<BatchConditionResult> _batchResults = new List<BatchConditionResult>();
    private string _batchTimestamp;

    public static ExperimentConfig Instance { get; private set; }

    /// <summary>
    /// 現在の試行番号（1始まり）
    /// </summary>
    public int CurrentTrialNumber => _currentTrialIndex + 1;

    /// <summary>
    /// 実験が完了したかどうか
    /// </summary>
    public bool IsExperimentCompleted => _experimentCompleted;

    /// <summary>
    /// 実験が進行中かどうか
    /// </summary>
    public bool IsExperimentRunning => _experimentStarted && !_experimentCompleted;

    void Awake()
    {
        // シングルトンパターン
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // ランダムシードを設定
        if (RandomSeed >= 0)
        {
            Random.InitState(RandomSeed);
            Debug.Log($"[ExperimentConfig] ランダムシードを設定: {RandomSeed}");
        }
        else
        {
            int seed = System.Environment.TickCount;
            Random.InitState(seed);
            Debug.Log($"[ExperimentConfig] ランダムシードを自動生成: {seed}");
        }
    }

    void Start()
    {
        _envManager = FindFirstObjectByType<EnvManager>();
        _simulationMetrics = FindFirstObjectByType<SimulationMetrics>();

        if (_envManager != null)
        {
            _envManager.OnEndEpisode += OnTrialEnd;
        }

        // 実験設定をログに出力
        Debug.Log($"[ExperimentConfig] 実験設定:" +
                  $"\n  - エージェントタイプ: {SelectedAgentType}" +
                  $"\n  - 試行回数: {NumberOfTrials}" +
                  $"\n  - 実験名: {ExperimentName}" +
                  $"\n  - 自動開始: {AutoStartOnPlay}" +
                  $"\n  - バイアス条件: {SelectedBiasCondition}" +
                  $"\n  - バイアスオーバーライド: {OverrideBiasForAllAgents}" +
                  $"\n  - 情報提供戦略: {SelectedInformationStrategy}" +
                  $"\n  - 津波高さ: {TsunamiHeight}m" +
                  $"\n  - 津波到達時刻: {TsunamiArrivalTime}秒");

        // 自動開始が有効な場合、実験を自動的に開始
        if (AutoStartOnPlay)
        {
            if (IsBatchMode)
            {
                StartBatchExperiment();
            }
            else
            {
                StartExperiment();
            }
        }
    }

    void OnDestroy()
    {
        if (_envManager != null)
        {
            _envManager.OnEndEpisode -= OnTrialEnd;
        }
    }

    /// <summary>
    /// アプリケーション終了時（エディタの再生停止を含む）にリセット
    /// </summary>
    void OnApplicationQuit()
    {
        // 次回実行時のためにリセット状態を記録
        Debug.Log("[ExperimentConfig] アプリケーション終了 - 実験状態をリセット");
        ResetExperimentState();
    }

#if UNITY_EDITOR
    /// <summary>
    /// エディタの再生モード変更時に呼ばれる
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void OnPlayModeStateChanged()
    {
        // ドメインリロード時に静的変数をリセット
        Instance = null;
    }
#endif

    /// <summary>
    /// 実験状態を内部的にリセット（ログ出力なし）
    /// </summary>
    private void ResetExperimentState()
    {
        _currentTrialIndex = 0;
        _experimentStarted = false;
        _experimentCompleted = false;
        _trialResults.Clear();
    }

    private void OnTrialEnd(float evacuationRate, float elapsedTime)
    {
        if (!_experimentStarted || _experimentCompleted) return;

        // ★重要: 条件切り替え前にSimulationMetricsのログ保存を明示的に実行
        // イベントハンドラの実行順序に依存せず、確実に現在の条件でログを保存する
        var metrics = SimulationMetrics.Instance ?? _simulationMetrics;
        if (metrics != null)
        {
            metrics.FinalizeAndSaveEpisodeLogs(elapsedTime);
        }

        // ★注意: SetPendingConditionはApplyCondition()内で呼び出されるようになったため、
        // ここでの事前設定は不要（OnConditionComplete→ApplyConditionで設定される）

        // ログ保存完了を通知 → これによりOnEpisodeBegin()でのリセットが許可される
        if (_envManager != null)
        {
            _envManager.SignalEpisodeFinalized();
        }

        // 試行結果を記録（経過時間はイベント発火時にキャプチャされた値を使用）
        var result = new ExperimentTrialResult
        {
            trialIndex = _currentTrialIndex + 1,
            evacuationRate = evacuationRate,
            elapsedTime = elapsedTime,
            timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        _trialResults.Add(result);

        _currentTrialIndex++;
        Debug.Log($"[ExperimentConfig] 試行 {_currentTrialIndex}/{NumberOfTrials} 完了 - 避難率: {evacuationRate:P1}, 経過時間: {elapsedTime:F1}秒");

        if (_currentTrialIndex >= NumberOfTrials)
        {
            OnExperimentComplete();
        }
        else
        {
            // まだ試行が残っている場合は、次の試行番号をSimulationMetricsに通知
            if (metrics != null && IsBatchMode)
            {
                var currentCondition = _batchConditions[_currentConditionIndex];
                string agentType = currentCondition.agentType.ToString();
                int nextTrialNumber = _currentTrialIndex + 1;
                metrics.SetPendingCondition(currentCondition.conditionId, agentType, nextTrialNumber);
            }

            // ML-Agentのエピソード終了処理を実行
            // （OnExperimentCompleteの場合はOnConditionComplete内でOnEpisodeBeginが呼ばれる）
            if (_envManager != null)
            {
                _envManager.FinalizeMLAgentEpisode();
            }
        }
    }

    /// <summary>
    /// 実験完了時の処理
    /// </summary>
    private void OnExperimentComplete()
    {
        _experimentCompleted = true;
        _experimentStarted = false;

        // バッチモード時は次の条件へ移行
        if (IsBatchMode)
        {
            OnConditionComplete();
            return;
        }

        // 単一実験モード時は従来の処理
        Debug.Log($"[ExperimentConfig] ========================================");
        Debug.Log($"[ExperimentConfig] 実験完了！全{NumberOfTrials}試行が終了しました");
        Debug.Log($"[ExperimentConfig] ========================================");

        // サマリーレポートを生成
        if (GenerateSummaryReport)
        {
            GenerateReport();
        }

        // 終了アクションを実行
        if (AutoStopOnComplete)
        {
            ExecuteEndAction();
        }
    }

    /// <summary>
    /// 実験終了時のアクションを実行
    /// </summary>
    private void ExecuteEndAction()
    {
        switch (EndAction)
        {
            case ExperimentEndAction.PauseSimulation:
                Time.timeScale = 0f;
                Debug.Log("[ExperimentConfig] シミュレーションを一時停止しました");
                break;

            case ExperimentEndAction.StopSimulation:
                Time.timeScale = 0f;
                if (_envManager != null)
                {
                    _envManager.EnableEnv = false;
                }
                Debug.Log("[ExperimentConfig] シミュレーションを停止しました");
                break;

            case ExperimentEndAction.QuitApplication:
                Debug.Log("[ExperimentConfig] アプリケーションを終了します...");
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                break;
        }
    }

    /// <summary>
    /// サマリーレポートを生成してログに出力
    /// </summary>
    private void GenerateReport()
    {
        if (_trialResults.Count == 0) return;

        float totalEvacRate = 0f;
        float minEvacRate = float.MaxValue;
        float maxEvacRate = float.MinValue;
        float totalTime = 0f;

        foreach (var result in _trialResults)
        {
            totalEvacRate += result.evacuationRate;
            totalTime += result.elapsedTime;
            if (result.evacuationRate < minEvacRate) minEvacRate = result.evacuationRate;
            if (result.evacuationRate > maxEvacRate) maxEvacRate = result.evacuationRate;
        }

        float avgEvacRate = totalEvacRate / _trialResults.Count;
        float avgTime = totalTime / _trialResults.Count;

        // 標準偏差を計算
        float variance = 0f;
        foreach (var result in _trialResults)
        {
            variance += (result.evacuationRate - avgEvacRate) * (result.evacuationRate - avgEvacRate);
        }
        float stdDev = Mathf.Sqrt(variance / _trialResults.Count);

        var sb = new StringBuilder();
        sb.AppendLine("\n========== 実験サマリーレポート ==========");
        sb.AppendLine($"実験名: {ExperimentName}");
        sb.AppendLine($"エージェントタイプ: {SelectedAgentType}");
        sb.AppendLine($"試行回数: {_trialResults.Count}");
        sb.AppendLine($"------------------------------------------");
        sb.AppendLine($"平均避難率: {avgEvacRate:P2}");
        sb.AppendLine($"最小避難率: {minEvacRate:P2}");
        sb.AppendLine($"最大避難率: {maxEvacRate:P2}");
        sb.AppendLine($"標準偏差: {stdDev:P2}");
        sb.AppendLine($"平均経過時間: {avgTime:F1}秒");
        sb.AppendLine($"------------------------------------------");
        sb.AppendLine("各試行の結果:");
        foreach (var result in _trialResults)
        {
            sb.AppendLine($"  試行{result.trialIndex}: 避難率 {result.evacuationRate:P2}, 時間 {result.elapsedTime:F1}秒");
        }
        sb.AppendLine("==========================================\n");

        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 現在のエージェントタイプを取得
    /// </summary>
    public static AgentType GetAgentType()
    {
        if (Instance != null)
        {
            return Instance.SelectedAgentType;
        }
        return AgentType.LLM; // デフォルト
    }

    /// <summary>
    /// エージェントがLLMを使用するかどうかを判定
    /// </summary>
    public static bool ShouldUseLLM()
    {
        return GetAgentType() == AgentType.LLM;
    }

    /// <summary>
    /// 現在のバイアス条件を取得
    /// </summary>
    public static BiasCondition GetBiasCondition()
    {
        return Instance != null ? Instance.SelectedBiasCondition : BiasCondition.None;
    }

    /// <summary>
    /// バイアス条件をオーバーライドするかどうかを判定
    /// </summary>
    public static bool ShouldOverrideBias()
    {
        return Instance != null && Instance.OverrideBiasForAllAgents;
    }

    /// <summary>
    /// 現在の情報提供戦略を取得
    /// </summary>
    public static InformationStrategy GetInformationStrategy()
    {
        return Instance != null ? Instance.SelectedInformationStrategy : InformationStrategy.Standard;
    }

    /// <summary>
    /// 津波の高さを取得（メートル）
    /// </summary>
    public static float GetTsunamiHeight()
    {
        return Instance != null ? Instance.TsunamiHeight : 8f;
    }

    /// <summary>
    /// 津波到達時刻を取得（秒）
    /// </summary>
    public static float GetTsunamiArrivalTime()
    {
        return Instance != null ? Instance.TsunamiArrivalTime : 1500f;
    }

    /// <summary>
    /// 情報提供戦略が「具体性:高」かどうかを判定
    /// </summary>
    public static bool IsDetailedStrategy()
    {
        var strategy = GetInformationStrategy();
        return strategy == InformationStrategy.Detailed || strategy == InformationStrategy.DetailedUrgent;
    }

    /// <summary>
    /// 情報提供戦略が「切迫感:高」かどうかを判定
    /// </summary>
    public static bool IsUrgentStrategy()
    {
        var strategy = GetInformationStrategy();
        return strategy == InformationStrategy.Urgent || strategy == InformationStrategy.DetailedUrgent;
    }

    /// <summary>
    /// 実験を開始
    /// </summary>
    public void StartExperiment()
    {
        _currentTrialIndex = 0;
        _experimentStarted = true;
        Debug.Log($"[ExperimentConfig] 実験開始: {ExperimentName}");
    }

    /// <summary>
    /// 実験をリセット
    /// </summary>
    public void ResetExperiment()
    {
        _currentTrialIndex = 0;
        _experimentStarted = false;
        _experimentCompleted = false;
        _trialResults.Clear();

        // ランダムシードを再設定
        if (RandomSeed >= 0)
        {
            Random.InitState(RandomSeed);
        }

        // TimeScaleを復元
        if (_envManager != null)
        {
            Time.timeScale = _envManager.TimeScale;
        }
        else
        {
            Time.timeScale = 1f;
        }

        Debug.Log("[ExperimentConfig] 実験をリセットしました");
    }

    /// <summary>
    /// 実験を再開（一時停止状態から）
    /// </summary>
    public void ResumeExperiment()
    {
        if (_experimentCompleted)
        {
            Debug.LogWarning("[ExperimentConfig] 実験は既に完了しています。ResetExperiment()を呼び出してください");
            return;
        }

        if (_envManager != null)
        {
            Time.timeScale = _envManager.TimeScale;
        }
        else
        {
            Time.timeScale = 1f;
        }

        Debug.Log("[ExperimentConfig] 実験を再開しました");
    }

    /// <summary>
    /// 現在の試行結果リストを取得
    /// </summary>
    public List<ExperimentTrialResult> GetTrialResults()
    {
        return new List<ExperimentTrialResult>(_trialResults);
    }

    /// <summary>
    /// 残り試行回数を取得
    /// </summary>
    public int RemainingTrials => NumberOfTrials - _currentTrialIndex;

    #region Batch Experiment

    /// <summary>
    /// バッチ実験を開始
    /// </summary>
    public void StartBatchExperiment()
    {
        if (BatchPreset == BatchExperimentPreset.None)
        {
            Debug.LogWarning("[ExperimentConfig] BatchPreset is None. Use StartExperiment() instead.");
            return;
        }

        _batchConditions = GenerateConditions(BatchPreset);
        _currentConditionIndex = 0;
        _batchResults.Clear();
        _batchTimestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        Debug.Log($"[ExperimentConfig] ========================================");
        Debug.Log($"[ExperimentConfig] バッチ実験開始: {BatchPreset}");
        Debug.Log($"[ExperimentConfig] 条件数: {_batchConditions.Count}, 各条件試行数: {TrialsPerCondition}");
        Debug.Log($"[ExperimentConfig] 総試行数: {_batchConditions.Count * TrialsPerCondition}");
        Debug.Log($"[ExperimentConfig] ========================================");

        ApplyCondition(_batchConditions[0]);
        StartExperiment();
    }

    /// <summary>
    /// プリセットに基づいて条件リストを生成
    /// AllExperimentsの場合、重複する条件（LLM+None+Standard）は共通ベースラインとして統合
    /// </summary>
    private List<ExperimentCondition> GenerateConditions(BatchExperimentPreset preset)
    {
        var conditions = new List<ExperimentCondition>();

        switch (preset)
        {
            // === 実験1: 個別条件 ===
            case BatchExperimentPreset.Exp1_A_LLM:
                conditions.Add(CreateCondition("Exp1-A", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                break;

            case BatchExperimentPreset.Exp1_B_RuleBased:
                conditions.Add(CreateCondition("Exp1-B", AgentType.RuleBased, BiasCondition.None, InformationStrategy.Standard, false));
                break;

            case BatchExperimentPreset.Experiment1:
                conditions.Add(CreateCondition("Exp1-A", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                conditions.Add(CreateCondition("Exp1-B", AgentType.RuleBased, BiasCondition.None, InformationStrategy.Standard, false));
                break;

            // === 実験2: 個別条件 ===
            case BatchExperimentPreset.Exp2_1_Baseline:
                conditions.Add(CreateCondition("Exp2-1", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                break;

            case BatchExperimentPreset.Exp2_2_NormalcyBias:
                conditions.Add(CreateCondition("Exp2-2", AgentType.LLM, BiasCondition.NormalcyBias, InformationStrategy.Standard, true));
                break;

            case BatchExperimentPreset.Exp2_3_ConformityBias:
                conditions.Add(CreateCondition("Exp2-3", AgentType.LLM, BiasCondition.ConformityBias, InformationStrategy.Standard, true));
                break;

            case BatchExperimentPreset.Exp2_4_CombinedBias:
                conditions.Add(CreateCondition("Exp2-4", AgentType.LLM, BiasCondition.Combined, InformationStrategy.Standard, true));
                break;

            case BatchExperimentPreset.Experiment2:
                conditions.Add(CreateCondition("Exp2-1", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                conditions.Add(CreateCondition("Exp2-2", AgentType.LLM, BiasCondition.NormalcyBias, InformationStrategy.Standard, true));
                conditions.Add(CreateCondition("Exp2-3", AgentType.LLM, BiasCondition.ConformityBias, InformationStrategy.Standard, true));
                conditions.Add(CreateCondition("Exp2-4", AgentType.LLM, BiasCondition.Combined, InformationStrategy.Standard, true));
                break;

            // === 実験3: 個別条件 ===
            case BatchExperimentPreset.Exp3_A_Standard:
                conditions.Add(CreateCondition("Exp3-A", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                break;

            case BatchExperimentPreset.Exp3_B_Urgent:
                conditions.Add(CreateCondition("Exp3-B", AgentType.LLM, BiasCondition.None, InformationStrategy.Urgent, false));
                break;

            case BatchExperimentPreset.Exp3_C_Detailed:
                conditions.Add(CreateCondition("Exp3-C", AgentType.LLM, BiasCondition.None, InformationStrategy.Detailed, false));
                break;

            case BatchExperimentPreset.Exp3_D_DetailedUrgent:
                conditions.Add(CreateCondition("Exp3-D", AgentType.LLM, BiasCondition.None, InformationStrategy.DetailedUrgent, false));
                break;

            case BatchExperimentPreset.Experiment3:
                conditions.Add(CreateCondition("Exp3-A", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                conditions.Add(CreateCondition("Exp3-B", AgentType.LLM, BiasCondition.None, InformationStrategy.Urgent, false));
                conditions.Add(CreateCondition("Exp3-C", AgentType.LLM, BiasCondition.None, InformationStrategy.Detailed, false));
                conditions.Add(CreateCondition("Exp3-D", AgentType.LLM, BiasCondition.None, InformationStrategy.DetailedUrgent, false));
                break;

            // === 全実験（重複統合） ===
            case BatchExperimentPreset.AllExperiments:
                // 共通ベースライン条件（Exp1-A, Exp2-1, Exp3-A を統合）
                conditions.Add(CreateCondition("Baseline-LLM", AgentType.LLM, BiasCondition.None, InformationStrategy.Standard, false));
                // 実験1固有
                conditions.Add(CreateCondition("Exp1-B", AgentType.RuleBased, BiasCondition.None, InformationStrategy.Standard, false));
                // 実験2固有（ベースライン除く）
                conditions.Add(CreateCondition("Exp2-2", AgentType.LLM, BiasCondition.NormalcyBias, InformationStrategy.Standard, true));
                conditions.Add(CreateCondition("Exp2-3", AgentType.LLM, BiasCondition.ConformityBias, InformationStrategy.Standard, true));
                conditions.Add(CreateCondition("Exp2-4", AgentType.LLM, BiasCondition.Combined, InformationStrategy.Standard, true));
                // 実験3固有（ベースライン除く）
                conditions.Add(CreateCondition("Exp3-B", AgentType.LLM, BiasCondition.None, InformationStrategy.Urgent, false));
                conditions.Add(CreateCondition("Exp3-C", AgentType.LLM, BiasCondition.None, InformationStrategy.Detailed, false));
                conditions.Add(CreateCondition("Exp3-D", AgentType.LLM, BiasCondition.None, InformationStrategy.DetailedUrgent, false));
                break;

            default:
                Debug.LogWarning($"[ExperimentConfig] Unknown preset: {preset}");
                break;
        }

        return conditions;
    }

    /// <summary>
    /// 実験条件を生成するヘルパーメソッド
    /// </summary>
    private ExperimentCondition CreateCondition(string conditionId, AgentType agentType, BiasCondition bias, InformationStrategy info, bool overrideBias)
    {
        return new ExperimentCondition
        {
            conditionId = conditionId,
            agentType = agentType,
            biasCondition = bias,
            informationStrategy = info,
            overrideBias = overrideBias
        };
    }

    /// <summary>
    /// 条件を適用
    /// </summary>
    private void ApplyCondition(ExperimentCondition condition)
    {
        SelectedAgentType = condition.agentType;
        SelectedBiasCondition = condition.biasCondition;
        SelectedInformationStrategy = condition.informationStrategy;
        OverrideBiasForAllAgents = condition.overrideBias;
        ExperimentName = condition.conditionId;
        NumberOfTrials = TrialsPerCondition;

        // ★重要: SimulationMetricsに即座に条件IDを通知
        // OnEpisodeStart()が呼ばれる前に確実に設定しておく
        var metrics = SimulationMetrics.Instance ?? _simulationMetrics;
        if (metrics != null)
        {
            string agentType = condition.agentType.ToString();
            // 条件適用時は試行番号1から開始（_currentTrialIndexは0始まりなので+1）
            // 注意: ApplyConditionはResetExperiment()後に呼ばれるので、_currentTrialIndexは0にリセット済み
            int trialNumber = _currentTrialIndex + 1;
            metrics.SetPendingCondition(condition.conditionId, agentType, trialNumber);
        }

        Debug.Log($"[ExperimentConfig] ----------------------------------------");
        Debug.Log($"[ExperimentConfig] 条件適用: {condition.conditionId}");
        Debug.Log($"[ExperimentConfig]   AgentType: {condition.agentType}");
        Debug.Log($"[ExperimentConfig]   BiasCondition: {condition.biasCondition}");
        Debug.Log($"[ExperimentConfig]   InformationStrategy: {condition.informationStrategy}");
        Debug.Log($"[ExperimentConfig]   OverrideBias: {condition.overrideBias}");
        Debug.Log($"[ExperimentConfig] ----------------------------------------");
    }

    /// <summary>
    /// 単一条件の完了時（バッチモードでOnExperimentCompleteから呼び出し）
    /// </summary>
    private void OnConditionComplete()
    {
        // 現在条件の結果を保存
        float avgRate = CalculateAverageEvacuationRate();
        float stdDev = CalculateStdDev();

        var conditionResult = new BatchConditionResult
        {
            conditionId = _batchConditions[_currentConditionIndex].conditionId,
            trialResults = new List<ExperimentTrialResult>(_trialResults),
            averageEvacuationRate = avgRate,
            stdDev = stdDev
        };
        _batchResults.Add(conditionResult);

        _currentConditionIndex++;

        if (_currentConditionIndex < _batchConditions.Count)
        {
            // 次の条件へ
            Debug.Log($"[ExperimentConfig] 条件 {_currentConditionIndex}/{_batchConditions.Count} 完了");
            Debug.Log($"[ExperimentConfig] 次の条件: {_batchConditions[_currentConditionIndex].conditionId}");

            // ★注意: SetPendingConditionはOnTrialEnd()で既に呼び出し済み
            // ここで再度呼ぶと「次の次の条件」を設定してしまうため、呼ばない
            // OnTrialEnd()で設定された_pendingConditionIdがOnEpisodeStart()で使用される

            ResetExperiment();

            // SpawnLocationManagerをリセットして次の条件で正しく再初期化されるようにする
            SpawnLocationManager.ResetInstance();

            // 家族データのキャッシュを完全クリア（条件間ではフルリセット）
            FamilyManager.ClearCache();

            ApplyCondition(_batchConditions[_currentConditionIndex]);
            StartExperiment();

            // エピソードをリスタート
            if (_envManager != null)
            {
                // ML-Agentのエピソード終了処理を実行（OnEpisodeBeginの前に呼ぶ）
                // ★注意: FinalizeMLAgentEpisode()→Agent.EndEpisode()により
                // ML-Agentsが自動的にOnEpisodeBegin()を呼び、OnEpisodeStart()が発火する
                // その後、_envManager.OnEpisodeBegin()でもOnEpisodeStart()が発火するが、
                // SimulationMetricsは2回目の呼び出しでは_conditionCaptured=trueのため
                // キャプチャをスキップし、_pendingConditionIdも保持される
                _envManager.FinalizeMLAgentEpisode();
                _envManager.OnEpisodeBegin();
            }
        }
        else
        {
            // 全条件完了
            OnBatchExperimentComplete();
        }
    }

    /// <summary>
    /// バッチ実験全体の完了
    /// </summary>
    private void OnBatchExperimentComplete()
    {
        Debug.Log($"[ExperimentConfig] ========================================");
        Debug.Log($"[ExperimentConfig] バッチ実験完了！");
        Debug.Log($"[ExperimentConfig] 全{_batchConditions.Count}条件が終了しました");
        Debug.Log($"[ExperimentConfig] ========================================");

        GenerateBatchReport();
        ExportBatchSummaryToCSV();
        ExecuteEndAction();
    }

    /// <summary>
    /// バッチ実験のサマリーレポートを生成
    /// </summary>
    private void GenerateBatchReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("\n========== バッチ実験サマリーレポート ==========");
        sb.AppendLine($"プリセット: {BatchPreset}");
        sb.AppendLine($"条件数: {_batchConditions.Count}");
        sb.AppendLine($"各条件試行数: {TrialsPerCondition}");
        sb.AppendLine($"総試行数: {_batchConditions.Count * TrialsPerCondition}");
        sb.AppendLine($"------------------------------------------");

        for (int i = 0; i < _batchResults.Count; i++)
        {
            var result = _batchResults[i];
            var condition = _batchConditions[i];
            sb.AppendLine($"条件 {result.conditionId}:");
            sb.AppendLine($"  Agent: {condition.agentType}, Bias: {condition.biasCondition}, Info: {condition.informationStrategy}");
            sb.AppendLine($"  平均避難率: {result.averageEvacuationRate:P2}, 標準偏差: {result.stdDev:P2}");
        }

        sb.AppendLine("==========================================\n");
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// バッチ実験のサマリーをCSVに出力
    /// </summary>
    private void ExportBatchSummaryToCSV()
    {
        string fileName = $"{BatchPreset}_{_batchTimestamp}_summary.csv";
        // タイムスタンプ付きのサブディレクトリに出力
        string directoryPath = Path.Combine(Application.dataPath, "..", "Logs", "experiment_results", _batchTimestamp);

        if (!Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var sb = new StringBuilder();
        sb.AppendLine("condition_id,agent_type,bias_condition,info_strategy,avg_evacuation_rate,std_dev,trial_count");

        for (int i = 0; i < _batchResults.Count; i++)
        {
            var result = _batchResults[i];
            var condition = _batchConditions[i];

            sb.AppendLine($"{result.conditionId},{condition.agentType},{condition.biasCondition}," +
                          $"{condition.informationStrategy},{result.averageEvacuationRate:F4}," +
                          $"{result.stdDev:F4},{result.trialResults.Count}");
        }

        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, sb.ToString());
        Debug.Log($"[ExperimentConfig] バッチサマリーをCSVに出力: {filePath}");
    }

    /// <summary>
    /// 平均避難率を計算
    /// </summary>
    private float CalculateAverageEvacuationRate()
    {
        if (_trialResults.Count == 0) return 0f;

        float total = 0f;
        foreach (var result in _trialResults)
        {
            total += result.evacuationRate;
        }
        return total / _trialResults.Count;
    }

    /// <summary>
    /// 標準偏差を計算
    /// </summary>
    private float CalculateStdDev()
    {
        if (_trialResults.Count == 0) return 0f;

        float avg = CalculateAverageEvacuationRate();
        float variance = 0f;
        foreach (var result in _trialResults)
        {
            variance += (result.evacuationRate - avg) * (result.evacuationRate - avg);
        }
        return Mathf.Sqrt(variance / _trialResults.Count);
    }

    /// <summary>
    /// バッチモードが有効かどうか
    /// </summary>
    public bool IsBatchMode => BatchPreset != BatchExperimentPreset.None;

    /// <summary>
    /// 現在の条件番号（1始まり）
    /// </summary>
    public int CurrentConditionNumber => _currentConditionIndex + 1;

    /// <summary>
    /// 総条件数
    /// </summary>
    public int TotalConditions => _batchConditions?.Count ?? 0;

    /// <summary>
    /// 条件あたりの総試行数
    /// </summary>
    public int TotalTrials => TrialsPerCondition;

    #endregion
}
