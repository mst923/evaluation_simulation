using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using TMPro;
using PLATEAU.CityInfo;
using PLATEAU.Util;
using Newtonsoft.Json;
using EvacSim.Evacuees;
using EvacSim.Shelters;
using EvacSim.Disaster;
using EvacSim.Environment;
using EvacSim.Traffic;

namespace EvacSim.Core
{
/// <summary>
/// シミュレータ環境全般の制御を行うクラス
/// </summary>
public class EnvManager : MonoBehaviour {
    /**シミュレーションモードの選択を定義*/
    public enum SimulateMode {
        Train, // モデル訓練
        Inference // モデル推論
    }

    public enum SpawnMode {
        Random, // 一定の範囲内でランダムに出現
        Custom, // 自身でスポーン位置・範囲を設定
    }

    /// <summary>
    /// エピソードのライフサイクル状態
    /// ログ保存完了を待ってからリセットを行うための状態管理
    /// </summary>
    public enum EpisodeState {
        Idle,           // 初期状態 or リセット完了待機中
        Running,        // エピソード実行中
        Ending,         // 終了検知、ログ保存待ち
        ReadyForReset   // ログ保存完了、リセット可能
    }

    /// <summary>
    /// 災害・状況シナリオの種類（震度ベース）
    /// （LLMプロンプト内の「状況」テキストを切り替えるために使用）
    /// </summary>
    public enum DisasterScenario {
        Shindo2,        // 震度2：少し揺れたかなという程度
        Shindo6,        // 震度6：立っていられないほどの強い揺れ
        Shindo7Tsunami  // 震度7クラス + 津波警報
    }

    [Header("Environment Settings")]
    public SimulateMode Mode = SimulateMode.Train; 
    public SpawnMode EvacSpawnMode = SpawnMode.Random; 
    public DisasterScenario Scenario = DisasterScenario.Shindo2; // 災害シナリオ
    public float TimeScale = 1.0f; // 推論時のシミュレーションの時間スケール
    public bool IsRecordData = false;
    /// <summary>
    /// 生成する避難者の人数に合わせて避難所の収容人数をスケーリングします.
    /// </summary>
    /// <example>
    /// スケーリング例:
    /// <list type="bullet">
    /// <item>
    /// <description>1.0f: 通常 → 収容人数算出式に合わせて避難所の収容人数を設定</description>
    /// </item>
    /// <item>
    /// <description>0.5f: 避難者の人数が半分 → 避難所の収容人数も半分</description>
    /// </item>
    /// </list>
    /// </example>
    public float AccSimulateScale = 1.0f; 
    public float MaxSeconds = 60.0f; // シミュレーションの最大時間（秒）
    public int SpawnEvacueeSize;
    public GameObject SpawnEvacueePref; // 避難者のプレハブ
    public float SpawnRadius = 10f; // スポーンエリアの半径
    public Vector3 spawnCenter = Vector3.zero; // スポーンエリアの中心位置

    [Header("Temporal Overrides")]
    public bool EnableTemporalOverrides;
    public float ManualTimeLimitSeconds = 0f;

    public GameObject AgentObj;
    public ShelterManagementAgent Agent;
    public bool IsDataCollectionMode;

    [Header("Objects")]
    [System.NonSerialized]
    public List<GameObject> Evacuees = new List<GameObject>(); // 避難者のリスト
    [System.NonSerialized]
    public List<GameObject> CurrentShelters = new List<GameObject>(); // 現在のアクティブな避難所のリスト
    public List<GameObject> Shelters; // 全避難所のリスト
    [System.NonSerialized]
    public List<GameObject> TsunamiEvacuationAreas; // 津波避難地域のリスト

    // 避難者の高速検索用インデックス
    private Dictionary<string, Evacuee> _evacueeById = new Dictionary<string, Evacuee>();
    private Dictionary<string, Evacuee> _evacueeByName = new Dictionary<string, Evacuee>();

    [Header("UI Elements")]
    public TextMeshProUGUI stepCounter;
    public TextMeshProUGUI evacRateCounter;
    public TextMeshProUGUI episodeInfoText; // エピソード情報（条件名、試行番号）

    // Event Listeners
    public delegate void EndEpisodeHandler(float evacueeRate, float elapsedTime);
    public EndEpisodeHandler OnEndEpisode;
    public delegate void StartEpisodeHandler();
    public StartEpisodeHandler OnStartEpisode;
    [Header("Parameters")]
    public float EvacuationRate; // 全体の避難率
    public bool EnableEnv = false; // 環境の準備が完了したか否か（利用不可の場合はfalse）

    /// <summary>
    /// 現在のエピソード状態（ログ保存とリセットのタイミング制御用）
    /// </summary>
    public EpisodeState CurrentEpisodeState { get; private set; } = EpisodeState.Idle;
    public int currentStep; // 現在のステップ数
    private float currentTimeSec; //現在の経過時間（秒）
    private List<Tuple<float, float>> evaRatePerSec = new List<Tuple<float, float>>(); // 避難率の時間変化を記録するリスト
    public int currentEpisodeId = 0; // エピソード番号
    public string recordID; // データ記録用に実行時間を元にしたIDを生成

    public float CurrentTimeSec => currentTimeSec;

    /// <summary>
    /// 現在選択されているシナリオをLLM側に渡すためのID文字列に変換
    /// </summary>
    public string GetScenarioId()
    {
        switch (Scenario)
        {
            case DisasterScenario.Shindo2:
                return "shindo_2";
            case DisasterScenario.Shindo6:
                return "shindo_6";
            case DisasterScenario.Shindo7Tsunami:
                return "shindo_7_tsunami";
            default:
                return "shindo_2";
        }
    }
    
