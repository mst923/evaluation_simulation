using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using PLATEAU.CityInfo;
using Newtonsoft.Json;
using EvacSim.Environment;

/// <summary>
/// 建物空間インデックスをBake（事前構築）するEditorツール
/// </summary>
public class BuildingSpatialIndexBaker : EditorWindow
{
    private const string DEFAULT_ASSET_PATH = "Assets/Config/BuildingSpatialIndex.asset";
    private BuildingSpatialIndex targetAsset;
    private float cellSize = 50f;
    private bool autoValidateOnPlay = true;

    [MenuItem("Tools/Environment/Bake Spatial Index")]
    public static void ShowWindow()
    {
        var window = GetWindow<BuildingSpatialIndexBaker>("Spatial Index Baker");
        window.minSize = new Vector2(400, 300);
    }

    [MenuItem("Tools/Environment/Validate Spatial Index")]
    public static void ValidateIndex()
    {
        var asset = AssetDatabase.LoadAssetAtPath<BuildingSpatialIndex>(DEFAULT_ASSET_PATH);

        if (asset == null)
        {
            EditorUtility.DisplayDialog(
                "インデックスが見つかりません",
                $"空間インデックスアセットが存在しません。\n\n" +
                $"「Tools → Environment → Bake Spatial Index」から作成してください。\n\n" +
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
                $"空間インデックスは現在のシーンと一致しています。\n\n" +
                $"建物数: {asset.buildings.Count}件\n" +
                $"Bake日時: {asset.bakedTimestamp}\n" +
                $"シーン: {asset.bakedSceneName}",
                "OK"
            );
        }
        else
        {
            bool rebake = EditorUtility.DisplayDialog(
                "⚠️ 不整合を検出",
                $"空間インデックスが現在のシーンと一致しません。\n\n" +
                $"シーンの建物が追加・削除・移動された可能性があります。\n" +
                $"再Bakeすることを推奨します。\n\n" +
                $"キャッシュ情報:\n" +
                $"- Bake日時: {asset.bakedTimestamp}\n" +
                $"- シーン: {asset.bakedSceneName}",
                "再Bakeする", "キャンセル"
            );

            if (rebake)
            {
                BakeIndex(asset);
            }
        }
    }

