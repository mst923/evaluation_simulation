using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using EvacSim.Environment;

namespace EvacSim.Evacuee
{
/// <summary>
/// 家族メンバーの情報
/// </summary>
[Serializable]
public class FamilyMember
{
    public int agent_id;           // -1 = シーン内に存在しないNPC、正の値 = シーン内の他の避難者
    public string name;            // 名前
    public string relation;        // 続柄（妻、夫、息子、娘、父、母など）
    public BuildingCategory spawn_category; // スポーンする建物カテゴリ
    public Vector3 spawn_position; // 実際のスポーン位置（初期化時に設定）
    public string spawn_building_name; // スポーンした建物名
    public bool has_phone;         // 連絡手段を持っているか
    public bool exists_in_scene;   // シーン内に実在するか

    // 探索用
    public string likely_location; // 想定される場所の説明（"自宅", "小学校"など）
    public Vector3 search_position; // 探索する座標
    public float distance_meters;   // 所有者からの距離（計算時に設定）

    // 合流状態
    public bool is_reunited;       // 合流済みかどうか
}

/// <summary>
/// 避難者の家族構成情報
/// </summary>
[Serializable]
public class FamilyData
{
    public int owner_agent_id;                  // この家族情報の所有者
    public BuildingCategory owner_spawn_category; // 所有者のスポーンカテゴリ
    public List<FamilyMember> members;          // 家族メンバーリスト

    public FamilyData()
    {
        members = new List<FamilyMember>();
    }
}

/// <summary>
/// 家族情報を管理するクラス
/// FamilyGroupManagerを内部で使用し、双方向の家族関係をサポート
/// </summary>
public static class FamilyManager
{
    private static Dictionary<int, FamilyData> _familiesCache = null;
    private static bool _useNewFormat = true;  // 新形式（family_groups.csv）を使用
    private static readonly string FamilyCsvPath = Path.Combine(Application.dataPath, "Config", "families.csv");
    private static readonly string FamilyGroupsCsvPath = Path.Combine(Application.dataPath, "Config", "family_groups.csv");

    /// <summary>
    /// 家族データを読み込む
    /// 新形式（family_groups.csv）が存在すれば優先使用、なければ旧形式（families.csv）を使用
    /// </summary>
    public static void LoadFamilies()
    {
        _familiesCache = new Dictionary<int, FamilyData>();

        // 新形式ファイルの存在確認
        _useNewFormat = File.Exists(FamilyGroupsCsvPath);

        if (_useNewFormat)
        {
            LoadFromFamilyGroups();
        }
        else
        {
            LoadFromLegacyFormat();
        }
    }

    /// <summary>
    /// 新形式（family_groups.csv）から読み込み
    /// FamilyGroupManagerを使用して双方向関係を自動生成
    /// </summary>
    private static void LoadFromFamilyGroups()
    {
        Debug.Log("[FamilyManager] Loading from new format (family_groups.csv)");

        // FamilyGroupManagerを初期化
        FamilyGroupManager.LoadFamilyGroups();

        // 全家族グループを取得
        var allGroups = FamilyGroupManager.GetAllFamilyGroups();

        // 各シーン内エージェントに対してFamilyDataを生成
        foreach (var group in allGroups.Values)
        {
            foreach (var member in group.members)
            {
                if (member.agent_id > 0)  // シーン内エージェントのみ
                {
                    var familyData = BuildFamilyDataForAgent(member.agent_id);
                    if (familyData != null)
                    {
                        _familiesCache[member.agent_id] = familyData;
                    }
                }
            }
        }

        Debug.Log($"[FamilyManager] Loaded family data for {_familiesCache.Count} agents from family_groups.csv (bidirectional)");
    }

    /// <summary>
    /// agent_idに対するFamilyDataを構築（双方向対応）
    /// </summary>
    private static FamilyData BuildFamilyDataForAgent(int agentId)
    {
        var familyMembers = FamilyGroupManager.GetFamilyMembersForAgent(agentId);
        var myInfo = FamilyGroupManager.GetMemberByAgentId(agentId);

        if (myInfo == null)
        {
            return null;
        }

        var familyData = new FamilyData
        {
            owner_agent_id = agentId,
            owner_spawn_category = myInfo.spawn_category
        };

        // 家族メンバーをFamilyMember形式に変換
        foreach (var groupMember in familyMembers)
        {
            var member = new FamilyMember
            {
                agent_id = groupMember.agent_id,
                name = groupMember.name,
                relation = groupMember.relation_to_me,  // 自分から見た続柄（動的生成済み）
                spawn_category = groupMember.spawn_category,
                has_phone = groupMember.has_phone,
                exists_in_scene = groupMember.exists_in_scene,
                likely_location = groupMember.likely_location,
                search_position = groupMember.search_position,
                spawn_building_name = groupMember.spawn_building_name,
                distance_meters = groupMember.distance_meters
            };

            familyData.members.Add(member);
        }

        return familyData;
    }