    void Start() {
        if(Mode == SimulateMode.Inference) {
            Time.timeScale = TimeScale; // 推論時のみシミュレーションの時間スケールを設定
        }

        if(AccSimulateScale > 1.0f) {
            Debug.LogError("AccSimulateScale is greater than 1.0f. Please set the value between 0.0f and 1.0f.");
        }
        // 日付-時間-分-秒を組み合わせた記録用IDを生成
        recordID = System.DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss");
        
        // ペルソナデータを読み込む
        PersonaManager.LoadPersonas();
        
        // 家族データを読み込む
        FamilyManager.LoadFamilies();
        
        // EnvironmentalContextProviderとSpawnLocationManagerを自動生成
        EnsureEnvironmentalContextProvider();
        EnsureSpawnLocationManager();
        EnsureAlertManager();
        EnsureSimulationMetrics();
        EnsureExperimentConfig();
        EnsureTrafficManagers();

        NavMesh.pathfindingIterationsPerFrame = 1000000; // パス検索の上限値を設定

        Agent = AgentObj.GetComponent<ShelterManagementAgent>();
        // 念のため null であっても初期化しておく（実行順で FixedUpdate が先行した場合のガード）
        Evacuees = new List<GameObject>(); // 避難者のリストを初期化
        _evacueeById.Clear();
        _evacueeByName.Clear();
        CurrentShelters = new List<GameObject>(); // 避難所のリストを初期化
        Shelters = new List<GameObject>(); // 避難所のリストを初期化
        currentStep = Agent.StepCount;

        if(IsDataCollectionMode) {
            Agent.Disabled = true;
        }

        // 避難所登録
        Shelters = new List<GameObject>(GameObject.FindGameObjectsWithTag("Shelter"));

        // 固定値の避難所を追加
        GameObject[] constSheleters = GameObject.FindGameObjectsWithTag("ConstShelter");
        foreach (var shelter in constSheleters) {
            Shelters.Add(shelter);
        }

        // Schoolタグを避難所として追加（Shelterと同じ動作）
        try {
            GameObject[] schools = GameObject.FindGameObjectsWithTag("School");
            foreach (var school in schools) {
                Shelters.Add(school);
            }
            if (schools.Length > 0) {
                Debug.Log($"[EnvManager] Schoolタグを{schools.Length}件、避難所として登録");
            }
        } catch (UnityException) {
            // Schoolタグが存在しない場合は無視
        }

        // 津波避難地域を登録
        TsunamiEvacuationAreas = new List<GameObject>(GameObject.FindGameObjectsWithTag("TsunamiEvacuationArea"));

        // PreSchoolタグを津波避難地域として追加（TsunamiEvacuationAreaと同じ動作）
        try {
            GameObject[] preschools = GameObject.FindGameObjectsWithTag("PreSchool");
            foreach (var preschool in preschools) {
                TsunamiEvacuationAreas.Add(preschool);
            }
            if (preschools.Length > 0) {
                Debug.Log($"[EnvManager] PreSchoolタグを{preschools.Length}件、津波避難地域として登録");
            }
        } catch (UnityException) {
            // PreSchoolタグが存在しない場合は無視
        }

        // 津波避難地域のコンポーネント初期化
        foreach (var area in TsunamiEvacuationAreas) {
            TsunamiEvacuationArea areaComponent = area.GetComponent<TsunamiEvacuationArea>();

            if (areaComponent == null) {
                areaComponent = area.AddComponent<TsunamiEvacuationArea>();
                areaComponent.uuid = System.Guid.NewGuid().ToString();

                if (string.IsNullOrEmpty(areaComponent.displayName)) {
                    areaComponent.displayName = area.name;
                }
            }
            else {
                if (string.IsNullOrEmpty(areaComponent.displayName)) {
                    areaComponent.displayName = area.name;
                }
            }
        }

        // 津波避難地域の一覧をログ出力
        if (TsunamiEvacuationAreas.Count > 0)
        {
            var areaNames = TsunamiEvacuationAreas
                .Where(a => a != null)
                .Select(a => {
                    var comp = a.GetComponent<TsunamiEvacuationArea>();
                    return comp != null && !string.IsNullOrEmpty(comp.displayName) ? comp.displayName : a.name;
                });
            Debug.Log($"[EnvManager] 津波避難地域を{TsunamiEvacuationAreas.Count}件登録: {string.Join(", ", areaNames)}");
        }
        else
        {
            Debug.Log($"[EnvManager] 津波避難地域: 0件");
        }

        // コンポーネントの初期化
        foreach (var shelter in Shelters) {
            Shelter tower = shelter.GetComponent<Shelter>();
            
            if(tower == null) {
                // 新規にShelterコンポーネントを追加
                tower = shelter.AddComponent<Shelter>();
                tower.uuid = System.Guid.NewGuid().ToString();
                tower.NowAccCount = 0;
                
                // 自動計算を有効化してスケール係数を設定
                tower.autoCalculateCapacity = true;
                tower.capacityScale = AccSimulateScale;
                
                // displayNameが空の場合はGameObject名をデフォルトとして設定
                if (string.IsNullOrEmpty(tower.displayName))
                {
                    tower.displayName = shelter.name;
                }
            }
            else
            {
                // 既存のShelterコンポーネントがある場合
                // 自動計算が有効な場合のみスケール係数を更新
                if (tower.autoCalculateCapacity)
                {
                    tower.capacityScale = AccSimulateScale;
                }
                
                // displayNameが空の場合はGameObject名をデフォルトとして設定
                if (string.IsNullOrEmpty(tower.displayName))
                {
                    tower.displayName = shelter.name;
                }
            }
        }

        // 避難所の一覧をログ出力
        if (Shelters.Count > 0)
        {
            var shelterNames = Shelters
                .Where(s => s != null)
                .Select(s => {
                    var comp = s.GetComponent<Shelter>();
                    return comp != null && !string.IsNullOrEmpty(comp.displayName) ? comp.displayName : s.name;
                });
            Debug.Log($"[EnvManager] 避難所を{Shelters.Count}件登録: {string.Join(", ", shelterNames)}");
        }
        else
        {
            Debug.Log($"[EnvManager] 避難所: 0件");
        }

        /** エピソード終了時の処理*/
        OnEndEpisode += OnEndEpisodeHandler;
    }

