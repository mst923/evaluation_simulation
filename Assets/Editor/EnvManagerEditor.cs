using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using EvacSim.Core;

/// <summary>
/// EnvManagerのカスタムインスペクター
/// Shelterタグの付いたGameObjectを自動的にSheltersリストに追加するボタンを提供します
/// </summary>
[CustomEditor(typeof(EnvManager))]
public class EnvManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // デフォルトのInspector表示
        DrawDefaultInspector();
        
        EnvManager envManager = (EnvManager)target;
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("避難所自動設定ツール", EditorStyles.boldLabel);
        
        // 現在のShelters数を表示
        if (envManager.Shelters != null)
        {
            EditorGUILayout.LabelField($"現在の避難所数: {envManager.Shelters.Count}個", EditorStyles.miniLabel);
        }
        
        EditorGUILayout.Space(5);
        
        // ボタン: Shelterタグの全GameObjectを追加
        GUI.backgroundColor = new Color(0.5f, 0.8f, 1.0f);
        if (GUILayout.Button("📍 Shelterタグの全GameObjectをSheltersに追加", GUILayout.Height(35)))
        {
            PopulateShelters(envManager);
        }
        GUI.backgroundColor = Color.white;
        
        EditorGUILayout.Space(3);
        
        // ボタン: リストをクリア
        GUI.backgroundColor = new Color(1.0f, 0.6f, 0.6f);
        if (GUILayout.Button("🗑 Sheltersリストをクリア", GUILayout.Height(25)))
        {
            if (EditorUtility.DisplayDialog(
                "確認",
                "Sheltersリストをクリアしますか？",
                "はい", "キャンセル"))
            {
                ClearShelters(envManager);
            }
        }
        GUI.backgroundColor = Color.white;
        
        EditorGUILayout.Space(5);
        
        // ヘルプボックス
        EditorGUILayout.HelpBox(
            "「Shelterタグの全GameObjectをSheltersに追加」ボタンで、シーン内の全ての" +
            "「Shelter」タグおよび「ConstShelter」タグ付きGameObjectを" +
            "自動的にSheltersリストに追加します。\n\n" +
            "※既存のリストは上書きされます。\n" +
            "※実行時には自動的にタグから取得されますが、エディタで事前に" +
            "設定しておくことで構成を確認できます。",
            MessageType.Info
        );
        
        // シーン内のタグ付きオブジェクト数を表示
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("シーン内の状況", EditorStyles.boldLabel);
        
        int shelterCount = GameObject.FindGameObjectsWithTag("Shelter").Length;
        int constShelterCount = 0;
        int schoolCount = 0;
        int preschoolCount = 0;
        int tsunamiAreaCount = 0;

        try { constShelterCount = GameObject.FindGameObjectsWithTag("ConstShelter").Length; } catch { }
        try { schoolCount = GameObject.FindGameObjectsWithTag("School").Length; } catch { }
        try { preschoolCount = GameObject.FindGameObjectsWithTag("PreSchool").Length; } catch { }
        try { tsunamiAreaCount = GameObject.FindGameObjectsWithTag("TsunamiEvacuationArea").Length; } catch { }

        EditorGUILayout.LabelField($"・Shelterタグ: {shelterCount}個", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・ConstShelterタグ: {constShelterCount}個", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・Schoolタグ: {schoolCount}個（避難所として動作）", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・PreSchoolタグ: {preschoolCount}個（安全地帯として動作）", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・TsunamiEvacuationAreaタグ: {tsunamiAreaCount}個", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・合計避難所: {shelterCount + constShelterCount + schoolCount}個", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・合計安全地帯: {tsunamiAreaCount + preschoolCount}個", EditorStyles.miniLabel);

        // 警告表示
        if (shelterCount + constShelterCount + schoolCount == 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                "⚠️ シーン内にShelterタグまたはConstShelterタグが付いたGameObjectが見つかりません。",
                MessageType.Warning
            );
        }
    }
    
    /// <summary>
    /// シーン内の全ShelterタグをSheltersリストに追加
    /// </summary>
    private void PopulateShelters(EnvManager envManager)
    {
        // シーン内の全Shelterタグを検索
        GameObject[] shelters = GameObject.FindGameObjectsWithTag("Shelter");
        GameObject[] constShelters = new GameObject[0];
        
        // ConstShelterタグが存在する場合のみ取得
        try
        {
            constShelters = GameObject.FindGameObjectsWithTag("ConstShelter");
        }
        catch (UnityException)
        {
            Debug.LogWarning("[EnvManager] ConstShelterタグが定義されていません。Shelterタグのみを追加します。");
        }
        
        // 見つからなかった場合の処理
        if (shelters.Length == 0 && constShelters.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Shelterが見つかりません",
                "シーン内に「Shelter」または「ConstShelter」タグのついた" +
                "GameObjectが見つかりませんでした。\n\n" +
                "以下を確認してください：\n" +
                "1. 対象のGameObjectにShelterタグが設定されているか\n" +
                "2. シーンが正しく読み込まれているか\n" +
                "3. PLATEAUの建物データがインポートされているか",
                "OK"
            );
            return;
        }
        
        // リストを作成して結合
        List<GameObject> allShelters = new List<GameObject>();
        allShelters.AddRange(shelters);
        allShelters.AddRange(constShelters);
        
        // Undo対応でSheltersに設定
        Undo.RecordObject(envManager, "Populate Shelters");
        envManager.Shelters = allShelters;
        EditorUtility.SetDirty(envManager);
        
        // 結果をログに出力
        Debug.Log($"[EnvManager] {allShelters.Count}個のShelterをSheltersリストに追加しました。" +
                  $"\n- Shelterタグ: {shelters.Length}個" +
                  $"\n- ConstShelterタグ: {constShelters.Length}個");
        
        // リストの内容を出力（最初の5件のみ）
        int displayCount = Mathf.Min(5, allShelters.Count);
        for (int i = 0; i < displayCount; i++)
        {
            Debug.Log($"  [{i + 1}] {allShelters[i].name}");
        }
        if (allShelters.Count > 5)
        {
            Debug.Log($"  ... 他 {allShelters.Count - 5}件");
        }
        
        // 完了ダイアログ
        EditorUtility.DisplayDialog(
            "✅ 完了",
            $"{allShelters.Count}個のShelterをSheltersリストに追加しました。\n\n" +
            $"内訳:\n" +
            $"・Shelterタグ: {shelters.Length}個\n" +
            $"・ConstShelterタグ: {constShelters.Length}個\n\n" +
            $"実行時には Start() メソッド内で\n" +
            $"これらの避難所にShelterコンポーネントが\n" +
            $"自動的にアタッチされ、収容人数が計算されます。\n\n" +
            $"詳細はConsoleログをご確認ください。",
            "OK"
        );
    }
    
    /// <summary>
    /// Sheltersリストをクリア
    /// </summary>
    private void ClearShelters(EnvManager envManager)
    {
        Undo.RecordObject(envManager, "Clear Shelters");
        envManager.Shelters = new List<GameObject>();
        EditorUtility.SetDirty(envManager);
        
        Debug.Log("[EnvManager] Sheltersリストをクリアしました。");
    }
}
