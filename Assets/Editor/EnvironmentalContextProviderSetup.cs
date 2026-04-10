using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using EvacSim.Environment;

/// <summary>
/// EnvironmentalContextProviderを自動セットアップするエディタスクリプト
/// </summary>
public class EnvironmentalContextProviderSetup : EditorWindow
{
    [MenuItem("Tools/Environment/Setup Environmental Context Provider")]
    public static void SetupProvider()
    {
        // すでに存在するか確認
        var existing = FindFirstObjectByType<EnvironmentalContextProvider>();
        if (existing != null)
        {
            bool replace = EditorUtility.DisplayDialog(
                "⚠️ 既に存在します",
                $"シーンには既にEnvironmentalContextProviderが存在します（{existing.gameObject.name}）。\n\n新しいものを作成しますか？",
                "新規作成", "キャンセル"
            );
            
            if (!replace)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }
        }
        
        // 新しいGameObjectを作成
        GameObject go = new GameObject("EnvironmentalContext");
        var provider = go.AddComponent<EnvironmentalContextProvider>();
        
        // デフォルト設定
        provider.cellSize = 50f;
        provider.exportToJsonOnStart = false;
        provider.cacheLifetime = 1f;
        
        // 選択状態にする
        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
        
        // シーンを変更済みにマーク
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        
        Debug.Log($"✅ [EnvironmentalContextProvider] セットアップ完了: {go.name}");
        
        EditorUtility.DisplayDialog(
            "✅ セットアップ完了",
            "EnvironmentalContextProviderをシーンに追加しました。\n\n" +
            "設定:\n" +
            $"- Cell Size: {provider.cellSize}m\n" +
            $"- Cache Lifetime: {provider.cacheLifetime}秒\n\n" +
            "シーンを保存してください（Ctrl/Cmd + S）",
            "OK"
        );
    }
    
    [MenuItem("Tools/Environment/Check Environmental Context Provider")]
    public static void CheckProvider()
    {
        var provider = FindFirstObjectByType<EnvironmentalContextProvider>();
        
        if (provider == null)
        {
            EditorUtility.DisplayDialog(
                "❌ 見つかりません",
                "現在のシーンにはEnvironmentalContextProviderが存在しません。\n\n" +
                "「Tools → Environment → Setup Environmental Context Provider」から追加してください。",
                "OK"
            );
        }
        else
        {
            Selection.activeGameObject = provider.gameObject;
            EditorGUIUtility.PingObject(provider.gameObject);
            
            string stats = provider.GetStatistics();
            
            EditorUtility.DisplayDialog(
                "✅ 見つかりました",
                $"EnvironmentalContextProviderが存在します。\n\n" +
                $"GameObject: {provider.gameObject.name}\n" +
                $"Cell Size: {provider.cellSize}m\n" +
                $"Cache Lifetime: {provider.cacheLifetime}秒\n\n" +
                $"統計情報:\n{stats}",
                "OK"
            );
        }
    }
}
