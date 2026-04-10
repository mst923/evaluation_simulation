using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using PLATEAU.CityInfo;
using Newtonsoft.Json;
using EvacSim.Environment;

/// <summary>
/// 地理空間インデックスをBake（事前構築）するEditorツール
/// 道路、土地利用、災害リスク区域等のデータを事前にインデックス化
/// </summary>
public class GeoSpatialIndexBaker : EditorWindow
{
    private const string DEFAULT_ASSET_PATH = "Assets/Config/GeoSpatialIndex.asset";
    private GeoSpatialIndex targetAsset;
    private float cellSize = 50f;

    // Bakeオプション
    private bool bakeRoads = true;
    private bool bakeLandUse = true;
    private bool bakeTsunamiRisk = true;
    private bool bakeLandslideRisk = true;
    private bool bakeTerrain = true;

    [MenuItem("Tools/Environment/Bake Geo Spatial Index")]
    public static void ShowWindow()
    {
        var window = GetWindow<GeoSpatialIndexBaker>("Geo Spatial Index Baker");
        window.minSize = new Vector2(450, 500);
    }

    [MenuItem("Tools/Environment/Validate Geo Spatial Index")]
    public static void ValidateIndex()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GeoSpatialIndex>(DEFAULT_ASSET_PATH);

        if (asset == null)
        {
            EditorUtility.DisplayDialog(
                "インデックスが見つかりません",
                $"地理空間インデックスアセットが存在しません。\n\n" +
                $"「Tools → Environment → Bake Geo Spatial Index」から作成してください。\n\n" +
                $"期待されるパス: {DEFAULT_ASSET_PATH}",
                "OK"
            );
            return;
        }

        bool isValid = asset.ValidateAgainstScene();

