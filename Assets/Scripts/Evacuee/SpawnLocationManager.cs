using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using EvacSim.Environment;

namespace EvacSim.Evacuees
{
/// <summary>
/// 建物カテゴリ別のスポーン位置を管理する
/// </summary>
public class SpawnLocationManager : MonoBehaviour
{
    private static SpawnLocationManager _instance;
    private Dictionary<BuildingCategory, List<EnvironmentalContextProvider.BuildingContext>> _buildingsByCategory;

    // タグベースのスポーン位置キャッシュ（エピソードリセットで消えないよう初回取得時に保存）
    private static List<(Vector3 position, string name)> _schoolPositionsCache;
    private static List<(Vector3 position, string name)> _preSchoolPositionsCache;

    [Header("設定")]
    [Tooltip("スポーン位置を地上に補正する")]
    public bool adjustToGround = true;
    public float groundYOffset = 0.5f; // 地面からの高さオフセット
    
    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    /// <summary>
    /// シングルトンインスタンスをリセットし、建物カテゴリを再分類する
    /// バッチ実験のエピソード遷移時に呼び出す
    /// </summary>
    public static void ResetInstance()
    {
        if (_instance != null)
        {
            _instance._buildingsByCategory = null;
            Debug.Log("[SpawnLocationManager] インスタンスをリセットしました。次回アクセス時に再初期化されます。");
        }
    }

    /// <summary>
    /// 強制的に建物カテゴリを再分類する
    /// エピソード開始時に呼び出すことで、古いデータをクリアして再取得する
    /// </summary>
    public static void ForceReinitialize()
    {
        if (_instance != null)
        {
            Debug.Log("[SpawnLocationManager] 強制再初期化を実行します");
            _instance.CategorizeAllBuildings();
        }
        else
        {
            Debug.LogWarning("[SpawnLocationManager] インスタンスが存在しないため、再初期化できません");
        }
    }
    
    void Start()
    {
        CategorizeAllBuildings();
    }
    
    /// <summary>
    /// 全建物をカテゴリごとに分類
    /// </summary>
    public void CategorizeAllBuildings()
    {
        _buildingsByCategory = new Dictionary<BuildingCategory, List<EnvironmentalContextProvider.BuildingContext>>();
        
        // 各カテゴリのリストを初期化
        foreach (BuildingCategory category in System.Enum.GetValues(typeof(BuildingCategory)))
        {
            _buildingsByCategory[category] = new List<EnvironmentalContextProvider.BuildingContext>();
        }
        
        // EnvironmentalContextProviderから全建物を取得
        var envProvider = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (envProvider == null)
        {
            Debug.LogError("[SpawnLocationManager] EnvironmentalContextProviderが見つかりません");
            return;
        }
        
        var allBuildings = envProvider.GetAllBuildings();
        
        foreach (var building in allBuildings)
        {
            BuildingCategory category = BuildingCategorizer.Categorize(building);
            _buildingsByCategory[category].Add(building);
        }
        
        // デバッグログ：カテゴリ別の建物数を表示
        Debug.Log("[SpawnLocationManager] 建物カテゴリ分類完了:");
        foreach (var kvp in _buildingsByCategory)
        {
            if (kvp.Value.Count > 0)
            {
                Debug.Log($"  - {BuildingCategorizer.GetCategoryDisplayName(kvp.Key)}: {kvp.Value.Count}件");
            }
        }
    }
    
    /// <summary>
    /// 初期化が完了しているかチェックし、未初期化の場合は初期化する（lazy initialization）
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_instance == null)
        {
            Debug.LogWarning("[SpawnLocationManager] インスタンスが存在しません");
            return;
        }
        