    /// <summary>
    /// EnvironmentalContextProviderが存在しない場合は自動的に作成
    /// </summary>
    private void EnsureEnvironmentalContextProvider()
    {
        var existing = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (existing == null)
        {
            GameObject providerObj = new GameObject("EnvironmentalContextProvider");
            providerObj.AddComponent<EnvironmentalContextProvider>();
            Debug.Log("[EnvManager] EnvironmentalContextProviderを自動生成しました");
        }
    }
    
    /// <summary>
    /// SpawnLocationManagerが存在しない場合は自動的に作成
    /// </summary>
    private void EnsureSpawnLocationManager()
    {
        var existing = FindFirstObjectByType<SpawnLocationManager>();
        if (existing == null)
        {
            GameObject managerObj = new GameObject("SpawnLocationManager");
            managerObj.AddComponent<SpawnLocationManager>();
            Debug.Log("[EnvManager] SpawnLocationManagerを自動生成しました");
        }
    }
    
    /// <summary>
    /// AlertManagerが存在しない場合は自動的に作成
    /// </summary>
    private void EnsureAlertManager()
    {
        var existing = FindFirstObjectByType<AlertManager>();
        if (existing == null)
        {
            GameObject managerObj = new GameObject("AlertManager");
            managerObj.transform.SetParent(transform); // EnvManagerの子として配置
            managerObj.AddComponent<AlertManager>();
            Debug.Log("[EnvManager] AlertManagerを自動生成しました");
        }
    }

    /// <summary>
    /// SimulationMetricsが存在しない場合は自動的に作成
    /// </summary>
    private void EnsureSimulationMetrics()
    {
        var existing = FindFirstObjectByType<SimulationMetrics>();
        if (existing == null)
        {
            GameObject metricsObj = new GameObject("SimulationMetrics");
            metricsObj.transform.SetParent(transform);
            metricsObj.AddComponent<SimulationMetrics>();
            Debug.Log("[EnvManager] SimulationMetricsを自動生成しました");
        }
    }

    /// <summary>
    /// ExperimentConfigが存在しない場合は自動的に作成
    /// </summary>
    private void EnsureExperimentConfig()
    {
        var existing = FindFirstObjectByType<ExperimentConfig>();
        if (existing == null)
        {
            GameObject configObj = new GameObject("ExperimentConfig");
            configObj.transform.SetParent(transform);
            configObj.AddComponent<ExperimentConfig>();
            Debug.Log("[EnvManager] ExperimentConfigを自動生成しました");
        }
    }

    /// <summary>
    /// 交通シミュレーション関連のマネージャーが存在しない場合は自動的に作成
    /// </summary>
    private void EnsureTrafficManagers()
    {
        if (FindFirstObjectByType<TrafficServerManager>() == null)
        {
            GameObject serverObj = new GameObject("TrafficServerManager");
            serverObj.transform.SetParent(transform);
            serverObj.AddComponent<TrafficServerManager>();
            Debug.Log("[EnvManager] TrafficServerManagerを自動生成しました");
        }

        if (FindFirstObjectByType<TrafficClient>() == null)
        {
            GameObject clientObj = new GameObject("TrafficClient");
            clientObj.transform.SetParent(transform);
            clientObj.AddComponent<TrafficClient>();
            Debug.Log("[EnvManager] TrafficClientを自動生成しました");
        }

        if (FindFirstObjectByType<RoadNetworkLoader>() == null)
        {
            GameObject loaderObj = new GameObject("RoadNetworkLoader");
            loaderObj.transform.SetParent(transform);
            loaderObj.AddComponent<RoadNetworkLoader>();
            Debug.Log("[EnvManager] RoadNetworkLoaderを自動生成しました");
        }

        if (FindFirstObjectByType<RoadNetworkVisualizer>() == null)
        {
            GameObject vizObj = new GameObject("RoadNetworkVisualizer");
            vizObj.transform.SetParent(transform);
            vizObj.AddComponent<RoadNetworkVisualizer>();
            Debug.Log("[EnvManager] RoadNetworkVisualizerを自動生成しました");
        }
    }

    void OnDrawGizmos() {
        if(EvacSpawnMode == SpawnMode.Random) {
            Gizmos.color = Color.red;
            DrawWireCircle(spawnCenter, SpawnRadius);
        }
    }

    void FixedUpdate() {
        if (!EnableEnv) return; // リセット中や終了後は処理をスキップ

        currentTimeSec += Time.deltaTime;
        EvacuationRate = GetCurrentEvacueeRate();
        evaRatePerSec.Add(new Tuple<float, float>(currentTimeSec, EvacuationRate));
        UpdateUI();
        if (currentTimeSec >= MaxSeconds || IsEvacuatedAll()) { // 制限時間 or 全避難者が避難完了した場合
            CurrentEpisodeState = EpisodeState.Ending; // 状態を「終了処理中」に変更
            EnableEnv = false; // 終了条件を満たしたらすぐに無効化（複数回発火防止）
            float capturedElapsedTime = currentTimeSec; // Dispose()でリセットされる前にキャプチャ
            OnEndEpisode?.Invoke(EvacuationRate, capturedElapsedTime); // エピソード終了のイベントを発火
        }
    }

