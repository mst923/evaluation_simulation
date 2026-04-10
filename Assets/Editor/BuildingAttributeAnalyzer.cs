using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using EvacSim.Environment;

/// <summary>
/// 建物属性を分析し、usage/majorUsageの分布を統計出力するツール
/// </summary>
public class BuildingAttributeAnalyzer : EditorWindow
{
    [MenuItem("Tools/Building Attribute Analyzer")]
    static void ShowWindow()
    {
        GetWindow<BuildingAttributeAnalyzer>("Building Attribute Analyzer");
    }
    
    void OnGUI()
    {
        GUILayout.Label("建物属性分析ツール", EditorStyles.boldLabel);
        GUILayout.Space(10);
        
        EditorGUILayout.HelpBox(
            "シーン内の全建物のPLATEAU属性を分析し、\n" +
            "usage / majorUsage / landUseType の分布を統計出力します。\n\n" +
            "出力先: Logs/building_stats/",
            MessageType.Info
        );
        
        GUILayout.Space(10);
        
        if (GUILayout.Button("統計を生成してログ出力", GUILayout.Height(40)))
        {
            AnalyzeAndExport();
        }
        
        GUILayout.Space(10);
        
        if (GUILayout.Button("カテゴリ別の建物リストも出力", GUILayout.Height(40)))
        {
            AnalyzeAndExportWithDetails();
        }
    }
    