    private void OnEnable()
    {
        // 既存のアセットを読み込み
        targetAsset = AssetDatabase.LoadAssetAtPath<BuildingSpatialIndex>(DEFAULT_ASSET_PATH);
        if (targetAsset != null)
        {
            cellSize = targetAsset.cellSize;
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("建物空間インデックス Baker", EditorStyles.boldLabel);
        GUILayout.Space(10);

        EditorGUILayout.HelpBox(
            "シーン内のPLATEAU建物データを事前にBakeして、\n" +
            "起動時間を大幅に短縮します。\n\n" +
            "シーンの建物を変更した場合は再Bakeが必要です。",
            MessageType.Info
        );

        GUILayout.Space(10);

        // 設定
        EditorGUILayout.LabelField("設定", EditorStyles.boldLabel);
        cellSize = EditorGUILayout.FloatField("Cell Size (m)", cellSize);
        autoValidateOnPlay = EditorGUILayout.Toggle("Play時に自動検証", autoValidateOnPlay);

        GUILayout.Space(10);

        // 現在のアセット情報
        EditorGUILayout.LabelField("現在のキャッシュ", EditorStyles.boldLabel);
        if (targetAsset != null && targetAsset.HasData)
        {
            EditorGUILayout.LabelField($"建物数: {targetAsset.buildings.Count}件");
            EditorGUILayout.LabelField($"Bake日時: {targetAsset.bakedTimestamp}");
            EditorGUILayout.LabelField($"シーン: {targetAsset.bakedSceneName}");

            // 整合性チェック
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
        if (GUILayout.Button("🔥 Bake Spatial Index", GUILayout.Height(40)))
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
        // アセットがなければ新規作成
        if (targetAsset == null)
        {
            targetAsset = ScriptableObject.CreateInstance<BuildingSpatialIndex>();

            // ディレクトリがなければ作成
            string directory = System.IO.Path.GetDirectoryName(DEFAULT_ASSET_PATH);
            if (!AssetDatabase.IsValidFolder(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            AssetDatabase.CreateAsset(targetAsset, DEFAULT_ASSET_PATH);
        }

        targetAsset.cellSize = cellSize;
        BakeIndex(targetAsset);

        // 保存
        EditorUtility.SetDirty(targetAsset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Repaint();
    }

    private static void BakeIndex(BuildingSpatialIndex asset)
    {
        var startTime = DateTime.Now;

        asset.buildings.Clear();

        // 全PLATEAUオブジェクトを検索
        var cityObjects = FindObjectsByType<PLATEAUCityObjectGroup>(FindObjectsSortMode.None);
        int skippedCount = 0;

        EditorUtility.DisplayProgressBar("Baking Spatial Index", "建物データを収集中...", 0);

        try
        {
            int totalCount = cityObjects.Length;
            int processedCount = 0;

            foreach (var cityObj in cityObjects)
            {
                processedCount++;

                if (processedCount % 100 == 0)
                {
                    EditorUtility.DisplayProgressBar(
                        "Baking Spatial Index",
                        $"処理中... {processedCount}/{totalCount}",
                        (float)processedCount / totalCount
                    );
                }

                // 建物のみを対象
                if (!cityObj.name.StartsWith("bldg_"))
                {
                    skippedCount++;
                    continue;
                }

                var renderer = cityObj.GetComponent<Renderer>();
                if (renderer == null)
                {
                    skippedCount++;
                    continue;
                }

                Vector3 center = renderer.bounds.center;

                // 建物情報を抽出
                var entry = ExtractBuildingEntry(cityObj, center);
                asset.buildings.Add(entry);
            }

            // 構造種別が不明な建物に対して、既知の割合に基づいて構造種別を割り振る
            int inferredCount = InferUnknownStructureTypes(asset.buildings);

            // メタデータを更新
            asset.bakedSceneHash = BuildingSpatialIndex.ComputeSceneHash();
            asset.bakedTimestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            asset.bakedSceneName = EditorSceneManager.GetActiveScene().name;

            var elapsed = DateTime.Now - startTime;

            // 統計情報を収集
            var stats = CollectStructureStatistics(asset.buildings);

            EditorUtility.DisplayDialog(
                "✅ Bake完了",
                $"空間インデックスをBakeしました。\n\n" +
                $"建物数: {asset.buildings.Count}件\n" +
                $"スキップ: {skippedCount}件\n" +
                $"構造種別推定: {inferredCount}件\n\n" +
                $"【構造種別内訳】\n" +
                $"木造・土蔵造: {stats.woodenCount}件 ({stats.woodenRatio:P1})\n" +
                $"非木造: {stats.nonWoodenCount}件 ({stats.nonWoodenRatio:P1})\n" +
                $"建築年あり: {stats.withYearCount}件\n\n" +
                $"処理時間: {elapsed.TotalSeconds:F2}秒\n" +
                $"保存先: {DEFAULT_ASSET_PATH}",
                "OK"
            );

            Debug.Log($"[SpatialIndexBaker] Bake完了: {asset.buildings.Count}件の建物をインデックス化" +
                      $"（構造種別推定: {inferredCount}件, 処理時間: {elapsed.TotalSeconds:F2}秒）");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// 構造種別が不明な建物に対して、既知の割合に基づいて構造種別を推定・割り振る
    /// </summary>
    private static int InferUnknownStructureTypes(List<BuildingSpatialIndex.BuildingEntry> buildings)
    {
        // まず既知の構造種別の割合を計算
        int woodenCount = 0;
        int nonWoodenCount = 0;
        var unknownBuildings = new List<BuildingSpatialIndex.BuildingEntry>();

        foreach (var building in buildings)
        {
            if (IsWoodenStructure(building.structureType))
            {
                woodenCount++;
            }
            else if (IsNonWoodenStructure(building.structureType))
            {
                nonWoodenCount++;
            }
            else
            {
                // 不明な建物
                unknownBuildings.Add(building);
            }
        }

        int knownTotal = woodenCount + nonWoodenCount;
        if (knownTotal == 0 || unknownBuildings.Count == 0)
        {
            // 既知のデータがないか、不明な建物がない場合は何もしない
            return 0;
        }

        // 既知の割合を計算
        float woodenRatio = (float)woodenCount / knownTotal;

        // 乱数のシードを固定して再現性を確保（建物名のハッシュを使用）
        int inferredCount = 0;
        foreach (var building in unknownBuildings)
        {
            // 建物名からハッシュ値を生成して0-1の値に正規化
            int hash = building.name.GetHashCode();
            float normalizedHash = (float)(Math.Abs(hash) % 10000) / 10000f;

            // 既知の割合に基づいて構造種別を割り振る
            if (normalizedHash < woodenRatio)
            {
                building.structureType = "木造・土蔵造";
            }
            else
            {
                building.structureType = "非木造";
            }
            building.isInferredStructure = true;
            inferredCount++;
        }

        Debug.Log($"[SpatialIndexBaker] 構造種別を推定: {inferredCount}件 " +
                  $"(既知の割合: 木造{woodenRatio:P1}, 非木造{1 - woodenRatio:P1})");

        return inferredCount;
    }

    /// <summary>
    /// 木造系の構造種別かどうかを判定
    /// </summary>
    private static bool IsWoodenStructure(string structureType)
    {
        if (string.IsNullOrEmpty(structureType)) return false;
        return structureType.Contains("木造") || structureType.Contains("土蔵");
    }

    /// <summary>
    /// 非木造系の構造種別かどうかを判定
    /// </summary>
    private static bool IsNonWoodenStructure(string structureType)
    {
        if (string.IsNullOrEmpty(structureType)) return false;
        return structureType.Contains("非木造") ||
               structureType.Contains("鉄筋") ||
               structureType.Contains("鉄骨") ||
               structureType.Contains("コンクリート");
    }

    /// <summary>
    /// 構造種別の統計情報を収集
    /// </summary>
    private static (int woodenCount, int nonWoodenCount, float woodenRatio, float nonWoodenRatio, int withYearCount)
        CollectStructureStatistics(List<BuildingSpatialIndex.BuildingEntry> buildings)
    {
        int woodenCount = 0;
        int nonWoodenCount = 0;
        int withYearCount = 0;

        foreach (var building in buildings)
        {
            if (IsWoodenStructure(building.structureType))
                woodenCount++;
            else if (IsNonWoodenStructure(building.structureType))
                nonWoodenCount++;

            if (building.yearOfConstruction > 0)
                withYearCount++;
        }

        int total = woodenCount + nonWoodenCount;
        float woodenRatio = total > 0 ? (float)woodenCount / total : 0;
        float nonWoodenRatio = total > 0 ? (float)nonWoodenCount / total : 0;

        return (woodenCount, nonWoodenCount, woodenRatio, nonWoodenRatio, withYearCount);
    }

    /// <summary>
    /// PLATEAU建物から属性情報を抽出
    /// </summary>
    private static BuildingSpatialIndex.BuildingEntry ExtractBuildingEntry(PLATEAUCityObjectGroup cityObj, Vector3 center)
    {
        var entry = new BuildingSpatialIndex.BuildingEntry
        {
            name = cityObj.gameObject.name,
            position = center,
            usage = "不明",
            majorUsage = "",
            height = 0,
            floors = 0,
            structureType = "",
            landUseType = "",
            gmlId = "",
            yearOfConstruction = 0,
            isInferredStructure = false
        };

        try
        {
            if (cityObj.CityObjects == null ||
                cityObj.CityObjects.rootCityObjects == null ||
                cityObj.CityObjects.rootCityObjects.Count == 0)
            {
                return entry;
            }

            var rootObject = cityObj.CityObjects.rootCityObjects[0];
            entry.gmlId = rootObject.GmlID;

            var jsonStr = JsonConvert.SerializeObject(rootObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(jsonStr);

            if (rootData?.Attributes == null)
            {
                return entry;
            }

            // 基本属性を抽出（CityObjectTypes.csのAttributeValue型を使用）
            foreach (var attr in rootData.Attributes)
            {
                if (attr.Key == "bldg:usage")
                {
                    entry.usage = attr.Value?.ToString() ?? "不明";
                }
                else if (attr.Key == "bldg:measuredheight")
                {
                    float.TryParse(attr.Value?.ToString(), out entry.height);
                }
                else if (attr.Key == "bldg:storeysaboveground")
                {
                    int.TryParse(attr.Value?.ToString(), out entry.floors);
                    if (entry.floors == 9999)
                    {
                        entry.floors = 1;
                    }
                }
                else if (attr.Key == "bldg:yearOfConstruction")
                {
                    // 直接の建築年属性
                    if (int.TryParse(attr.Value?.ToString(), out int year))
                    {
                        entry.yearOfConstruction = year;
                    }
                }
                else if (attr.Key == "uro:buildingDetailAttribute")
                {
                    // AttributeValue.AttributeSetValue は Type == "AttributeSet" の場合に設定される
                    var detailAttrs = attr.Value as List<AttributeValue>;
                    if (detailAttrs != null)
                    {
                        foreach (var detailAttr in detailAttrs)
                        {
                            if (detailAttr.Key == "uro:BuildingDetailAttribute")
                            {
                                var uroAttrs = detailAttr.Value as List<AttributeValue>;
                                if (uroAttrs != null)
                                {
                                    foreach (var uroAttr in uroAttrs)
                                    {
                                        if (uroAttr.Key == "uro:buildingStructureType")
                                            entry.structureType = uroAttr.Value?.ToString() ?? "";
                                        else if (uroAttr.Key == "uro:landUseType")
                                            entry.landUseType = uroAttr.Value?.ToString() ?? "";
                                        else if (uroAttr.Key == "uro:majorUsage")
                                            entry.majorUsage = uroAttr.Value?.ToString() ?? "";
                                        else if (uroAttr.Key == "uro:buildingYearOfConstruction")
                                        {
                                            // uro:BuildingDetailAttribute内の建築年
                                            if (int.TryParse(uroAttr.Value?.ToString(), out int uroYear))
                                            {
                                                entry.yearOfConstruction = uroYear;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SpatialIndexBaker] {cityObj.name} の属性抽出に失敗: {ex.Message}");
        }

        return entry;
    }

    /// <summary>
    /// Play開始時に自動で整合性をチェック（InitializeOnLoad）
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

            var asset = AssetDatabase.LoadAssetAtPath<BuildingSpatialIndex>(DEFAULT_ASSET_PATH);

            if (asset == null || !asset.HasData)
            {
                // アセットがない場合は警告のみ
                Debug.LogWarning("[SpatialIndexBaker] 空間インデックスがBakeされていません。起動時間が長くなる可能性があります。");
                return;
            }

            bool isValid = asset.ValidateAgainstScene();
            if (!isValid)
            {
                Debug.LogWarning(
                    $"[SpatialIndexBaker] 空間インデックスが現在のシーンと一致しません。\n" +
                    $"Bake日時: {asset.bakedTimestamp}\n" +
                    $"「Tools → Environment → Bake Spatial Index」で再Bakeしてください。"
                );
            }
        }
    }
}

// RootObject, AttributeValueはCityObjectTypes.csで定義済み