    /// <summary>
    /// 旧形式（families.csv）から読み込み（後方互換性のため維持）
    /// </summary>
    private static void LoadFromLegacyFormat()
    {
        Debug.Log("[FamilyManager] Loading from legacy format (families.csv)");

        if (!File.Exists(FamilyCsvPath))
        {
            Debug.LogWarning($"[FamilyManager] CSV file not found: {FamilyCsvPath}");
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(FamilyCsvPath);
            if (lines.Length < 2)
            {
                Debug.LogWarning("[FamilyManager] CSV file is empty or has no data rows");
                return;
            }

            // ヘッダー行をスキップ（1行目）
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                var fields = line.Split(',');
                if (fields.Length < 7)
                {
                    Debug.LogWarning($"[FamilyManager] Invalid CSV line {i}: {line}");
                    continue;
                }

                int ownerId = int.Parse(fields[0]);
                string relation = fields[1];
                int targetAgentId = int.Parse(fields[2]);
                string targetName = fields[3];
                string spawnCategoryStr = fields[4];
                bool hasPhone = bool.Parse(fields[5]);
                bool existsInScene = bool.Parse(fields[6]);

                // BuildingCategoryをパース
                BuildingCategory spawnCategory;
                if (!Enum.TryParse(spawnCategoryStr, true, out spawnCategory))
                {
                    Debug.LogWarning($"[FamilyManager] Invalid spawn_category '{spawnCategoryStr}' at line {i}");
                    spawnCategory = BuildingCategory.Other;
                }

                // 所有者のFamilyDataを取得または作成
                if (!_familiesCache.ContainsKey(ownerId))
                {
                    _familiesCache[ownerId] = new FamilyData
                    {
                        owner_agent_id = ownerId,
                        owner_spawn_category = BuildingCategory.Other // 後で設定
                    };
                }

                // relationが"なし"や"self"でない場合は家族メンバーとして追加
                if (relation.ToLower() != "なし" && relation.ToLower() != "self")
                {
                    var member = new FamilyMember
                    {
                        agent_id = targetAgentId,
                        name = targetName,
                        relation = relation,
                        spawn_category = spawnCategory,
                        has_phone = hasPhone,
                        exists_in_scene = existsInScene,
                        likely_location = BuildingCategorizer.GetCategoryDisplayName(spawnCategory)
                    };

                    _familiesCache[ownerId].members.Add(member);
                }
                else
                {
                    // 本人のスポーンカテゴリを設定
                    _familiesCache[ownerId].owner_spawn_category = spawnCategory;
                }
            }

            Debug.Log($"[FamilyManager] Loaded family data for {_familiesCache.Count} agents from {FamilyCsvPath} (legacy format)");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FamilyManager] Failed to load families: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// エージェントIDに対応する家族データを取得
    /// </summary>
    public static FamilyData GetFamily(int agentId)
    {
        if (_familiesCache == null)
        {
            LoadFamilies();
        }

        if (_familiesCache != null && _familiesCache.TryGetValue(agentId, out var family))
        {
            // 一人暮らしの場合はmembersが空になるのは正常なので警告を出さない
            return family;
        }

        // キャッシュにない場合は動的に生成を試みる（新形式のみ）
        if (_useNewFormat)
        {
            var familyData = BuildFamilyDataForAgent(agentId);
            if (familyData != null)
            {
                _familiesCache[agentId] = familyData;
                if (familyData.members == null || familyData.members.Count == 0)
                {
                    Debug.LogWarning($"[FamilyManager] Agent {agentId}: 動的生成した家族データのメンバーが空です");
                }
                return familyData;
            }
            else
            {
                Debug.LogWarning($"[FamilyManager] Agent {agentId}: 家族データの動的生成に失敗しました（FamilyGroupManagerに登録なし？）");
            }
        }

        Debug.LogWarning($"[FamilyManager] Agent {agentId}: 家族データが見つかりません（キャッシュにもFamilyGroupManagerにも存在しない）");
        return null;
    }

    /// <summary>
    /// 全ての家族データを取得
    /// </summary>
    public static Dictionary<int, FamilyData> GetAllFamilies()
    {
        if (_familiesCache == null)
        {
            LoadFamilies();
        }
        return _familiesCache ?? new Dictionary<int, FamilyData>();
    }

    /// <summary>
    /// 新形式を使用しているかどうか
    /// </summary>
    public static bool IsUsingNewFormat()
    {
        return _useNewFormat;
    }

    /// <summary>
    /// キャッシュをクリア（テスト用）
    /// </summary>
    public static void ClearCache()
    {
        _familiesCache = null;
        FamilyGroupManager.ClearCache();
    }

    /// <summary>
    /// エピソード開始時に家族メンバーの動的データをリセット
    /// キャッシュ自体は保持し、search_position等のエピソード依存データのみクリア
    /// </summary>
    public static void ResetEpisodeState()
    {
        if (_familiesCache == null) return;

        foreach (var family in _familiesCache.Values)
        {
            foreach (var member in family.members)
            {
                member.search_position = Vector3.zero;
                member.spawn_building_name = "";
                member.distance_meters = 0f;
                member.is_reunited = false;
            }
        }

        // FamilyGroupManagerの状態もリセット
        FamilyGroupManager.ResetEpisodeState();

        Debug.Log("[FamilyManager] エピソード状態をリセットしました");
    }
}
}
