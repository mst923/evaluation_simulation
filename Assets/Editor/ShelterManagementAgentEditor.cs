using UnityEngine;
using UnityEditor;
using EvacSim.Shelters;

/// <summary>
/// ShelterManagementAgentのカスタムインスペクター
/// Shelterタグの付いたGameObjectを自動的にShelterCandidatesに追加するボタンを提供します
/// </summary>
[CustomEditor(typeof(ShelterManagementAgent))]
public class ShelterManagementAgentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // デフォルトのInspector表示
        DrawDefaultInspector();
        
        ShelterManagementAgent agent = (ShelterManagementAgent)target;
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("自動設定ツール", EditorStyles.boldLabel);
        
        // 現在の候補数を表示
        if (agent.ShelterCandidates != null)
        {
            EditorGUILayout.LabelField($"現在の候補数: {agent.ShelterCandidates.Length}個", EditorStyles.miniLabel);
        }
        
        EditorGUILayout.Space(5);
        
        // ボタン: Shelterタグの全GameObjectを追加
        GUI.backgroundColor = new Color(0.5f, 0.8f, 1.0f);
        if (GUILayout.Button("📍 Shelterタグの全GameObjectを追加", GUILayout.Height(35)))
        {
            PopulateShelterCandidates(agent);
        }
        GUI.backgroundColor = Color.white;
        
        EditorGUILayout.Space(3);
        
        // ボタン: 配列をクリア
        GUI.backgroundColor = new Color(1.0f, 0.6f, 0.6f);
        if (GUILayout.Button("🗑 ShelterCandidatesをクリア", GUILayout.Height(25)))
        {
            if (EditorUtility.DisplayDialog(
                "確認",
                "ShelterCandidates配列をクリアしますか？",
                "はい", "キャンセル"))
            {
                ClearShelterCandidates(agent);
            }
        }
        GUI.backgroundColor = Color.white;
        
        EditorGUILayout.Space(5);
        
        // ヘルプボックス
        EditorGUILayout.HelpBox(
            "「Shelterタグの全GameObjectを追加」ボタンで、シーン内の全ての" +
            "「Shelter」タグおよび「ConstShelter」タグ付きGameObjectを" +
            "自動的にShelterCandidatesに追加します。\n\n" +
            "※既存の配列は上書きされます。",
            MessageType.Info
        );
        
        // シーン内のタグ付きオブジェクト数を表示
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("シーン内の状況", EditorStyles.boldLabel);
        
        int shelterCount = GameObject.FindGameObjectsWithTag("Shelter").Length;
        int constShelterCount = 0;
        
        try
        {
            constShelterCount = GameObject.FindGameObjectsWithTag("ConstShelter").Length;
        }
        catch
        {
            // ConstShelterタグが存在しない場合は0
        }
        
        EditorGUILayout.LabelField($"・Shelterタグ: {shelterCount}個", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・ConstShelterタグ: {constShelterCount}個", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"・合計: {shelterCount + constShelterCount}個", EditorStyles.miniLabel);
    }
    
    /// <summary>
    /// シーン内の全ShelterタグをShelterCandidatesに追加
    /// </summary>
    private void PopulateShelterCandidates(ShelterManagementAgent agent)
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
            Debug.LogWarning("[ShelterManagementAgent] ConstShelterタグが定義されていません。Shelterタグのみを追加します。");
        }

        // Schoolタグも避難所として取得
        GameObject[] schools = new GameObject[0];
        try
        {
            schools = GameObject.FindGameObjectsWithTag("School");
        }
        catch (UnityException)
        {
            // Schoolタグが存在しない場合は無視
        }

        // 配列を結合
        GameObject[] allShelters = new GameObject[shelters.Length + constShelters.Length + schools.Length];
        shelters.CopyTo(allShelters, 0);
        constShelters.CopyTo(allShelters, shelters.Length);
        schools.CopyTo(allShelters, shelters.Length + constShelters.Length);
        
        // 見つからなかった場合の処理
        if (allShelters.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Shelterが見つかりません",
                "シーン内に「Shelter」または「ConstShelter」タグのついた" +
                "GameObjectが見つかりませんでした。\n\n" +
                "以下を確認してください：\n" +
                "1. 対象のGameObjectにShelterタグが設定されているか\n" +
                "2. シーンが正しく読み込まれているか",
                "OK"
            );
            return;
        }
        
        // Undo対応でShelterCandidatesに設定
        Undo.RecordObject(agent, "Populate Shelter Candidates");
        agent.ShelterCandidates = allShelters;
        EditorUtility.SetDirty(agent);
        
        // 結果をログに出力
        Debug.Log($"[ShelterManagementAgent] {allShelters.Length}個のShelterを追加しました。" +
                  $"\n- Shelter: {shelters.Length}個" +
                  $"\n- ConstShelter: {constShelters.Length}個");
        
        // 完了ダイアログ
        EditorUtility.DisplayDialog(
            "✅ 完了",
            $"{allShelters.Length}個のShelterをShelterCandidatesに追加しました。\n\n" +
            $"内訳:\n" +
            $"・Shelter: {shelters.Length}個\n" +
            $"・ConstShelter: {constShelters.Length}個\n\n" +
            $"詳細はConsoleログをご確認ください。",
            "OK"
        );
    }
    
    /// <summary>
    /// ShelterCandidates配列をクリア
    /// </summary>
    private void ClearShelterCandidates(ShelterManagementAgent agent)
    {
        Undo.RecordObject(agent, "Clear Shelter Candidates");
        agent.ShelterCandidates = new GameObject[0];
        EditorUtility.SetDirty(agent);
        
        Debug.Log("[ShelterManagementAgent] ShelterCandidatesをクリアしました。");
    }
}
