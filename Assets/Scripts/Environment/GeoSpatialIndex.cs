using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using PLATEAU.CityInfo;

namespace EvacSim.Environment
{
/// <summary>
/// 地理空間インデックスのキャッシュデータを保持するScriptableObject
/// 道路、土地利用、災害リスク区域等のデータをEditorで事前にBakeして起動時間を短縮する
/// </summary>
[CreateAssetMenu(fileName = "GeoSpatialIndex", menuName = "Evacuation/Geo Spatial Index")]
public class GeoSpatialIndex : ScriptableObject
{
    #region Entry Classes

    /// <summary>
    /// 道路エントリ
    /// </summary>
    [Serializable]
    public class RoadEntry
    {
        public string name;
        public Vector3 position;
        public int functionCode;        // 道路機能コード
        public string functionName;     // 日本語名
        public int sectionType;         // 区間種別
        public string sectionTypeName;  // 日本語名
        public string gmlId;
    }

    /// <summary>
    /// 土地利用エントリ
    /// </summary>
    [Serializable]
    public class LandUseEntry
    {
        public string name;
        public Vector3 position;
        public Bounds bounds;           // 土地利用区域の境界ボックス
        public string landUseClass;
        public string landUseClassName;
        public string usage;
        public float area;
        public string gmlId;
    }

    /// <summary>
    /// 津波リスクエントリ
    /// </summary>
    [Serializable]
    public class TsunamiRiskEntry
    {
        public string name;
        public Vector3 position;
        public Bounds bounds;           // 浸水想定区域の境界ボックス
        public string description;
        public string rank;
        public float estimatedDepth;
        public string gmlId;
    }

    /// <summary>
    /// 土砂災害リスクエントリ
    /// </summary>
    [Serializable]
    public class LandslideRiskEntry
    {
        public string name;
        public Vector3 position;
        public Bounds bounds;           // 警戒区域の境界ボックス
        public int areaType;            // 区域タイプ
        public string areaTypeName;
        public int descriptionCode;     // 災害種別
        public string descriptionName;
        public bool isSpecialZone;
        public string gmlId;
    }

    /// <summary>
    /// 地形エントリ（DEMメッシュの参照情報）
    /// </summary>
    [Serializable]
    public class TerrainEntry
    {
        public string name;
        public Bounds bounds;           // DEMの境界ボックス
        public float minElevation;
        public float maxElevation;
        public string gmlId;
    }

    #endregion

    #region Data Fields

    [Header("インデックス設定")]
    [Tooltip("グリッドセルのサイズ（メートル）")]
    public float cellSize = 50f;

    [Header("道路データ")]
    public List<RoadEntry> roads = new List<RoadEntry>();

    [Header("土地利用データ")]
    public List<LandUseEntry> landUses = new List<LandUseEntry>();

    [Header("津波リスクデータ")]
    public List<TsunamiRiskEntry> tsunamiRisks = new List<TsunamiRiskEntry>();

    [Header("土砂災害リスクデータ")]
    public List<LandslideRiskEntry> landslideRisks = new List<LandslideRiskEntry>();

    [Header("地形データ")]
    public List<TerrainEntry> terrains = new List<TerrainEntry>();

    [Header("メタデータ")]
    public string bakedTimestamp;
    public string bakedSceneName;
    public string bakedSceneHash;

    #endregion

    #region Properties

    /// <summary>
    /// キャッシュが有効かどうか
    /// </summary>
    public bool HasData =>
        (roads != null && roads.Count > 0) ||
        (landUses != null && landUses.Count > 0) ||
        (tsunamiRisks != null && tsunamiRisks.Count > 0) ||
        (landslideRisks != null && landslideRisks.Count > 0) ||
        (terrains != null && terrains.Count > 0);

    public int RoadCount => roads?.Count ?? 0;
    public int LandUseCount => landUses?.Count ?? 0;
    public int TsunamiRiskCount => tsunamiRisks?.Count ?? 0;
    public int LandslideRiskCount => landslideRisks?.Count ?? 0;
    public int TerrainCount => terrains?.Count ?? 0;

    #endregion

    #region Methods

    /// <summary>
    /// 現在のシーンとキャッシュの整合性をチェック
    /// </summary>
    public bool ValidateAgainstScene()
    {
        string currentHash = ComputeSceneHash();
        return currentHash == bakedSceneHash;
    }

    /// <summary>
    /// シーン内のPLATEAUオブジェクトからハッシュ値を計算
    /// </summary>
    public static string ComputeSceneHash()
    {
        var cityObjects = UnityEngine.Object.FindObjectsByType<PLATEAUCityObjectGroup>(FindObjectsSortMode.None);

        var sb = new StringBuilder();
        int tranCount = 0, luseCount = 0, tnmCount = 0, lsldCount = 0, demCount = 0;

        foreach (var cityObj in cityObjects)
        {
            var renderer = cityObj.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            Vector3 center = renderer.bounds.center;

            if (cityObj.name.StartsWith("tran_"))
            {
                sb.Append(cityObj.name);
                sb.Append(center.x.ToString("F1"));
                sb.Append(center.z.ToString("F1"));
                tranCount++;
            }
            else if (cityObj.name.StartsWith("luse_"))
            {
                sb.Append(cityObj.name);
                luseCount++;
            }
            else if (cityObj.name.StartsWith("tnm_"))
            {
                sb.Append(cityObj.name);
                tnmCount++;
            }
            else if (cityObj.name.StartsWith("lsld_"))
            {
                sb.Append(cityObj.name);
                lsldCount++;
            }
            else if (cityObj.name.StartsWith("dem_"))
            {
                sb.Append(cityObj.name);
                demCount++;
            }
        }

        sb.Append($"_TRAN_{tranCount}_LUSE_{luseCount}_TNM_{tnmCount}_LSLD_{lsldCount}_DEM_{demCount}");

        using (var md5 = MD5.Create())
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] hashBytes = md5.ComputeHash(inputBytes);

            var hashSb = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                hashSb.Append(b.ToString("x2"));
            }
            return hashSb.ToString();
        }
    }

    /// <summary>
    /// 統計情報を取得
    /// </summary>
    public string GetStatistics()
    {
        if (!HasData)
            return "データなし";

        return $"道路: {RoadCount}件, " +
               $"土地利用: {LandUseCount}件, " +
               $"津波リスク: {TsunamiRiskCount}件, " +
               $"土砂災害リスク: {LandslideRiskCount}件, " +
               $"地形: {TerrainCount}件, " +
               $"Bake日時: {bakedTimestamp}";
    }

    /// <summary>
    /// 全データをクリア
    /// </summary>
    public void ClearAll()
    {
        roads.Clear();
        landUses.Clear();
        tsunamiRisks.Clear();
        landslideRisks.Clear();
        terrains.Clear();
        bakedTimestamp = "";
        bakedSceneName = "";
        bakedSceneHash = "";
    }

    #endregion
}
}