    private void OnEndEpisodeHandler(float evacuateRate, float elapsedTime) {
        // エピソード終了をConsoleに出力
        string endReason = elapsedTime >= MaxSeconds ? "Timeout" : "All Evacuated";
        Debug.Log($"[EnvManager] Episode {currentEpisodeId} End ({endReason})");

        // エピソード終了時のUI更新
        if (episodeInfoText != null)
        {
            int currentTrial = ExperimentConfig.Instance?.CurrentTrialNumber ?? (currentEpisodeId + 1);
            int totalTrials = ExperimentConfig.Instance?.NumberOfTrials ?? 1;
            episodeInfoText.text = $"Episode {currentTrial}/{totalTrials} End: {endReason}";
        }

        // 1. 避難率による報酬
        float evacuationRateReward = GetCurrentEvacueeRate();

        // 2. 経過時間によりボーナスを与える
        float timeBonus = (MaxSeconds - currentTimeSec) / MaxSeconds;

        // 総合報酬
        float totalReward = evacuationRateReward + timeBonus;
        Debug.Log("Total Reward: " + totalReward);
        Agent.AddReward(totalReward);

        if(IsRecordData) {
            Utils.SaveResultCSV(
                new string[] { "Time", "EvacuationRate" },
                evaRatePerSec,
                (data) => new string[] { data.Item1.ToString(), data.Item2.ToString() },
                $"{recordID}/EvaRatesPerSec_Episode_{currentEpisodeId}.csv"
            );
        }

        /**エピソード終了の発行*/
        Agent.OnEndEpisode();
        // ★重要: Agent.EndEpisode()はここでは呼ばない
        // EndEpisode()を呼ぶとML-Agentsが自動的にOnEpisodeBegin()を呼ぶが、
        // その時点ではまだExperimentConfig.OnTrialEnd()でSetPendingCondition()が呼ばれていない
        // → ExperimentConfig.OnTrialEnd()の最後でFinalizeMLAgentEpisode()を呼ぶことで、
        //    SetPendingCondition()の後にAgent.EndEpisode()が呼ばれることを保証する
        // 注意: currentEpisodeId++はOnEpisodeBegin()に移動（ログ保存完了後にインクリメント）
    }

    /// <summary>
    /// ML-Agentsのエピソード終了処理を実行する
    /// ExperimentConfig.OnTrialEnd()の最後で呼び出される
    /// これにより、SetPendingCondition()の後にAgent.EndEpisode()が呼ばれることを保証する
    /// </summary>
    public void FinalizeMLAgentEpisode()
    {
        Agent.EndEpisode();
        Debug.Log($"[EnvManager] ML-Agentエピソード終了処理完了");
    }

    /// <summary>
    /// ログ保存完了を通知し、リセット処理を許可する
    /// ExperimentConfigから呼び出される
    /// </summary>
    public void SignalEpisodeFinalized()
    {
        if (CurrentEpisodeState == EpisodeState.Ending)
        {
            CurrentEpisodeState = EpisodeState.ReadyForReset;
            Debug.Log($"[EnvManager] エピソード {currentEpisodeId} ログ保存完了、リセット準備完了");
        }
        else
        {
            Debug.LogWarning($"[EnvManager] SignalEpisodeFinalized: 予期しない状態 ({CurrentEpisodeState})");
        }
    }

    /// <summary>
    /// エピソード開始時の初期化処理
    /// この関数はエージェントのイベント関数から参照されます
    /// </summary>
    public void OnEpisodeBegin() {
        // 状態チェック: ReadyForReset または Idle（初回起動）のみ許可
        if (CurrentEpisodeState != EpisodeState.ReadyForReset &&
            CurrentEpisodeState != EpisodeState.Idle)
        {
            Debug.LogWarning($"[EnvManager] OnEpisodeBegin: 現在の状態({CurrentEpisodeState})ではリセットできません。ログ保存完了を待機してください。");
            return;
        }

        // 前エピソードが正常終了していた場合、エピソードIDをインクリメント
        if (CurrentEpisodeState == EpisodeState.ReadyForReset)
        {
            currentEpisodeId++;
        }

        CurrentEpisodeState = EpisodeState.Idle;
        EnableEnv = false;
        Dispose();

        // SpawnLocationManagerを強制的に再初期化（バッチ実験のエピソード遷移対策）
        SpawnLocationManager.ForceReinitialize();

        // 家族データの動的状態をリセット（バッチ実験のエピソード遷移対策）
        FamilyManager.ResetEpisodeState();

        Create();
        
        // AlertManagerをリセット
        var alertManager = FindFirstObjectByType<AlertManager>();
        if (alertManager != null)
        {
            alertManager.OnEpisodeStart();
        }
        
        // 全避難者の状態をリセット（警報・行動履歴・会話など全ての動的状態）
        foreach (var evacueeObj in Evacuees)
        {
            if (evacueeObj != null)
            {
                var evacuee = evacueeObj.GetComponent<Evacuee>();
                if (evacuee != null)
                {
                    evacuee.ResetForNewEpisode();
                }
            }
        }
        
        OnStartEpisode?.Invoke();
        Debug.Log($"[EnvManager] エピソード {currentEpisodeId} 開始 (避難者数: {Evacuees.Count})");
        CurrentEpisodeState = EpisodeState.Running;
        EnableEnv = true;
    }

