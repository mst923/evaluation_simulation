using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using PLATEAU.CityInfo;
using Newtonsoft.Json;
using EvacSim.Core;
using EvacSim.Evacuee;

namespace EvacSim.Shelter
{
/// <summary>
/// 避難所に関するスクリプト（オブジェクト１台分）
/// 現在の収容人数や、受け入れ可否等のデータを用意
/// </summary>
public class Shelter : MonoBehaviour{
    [Header("避難所情報")]
    [Tooltip("避難所の表示名（例: 豊間小学校）")]
    public string displayName = ""; // 表示名

    [Tooltip("避難所の説明（例: 海抜20m、耐震構造）")]
    [TextArea(2, 4)]
    public string description = ""; // 説明文

    [Tooltip("避難所の海抜（メートル）。0の場合はY座標から自動計算")]
    public float elevationMeters = 0f; // 海抜（メートル）
    
    [Header("収容情報")]
    [Tooltip("trueの場合、PLATEAUの床面積データから自動計算します")]
    public bool autoCalculateCapacity = true; // 自動計算フラグ
    
    [Tooltip("手動で設定する収容人数（autoCalculateCapacity=falseの場合のみ有効）")]
    [SerializeField]
    private int manualMaxCapacity = 10; // 手動設定値
    
    [Tooltip("床面積から収容人数を計算する際のスケール係数（デフォルト: 1.0）")]
    public float capacityScale = 1.0f; // スケール係数
    
    /// <summary>
    /// 最大収容人数（自動計算または手動設定）
    /// </summary>
    public int MaxCapacity 
    {
        get 
        {
            if (autoCalculateCapacity)
            {
                return CalculateCapacityFromFloorArea();
            }
            else
            {
                return manualMaxCapacity;
            }
        }
        set 
        {
            // 手動で設定した場合は自動計算を無効化
            autoCalculateCapacity = false;
            manualMaxCapacity = value;
        }
    }
    
    public int NowAccCount = 0; //現在の収容人数
    
    // 現在の受け入れ可能人数：最大収容人数 - 現在の収容人数（常に最新の値を計算して返す）
    public int currentCapacity => MaxCapacity - NowAccCount;

    public string uuid; //タワーの識別子
    /**Events */
    public delegate void AcceptRejected(int NowAccCount) ; //収容定員が超過した時に発火する
    public AcceptRejected onRejected;

    private EnvManager _env;

    // NavMesh上の位置キャッシュ（パフォーマンス最適化のため）
    private Vector3? _cachedNavMeshPosition = null;
    private bool _navMeshPositionCalculated = false;

    // 収容状況ログ用：最後に出力した閾値（残りキャパシティが75%, 50%, 25%, 5%を下回った時）
    private int _lastLoggedThreshold = 100;

    void Start() {
        _env = GetComponentInParent<EnvManager>();
        _env.OnEndEpisode += (float _, float __) => {
            // 環境側のエピソード終了時に収容人数をリセット
            NowAccCount = 0;
            // NavMeshキャッシュもリセット
            _navMeshPositionCalculated = false;
            _cachedNavMeshPosition = null;
            // 収容状況ログ閾値もリセット（100%からスタート）
            _lastLoggedThreshold = 100;
            string shelterDisplayName = !string.IsNullOrEmpty(displayName) ? displayName : gameObject.name;
            Debug.Log($"[Shelter] {shelterDisplayName}: エピソード終了 - 収容人数をリセットしました。MaxCapacity: {MaxCapacity}, NowAccCount: {NowAccCount}, CurrentCapacity: {currentCapacity}");
        };
        
        // 初期値をログ出力
        Debug.Log($"[Shelter] {gameObject.name}: 初期化完了 - MaxCapacity: {MaxCapacity}, NowAccCount: {NowAccCount}, CurrentCapacity: {currentCapacity}");
    }