    /// <summary>
    /// 統計のみを出力
    /// </summary>
    void AnalyzeAndExport()
    {
        var envProvider = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (envProvider == null)
        {
            EditorUtility.DisplayDialog("エラー", 
                "EnvironmentalContextProviderが見つかりません。\n" +
                "シーンを再生して空間インデックスを構築してください。", 
                "OK");
            return;
        }
        
        var allBuildings = envProvider.GetAllBuildings();
        if (allBuildings.Count == 0)
        {
            EditorUtility.DisplayDialog("エラー", 
                "建物データが見つかりません。\n" +
                "シーンを再生して空間インデックスを構築してください。", 
                "OK");
            return;
        }
        
        // 統計収集（usage → majorUsage → landUseType の階層構造）
        Dictionary<string, Dictionary<string, Dictionary<string, int>>> hierarchicalStats 
            = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();
        
        foreach (var building in allBuildings)
        {
            string usage = building.usage ?? "不明";
            string majorUsage = building.majorUsage ?? "不明";
            string landUseType = building.landUseType ?? "不明";
            
            // usage レベル
            if (!hierarchicalStats.ContainsKey(usage))
            {
                hierarchicalStats[usage] = new Dictionary<string, Dictionary<string, int>>();
            }
            
            // majorUsage レベル
            if (!hierarchicalStats[usage].ContainsKey(majorUsage))
            {
                hierarchicalStats[usage][majorUsage] = new Dictionary<string, int>();
            }
            
            // landUseType レベル
            if (!hierarchicalStats[usage][majorUsage].ContainsKey(landUseType))
            {
                hierarchicalStats[usage][majorUsage][landUseType] = 0;
            }
            hierarchicalStats[usage][majorUsage][landUseType]++;
        }
        
        // ログ出力
        string logDir = Path.Combine(Application.dataPath, "..", "Logs", "building_stats");
        Directory.CreateDirectory(logDir);
        
        string timestamp = System.DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss");
        string filePath = Path.Combine(logDir, $"attributes_{timestamp}.txt");
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            writer.WriteLine(new string('=', 80));
            writer.WriteLine("建物属性統計レポート（階層構造）");
            writer.WriteLine($"総建物数: {allBuildings.Count}件");
            writer.WriteLine($"usage の種類数: {hierarchicalStats.Count}種類");
            writer.WriteLine($"生成日時: {System.DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            writer.WriteLine(new string('=', 80));
            writer.WriteLine();
            writer.WriteLine("形式: usage → majorUsage → landUseType の階層で表示");
            writer.WriteLine("※ 全 usage の種類について、それぞれの内訳を表示します");
            writer.WriteLine();
            
            // usage ごとに、その内訳を表示
            var sortedUsages = hierarchicalStats.OrderByDescending(x => 
            {
                // usage の総件数を計算
                int total = 0;
                foreach (var majorUsageDict in x.Value.Values)
                {
                    total += majorUsageDict.Values.Sum();
                }
                return total;
            });
            
            // usage の一覧を先に表示
            writer.WriteLine("【usage の一覧（件数降順）】");
            writer.WriteLine(new string('-', 80));
            int previewIndex = 0;
            foreach (var usageKvp in sortedUsages)
            {
                previewIndex++;
                string usage = usageKvp.Key;
                int usageTotal = 0;
                foreach (var majorUsageDict in usageKvp.Value.Values)
                {
                    usageTotal += majorUsageDict.Values.Sum();
                }
                double usagePercent = (double)usageTotal / allBuildings.Count * 100;
                writer.WriteLine($"{previewIndex,3}. \"{usage}\": {usageTotal,6}件 ({usagePercent,5:F1}%)");
            }
            writer.WriteLine();
            writer.WriteLine(new string('=', 80));
            writer.WriteLine();
            
            int usageIndex = 0;
            int totalUsages = sortedUsages.Count();
            
            foreach (var usageKvp in sortedUsages)
            {
                usageIndex++;
                string usage = usageKvp.Key;
                var majorUsageDict = usageKvp.Value;
                
                // usage の総件数を計算
                int usageTotal = 0;
                foreach (var landUseTypeDict in majorUsageDict.Values)
                {
                    usageTotal += landUseTypeDict.Values.Sum();
                }
                
                double usagePercent = (double)usageTotal / allBuildings.Count * 100;
                
                writer.WriteLine(new string('=', 80));
                writer.WriteLine($"■ [{usageIndex}/{totalUsages}] bldg:usage = \"{usage}\": {usageTotal}件 ({usagePercent:F1}%)");
                writer.WriteLine(new string('=', 80));
                writer.WriteLine();
                
                // majorUsage の内訳
                var sortedMajorUsages = majorUsageDict.OrderByDescending(x => x.Value.Values.Sum());
                
                foreach (var majorUsageKvp in sortedMajorUsages)
                {
                    string majorUsage = majorUsageKvp.Key;
                    var landUseTypeDict = majorUsageKvp.Value;
                    
                    int majorUsageTotal = landUseTypeDict.Values.Sum();
                    double majorUsagePercent = (double)majorUsageTotal / usageTotal * 100;
                    
                    writer.WriteLine($"  ├─ uro:majorUsage = \"{majorUsage}\": {majorUsageTotal}件 ({majorUsagePercent:F1}% of {usage})");
                    
                    // landUseType の内訳
                    var sortedLandUseTypes = landUseTypeDict.OrderByDescending(x => x.Value);
                    
                    int landUseTypeCount = sortedLandUseTypes.Count();
                    int currentIndex = 0;
                    
                    foreach (var landUseTypeKvp in sortedLandUseTypes)
                    {
                        string landUseType = landUseTypeKvp.Key;
                        int count = landUseTypeKvp.Value;
                        double landUseTypePercent = (double)count / majorUsageTotal * 100;
                        
                        currentIndex++;
                        string prefix = (currentIndex == landUseTypeCount) ? "     └─" : "     ├─";
                        
                        writer.WriteLine($"{prefix} uro:landUseType = \"{landUseType}\": {count}件 ({landUseTypePercent:F1}% of {majorUsage})");
                    }
                    
                    writer.WriteLine();
                }
                
                // usage セクションの終了を示す区切り
                if (usageIndex < totalUsages)
                {
                    writer.WriteLine();
                    writer.WriteLine(new string('-', 80));
                    writer.WriteLine();
                }
            }
            
            writer.WriteLine();
            writer.WriteLine(new string('=', 80));
            writer.WriteLine();
            
            // カテゴリ分類統計
            writer.WriteLine(new string('=', 80));
            writer.WriteLine("【BuildingCategorizer による分類結果】");
            writer.WriteLine(new string('-', 80));
            
            Dictionary<BuildingCategory, int> categoryStats = new Dictionary<BuildingCategory, int>();
            foreach (var building in allBuildings)
            {
                BuildingCategory category = BuildingCategorizer.Categorize(building);
                if (!categoryStats.ContainsKey(category)) categoryStats[category] = 0;
                categoryStats[category]++;
            }
            
            foreach (var kvp in categoryStats.OrderByDescending(x => x.Value))
            {
                double percent = (double)kvp.Value / allBuildings.Count * 100;
                writer.WriteLine($"{kvp.Value,6}件 ({percent,5:F1}%) | {BuildingCategorizer.GetCategoryDisplayName(kvp.Key)}");
            }
        }
        
        EditorUtility.DisplayDialog("完了", 
            $"統計をエクスポートしました:\n{filePath}\n\n総建物数: {allBuildings.Count}件", 
            "OK");
        
        Debug.Log($"[BuildingAttributeAnalyzer] エクスポート完了: {filePath}");
        
        // ファイルを開く
        System.Diagnostics.Process.Start(filePath);
    }
    
