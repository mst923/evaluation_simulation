using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using EvacSim.Environment;

namespace EvacSim.Evacuees
{
/// <summary>
/// 家族グループ内のメンバー情報
/// </summary>
[Serializable]
public class FamilyGroupMember
{
    public string family_id;           // 家族グループID
    public string member_id;           // グループ内ID (m1, m2, ...)
    public string name;                // 名前
    public string role_in_family;      // 家族内での役割 (父/母/子/本人/祖父/祖母)
    public int agent_id;               // シーン内エージェントID (-1 = NPC)
    public BuildingCategory spawn_category;  // スポーンカテゴリ
    public bool has_phone;             // 連絡手段の有無
    public bool exists_in_scene;       // シーン内に存在するか
    public string dependency;          // 依存関係 (none/requires_assistance/caregiver:X/dependent:X)

    // 動的に設定されるフィールド
    public string relation_to_me;      // 自分から見た続柄（動的生成）
    public Vector3 search_position;    // 探索位置
    public string spawn_building_name; // スポーンした建物名
    public string likely_location;     // 推定所在地の説明
    public float distance_meters;      // 現在位置からの距離
}

/// <summary>
/// 家族グループデータ
/// </summary>
[Serializable]
public class FamilyGroup
{
    public string family_id;
    public List<FamilyGroupMember> members;

    public FamilyGroup()
    {
        members = new List<FamilyGroupMember>();
    }
}

/// <summary>
/// 家族グループ情報を管理するクラス（双方向対応）
/// </summary>
public static class FamilyGroupManager
{
    private static Dictionary<string, FamilyGroup> _familyGroups = null;
    private static Dictionary<int, string> _agentToFamilyMap = null;  // agent_id → family_id
    private static readonly string FamilyGroupsCsvPath = Path.Combine(Application.dataPath, "Config", "family_groups.csv");

    // 続柄変換テーブル: (自分の役割, 相手の役割) → 相手の続柄
    private static readonly Dictionary<(string, string), string> RelationMap = new Dictionary<(string, string), string>
    {
        // 父から見た続柄
        { ("父", "母"), "妻" },
        { ("父", "子"), "子供" },
        { ("父", "祖父"), "父" },
        { ("父", "祖母"), "母" },

        // 母から見た続柄
        { ("母", "父"), "夫" },
        { ("母", "子"), "子供" },
        { ("母", "祖父"), "義父" },
        { ("母", "祖母"), "義母" },

        // 子から見た続柄
        { ("子", "父"), "父" },
        { ("子", "母"), "母" },
        { ("子", "子"), "兄弟" },
        { ("子", "祖父"), "祖父" },
        { ("子", "祖母"), "祖母" },

        // 本人（単身者）から見た続柄
        { ("本人", "介護者"), "介護者" },
        { ("本人", "父"), "父" },
        { ("本人", "母"), "母" },
        { ("本人", "子"), "子供" },

        // 祖父から見た続柄
        { ("祖父", "祖母"), "妻" },
        { ("祖父", "父"), "息子" },
        { ("祖父", "母"), "嫁" },
        { ("祖父", "子"), "孫" },

        // 祖母から見た続柄
        { ("祖母", "祖父"), "夫" },
        { ("祖母", "父"), "息子" },
        { ("祖母", "母"), "嫁" },
        { ("祖母", "子"), "孫" },
    };

