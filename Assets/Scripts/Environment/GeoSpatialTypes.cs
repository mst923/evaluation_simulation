using System;
using UnityEngine;

namespace EvacSim.Environment
{
/// <summary>
/// PLATEAU GameObjectの種類を表す列挙型
/// </summary>
public enum GeoObjectType
{
    Building,       // bldg_ - 建物
    Road,           // tran_ - 道路
    LandUse,        // luse_ - 土地利用
    TsunamiRisk,    // tnm_  - 津波浸水想定区域
    LandslideRisk,  // lsld_ - 土砂災害リスク区域
    Terrain,        // dem_  - 地形(DEM)
    UrbanPlan       // urf_  - 都市計画区域
}

/// <summary>
/// 津波リスクのコンテキスト情報
/// </summary>
[Serializable]
public class TsunamiRiskContext
{
    public string name;
    public Vector3 position;
    public string gmlId;
    public string description;      // 浸水想定区域の説明
    public string rank;             // 浸水深ランク（例: "5m以上10m未満"）
    public float estimatedDepth;    // 推定浸水深(m)

    /// <summary>
    /// 浸水深ランクから推定浸水深を算出
    /// </summary>
    public static float ParseDepthFromRank(string rank)
    {
        if (string.IsNullOrEmpty(rank)) return 0f;
        if (rank.Contains("10m以上") || rank.Contains("10m～")) return 12f;
        if (rank.Contains("5m以上") || rank.Contains("5m～")) return 7.5f;
        if (rank.Contains("3m以上") || rank.Contains("3m～")) return 4f;
        if (rank.Contains("2m以上") || rank.Contains("2m～")) return 2.5f;
        if (rank.Contains("1m以上") || rank.Contains("1m～")) return 1.5f;
        if (rank.Contains("0.5m以上") || rank.Contains("0.5m～")) return 0.75f;
        if (rank.Contains("0.3m以上") || rank.Contains("0.3m～")) return 0.4f;
        return 0.3f;
    }
}

/// <summary>
/// 土砂災害リスクのコンテキスト情報
/// </summary>
[Serializable]
public class LandslideRiskContext
{
    public string name;
    public Vector3 position;
    public string gmlId;

    // urf:areaType - 区域タイプ
    public int areaType;            // 1=警戒区域(指定済), 2=特別警戒区域(指定済), 3=警戒区域(指定前), 4=特別警戒区域(指定前)
    public string areaTypeName;     // 日本語名

    // urf:description - 災害種別
    public int descriptionCode;     // 1=急傾斜地の崩落, 2=土石流, 3=地すべり
    public string descriptionName;  // 日本語名

    public bool isSpecialZone;      // 特別警戒区域かどうか（areaType == 2 or 4）

    /// <summary>
    /// 区域タイプコードから日本語名を取得
    /// </summary>
    public static string GetAreaTypeName(int areaType)
    {
        return areaType switch
        {
            1 => "土砂災害警戒区域（指定済）",
            2 => "土砂災害特別警戒区域（指定済）",
            3 => "土砂災害警戒区域（指定前）",
            4 => "土砂災害特別警戒区域（指定前）",
            _ => "不明"
        };
    }

    /// <summary>
    /// 災害種別コードから日本語名を取得
    /// </summary>
    public static string GetDescriptionName(int descriptionCode)
    {
        return descriptionCode switch
        {
            1 => "急傾斜地の崩落",
            2 => "土石流",
            3 => "地すべり",
            _ => "不明"
        };
    }

    /// <summary>
    /// 特別警戒区域かどうかを判定
    /// </summary>
    public static bool IsSpecialZone(int areaType)
    {
        return areaType == 2 || areaType == 4;
    }
}

/// <summary>
/// 地形コンテキスト情報
/// </summary>
[Serializable]
public class TerrainContext
{
    public float elevation;             // 現在地の標高(m)
    public float nearbyMaxElevation;    // 周辺の最高標高
    public Vector3 highGroundDirection; // 高台の方向（正規化済み）
    public float distanceToHighGround;  // 高台までの距離(m)
}

/// <summary>
/// 道路のコンテキスト情報
/// </summary>
[Serializable]
public class RoadContext
{
    public string name;
    public Vector3 position;
    public float distance;
    public string gmlId;

    // tran:function - 道路機能
    public int functionCode;        // 1=高速, 2=一般国道, 3=都道府県道, 4=市町村道, 9020=不明
    public string functionName;     // 日本語名

    // uro:sectionType - 区間種別
    public int sectionType;         // 1=土工区間, 2=高架橋, 3=橋梁, 4=交差部, 5=アンダーパス, 6=トンネル
    public string sectionTypeName;  // 日本語名

    /// <summary>OSM道路ネットワークとの紐付け用エッジID</summary>
    public string edgeId;

    /// <summary>
    /// 道路機能コードから日本語名を取得
    /// </summary>
    public static string GetFunctionName(int functionCode)
    {
        return functionCode switch
        {
            1 => "高速自動車国道",
            2 => "一般国道",
            3 => "都道府県道",
            4 => "市町村道",
            10 => "建築基準法第42条1項2号道路",
            11 => "建築基準法第42条1項3号道路",
            12 => "建築基準法第42条1項4号道路",
            13 => "建築基準法第42条1項5号道路",
            14 => "建築基準法第42条2項道路",
            15 => "建築基準法第43条２項道路",
            9000 => "未調査",
            9010 => "対象外",
            9020 => "不明",
            _ => "道路"
        };
    }

    /// <summary>
    /// 区間種別コードから日本語名を取得
    /// </summary>
    public static string GetSectionTypeName(int sectionType)
    {
        return sectionType switch
        {
            1 => "土工区間・通常区間",
            2 => "高架橋",
            3 => "橋梁",
            4 => "交差部",
            5 => "アンダーパス",
            6 => "トンネル",
            _ => ""
        };
    }

    /// <summary>
    /// 主要道路かどうかを判定（国道・都道府県道）
    /// </summary>
    public bool IsMajorRoad => functionCode >= 1 && functionCode <= 3;
}

/// <summary>
/// 土地利用のコンテキスト情報
/// </summary>
[Serializable]
public class LandUseContext
{
    public string name;
    public Vector3 position;
    public float distance;
    public string gmlId;

    // luse:class - 土地利用分類
    public string landUseClass;
    public string landUseClassName; // 日本語名

    // luse:usage - 用途
    public string usage;

    // uro:landUseDetailAttribute
    public float area;              // 面積(㎡)
    public float buildingCoverage;  // 建蔽率(%)
    public float floorAreaRatio;    // 容積率(%)
}

/// <summary>
/// 都市計画区域のコンテキスト情報
/// </summary>
[Serializable]
public class UrbanPlanContext
{
    public string name;
    public Vector3 position;
    public string gmlId;

    public string zoneType;         // 用途地域タイプ
    public string zoneTypeName;     // 日本語名
}
}
