using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using EvacSim.Shelters;

/// <summary>
/// 避難所の名前と説明を一括編集するEditorウィンドウ
/// </summary>
public class ShelterInfoEditor : EditorWindow
{
    private Vector2 scrollPosition;
    private List<Shelter> shelters = new List<Shelter>();
    private bool autoRefresh = true;

    [MenuItem("Tools/Shelter/Shelter Info Editor")]
    public static void ShowWindow()
    {
        var window = GetWindow<ShelterInfoEditor>("避難所情報エディタ");
        window.minSize = new Vector2(600, 400);
        window.Show();
    }

    private void OnEnable()
    {
        RefreshShelterList();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(10);
        
        // シーンの状態を確認
        var activeScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        if (!activeScene.isLoaded)
        {
            EditorGUILayout.HelpBox("シーンがロードされていません。シーンを開いてください。", MessageType.Error);
            return;
        }
        
        // ヘッダー
        EditorGUILayout.LabelField("避難所情報の一括編集", EditorStyles.boldLabel);
        
        // シーン名を表示
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"現在のシーン: {activeScene.name}", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.HelpBox(
            "シーン内のShelterタグが付いたオブジェクトの表示名と説明を編集できます。\n" +
            "表示名と説明はLLMへのプロンプトに含まれます。",
            MessageType.Info
        );
        
        EditorGUILayout.Space(5);
        
        // ツールバー
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("🔄 リストを更新", GUILayout.Width(120)))
        {
            RefreshShelterList();
        }
        
        autoRefresh = EditorGUILayout.ToggleLeft("自動更新", autoRefresh, GUILayout.Width(80));
        
        GUILayout.FlexibleSpace();
        
        EditorGUILayout.LabelField($"避難所数: {shelters.Count}件", GUILayout.Width(100));
        
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space(5);
        
        // 自動更新
        if (autoRefresh && Event.current.type == EventType.Layout)
        {
            RefreshShelterList();
        }
        
        // 避難所リスト
        if (shelters.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "シーン内にShelterコンポーネントが付いた避難所が見つかりません。\n\n" +
                "確認事項:\n" +
                "1. シーンに「Shelter」または「ConstShelter」タグの付いたオブジェクトがあるか\n" +
                "2. そのオブジェクトに「Shelter」コンポーネントが付いているか\n" +
                "3. Consoleウィンドウで詳細なログを確認してください",
                MessageType.Warning
            );
            
            EditorGUILayout.Space(10);
            
            if (GUILayout.Button("🔍 デバッグ情報を表示", GUILayout.Height(30)))
            {
                ShowDebugInfo();
            }
            