        if (_instance._buildingsByCategory == null)
        {
            Debug.Log("[SpawnLocationManager] 初期化が未完了のため、遅延初期化を実行します");
            _instance.CategorizeAllBuildings();
            
            // それでも null のまま（EnvironmentalContextProvider が無いなど）の場合を防御
            if (_instance._buildingsByCategory == null)
            {
                Debug.LogWarning("[SpawnLocationManager] 建物カテゴリ辞書の初期化に失敗しました");
            }
        }
    }
    
    /// <summary>
    /// 指定カテゴリの建物からランダムにスポーン位置を取得
    /// </summary>
    public static Vector3 GetRandomSpawnPosition(BuildingCategory category)
    {
        EnsureInitialized();
        
        if (_instance == null || _instance._buildingsByCategory == null || !_instance._buildingsByCategory.ContainsKey(category))
        {
            Debug.LogWarning($"[SpawnLocationManager] カテゴリ {category} の建物が見つかりません");
            return Vector3.zero;
        }
        
        var buildings = _instance._buildingsByCategory[category];
        if (buildings.Count == 0)
        {
            Debug.LogWarning($"[SpawnLocationManager] カテゴリ {category} に建物がありません");
            return Vector3.zero;
        }
        
        // ランダムに選択
        int randomIndex = Random.Range(0, buildings.Count);
        Vector3 position = buildings[randomIndex].position;
        
        // 地上に補正
        if (_instance.adjustToGround)
        {
            position.y = _instance.groundYOffset;
        }
        
        return position;
    }
    
    /// <summary>
    /// 指定カテゴリの建物情報をランダムに取得（建物名も含む）
    /// </summary>
    public static (Vector3 position, string buildingName) GetRandomSpawnPositionWithName(BuildingCategory category)
    {
        EnsureInitialized();
        
        if (_instance == null || _instance._buildingsByCategory == null || !_instance._buildingsByCategory.ContainsKey(category))
        {
            Debug.LogWarning($"[SpawnLocationManager] カテゴリ {category} の建物が見つかりません");
            return (Vector3.zero, "不明");
        }
        
        var buildings = _instance._buildingsByCategory[category];
        if (buildings.Count == 0)
        {
            Debug.LogWarning($"[SpawnLocationManager] カテゴリ {category} に建物がありません");
            return (Vector3.zero, "不明");
        }
        
        // ランダムに選択
        int randomIndex = Random.Range(0, buildings.Count);
        var building = buildings[randomIndex];
        Vector3 position = building.position;
        
        // 地上に補正
        if (_instance.adjustToGround)
        {
            position.y = _instance.groundYOffset;
        }
        
        return (position, building.name);
    }
    
    /// <summary>
    /// 指定カテゴリの建物数を取得
    /// </summary>
    public static int GetBuildingCount(BuildingCategory category)
    {
        EnsureInitialized();
        
        if (_instance == null || _instance._buildingsByCategory == null || !_instance._buildingsByCategory.ContainsKey(category))
            return 0;
        
        return _instance._buildingsByCategory[category].Count;
    }
    
    /// <summary>
    /// 全カテゴリの建物数を取得（デバッグ用）
    /// </summary>
    public static Dictionary<BuildingCategory, int> GetAllCategoryCounts()
    {
        EnsureInitialized();

        if (_instance == null || _instance._buildingsByCategory == null)
            return new Dictionary<BuildingCategory, int>();

        return _instance._buildingsByCategory.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Count
        );
    }

    /// <summary>
    /// Schoolタグ付きオブジェクトからランダムにスポーン位置を取得
    /// PLATEAUデータに学校がない場合のフォールバック用
    /// 初回取得時にキャッシュし、以降はキャッシュから返す（エピソードリセット対策）
    /// </summary>
    public static (Vector3 position, string buildingName) GetSchoolSpawnPosition()
    {
        // キャッシュがなければ初回取得
        if (_schoolPositionsCache == null)
        {
            _schoolPositionsCache = new List<(Vector3, string)>();
            try
            {
                GameObject[] schools = GameObject.FindGameObjectsWithTag("School");
                foreach (var school in schools)
                {
                    Vector3 pos = school.transform.position;
                    if (_instance != null && _instance.adjustToGround)
                    {
                        pos.y = _instance.groundYOffset;
                    }
                    _schoolPositionsCache.Add((pos, school.name));
                }
                Debug.Log($"[SpawnLocationManager] Schoolタグ位置をキャッシュ: {_schoolPositionsCache.Count}件");
            }
            catch (UnityException)
            {
                // Schoolタグが存在しない場合は無視
            }
        }

        // キャッシュからランダムに返す
        if (_schoolPositionsCache.Count > 0)
        {
            int randomIndex = Random.Range(0, _schoolPositionsCache.Count);
            return _schoolPositionsCache[randomIndex];
        }

        return (Vector3.zero, "不明");
    }

    /// <summary>
    /// PreSchoolタグ付きオブジェクトからランダムにスポーン位置を取得
    /// PLATEAUデータに保育園がない場合のフォールバック用
    /// 初回取得時にキャッシュし、以降はキャッシュから返す（エピソードリセット対策）
    /// </summary>
    public static (Vector3 position, string buildingName) GetPreSchoolSpawnPosition()
    {
        // キャッシュがなければ初回取得
        if (_preSchoolPositionsCache == null)
        {
            _preSchoolPositionsCache = new List<(Vector3, string)>();
            try
            {
                GameObject[] preschools = GameObject.FindGameObjectsWithTag("PreSchool");
                foreach (var preschool in preschools)
                {
                    Vector3 pos = preschool.transform.position;
                    if (_instance != null && _instance.adjustToGround)
                    {
                        pos.y = _instance.groundYOffset;
                    }
                    _preSchoolPositionsCache.Add((pos, preschool.name));
                }
                Debug.Log($"[SpawnLocationManager] PreSchoolタグ位置をキャッシュ: {_preSchoolPositionsCache.Count}件");
            }
            catch (UnityException)
            {
                // PreSchoolタグが存在しない場合は無視
            }
        }

        // キャッシュからランダムに返す
        if (_preSchoolPositionsCache.Count > 0)
        {
            int randomIndex = Random.Range(0, _preSchoolPositionsCache.Count);
            return _preSchoolPositionsCache[randomIndex];
        }

        return (Vector3.zero, "不明");
    }

    /// <summary>
    /// 指定カテゴリの建物情報をランダムに取得（タグベースのフォールバック付き）
    /// School/PreSchoolカテゴリはタグから直接取得（PLATEAUデータには含まれないため）
    /// </summary>
    public static (Vector3 position, string buildingName) GetRandomSpawnPositionWithNameAndFallback(BuildingCategory category)
    {
        // School/PreSchoolカテゴリはタグから直接取得（PLATEAUデータには含まれない）
        if (category == BuildingCategory.School)
        {
            return GetSchoolSpawnPosition();
        }
        else if (category == BuildingCategory.PreSchool)
        {
            return GetPreSchoolSpawnPosition();
        }

        // その他のカテゴリはPLATEAUデータから取得
        return GetRandomSpawnPositionWithName(category);
    }
}
}