    /// <summary>
    /// PLATEAUの床面積データから収容人数を計算
    /// 【計算式】
    /// 収容可能人数＝ 床総面積㎡×0.8÷1.65㎡ × capacityScale
    /// ※出典：https://manboukama.ldblog.jp/archives/50540532.html
    /// </summary>
    /// <returns>計算された収容人数</returns>
    public int CalculateCapacityFromFloorArea()
    {
        double? totalFloorArea = GetTotalFloorArea();
        
        if (totalFloorArea == null || totalFloorArea <= 0)
        {
            // 床面積が取得できない場合はデフォルト値（250㎡相当）
            return Mathf.Max(1, (int)((250 * 0.8 / 1.65) * capacityScale));
        }
        
        // 計算式を適用
        int capacity = Mathf.Max(1, (int)((totalFloorArea.Value * 0.8 / 1.65) * capacityScale));
        return capacity;
    }
    
    /// <summary>
    /// PLATEAUデータから床総面積(uro:totalFloorArea)を取得
    /// </summary>
    /// <returns>床総面積（㎡）、取得できない場合はnull</returns>
    public double? GetTotalFloorArea()
    {
        var cityObjectGroup = GetComponent<PLATEAUCityObjectGroup>();
        
        if (cityObjectGroup == null || 
            cityObjectGroup.CityObjects == null || 
            cityObjectGroup.CityObjects.rootCityObjects == null || 
            cityObjectGroup.CityObjects.rootCityObjects.Count == 0)
        {
            return null;
        }
        
        try
        {
            var rootCityObject = cityObjectGroup.CityObjects.rootCityObjects[0];
            var cityObjectJsonStr = JsonConvert.SerializeObject(rootCityObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(cityObjectJsonStr);
            
            if (rootData?.Attributes == null)
            {
                return null;
            }
            
            // 属性値リストを巡回して床総面積を検索
            foreach (var attribute in rootData.Attributes)
            {
                if (attribute.Key == "uro:buildingDetailAttribute")
                {
                    foreach (var detailAttr in attribute.AttributeSetValue)
                    {
                        // パターン1: 3層構造（uro:BuildingDetailAttribute → uro:totalFloorArea）
                        if (detailAttr.Key == "uro:BuildingDetailAttribute" && detailAttr.AttributeSetValue != null)
                        {
                            foreach (var uroAttr in detailAttr.AttributeSetValue)
                            {
                                if (uroAttr.Key == "uro:totalFloorArea")
                                {
                                    if (double.TryParse(uroAttr.Value.ToString(), out double parsedValue))
                                    {
                                        // -9999は「不明」を意味するのでnullとして扱う
                                        if (parsedValue == -9999)
                                        {
                                            return null;
                                        }
                                        if (parsedValue > 0)
                                        {
                                            return parsedValue;
                                        }
                                    }
                                }
                            }
                        }
                        // パターン2: 2層構造（uro:totalFloorArea）
                        else if (detailAttr.Key == "uro:totalFloorArea")
                        {
                            if (double.TryParse(detailAttr.Value.ToString(), out double parsedValue))
                            {
                                if (parsedValue == -9999)
                                {
                                    return null;
                                }
                                if (parsedValue > 0)
                                {
                                    return parsedValue;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Shelter] {gameObject.name}: 床面積の取得に失敗しました - {ex.Message}");
        }
        
        return null;
    }

    /// <summary>
    /// 避難所の海抜（メートル）を取得
    /// elevationMetersが設定されている場合はその値を返し、
    /// 0の場合はY座標を海抜として返す
    /// </summary>
    /// <returns>海抜（メートル）</returns>
    public float GetElevation()
    {
        if (elevationMeters > 0f)
        {
            return elevationMeters;
        }
        // elevationMetersが設定されていない場合はY座標を使用
        return transform.position.y;
    }

    /// <summary>
    /// ShelterPoint（子オブジェクト）のNavMesh上の位置を取得
    /// キャッシュを使用してパフォーマンスを最適化
    /// </summary>
    /// <returns>NavMesh上の位置、取得できない場合はnull</returns>
    public Vector3? GetNavMeshPosition()
    {
        try
        {
            // 既にキャッシュされている場合はそれを返す
            if (_navMeshPositionCalculated)
            {
                return _cachedNavMeshPosition;
            }
            
            // ShelterPointの位置を取得
            Transform pointTransform = transform.childCount > 0 ? transform.GetChild(0) : transform;
            if (pointTransform == null)
            {
                _navMeshPositionCalculated = true;
                return null;
            }
            
            Vector3 targetPosition = pointTransform.position;
            
            // NavMesh上の最も近い点を探す
            NavMeshHit hit;
            if (NavMesh.SamplePosition(targetPosition, out hit, 50f, NavMesh.AllAreas))
            {
                _cachedNavMeshPosition = hit.position;
            }
            else
            {
                _cachedNavMeshPosition = null;
            }
            
            _navMeshPositionCalculated = true;
            return _cachedNavMeshPosition;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Shelter] {gameObject.name}: GetNavMeshPosition failed - {ex.Message}");
            _navMeshPositionCalculated = true;
            _cachedNavMeshPosition = null;
            return null;
        }
    }

    /// <summary>
    /// リアルタイムで収容人数をチェック
    /// </summary>
    void Update() {
        if (currentCapacity <= 0) {
            onRejected?.Invoke(NowAccCount);
        }
    }

    /// <summary>
    /// 避難者オブジェクトが建物に到達したときに呼び出される。当たり関数
    /// </summary>
    /// <param name="other"></param>
    void OnTriggerEnter(Collider other) {
        string shelterDisplayName = !string.IsNullOrEmpty(displayName) ? displayName : gameObject.name;

        bool isEvacuee = other.CompareTag("Evacuee");
        if (isEvacuee) {
            Evacuee evacuee = other.GetComponent<Evacuee>();
            if (evacuee != null) {
                // 避難処理を実行（記録はEvacuee.Evacuation内で避難成功時にのみ行う）
                evacuee.Evacuation(this);

                // 収容率が閾値（5%, 25%, 50%, 75%）を超えた時のみログ出力
                LogCapacityIfThresholdCrossed(shelterDisplayName);
            } else {
                Debug.LogError($"[Shelter] {shelterDisplayName}: Evacuee component not found on {other.name}");
            }
        }
    }

    /// <summary>
    /// 残りキャパシティ率が閾値を下回った時のみログを出力
    /// 閾値: 75%, 50%, 25%, 5%（残りキャパシティがこれらを下回った時）
    /// </summary>
    private void LogCapacityIfThresholdCrossed(string shelterDisplayName)
    {
        if (MaxCapacity <= 0) return;

        // 残りキャパシティ率（残り収容可能人数 / 最大収容人数）を計算
        float remainingRate = (float)currentCapacity / MaxCapacity * 100f;

        // 閾値リスト（降順：75% → 50% → 25% → 5%）
        int[] thresholds = { 75, 50, 25, 5 };

        foreach (int threshold in thresholds)
        {
            // 残りキャパシティ率が閾値を下回り、かつまだこの閾値のログを出力していない場合
            if (remainingRate < threshold && _lastLoggedThreshold > threshold)
            {
                _lastLoggedThreshold = threshold;
                Debug.Log($"[Shelter] {shelterDisplayName}: 残り収容可能人数{threshold}%未満 - 収容可能人数: {MaxCapacity}, 残り収容可能人数: {currentCapacity}");
            }
        }
    }

    /// <summary>
    /// SimulationMetricsに避難完了を記録
    /// </summary>
    private void RecordEvacuationToMetrics(Evacuee evacuee)
    {
        var metrics = FindFirstObjectByType<SimulationMetrics>();
        if (metrics != null && _env != null)
        {
            string agentId = evacuee.EvacueeId ?? evacuee.gameObject.name;
            float evacuationTime = _env.CurrentTimeSec;
            string shelterName = !string.IsNullOrEmpty(displayName) ? displayName : gameObject.name;
            string agentType = evacuee.UseLLMDecision ? "LLM" : "RuleBased";

            metrics.RecordEvacuation(agentId, evacuationTime, shelterName, agentType);
        }
    }
}
}