    /// <summary>
    /// 統計 + カテゴリ別の詳細リストを出力
    /// </summary>
    void AnalyzeAndExportWithDetails()
    {
        var envProvider = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (envProvider == null)
        {
            EditorUtility.DisplayDialog("エラー", 
                "EnvironmentalContextProviderが見つかりません。", 
                "OK");
            return;
        }
        
        var allBuildings = envProvider.GetAllBuildings();
        if (allBuildings.Count == 0)
        {
            EditorUtility.DisplayDialog("エラー", 
                "建物データが見つかりません。", 
                "OK");
            return;
        }
        
        // カテゴリ別に分類
        Dictionary<BuildingCategory, List<EnvironmentalContextProvider.BuildingContext>> buildingsByCategory 
            = new Dictionary<BuildingCategory, List<EnvironmentalContextProvider.BuildingContext>>();
        
        foreach (BuildingCategory category in System.Enum.GetValues(typeof(BuildingCategory)))
        {
            buildingsByCategory[category] = new List<EnvironmentalContextProvider.BuildingContext>();
        }
        
        foreach (var building in allBuildings)
        {
            BuildingCategory category = BuildingCategorizer.Categorize(building);
            buildingsByCategory[category].Add(building);
        }
        
        // ログ出力
        string logDir = Path.Combine(Application.dataPath, "..", "Logs", "building_stats");
        Directory.CreateDirectory(logDir);
        
        string timestamp = System.DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss");
        string filePath = Path.Combine(logDir, $"attributes_detailed_{timestamp}.txt");
        
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            writer.WriteLine(new string('=', 80));
            writer.WriteLine("建物属性詳細レポート（カテゴリ別リスト付き）");
            writer.WriteLine($"総建物数: {allBuildings.Count}件");
            writer.WriteLine($"生成日時: {System.DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            writer.WriteLine(new string('=', 80));
            writer.WriteLine();
            
            // カテゴリ別に詳細を出力
            foreach (var kvp in buildingsByCategory.OrderByDescending(x => x.Value.Count))
            {
                BuildingCategory category = kvp.Key;
                var buildings = kvp.Value;
                
                if (buildings.Count == 0) continue;
                
                writer.WriteLine();
                writer.WriteLine(new string('=', 80));
                writer.WriteLine($"■ {BuildingCategorizer.GetCategoryDisplayName(category)}: {buildings.Count}件");
                writer.WriteLine(new string('=', 80));
                writer.WriteLine();
                
                // 最初の20件を表示
                int displayCount = Mathf.Min(buildings.Count, 20);
                for (int i = 0; i < displayCount; i++)
                {
                    var building = buildings[i];
                    writer.WriteLine($"  [{i+1}] {building.name}");
                    writer.WriteLine($"      usage: {building.usage ?? "(なし)"}");
                    writer.WriteLine($"      majorUsage: {building.majorUsage ?? "(なし)"}");
                    writer.WriteLine($"      landUseType: {building.landUseType ?? "(なし)"}");
                    writer.WriteLine($"      position: {building.position}");
                    writer.WriteLine();
                }
                
                if (buildings.Count > 20)
                {
                    writer.WriteLine($"  ... 他 {buildings.Count - 20}件");
                    writer.WriteLine();
                }
            }
        }
        
        EditorUtility.DisplayDialog("完了", 
            $"詳細レポートをエクスポートしました:\n{filePath}\n\n総建物数: {allBuildings.Count}件", 
            "OK");
        
        Debug.Log($"[BuildingAttributeAnalyzer] 詳細レポート完了: {filePath}");
        
        // ファイルを開く
        System.Diagnostics.Process.Start(filePath);
    }
}








