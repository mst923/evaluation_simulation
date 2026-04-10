using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using PLATEAU.CityInfo;

namespace EvacSim.Environment
{
/// <summary>
/// 建物空間インデックスのキャッシュデータを保持するScriptableObject
/// Editorで事前にBakeして起動時間を短縮する
/// </summary>
[CreateAssetMenu(fileName = "BuildingSpatialIndex", menuName = "Evacuation/Building Spatial Index")]
public class BuildingSpatialIndex : ScriptableObject
{
    /// <summary>
    /// 建物エントリ（キャッシュデータの1件）
    /// </summary>
    [Serializable]
    public class BuildingEntry
    {
        public string name;
        public Vector3 position;
        public string usage;
        public string majorUsage;
        public float height;
        public int floors;
        public string structureType;
        public string landUseType;
        public string gmlId;
        public int yearOfConstruction;  // 建築年（不明の場合は0）
        public bool isInferredStructure; // 構造種別が推定値かどうか
    }

    [Header("インデックスデータ")]
    [Tooltip("グリッドセルのサイズ（メートル）")]
    public float cellSize = 50f;

    [Tooltip("Bakeされた建物データ")]
    public List<BuildingEntry> buildings = new List<BuildingEntry>();

    [Header("整合性チェック用")]
    [Tooltip("Bake時のシーン内建物のハッシュ値")]
    public string bakedSceneHash;

    [Tooltip("Bake日時")]
    public string bakedTimestamp;

    [Tooltip("Bake時のシーン名")]
    public string bakedSceneName;

    /// <summary>
    /// キャッシュが有効かどうか（建物データが存在するか）
    /// </summary>
    public bool HasData => buildings != null && buildings.Count > 0;

    /// <summary>
    /// 現在のシーンとキャッシュの整合性をチェック
    /// </summary>
    public bool ValidateAgainstScene()
    {
        string currentHash = ComputeSceneHash();
        return currentHash == bakedSceneHash;
    }

    /// <summary>
    /// シーン内の建物からハッシュ値を計算
    /// </summary>
    public static string ComputeSceneHash()
    {
        var cityObjects = UnityEngine.Object.FindObjectsByType<PLATEAUCityObjectGroup>(FindObjectsSortMode.None);

        var sb = new StringBuilder();
        int buildingCount = 0;

        foreach (var cityObj in cityObjects)
        {
            if (!cityObj.name.StartsWith("bldg_"))
                continue;

            var renderer = cityObj.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            // 建物名と位置をハッシュに含める
            Vector3 center = renderer.bounds.center;
            sb.Append(cityObj.name);
            sb.Append(center.x.ToString("F2"));
            sb.Append(center.y.ToString("F2"));
            sb.Append(center.z.ToString("F2"));
            buildingCount++;
        }

        // 建物数もハッシュに含める
        sb.Append($"_COUNT_{buildingCount}");

        // MD5ハッシュを計算
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

        return $"建物数: {buildings.Count}件, " +
               $"Bake日時: {bakedTimestamp}, " +
               $"シーン: {bakedSceneName}";
    }
}
}