    /// <summary>
    /// 環境をリセット,破棄をする関数。
    /// - 避難者のクリア
    /// - 避難所のクリア
    /// </summary>
    public void Dispose() {
        foreach (var evacuee in Evacuees) {
            Destroy(evacuee);
        }
        // 避難者スポーン地点の表示を非表示にする
        GameObject[] spawnPoints = GameObject.FindGameObjectsWithTag("SpawnPos");
        foreach (var spawnPoint in spawnPoints) {
            var point = spawnPoint.GetComponent<EvacueeSpawnPoint>();
            point.ShowRangeOff();
        }
        Evacuees = new List<GameObject>(); // 新しいリストを作成
        _evacueeById.Clear();
        _evacueeByName.Clear();
        CurrentShelters = new List<GameObject>(); // 新しいリストを作成
        currentTimeSec = 0;
        evaRatePerSec.Clear();
        
        // 避難所の収容人数をリセット
        foreach (var shelterObj in Shelters) {
            if (shelterObj != null) {
                var shelter = shelterObj.GetComponent<Shelter>();
                if (shelter != null) {
                    shelter.NowAccCount = 0;
                    Debug.Log($"[EnvManager] {shelterObj.name}: エピソード開始時に収容人数をリセットしました。MaxCapacity: {shelter.MaxCapacity}, NowAccCount: {shelter.NowAccCount}, CurrentCapacity: {shelter.currentCapacity}");
                }
            }
        }

        // 津波避難地域のリセット
        foreach (var areaObj in TsunamiEvacuationAreas) {
            if (areaObj != null) {
                var area = areaObj.GetComponent<TsunamiEvacuationArea>();
                if (area != null) {
                    area.Reset();
                }
            }
        }
    }