    /// <summary>
    /// CSVファイルから家族グループデータを読み込む
    /// </summary>
    public static void LoadFamilyGroups()
    {
        _familyGroups = new Dictionary<string, FamilyGroup>();
        _agentToFamilyMap = new Dictionary<int, string>();

        if (!File.Exists(FamilyGroupsCsvPath))
        {
            Debug.LogWarning($"[FamilyGroupManager] CSV file not found: {FamilyGroupsCsvPath}");
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(FamilyGroupsCsvPath);
            if (lines.Length < 2)
            {
                Debug.LogWarning("[FamilyGroupManager] CSV file is empty or has no data rows");
                return;
            }

            // ヘッダー行をスキップ（1行目）
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                var fields = line.Split(',');
                if (fields.Length < 9)
                {
                    Debug.LogWarning($"[FamilyGroupManager] Invalid CSV line {i}: {line}");
                    continue;
                }

                string familyId = fields[0];
                string memberId = fields[1];
                string memberName = fields[2];
                string roleInFamily = fields[3];
                int agentId = int.Parse(fields[4]);
                string spawnCategoryStr = fields[5];
                bool hasPhone = bool.Parse(fields[6]);
                bool existsInScene = bool.Parse(fields[7]);
                string dependency = fields[8];

                // BuildingCategoryをパース
                BuildingCategory spawnCategory;
                if (!Enum.TryParse(spawnCategoryStr, true, out spawnCategory))
                {
                    Debug.LogWarning($"[FamilyGroupManager] Invalid spawn_category '{spawnCategoryStr}' at line {i}");
                    spawnCategory = BuildingCategory.Other;
                }

                // FamilyGroupを取得または作成
                if (!_familyGroups.ContainsKey(familyId))
                {
                    _familyGroups[familyId] = new FamilyGroup
                    {
                        family_id = familyId
                    };
                }

                var member = new FamilyGroupMember
                {
                    family_id = familyId,
                    member_id = memberId,
                    name = memberName,
                    role_in_family = roleInFamily,
                    agent_id = agentId,
                    spawn_category = spawnCategory,
                    has_phone = hasPhone,
                    exists_in_scene = existsInScene,
                    dependency = dependency,
                    likely_location = BuildingCategorizer.GetCategoryDisplayName(spawnCategory)
                };

                _familyGroups[familyId].members.Add(member);

                // agent_id → family_id のマッピングを作成
                if (agentId > 0)
                {
                    _agentToFamilyMap[agentId] = familyId;
                }
            }

            Debug.Log($"[FamilyGroupManager] Loaded {_familyGroups.Count} family groups from {FamilyGroupsCsvPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FamilyGroupManager] Failed to load family groups: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// agent_idから見た家族メンバー一覧を取得（双方向対応）
    /// </summary>
    public static List<FamilyGroupMember> GetFamilyMembersForAgent(int agentId)
    {
        EnsureLoaded();

        // このagentが属するfamily_idを取得
        if (!_agentToFamilyMap.TryGetValue(agentId, out string familyId))
        {
            return new List<FamilyGroupMember>();
        }

        // 自分の情報を取得
        var myInfo = GetMemberByAgentId(agentId);
        if (myInfo == null)
        {
            return new List<FamilyGroupMember>();
        }

        // 同じfamily_idの他メンバーを取得
        var familyMembers = _familyGroups[familyId].members
            .Where(m => m.agent_id != agentId)
            .Select(m => CloneMemberWithRelation(m, myInfo.role_in_family))
            .ToList();

        return familyMembers;
    }

    /// <summary>
    /// メンバーをクローンし、自分から見た続柄を設定
    /// </summary>
    private static FamilyGroupMember CloneMemberWithRelation(FamilyGroupMember original, string myRole)
    {
        return new FamilyGroupMember
        {
            family_id = original.family_id,
            member_id = original.member_id,
            name = original.name,
            role_in_family = original.role_in_family,
            agent_id = original.agent_id,
            spawn_category = original.spawn_category,
            has_phone = original.has_phone,
            exists_in_scene = original.exists_in_scene,
            dependency = original.dependency,
            likely_location = original.likely_location,
            search_position = original.search_position,
            spawn_building_name = original.spawn_building_name,
            distance_meters = original.distance_meters,
            relation_to_me = GetRelation(myRole, original.role_in_family)
        };
    }

    /// <summary>
    /// 自分の役割と相手の役割から、相手の続柄を取得
    /// </summary>
    public static string GetRelation(string myRole, string theirRole)
    {
        if (RelationMap.TryGetValue((myRole, theirRole), out var relation))
        {
            return relation;
        }
        // フォールバック: そのまま返す
        return theirRole;
    }

    /// <summary>
    /// agent_idからメンバー情報を取得
    /// </summary>
    public static FamilyGroupMember GetMemberByAgentId(int agentId)
    {
        EnsureLoaded();

        if (!_agentToFamilyMap.TryGetValue(agentId, out string familyId))
        {
            return null;
        }

        return _familyGroups[familyId].members.FirstOrDefault(m => m.agent_id == agentId);
    }

    /// <summary>
    /// family_idから家族グループを取得
    /// </summary>
    public static FamilyGroup GetFamilyGroup(string familyId)
    {
        EnsureLoaded();

        if (_familyGroups.TryGetValue(familyId, out var group))
        {
            return group;
        }
        return null;
    }

    /// <summary>
    /// agent_idが属する家族グループのfamily_idを取得
    /// </summary>
    public static string GetFamilyIdForAgent(int agentId)
    {
        EnsureLoaded();

        if (_agentToFamilyMap.TryGetValue(agentId, out string familyId))
        {
            return familyId;
        }
        return null;
    }

    /// <summary>
    /// 全ての家族グループを取得
    /// </summary>
    public static Dictionary<string, FamilyGroup> GetAllFamilyGroups()
    {
        EnsureLoaded();
        return _familyGroups ?? new Dictionary<string, FamilyGroup>();
    }

    /// <summary>
    /// agent_idのスポーンカテゴリを取得
    /// </summary>
    public static BuildingCategory GetSpawnCategoryForAgent(int agentId)
    {
        var member = GetMemberByAgentId(agentId);
        return member?.spawn_category ?? BuildingCategory.Other;
    }

    /// <summary>
    /// 必要に応じてデータを読み込む
    /// </summary>
    private static void EnsureLoaded()
    {
        if (_familyGroups == null)
        {
            LoadFamilyGroups();
        }
    }

    /// <summary>
    /// キャッシュをクリア（テスト用）
    /// </summary>
    public static void ClearCache()
    {
        _familyGroups = null;
        _agentToFamilyMap = null;
    }

    /// <summary>
    /// エピソード開始時に家族グループメンバーの動的データをリセット
    /// キャッシュ自体は保持し、search_position等のエピソード依存データのみクリア
    /// </summary>
    public static void ResetEpisodeState()
    {
        if (_familyGroups == null) return;

        foreach (var group in _familyGroups.Values)
        {
            foreach (var member in group.members)
            {
                member.search_position = Vector3.zero;
                member.spawn_building_name = "";
                member.distance_meters = 0f;
                member.relation_to_me = "";
                member.likely_location = BuildingCategorizer.GetCategoryDisplayName(member.spawn_category);
            }
        }

        Debug.Log("[FamilyGroupManager] エピソード状態をリセットしました");
    }
}
}