        if (isValid)
        {
            EditorUtility.DisplayDialog(
                "✅ 整合性OK",
                $"地理空間インデックスは現在のシーンと一致しています。\n\n" +
                asset.GetStatistics(),
                "OK"
            );
        }
        else
        {
            bool rebake = EditorUtility.DisplayDialog(
                "⚠️ 不整合を検出",
                $"地理空間インデックスが現在のシーンと一致しません。\n\n" +
                $"シーンのオブジェクトが変更された可能性があります。\n" +
                $"再Bakeすることを推奨します。\n\n" +
                $"キャッシュ情報:\n" +
                $"- Bake日時: {asset.bakedTimestamp}\n" +
                $"- シーン: {asset.bakedSceneName}",
                "再Bakeする", "キャンセル"
            );

            if (rebake)
            {
                BakeIndex(asset, true, true, true, true, true);
            }
        }
    }

    private void OnEnable()
    {
        targetAsset = AssetDatabase.LoadAssetAtPath<GeoSpatialIndex>(DEFAULT_ASSET_PATH);
        if (targetAsset != null)
        {
            cellSize = targetAsset.cellSize;
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("地理空間インデックス Baker", EditorStyles.boldLabel);
        GUILayout.Space(10);

        EditorGUILayout.HelpBox(
            "シーン内のPLATEAU地理データを事前にBakeして、\n" +
            "起動時間を大幅に短縮します。\n\n" +
            "対象: 道路(tran)、土地利用(luse)、津波リスク(tnm)、\n" +
            "土砂災害リスク(lsld)、地形(dem)",
            MessageType.Info
        );

        GUILayout.Space(10);

        // 設定
        EditorGUILayout.LabelField("設定", EditorStyles.boldLabel);
        cellSize = EditorGUILayout.FloatField("Cell Size (m)", cellSize);

        GUILayout.Space(5);

        // Bakeオプション
        EditorGUILayout.LabelField("Bake対象", EditorStyles.boldLabel);
        bakeRoads = EditorGUILayout.Toggle("道路 (tran_)", bakeRoads);
        bakeLandUse = EditorGUILayout.Toggle("土地利用 (luse_)", bakeLandUse);
        bakeTsunamiRisk = EditorGUILayout.Toggle("津波リスク (tnm_)", bakeTsunamiRisk);
        bakeLandslideRisk = EditorGUILayout.Toggle("土砂災害リスク (lsld_)", bakeLandslideRisk);
        bakeTerrain = EditorGUILayout.Toggle("地形 (dem_)", bakeTerrain);

        GUILayout.Space(10);

        // 現在のアセット情報
        EditorGUILayout.LabelField("現在のキャッシュ", EditorStyles.boldLabel);
        if (targetAsset != null && targetAsset.HasData)
        {
            EditorGUILayout.LabelField($"道路: {targetAsset.RoadCount}件");
            EditorGUILayout.LabelField($"土地利用: {targetAsset.LandUseCount}件");
            EditorGUILayout.LabelField($"津波リスク: {targetAsset.TsunamiRiskCount}件");
            EditorGUILayout.LabelField($"土砂災害リスク: {targetAsset.LandslideRiskCount}件");
            EditorGUILayout.LabelField($"地形: {targetAsset.TerrainCount}件");
            EditorGUILayout.LabelField($"Bake日時: {targetAsset.bakedTimestamp}");

            bool isValid = targetAsset.ValidateAgainstScene();
            if (isValid)
            {
                EditorGUILayout.HelpBox("✅ シーンと整合しています", MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox("⚠️ シーンと不整合があります。再Bakeを推奨します。", MessageType.Warning);
            }
        }
        else
        {
            EditorGUILayout.LabelField("キャッシュなし");
        }

        GUILayout.Space(20);

        // Bakeボタン
        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("🔥 Bake Geo Spatial Index", GUILayout.Height(40)))
        {
            DoBake();
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(10);

        // 検証ボタン
        if (GUILayout.Button("🔍 整合性をチェック", GUILayout.Height(30)))
        {
            ValidateIndex();
        }

        GUILayout.Space(10);

        // アセットを開くボタン
        if (targetAsset != null)
        {
            if (GUILayout.Button("📁 アセットを選択", GUILayout.Height(25)))
            {
                Selection.activeObject = targetAsset;
                EditorGUIUtility.PingObject(targetAsset);
            }
        }
    }

    private void DoBake()
    {
        if (targetAsset == null)
        {
            targetAsset = ScriptableObject.CreateInstance<GeoSpatialIndex>();

            string directory = System.IO.Path.GetDirectoryName(DEFAULT_ASSET_PATH);
            if (!AssetDatabase.IsValidFolder(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            AssetDatabase.CreateAsset(targetAsset, DEFAULT_ASSET_PATH);
        }

        targetAsset.cellSize = cellSize;
        BakeIndex(targetAsset, bakeRoads, bakeLandUse, bakeTsunamiRisk, bakeLandslideRisk, bakeTerrain);

        EditorUtility.SetDirty(targetAsset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Repaint();
    }

    private static void BakeIndex(GeoSpatialIndex asset, bool roads, bool landUse, bool tsunamiRisk, bool landslideRisk, bool terrain)
    {
        var startTime = DateTime.Now;

        asset.ClearAll();

        var cityObjects = FindObjectsByType<PLATEAUCityObjectGroup>(FindObjectsSortMode.None);

        EditorUtility.DisplayProgressBar("Baking Geo Spatial Index", "データを収集中...", 0);

        try
        {
            int totalCount = cityObjects.Length;
            int processedCount = 0;
            int roadCount = 0, luseCount = 0, tnmCount = 0, lsldCount = 0, demCount = 0;

            foreach (var cityObj in cityObjects)
            {
                processedCount++;

                if (processedCount % 100 == 0)
                {
                    EditorUtility.DisplayProgressBar(
                        "Baking Geo Spatial Index",
                        $"処理中... {processedCount}/{totalCount}",
                        (float)processedCount / totalCount
                    );
                }

                var renderer = cityObj.GetComponent<Renderer>();
                if (renderer == null)
                    continue;

                Vector3 center = renderer.bounds.center;
                Bounds bounds = renderer.bounds;

                // 道路
                if (roads && cityObj.name.StartsWith("tran_"))
                {
                    var entry = ExtractRoadEntry(cityObj, center);
                    if (entry != null)
                    {
                        asset.roads.Add(entry);
                        roadCount++;
                    }
                }
                // 土地利用
                else if (landUse && cityObj.name.StartsWith("luse_"))
                {
                    var entry = ExtractLandUseEntry(cityObj, center, bounds);
                    if (entry != null)
                    {
                        asset.landUses.Add(entry);
                        luseCount++;
                    }
                }
                // 津波リスク
                else if (tsunamiRisk && cityObj.name.StartsWith("tnm_"))
                {
                    var entry = ExtractTsunamiRiskEntry(cityObj, center, bounds);
                    if (entry != null)
                    {
                        asset.tsunamiRisks.Add(entry);
                        tnmCount++;
                    }
                }
                // 土砂災害リスク
                else if (landslideRisk && cityObj.name.StartsWith("lsld_"))
                {
                    var entry = ExtractLandslideRiskEntry(cityObj, center, bounds);
                    if (entry != null)
                    {
                        asset.landslideRisks.Add(entry);
                        lsldCount++;
                    }
                }
                // 地形
                else if (terrain && cityObj.name.StartsWith("dem_"))
                {
                    var entry = ExtractTerrainEntry(cityObj, bounds);
                    if (entry != null)
                    {
                        asset.terrains.Add(entry);
                        demCount++;
                    }
                }
            }

            // メタデータを更新
            asset.bakedSceneHash = GeoSpatialIndex.ComputeSceneHash();
            asset.bakedTimestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            asset.bakedSceneName = EditorSceneManager.GetActiveScene().name;

            var elapsed = DateTime.Now - startTime;

            EditorUtility.DisplayDialog(
                "✅ Bake完了",
                $"地理空間インデックスをBakeしました。\n\n" +
                $"道路: {roadCount}件\n" +
                $"土地利用: {luseCount}件\n" +
                $"津波リスク: {tnmCount}件\n" +
                $"土砂災害リスク: {lsldCount}件\n" +
                $"地形: {demCount}件\n\n" +
                $"処理時間: {elapsed.TotalSeconds:F2}秒\n" +
                $"保存先: {DEFAULT_ASSET_PATH}",
                "OK"
            );

            Debug.Log($"[GeoSpatialIndexBaker] Bake完了（処理時間: {elapsed.TotalSeconds:F2}秒）");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    #region Extract Methods

    /// <summary>
    /// 道路属性を抽出
    /// </summary>
    private static GeoSpatialIndex.RoadEntry ExtractRoadEntry(PLATEAUCityObjectGroup cityObj, Vector3 center)
    {
        var entry = new GeoSpatialIndex.RoadEntry
        {
            name = cityObj.gameObject.name,
            position = center,
            functionCode = 9020,
            functionName = "不明",
            sectionType = 1,
            sectionTypeName = "",
            gmlId = ""
        };

        try
        {
            if (cityObj.CityObjects?.rootCityObjects == null || cityObj.CityObjects.rootCityObjects.Count == 0)
                return entry;

            var rootObject = cityObj.CityObjects.rootCityObjects[0];
            entry.gmlId = rootObject.GmlID;

            var jsonStr = JsonConvert.SerializeObject(rootObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(jsonStr);

            if (rootData?.Attributes == null)
                return entry;

            foreach (var attr in rootData.Attributes)
            {
                if (attr.Key == "tran:function")
                {
                    if (int.TryParse(attr.Value?.ToString(), out int funcCode))
                    {
                        entry.functionCode = funcCode;
                        entry.functionName = RoadContext.GetFunctionName(funcCode);
                    }
                }
                else if (attr.Key == "uro:roadStructureAttribute")
                {
                    // 入れ子の属性を処理
                    ExtractRoadStructureAttribute(attr, entry);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GeoSpatialIndexBaker] {cityObj.name} の道路属性抽出に失敗: {ex.Message}");
        }

        return entry;
    }

    private static void ExtractRoadStructureAttribute(AttributeValue attr, GeoSpatialIndex.RoadEntry entry)
    {
        try
        {
            var structAttrs = attr.Value as List<AttributeValue>;
            if (structAttrs != null)
            {
                foreach (var structAttr in structAttrs)
                {
                    if (structAttr.Key == "uro:RoadStructureAttribute")
                    {
                        var uroAttrs = structAttr.Value as List<AttributeValue>;
                        if (uroAttrs != null)
                        {
                            foreach (var uroAttr in uroAttrs)
                            {
                                if (uroAttr.Key == "uro:sectionType")
                                {
                                    if (int.TryParse(uroAttr.Value?.ToString(), out int sectType))
                                    {
                                        entry.sectionType = sectType;
                                        entry.sectionTypeName = RoadContext.GetSectionTypeName(sectType);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 土地利用属性を抽出
    /// </summary>
    private static GeoSpatialIndex.LandUseEntry ExtractLandUseEntry(PLATEAUCityObjectGroup cityObj, Vector3 center, Bounds bounds)
    {
        var entry = new GeoSpatialIndex.LandUseEntry
        {
            name = cityObj.gameObject.name,
            position = center,
            bounds = bounds,
            landUseClass = "",
            landUseClassName = "",
            usage = "",
            area = 0,
            gmlId = ""
        };

        try
        {
            if (cityObj.CityObjects?.rootCityObjects == null || cityObj.CityObjects.rootCityObjects.Count == 0)
                return entry;

            var rootObject = cityObj.CityObjects.rootCityObjects[0];
            entry.gmlId = rootObject.GmlID;

            var jsonStr = JsonConvert.SerializeObject(rootObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(jsonStr);

            if (rootData?.Attributes == null)
                return entry;

            foreach (var attr in rootData.Attributes)
            {
                if (attr.Key == "luse:class")
                {
                    entry.landUseClass = attr.Value?.ToString() ?? "";
                }
                else if (attr.Key == "luse:usage")
                {
                    entry.usage = attr.Value?.ToString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GeoSpatialIndexBaker] {cityObj.name} の土地利用属性抽出に失敗: {ex.Message}");
        }

        return entry;
    }

    /// <summary>
    /// 津波リスク属性を抽出
    /// </summary>
    private static GeoSpatialIndex.TsunamiRiskEntry ExtractTsunamiRiskEntry(PLATEAUCityObjectGroup cityObj, Vector3 center, Bounds bounds)
    {
        var entry = new GeoSpatialIndex.TsunamiRiskEntry
        {
            name = cityObj.gameObject.name,
            position = center,
            bounds = bounds,
            description = "",
            rank = "",
            estimatedDepth = 0,
            gmlId = ""
        };

        try
        {
            if (cityObj.CityObjects?.rootCityObjects == null || cityObj.CityObjects.rootCityObjects.Count == 0)
                return entry;

            var rootObject = cityObj.CityObjects.rootCityObjects[0];
            entry.gmlId = rootObject.GmlID;

            var jsonStr = JsonConvert.SerializeObject(rootObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(jsonStr);

            if (rootData?.Attributes == null)
                return entry;

            foreach (var attr in rootData.Attributes)
            {
                if (attr.Key == "uro:TsunamiRiskAttribute")
                {
                    var riskAttrs = attr.Value as List<AttributeValue>;
                    if (riskAttrs != null)
                    {
                        foreach (var riskAttr in riskAttrs)
                        {
                            if (riskAttr.Key == "uro:description")
                            {
                                entry.description = riskAttr.Value?.ToString() ?? "";
                            }
                            else if (riskAttr.Key == "uro:rank")
                            {
                                entry.rank = riskAttr.Value?.ToString() ?? "";
                                entry.estimatedDepth = TsunamiRiskContext.ParseDepthFromRank(entry.rank);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GeoSpatialIndexBaker] {cityObj.name} の津波リスク属性抽出に失敗: {ex.Message}");
        }

        return entry;
    }

    /// <summary>
    /// 土砂災害リスク属性を抽出
    /// </summary>
    private static GeoSpatialIndex.LandslideRiskEntry ExtractLandslideRiskEntry(PLATEAUCityObjectGroup cityObj, Vector3 center, Bounds bounds)
    {
        var entry = new GeoSpatialIndex.LandslideRiskEntry
        {
            name = cityObj.gameObject.name,
            position = center,
            bounds = bounds,
            areaType = 0,
            areaTypeName = "",
            descriptionCode = 0,
            descriptionName = "",
            isSpecialZone = false,
            gmlId = ""
        };

        try
        {
            if (cityObj.CityObjects?.rootCityObjects == null || cityObj.CityObjects.rootCityObjects.Count == 0)
                return entry;

            var rootObject = cityObj.CityObjects.rootCityObjects[0];
            entry.gmlId = rootObject.GmlID;

            var jsonStr = JsonConvert.SerializeObject(rootObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(jsonStr);

            if (rootData?.Attributes == null)
                return entry;

            foreach (var attr in rootData.Attributes)
            {
                if (attr.Key == "urf:areaType")
                {
                    if (int.TryParse(attr.Value?.ToString(), out int areaType))
                    {
                        entry.areaType = areaType;
                        entry.areaTypeName = LandslideRiskContext.GetAreaTypeName(areaType);
                        entry.isSpecialZone = LandslideRiskContext.IsSpecialZone(areaType);
                    }
                }
                else if (attr.Key == "urf:description")
                {
                    if (int.TryParse(attr.Value?.ToString(), out int descCode))
                    {
                        entry.descriptionCode = descCode;
                        entry.descriptionName = LandslideRiskContext.GetDescriptionName(descCode);
                    }
                    else
                    {
                        // 文字列の場合
                        entry.descriptionName = attr.Value?.ToString() ?? "";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GeoSpatialIndexBaker] {cityObj.name} の土砂災害リスク属性抽出に失敗: {ex.Message}");
        }

        return entry;
    }

    /// <summary>
    /// 地形属性を抽出
    /// </summary>
    private static GeoSpatialIndex.TerrainEntry ExtractTerrainEntry(PLATEAUCityObjectGroup cityObj, Bounds bounds)
    {
        var entry = new GeoSpatialIndex.TerrainEntry
        {
            name = cityObj.gameObject.name,
            bounds = bounds,
            minElevation = bounds.min.y,
            maxElevation = bounds.max.y,
            gmlId = ""
        };

        try
        {
            if (cityObj.CityObjects?.rootCityObjects != null && cityObj.CityObjects.rootCityObjects.Count > 0)
            {
                entry.gmlId = cityObj.CityObjects.rootCityObjects[0].GmlID;
            }

            // メッシュからより正確な標高範囲を取得
            var meshFilter = cityObj.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                var vertices = meshFilter.sharedMesh.vertices;
                if (vertices.Length > 0)
                {
                    float minY = float.MaxValue;
                    float maxY = float.MinValue;
                    foreach (var v in vertices)
                    {
                        if (v.y < minY) minY = v.y;
                        if (v.y > maxY) maxY = v.y;
                    }
                    entry.minElevation = minY;
                    entry.maxElevation = maxY;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GeoSpatialIndexBaker] {cityObj.name} の地形属性抽出に失敗: {ex.Message}");
        }

        return entry;
    }

    #endregion

    /// <summary>
    /// Play開始時に自動で整合性をチェック
    /// </summary>
    [InitializeOnLoad]
    private static class PlayModeValidator
    {
        static PlayModeValidator()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
                return;

            var asset = AssetDatabase.LoadAssetAtPath<GeoSpatialIndex>(DEFAULT_ASSET_PATH);

            if (asset == null || !asset.HasData)
            {
                Debug.LogWarning("[GeoSpatialIndexBaker] 地理空間インデックスがBakeされていません。");
                return;
            }

            bool isValid = asset.ValidateAgainstScene();
            if (!isValid)
            {
                Debug.LogWarning(
                    $"[GeoSpatialIndexBaker] 地理空間インデックスが現在のシーンと一致しません。\n" +
                    $"「Tools → Environment → Bake Geo Spatial Index」で再Bakeしてください。"
                );
            }
        }
    }
}