    /// <summary>
    /// 環境の生成を行う関数.
    /// - 避難者のスポーン 処理
    /// </summary>
    public void Create() {

        if(Mode == SimulateMode.Train) {
            if(EvacSpawnMode == SpawnMode.Custom) {
                // Custom Spawnエリアの中からランダムに1つ選択し、避難者をスポーンさせ、避難者位置に分布を持たせる
                GameObject[] spawnPoints = GameObject.FindGameObjectsWithTag("SpawnPos");
                GameObject selectSpawnPoint = spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)];
                var point = selectSpawnPoint.GetComponent<EvacueeSpawnPoint>();
                point.ShowRangeOn();
                float radius = point.SpawnRadius;
                Vector3 spawnCenter = selectSpawnPoint.transform.position;
                // 生成ポイントを中心としたランダムなナビメッシュ上の位置を取得
                Vector3 spawnPos = GetRandomPositionOnNavMesh(radius, spawnCenter);
                for (int i = 0; i < SpawnEvacueeSize; i++) {
                    SpawnEvacuee(spawnPos);
                }
            } else {
                for (int i = 0; i < SpawnEvacueeSize; i++) {
                    Vector3 spawnPos = GetRandomPositionOnNavMesh(SpawnRadius, spawnCenter);
                    if (spawnPos != Vector3.zero) {
                        SpawnEvacuee(spawnPos);
                    }
                }
            }
            

        } else if(Mode == SimulateMode.Inference) {
            if(EvacSpawnMode == SpawnMode.Custom) {
                GameObject[] spawnPoints = GameObject.FindGameObjectsWithTag("SpawnPos");
                foreach (var spawnPoint in spawnPoints) {
                    var point = spawnPoint.GetComponent<EvacueeSpawnPoint>();
                    float radius = point.SpawnRadius;
                    Vector3 spawnCenter = spawnPoint.transform.position;
                    Vector3 spawnPos = GetRandomPositionOnNavMesh(radius, spawnCenter);
                    for (int i = 0; i < point.SpawnSize; i++) {
                        SpawnEvacuee(spawnPos);
                    }
                }
            } else {
                for (int i = 0; i < SpawnEvacueeSize; i++) {
                    Vector3 spawnPos = GetRandomPositionOnNavMesh(SpawnRadius, spawnCenter);
                    if (spawnPos != Vector3.zero) {
                        SpawnEvacuee(spawnPos);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 避難者１体を生成、登録する関数
    /// </summary>
    /// <param name="spawnPos"></param>
    private void SpawnEvacuee(Vector3 spawnPos) {
        int agentId = Evacuees.Count + 1;

        // 家族情報を取得してスポーン位置を決定
        var familyData = FamilyManager.GetFamily(agentId);
        Vector3 finalSpawnPos = spawnPos;

        if (familyData != null && familyData.owner_spawn_category != BuildingCategory.Other)
        {
            // 家族情報に基づいてスポーン位置を取得（School/PreSchoolはタグからフォールバック）
            var (position, buildingName) = SpawnLocationManager.GetRandomSpawnPositionWithNameAndFallback(familyData.owner_spawn_category);
            if (position != Vector3.zero)
            {
                finalSpawnPos = position;
            }
            else
            {
                Debug.LogWarning($"[EnvManager] Agent {agentId}: {BuildingCategorizer.GetCategoryDisplayName(familyData.owner_spawn_category)}のスポーン位置が見つかりません");
            }

            // 家族メンバーの探索位置を設定（School/PreSchoolはタグからフォールバック）
            foreach (var member in familyData.members)
            {
                var (searchPos, searchBuildingName) = SpawnLocationManager.GetRandomSpawnPositionWithNameAndFallback(member.spawn_category);
                if (searchPos != Vector3.zero)
                {
                    member.search_position = searchPos;
                    member.spawn_building_name = searchBuildingName;
                    member.likely_location = $"{BuildingCategorizer.GetCategoryDisplayName(member.spawn_category)}（{searchBuildingName}）";
                }
                else
                {
                    Debug.LogWarning($"[EnvManager] Agent {agentId}: 家族 {member.name} の探索位置取得に失敗 (カテゴリ: {member.spawn_category}, 建物数: {SpawnLocationManager.GetBuildingCount(member.spawn_category)})");
                }
            }
        }

        // NavMesh上の最寄り位置に補正（段階的に探索範囲を拡大）
        NavMeshHit navHit;
        float[] searchRadii = { 50f, 100f, 200f };
        bool foundNavMesh = false;

        foreach (float radius in searchRadii)
        {
            if (NavMesh.SamplePosition(finalSpawnPos, out navHit, radius, NavMesh.AllAreas))
            {
                finalSpawnPos = navHit.position;
                foundNavMesh = true;
                break;
            }
        }

        if (!foundNavMesh)
        {
            // 最終フォールバック: 元のspawnPos引数を使用
            if (NavMesh.SamplePosition(spawnPos, out navHit, 50f, NavMesh.AllAreas))
            {
                finalSpawnPos = navHit.position;
                Debug.LogWarning($"[EnvManager] Agent {agentId}: 建物位置からNavMeshが見つからないため、デフォルト位置を使用します。");
            }
            else
            {
                Debug.LogWarning($"[EnvManager] Agent {agentId}: NavMesh上の位置が見つかりません。元の位置 {finalSpawnPos} を使用します。");
            }
        }

        GameObject evacuee = Instantiate(SpawnEvacueePref, finalSpawnPos, Quaternion.identity, transform);
        evacuee.tag = "Evacuee";
        Evacuees.Add(evacuee);

        // 避難者に生成順序のIDを設定（1から始まる）
        var evacueeComponent = evacuee.GetComponent<Evacuee>();
        if (evacueeComponent != null)
        {
            evacueeComponent.SetEvacueeId(agentId);

            // 高速検索用インデックスに登録
            _evacueeById[agentId.ToString()] = evacueeComponent;

            // ペルソナ名が設定されていれば名前インデックスにも登録
            var persona = evacueeComponent.GetPersona();
            if (persona != null && !string.IsNullOrEmpty(persona.name))
            {
                _evacueeByName[persona.name] = evacueeComponent;
            }
        }
    }

    /// <summary>
    /// IDで避難者を高速検索（O(1)）
    /// </summary>
    public Evacuee GetEvacueeById(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        return _evacueeById.TryGetValue(id, out var evacuee) ? evacuee : null;
    }

    /// <summary>
    /// 名前で避難者を高速検索（O(1)、完全一致）
    /// </summary>
    public Evacuee GetEvacueeByName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        return _evacueeByName.TryGetValue(name, out var evacuee) ? evacuee : null;
    }

    /// <summary>
    /// 名前で避難者を検索（部分一致対応）
    /// 完全一致で見つからない場合に部分一致で検索
    /// </summary>
    public Evacuee GetEvacueeByNamePartial(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        // まず完全一致を試行
        if (_evacueeByName.TryGetValue(name, out var evacuee))
            return evacuee;

        // 部分一致で検索
        foreach (var kvp in _evacueeByName)
        {
            if (kvp.Key.Contains(name) || name.Contains(kvp.Key))
                return kvp.Value;
        }
        return null;
    }

    /// <summary>
    /// 範囲内のナビメッシュ上の任意の座標を取得する。
    /// </summary>
    /// <returns>ランダムなナビメッシュ上の座標 or Vector3.zero</returns>
    private static Vector3 GetRandomPositionOnNavMesh(float radius, Vector3 center) {
        Vector3 randomDirection = UnityEngine.Random.insideUnitSphere * radius; // 半径内のランダムな位置を取得
        randomDirection += center; // 中心位置を加算
        NavMeshHit hit;
        if (NavMesh.SamplePosition(randomDirection, out hit, radius, NavMesh.AllAreas)) {
            return hit.position;
        }
        return Vector3.zero; // ナビメッシュが見つからなかった場合
    }

    private void UpdateUI() {
        stepCounter.text = $"Remain: {MaxSeconds - currentTimeSec:F1}s";
        evacRateCounter.text = $"Evacuation Rate: {EvacuationRate:P1}";

        // エピソード情報を表示
        if (episodeInfoText != null)
        {
            string conditionName = GetConditionDisplayName();
            int currentTrial = ExperimentConfig.Instance?.CurrentTrialNumber ?? (currentEpisodeId + 1);
            int totalTrials = ExperimentConfig.Instance?.NumberOfTrials ?? 1;
            episodeInfoText.text = $"[{conditionName}] Episode {currentTrial}/{totalTrials}";
        }
    }

    /// <summary>
    /// 現在の実験条件の表示名を取得する
    /// </summary>
    private string GetConditionDisplayName()
    {
        if (ExperimentConfig.Instance == null)
        {
            return "Default";
        }

        var config = ExperimentConfig.Instance;

        // 実験IDを取得
        string expId = GetExperimentId(config);

        // エージェントタイプ
        string agentType = config.SelectedAgentType == ExperimentConfig.AgentType.LLM ? "LLM" : "Rule";

        // バイアス条件
        string biasName = config.SelectedBiasCondition switch
        {
            ExperimentConfig.BiasCondition.None => "",
            ExperimentConfig.BiasCondition.NormalcyBias => "Normalcy",
            ExperimentConfig.BiasCondition.ConformityBias => "Conformity",
            ExperimentConfig.BiasCondition.Combined => "Combined",
            _ => ""
        };

        // 情報戦略
        string strategyName = config.SelectedInformationStrategy switch
        {
            ExperimentConfig.InformationStrategy.Standard => "",
            ExperimentConfig.InformationStrategy.Urgent => "Urgent",
            ExperimentConfig.InformationStrategy.Detailed => "Detailed",
            ExperimentConfig.InformationStrategy.DetailedUrgent => "Detailed+Urgent",
            _ => ""
        };

        // 条件名を組み立て
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(expId)) parts.Add(expId);
        parts.Add(agentType);
        if (!string.IsNullOrEmpty(biasName)) parts.Add(biasName);
        if (!string.IsNullOrEmpty(strategyName)) parts.Add(strategyName);

        return string.Join("/", parts);
    }

    /// <summary>
    /// 現在の設定から実験IDを推定する
    /// </summary>
    private string GetExperimentId(ExperimentConfig config)
    {
        // バッチプリセットから直接IDを取得
        string presetId = config.BatchPreset switch
        {
            ExperimentConfig.BatchExperimentPreset.Exp1_A_LLM => "1-A",
            ExperimentConfig.BatchExperimentPreset.Exp1_B_RuleBased => "1-B",
            ExperimentConfig.BatchExperimentPreset.Exp2_1_Baseline => "2-1",
            ExperimentConfig.BatchExperimentPreset.Exp2_2_NormalcyBias => "2-2",
            ExperimentConfig.BatchExperimentPreset.Exp2_3_ConformityBias => "2-3",
            ExperimentConfig.BatchExperimentPreset.Exp2_4_CombinedBias => "2-4",
            ExperimentConfig.BatchExperimentPreset.Exp3_A_Standard => "3-A",
            ExperimentConfig.BatchExperimentPreset.Exp3_B_Urgent => "3-B",
            ExperimentConfig.BatchExperimentPreset.Exp3_C_Detailed => "3-C",
            ExperimentConfig.BatchExperimentPreset.Exp3_D_DetailedUrgent => "3-D",
            _ => ""
        };

        if (!string.IsNullOrEmpty(presetId)) return presetId;

        // プリセットがNoneの場合、設定値から推定
        // 実験1: エージェントタイプのみで判断
        if (config.SelectedBiasCondition == ExperimentConfig.BiasCondition.None &&
            config.SelectedInformationStrategy == ExperimentConfig.InformationStrategy.Standard)
        {
            return config.SelectedAgentType == ExperimentConfig.AgentType.LLM ? "1-A" : "1-B";
        }

        // 実験2: バイアス条件で判断
        if (config.SelectedBiasCondition != ExperimentConfig.BiasCondition.None)
        {
            return config.SelectedBiasCondition switch
            {
                ExperimentConfig.BiasCondition.NormalcyBias => "2-2",
                ExperimentConfig.BiasCondition.ConformityBias => "2-3",
                ExperimentConfig.BiasCondition.Combined => "2-4",
                _ => "2-1"
            };
        }

        // 実験3: 情報戦略で判断
        if (config.SelectedInformationStrategy != ExperimentConfig.InformationStrategy.Standard)
        {
            return config.SelectedInformationStrategy switch
            {
                ExperimentConfig.InformationStrategy.Urgent => "3-B",
                ExperimentConfig.InformationStrategy.Detailed => "3-C",
                ExperimentConfig.InformationStrategy.DetailedUrgent => "3-D",
                _ => "3-A"
            };
        }

        return "";
    }

    /// <summary>
    /// 現在の避難完了率を取得する
    /// </summary>
    /// <returns>現在の避難完了率: 0～1</returns>
    private float GetCurrentEvacueeRate() {
        int evacueeSize = Evacuees.Count;
        if (evacueeSize == 0) return 0f;
        int evacuatedSize = 0;
        foreach (var evacuee in Evacuees) {
            if (!evacuee.activeSelf) {
                evacuatedSize++;
            }
        }
        return (float)evacuatedSize / evacueeSize;
    }



    /// <summary>
    /// 避難者のランダムスポーン範囲を描画する
    /// </summary>
    private static void DrawWireCircle(Vector3 center, float radius, int segments = 36) {
        float angle = 0f;
        float angleStep = 360f / segments;

        Vector3 prevPoint = center + new Vector3(radius, 0, 0); // 初期点

        for (int i = 1; i <= segments; i++) {
            angle += angleStep;
            float rad = Mathf.Deg2Rad * angle;

            Vector3 newPoint = center + new Vector3(Mathf.Cos(rad) * radius, 5, Mathf.Sin(rad) * radius);
            Gizmos.DrawLine(prevPoint, newPoint);

            prevPoint = newPoint; // 次の線を描画するために現在の点を更新
        }
    }


    /// <summary>
    /// 属性情報から避難所の収容人数を取得する
    /// 【計算式】
    /// 収容可能人数＝ 床総面積㎡×0.8÷1.65㎡
    /// ※出典：https://manboukama.ldblog.jp/archives/50540532.html
    /// </summary>
    /// <param name="shelterBldg">避難所のGameObject</param>
    /// <returns>避難所の収容人数(設定パラメータによりスケーリングされます)</returns>
    private int GetAccSize(GameObject shelterBldg) {
        double? totalFloorSize = null;
        bool isErrorValue = false; // -9999が検出されたかどうかのフラグ
        
        // PLATEAU City Objectから、建物の高さを取得し、避難所の収容人数を動的に設定する
        var cityObjectGroup = shelterBldg.GetComponent<PLATEAUCityObjectGroup>();
        
        // PLATEAUCityObjectGroupの存在確認
        if(cityObjectGroup == null) {
            Debug.LogWarning($"[EnvManager] GetAccSize: {shelterBldg.name} - PLATEAUCityObjectGroup component not found. Using default value.");
            return Mathf.Max(1, (int)((250 * 0.8 / 1.65) * AccSimulateScale));
        }
        
        // CityObjectsの存在確認
        if(cityObjectGroup.CityObjects == null || 
           cityObjectGroup.CityObjects.rootCityObjects == null || 
           cityObjectGroup.CityObjects.rootCityObjects.Count == 0) {
            Debug.LogWarning($"[EnvManager] GetAccSize: {shelterBldg.name} - CityObjects data not found. Using default value.");
            return Mathf.Max(1, (int)((250 * 0.8 / 1.65) * AccSimulateScale));
        }
        
        var rootCityObject = cityObjectGroup.CityObjects.rootCityObjects[0];

        // Newtonsoft.Jsonを使用して、CityObjectの属性情報クラスにデシリアライズして取得
        var cityObjectJsonStr = JsonConvert.SerializeObject(rootCityObject);
        var attributes = JsonConvert.DeserializeObject<RootObject>(cityObjectJsonStr).Attributes;
        
        // 属性情報の存在確認
        if(attributes == null) {
            Debug.LogWarning($"[EnvManager] GetAccSize: {shelterBldg.name} - Attributes not found. Using default value.");
            return Mathf.Max(1, (int)((250 * 0.8 / 1.65) * AccSimulateScale));
        }
        
        // 属性値リストを巡回し、床総面積から収容人数を算出
        foreach(var attribute in attributes) {
            if(attribute.Key == "uro:buildingDetailAttribute") {
                foreach(var detailAttr in attribute.AttributeSetValue) {
                    // パターン1: 3層構造（uro:buildingDetailAttribute → uro:BuildingDetailAttribute → uro:totalFloorArea）
                    if(detailAttr.Key == "uro:BuildingDetailAttribute") {
                        if(detailAttr.AttributeSetValue != null) {
                            foreach(var uroAttr in detailAttr.AttributeSetValue) {
                                if(uroAttr.Key == "uro:totalFloorArea") {
                                    if(double.TryParse(uroAttr.Value.ToString(), out double parsedValue)) {
                                        // -9999の場合は特別に処理
                                        if(parsedValue == -9999) {
                                            isErrorValue = true;
                                            Debug.Log($"[EnvManager] GetAccSize: {shelterBldg.name} - totalFloorArea is -9999 (unknown), using default value");
                                        } else if(parsedValue > 0) {
                                            totalFloorSize = parsedValue;
                                            Debug.Log($"[EnvManager] GetAccSize: {shelterBldg.name} - totalFloorArea found: {parsedValue}");
                                        } else {
                                            Debug.LogWarning($"[EnvManager] GetAccSize: {shelterBldg.name} - Invalid totalFloorArea value: {parsedValue} (negative or zero)");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // パターン2: 2層構造（uro:buildingDetailAttribute → uro:totalFloorArea）
                    else if(detailAttr.Key == "uro:totalFloorArea") {
                        if(double.TryParse(detailAttr.Value.ToString(), out double parsedValue)) {
                            // -9999の場合は特別に処理
                            if(parsedValue == -9999) {
                                isErrorValue = true;
                                Debug.Log($"[EnvManager] GetAccSize: {shelterBldg.name} - totalFloorArea is -9999 (unknown), using default value");
                            } else if(parsedValue > 0) {
                                totalFloorSize = parsedValue;
                                Debug.Log($"[EnvManager] GetAccSize: {shelterBldg.name} - totalFloorArea found: {parsedValue}");
                            } else {
                                Debug.LogWarning($"[EnvManager] GetAccSize: {shelterBldg.name} - Invalid totalFloorArea value: {parsedValue} (negative or zero)");
                            }
                        }
                    }
                }
            }
        }

        // -9999が検出された場合の処理
        if(isErrorValue) {
            // デフォルト値: 250㎡相当の建物を想定
            int defaultCapacity = Mathf.Max(1, (int)((250 * 0.8 / 1.65) * AccSimulateScale));
            Debug.Log($"[EnvManager] GetAccSize: {shelterBldg.name} - Using default capacity: {defaultCapacity} people (totalFloorArea was -9999)");
            return defaultCapacity;
        }
        
        // nullの場合（データが取得できなかった場合）の処理
        if(totalFloorSize == null) {
            Debug.LogWarning($"[EnvManager] GetAccSize: {shelterBldg.name} - totalFloorArea not found in attributes. Using default value (250㎡相当).");
            // デフォルト値を返す（エラーで止めずに動作を継続）
            int defaultCapacity = Mathf.Max(1, (int)((250 * 0.8 / 1.65) * AccSimulateScale));
            return defaultCapacity;
        } else {
            // 収容可能人数＝総面積×0.8÷1.65㎡とする
            int capacity = (int)((totalFloorSize * 0.8 / 1.65) * AccSimulateScale);
            Debug.Log($"[EnvManager] GetAccSize: {shelterBldg.name} - Calculated capacity: {capacity} people (totalFloorArea: {totalFloorSize}㎡)");
            return capacity;
        }
    }


    private bool IsEvacuatedAll() {
        if (Evacuees.Count == 0)
        return false; // まだスポーンしていない場合は、避難完了とみなさない
        
        foreach (var evacuee in Evacuees) {
            if (evacuee.activeSelf) {
                return false;
            }
        }
        return true;
    }
}
}
