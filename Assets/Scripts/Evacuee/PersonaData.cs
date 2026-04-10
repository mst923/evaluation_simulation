using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace EvacSim.Evacuee
{
/// <summary>
/// ペルソナデータを保持するクラス
/// </summary>
[Serializable]
public class PersonaData
{
    public int agent_id;
    public string name;
    public string role;
    public string age_group;
    public float speed_multiplier;
    public string mental_state;
    public string priority;
    public string system_prompt_context;
    public bool has_smartphone = true; // デフォルトはtrue（スマホを持っている）

    // 拡張フィールド: 生活背景情報
    public string home_location_category;    // 自宅位置カテゴリ: "coastal", "hill", "center"
    public int home_elevation;               // 自宅の海抜(m)
    public string home_structure;            // 自宅の構造: "wooden_1story", "wooden_2story", "rc_2story", "rc_3story"
    public int residence_years;              // 居住年数
    public string local_knowledge_level;     // 土地勘レベル: "newcomer", "resident", "native"
    public string current_location_reason;   // 現在地にいる理由
    public string past_disaster_experience;  // 過去の災害経験
    public string physical_condition;        // 身体状態

    // 交通シミュレーション用
    public bool has_vehicle;                 // 車両を所有しているか
    public bool can_drive = true;            // 運転可能か（免許保有・身体状況）
}

/// <summary>
/// ペルソナデータを管理するクラス
/// </summary>
public static class PersonaManager
{
    private static Dictionary<int, PersonaData> _personas = null;
    private static readonly string PersonaCsvPath = Path.Combine(Application.dataPath, "Config", "personas.csv");

    /// <summary>
    /// Play開始時に静的キャッシュをクリアする。
    /// Enter Play Mode (Domain Reload無効) 対策: 前セッションでロードした_personasを強制リロード。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticCache()
    {
        _personas = null;
        Debug.Log("[PersonaManager] 静的キャッシュをリセットしました（Enter Play Mode対応）");
    }

    /// <summary>
    /// CSVファイルからペルソナデータを読み込む
    /// </summary>
    public static void LoadPersonas()
    {
        _personas = new Dictionary<int, PersonaData>();

        if (!File.Exists(PersonaCsvPath))
        {
            Debug.LogWarning($"[PersonaManager] CSV file not found: {PersonaCsvPath}");
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(PersonaCsvPath);
            if (lines.Length < 2)
            {
                Debug.LogWarning("[PersonaManager] CSV file is empty or has no data rows");
                return;
            }

            // ヘッダー行をスキップ（1行目）
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                // CSVのパース（カンマ区切り、ただしsystem_prompt_context内のカンマは考慮）
                var fields = ParseCsvLine(line);
                if (fields.Length < 8)
                {
                    Debug.LogWarning($"[PersonaManager] Invalid CSV line {i}: {line}");
                    continue;
                }

                var persona = new PersonaData
                {
                    agent_id = int.Parse(fields[0]),
                    name = fields[1],
                    role = fields[2],
                    age_group = fields[3],
                    speed_multiplier = float.Parse(fields[4]),
                    mental_state = fields[5],
                    priority = fields[6],
                    system_prompt_context = fields[7]
                };

                // 拡張フィールドの読み込み（オプショナル、後方互換性のため）
                // カラム順序: 8=home_location_category, 9=home_elevation, 10=home_structure,
                //            11=residence_years, 12=local_knowledge_level, 13=current_location_reason,
                //            14=past_disaster_experience, 15=physical_condition
                if (fields.Length >= 9)
                    persona.home_location_category = fields[8];
                if (fields.Length >= 10 && int.TryParse(fields[9], out int elevation))
                    persona.home_elevation = elevation;
                if (fields.Length >= 11)
                    persona.home_structure = fields[10];
                if (fields.Length >= 12 && int.TryParse(fields[11], out int years))
                    persona.residence_years = years;
                if (fields.Length >= 13)
                    persona.local_knowledge_level = fields[12];
                if (fields.Length >= 14)
                    persona.current_location_reason = fields[13];
                if (fields.Length >= 15)
                    persona.past_disaster_experience = fields[14];
                if (fields.Length >= 16)
                    persona.physical_condition = fields[15];

                // has_smartphone: 16番目のカラム（オプショナル、デフォルトはtrue）
                if (fields.Length >= 17)
                {
                    string smartphoneValue = fields[16].Trim().ToLower();
                    if (smartphoneValue == "false" || smartphoneValue == "0")
                        persona.has_smartphone = false;
                    else if (smartphoneValue == "true" || smartphoneValue == "1" || string.IsNullOrEmpty(smartphoneValue))
                        persona.has_smartphone = true;
                    else if (bool.TryParse(fields[16], out bool hasPhone))
                        persona.has_smartphone = hasPhone;
                }

                // has_vehicle: 17番目のカラム（オプショナル、デフォルトはfalse）
                if (fields.Length >= 18)
                {
                    string vehicleValue = fields[17].Trim().ToLower();
                    persona.has_vehicle = vehicleValue == "true" || vehicleValue == "1";
                }

                // can_drive: 18番目のカラム（オプショナル、デフォルトはtrue）
                if (fields.Length >= 19)
                {
                    string driveValue = fields[18].Trim().ToLower();
                    if (driveValue == "false" || driveValue == "0")
                        persona.can_drive = false;
                }

                _personas[persona.agent_id] = persona;
            }

            Debug.Log($"[PersonaManager] Loaded {_personas.Count} personas from {PersonaCsvPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PersonaManager] Failed to load personas: {ex.Message}");
        }
    }

    /// <summary>
    /// CSV行をパース（ダブルクォートで囲まれたフィールドを考慮）
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = new List<string>();
        bool inQuotes = false;
        string currentField = "";

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(currentField);
                currentField = "";
            }
            else
            {
                currentField += c;
            }
        }
        fields.Add(currentField); // 最後のフィールド

        return fields.ToArray();
    }

    /// <summary>
    /// エージェントIDに対応するペルソナデータを取得
    /// </summary>
    public static PersonaData GetPersona(int agentId)
    {
        if (_personas == null)
        {
            LoadPersonas();
        }

        if (_personas != null && _personas.TryGetValue(agentId, out var persona))
        {
            return persona;
        }

        return null;
    }

    /// <summary>
    /// 全てのペルソナデータを取得
    /// </summary>
    public static Dictionary<int, PersonaData> GetAllPersonas()
    {
        if (_personas == null)
        {
            LoadPersonas();
        }
        return _personas ?? new Dictionary<int, PersonaData>();
    }
}
}