            return;
        }
        
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("避難所リスト", EditorStyles.boldLabel);
        
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        
        foreach (var shelter in shelters)
        {
            if (shelter == null) continue;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            // オブジェクト名（クリックで選択）
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("📍", GUILayout.Width(30)))
            {
                Selection.activeGameObject = shelter.gameObject;
                EditorGUIUtility.PingObject(shelter.gameObject);
            }
            EditorGUILayout.LabelField($"GameObject: {shelter.gameObject.name}", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            
            EditorGUI.BeginChangeCheck();
            
            // 表示名
            string newDisplayName = EditorGUILayout.TextField("表示名", shelter.displayName);
            
            // 説明（複数行）
            EditorGUILayout.LabelField("説明");
            string newDescription = EditorGUILayout.TextArea(
                shelter.description, 
                GUILayout.Height(60)
            );
            
            EditorGUILayout.Space(5);
            
            // 収容情報
            EditorGUILayout.LabelField("収容情報", EditorStyles.boldLabel);
            
            // 床面積情報を取得
            double? floorArea = shelter.GetTotalFloorArea();
            string floorAreaText = floorArea.HasValue ? $"{floorArea.Value:F1}㎡" : "不明（デフォルト250㎡相当）";
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("床面積（PLATEAU）:", GUILayout.Width(150));
            EditorGUILayout.LabelField(floorAreaText, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            
            // 自動計算チェックボックス
            bool newAutoCalculate = EditorGUILayout.Toggle("床面積から自動計算", shelter.autoCalculateCapacity);
            
            if (newAutoCalculate)
            {
                // 自動計算モード
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("計算式:", GUILayout.Width(150));
                EditorGUILayout.LabelField("床面積 × 0.8 ÷ 1.65 × スケール", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                
                float newCapacityScale = EditorGUILayout.Slider("スケール係数", shelter.capacityScale, 0.1f, 5.0f);
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"計算結果: {shelter.MaxCapacity}人", GUILayout.Width(200));
                if (GUILayout.Button("🔄 再計算", GUILayout.Width(100)))
                {
                    Undo.RecordObject(shelter, "Recalculate Capacity");
                    shelter.capacityScale = newCapacityScale;
                    EditorUtility.SetDirty(shelter);
                }
                EditorGUILayout.EndHorizontal();
                
                if (newCapacityScale != shelter.capacityScale)
                {
                    Undo.RecordObject(shelter, "Change Capacity Scale");
                    shelter.capacityScale = newCapacityScale;
                    EditorUtility.SetDirty(shelter);
                }
            }
            else
            {
                // 手動設定モード
                EditorGUILayout.HelpBox("手動設定モードです。床面積から自動計算されません。", MessageType.Info);
                
                int newManualCapacity = EditorGUILayout.IntField("最大収容人数", shelter.MaxCapacity);
                
                if (newManualCapacity != shelter.MaxCapacity)
                {
                    Undo.RecordObject(shelter, "Change Manual Capacity");
                    shelter.MaxCapacity = newManualCapacity;
                    EditorUtility.SetDirty(shelter);
                }
            }
            
            if (newAutoCalculate != shelter.autoCalculateCapacity)
            {
                Undo.RecordObject(shelter, "Toggle Auto Calculate");
                shelter.autoCalculateCapacity = newAutoCalculate;
                EditorUtility.SetDirty(shelter);
            }
            
            // 現在の収容状況
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"現在の収容: {shelter.NowAccCount}人", GUILayout.Width(200));
            EditorGUILayout.LabelField($"受け入れ可能: {shelter.currentCapacity}人", GUILayout.Width(200));
            EditorGUILayout.EndHorizontal();
            
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shelter, "Edit Shelter Info");
                shelter.displayName = newDisplayName;
                shelter.description = newDescription;
                EditorUtility.SetDirty(shelter);
            }
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }
        
        EditorGUILayout.EndScrollView();
        
        EditorGUILayout.Space(10);
        
        // 一括操作エリア
        EditorGUILayout.LabelField("一括操作", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("🔄 全て自動計算に変更", GUILayout.Height(25)))
        {
            SetAllAutoCalculate(true);
        }
        
        if (GUILayout.Button("✏️ 全て手動設定に変更", GUILayout.Height(25)))
        {
            SetAllAutoCalculate(false);
        }
        
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space(10);
        
        // フッター
        EditorGUILayout.BeginHorizontal();
        
        if (GUILayout.Button("💾 すべて保存", GUILayout.Height(30)))
        {
            SaveAllChanges();
        }
        
        if (GUILayout.Button("📋 CSVにエクスポート", GUILayout.Height(30)))
        {
            ExportToCSV();
        }
        
        EditorGUILayout.EndHorizontal();
    }

    private void RefreshShelterList()
    {
        // Shelterタグのオブジェクトを検索
        GameObject[] shelterArray;
        try
        {
            shelterArray = GameObject.FindGameObjectsWithTag("Shelter");
        }
        catch (UnityException)
        {
            shelterArray = new GameObject[0];
        }
        
        var shelterObjects = shelterArray.ToList();
        
        // ConstShelterタグも追加（存在する場合）
        try
        {
            var constShelters = GameObject.FindGameObjectsWithTag("ConstShelter");
            shelterObjects.AddRange(constShelters);
        }
        catch (UnityException)
        {
            // ConstShelterタグが存在しない場合は無視
        }

        // Schoolタグも追加（避難所として動作するため）
        try
        {
            var schools = GameObject.FindGameObjectsWithTag("School");
            shelterObjects.AddRange(schools);
        }
        catch (UnityException)
        {
            // Schoolタグが存在しない場合は無視
        }

        // Shelterコンポーネントを取得
        shelters = new List<Shelter>();
        
        foreach (var go in shelterObjects)
        {
            if (go == null) continue;
            
            var shelter = go.GetComponent<Shelter>();
            if (shelter != null)
            {
                shelters.Add(shelter);
            }
        }
        
        shelters = shelters.OrderBy(s => s.gameObject.name).ToList();
    }

    private void SaveAllChanges()
    {
        foreach (var shelter in shelters)
        {
            if (shelter != null)
            {
                EditorUtility.SetDirty(shelter);
            }
        }
        
        // シーンを保存
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene()
        );
        
        EditorUtility.DisplayDialog("✅ 保存完了", "すべての避難所情報を保存しました。", "OK");
    }

    private void ExportToCSV()
    {
        string path = EditorUtility.SaveFilePanel(
            "避難所情報をCSVにエクスポート",
            "Assets",
            "shelters_info.csv",
            "csv"
        );
        
        if (string.IsNullOrEmpty(path))
            return;
        
        try
        {
            using (var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                // ヘッダー
                writer.WriteLine("GameObject名,表示名,説明,自動計算,床面積(㎡),スケール係数,最大収容人数,UUID");
                
                // データ
                foreach (var shelter in shelters)
                {
                    if (shelter == null) continue;
                    
                    string displayName = shelter.displayName.Replace(",", "，"); // カンマをエスケープ
                    string description = shelter.description.Replace(",", "，").Replace("\n", " "); // カンマと改行をエスケープ
                    
                    double? floorArea = shelter.GetTotalFloorArea();
                    string floorAreaText = floorArea.HasValue ? floorArea.Value.ToString("F1") : "不明";
                    string autoCalcText = shelter.autoCalculateCapacity ? "有効" : "無効";
                    
                    writer.WriteLine($"{shelter.gameObject.name},{displayName},{description},{autoCalcText},{floorAreaText},{shelter.capacityScale:F2},{shelter.MaxCapacity},{shelter.uuid}");
                }
            }
            
            EditorUtility.DisplayDialog(
                "✅ エクスポート完了", 
                $"CSVファイルを保存しました:\n{path}", 
                "OK"
            );
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("❌ エラー", $"CSVファイルの保存に失敗しました:\n{ex.Message}", "OK");
        }
    }

    private void SetAllAutoCalculate(bool autoCalculate)
    {
        string mode = autoCalculate ? "自動計算" : "手動設定";
        
        bool confirm = EditorUtility.DisplayDialog(
            $"⚠️ 一括変更: {mode}",
            $"すべての避難所を{mode}モードに変更しますか？\n\n" +
            $"対象: {shelters.Count}件の避難所",
            "変更する", "キャンセル"
        );
        
        if (!confirm)
            return;
        
        int changedCount = 0;
        
        foreach (var shelter in shelters)
        {
            if (shelter == null) continue;
            
            if (shelter.autoCalculateCapacity != autoCalculate)
            {
                Undo.RecordObject(shelter, $"Set Auto Calculate to {autoCalculate}");
                shelter.autoCalculateCapacity = autoCalculate;
                EditorUtility.SetDirty(shelter);
                changedCount++;
            }
        }
        
        // シーンを保存
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene()
        );
        
        EditorUtility.DisplayDialog(
            "✅ 一括変更完了",
            $"{changedCount}件の避難所を{mode}モードに変更しました。",
            "OK"
        );
    }

    private void ShowDebugInfo()
    {
        Debug.Log("========== [ShelterInfoEditor] デバッグ情報 ==========");
        
        // シーン情報
        var activeScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        Debug.Log($"現在のシーン: {activeScene.name} (Path: {activeScene.path})");
        
        // タグの確認
        Debug.Log("\n--- タグ検索結果 ---");
        
        try
        {
            var shelterTagged = GameObject.FindGameObjectsWithTag("Shelter");
            Debug.Log($"「Shelter」タグ: {shelterTagged.Length}件");
            foreach (var go in shelterTagged)
            {
                var shelter = go.GetComponent<Shelter>();
                Debug.Log($"  - {go.name} (Shelterコンポーネント: {(shelter != null ? "あり" : "なし")})");
            }
        }
        catch (UnityException ex)
        {
            Debug.LogWarning($"「Shelter」タグが存在しません: {ex.Message}");
        }
        
        try
        {
            var constShelterTagged = GameObject.FindGameObjectsWithTag("ConstShelter");
            Debug.Log($"「ConstShelter」タグ: {constShelterTagged.Length}件");
            foreach (var go in constShelterTagged)
            {
                var shelter = go.GetComponent<Shelter>();
                Debug.Log($"  - {go.name} (Shelterコンポーネント: {(shelter != null ? "あり" : "なし")})");
            }
        }
        catch (UnityException ex)
        {
            Debug.Log($"「ConstShelter」タグは存在しません: {ex.Message}");
        }
        
        // すべてのShelterコンポーネントを持つオブジェクトを検索
        Debug.Log("\n--- Shelterコンポーネント検索結果 ---");
        var allShelters = FindObjectsByType<Shelter>(FindObjectsSortMode.None);
        Debug.Log($"Shelterコンポーネントを持つオブジェクト: {allShelters.Length}件");
        foreach (var shelter in allShelters)
        {
            Debug.Log($"  - {shelter.gameObject.name} (タグ: {shelter.gameObject.tag})");
        }
        
        Debug.Log("====================================================");
        
        EditorUtility.DisplayDialog(
            "🔍 デバッグ情報",
            "デバッグ情報をConsoleに出力しました。\n\n" +
            "Consoleウィンドウを確認してください。",
            "OK"
        );
    }
}
