using UnityEngine;

namespace EvacSim.Environment
{
/// <summary>
/// 建物のカテゴリ（用途種別）
/// </summary>
public enum BuildingCategory
{
    Residential,          // 住宅
    WorkplaceResidential, // 職場兼住宅（作業所共同住宅・店舗共同住宅など）
    Office,               // 職場・オフィス・工場等
    Commercial,           // 商業施設
    PublicFacility,       // 公共施設（官公庁施設など）
    School,               // 学校（※現時点ではusageでは判定しない、タグで対応）
    PreSchool,            // 保育園・幼稚園（※現時点ではusageでは判定しない、タグで対応）
    Hospital,             // 病院・医療施設（※現時点ではusageでは判定しない）
    Unknown,              // 用途が不明
    Other                 // 上記以外
}

/// <summary>
/// PLATEAU建物データから用途カテゴリを判定する
/// </summary>
public static class BuildingCategorizer
{
    /// <summary>
    /// 建物のusageからカテゴリを判定（シンプルルール）
    /// </summary>
    public static BuildingCategory Categorize(EnvironmentalContextProvider.BuildingContext building)
    {
        if (building == null)
            return BuildingCategory.Unknown;

        // usageのみを参照し、小文字で判定（PLATEAUの日本語ラベルを想定）
        string usage = (building.usage ?? string.Empty).ToLower();

        // usageが空、または「不明」の場合は Unknown
        if (string.IsNullOrWhiteSpace(usage) || usage.Contains("不明"))
        {
            return BuildingCategory.Unknown;
        }

        // 1) usageが商業施設に属するものは「商業施設（Commercial）」として分類
        if (usage.Contains("商業施設"))
        {
            return BuildingCategory.Commercial;
        }

        // 2) usageが工場、供給処理施設に属するものは「職場（Office）」として分類
        //    例: "工場", "供給処理施設" など
        if (usage.Contains("工場") ||
            usage.Contains("供給処理施設"))
        {
            return BuildingCategory.Office;
        }

        // 3) usageが官公庁施設に属するものは「公共施設（PublicFacility）」として分類
        if (usage.Contains("官公庁施設"))
        {
            return BuildingCategory.PublicFacility;
        }

        // 4) usageが作業所共同住宅、店舗共同住宅に属するものは「職場兼住宅」として分類
        //    PLATEAUの実データでは「作業所兼用住宅」「店舗等併用住宅」等のバリエーションも考えられるため
        //    「作業所」かつ「住宅」、「店舗」かつ「住宅」を含む場合を職場兼住宅とみなす
        if ((usage.Contains("作業所") && usage.Contains("住宅")) ||
            (usage.Contains("店舗") && usage.Contains("住宅")))
        {
            return BuildingCategory.WorkplaceResidential;
        }

        // 5) usageが住宅に所属するものは「住宅」に分類
        //    ここでは「住宅」「共同住宅」などを広く含める
        if (usage.Contains("住宅"))
        {
            return BuildingCategory.Residential;
        }

        // 6) usageが上記以外の場合は「その他」として分類
        return BuildingCategory.Other;
    }

    /// <summary>
    /// カテゴリの日本語名を取得
    /// </summary>
    public static string GetCategoryDisplayName(BuildingCategory category)
    {
        switch (category)
        {
            case BuildingCategory.Residential:
                return "住宅";
            case BuildingCategory.WorkplaceResidential:
                return "職場兼住宅";
            case BuildingCategory.Office:
                return "職場・オフィス";
            case BuildingCategory.Commercial:
                return "商業施設";
            case BuildingCategory.PublicFacility:
                return "公共施設";
            case BuildingCategory.School:
                return "学校";
            case BuildingCategory.PreSchool:
                return "保育園・幼稚園";
            case BuildingCategory.Hospital:
                return "病院";
            case BuildingCategory.Unknown:
                return "不明";
            case BuildingCategory.Other:
                return "その他";
            default:
                return "不明";
        }
    }
}
}
