using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PLATEAU.CityInfo;
using Newtonsoft.Json;
using EvacSim.Traffic;

namespace EvacSim.Environment
{
/// <summary>
/// 環境コンテキストプロバイダー
/// 空間インデックス（Grid方式）を使って、避難者の周辺環境情報を高速に取得します
/// </summary>
public class EnvironmentalContextProvider : MonoBehaviour
{
    [Header("キャッシュ設定")]
    [Tooltip("事前構築したインデックスを使用するか（高速起動）")]
    public bool useCachedIndex = true;

    [Tooltip("事前構築した建物インデックス（Assets/Config/BuildingSpatialIndex.asset）")]
    public BuildingSpatialIndex cachedIndex;

    [Tooltip("事前構築した地理空間インデックス（Assets/Config/GeoSpatialIndex.asset）")]
    public GeoSpatialIndex geoSpatialIndex;

    [Header("空間インデックス設定")]
    [Tooltip("グリッドセルのサイズ（メートル）。検索範囲の約半分が推奨")]
    public float cellSize = 50f;

    [Header("デバッグ設定")]
    [Tooltip("シーン起動時にインデックス情報をJSON出力するか")]
    public bool exportToJsonOnStart = false;
    public string jsonExportPath = "Assets/ExportedData/spatial_index_debug.json";
    
    [Header("パフォーマンス設定")]
    [Tooltip("検索結果をキャッシュする時間（秒）。0で無効化")]
    public float cacheLifetime = 1f;
    
    // 空間分割グリッド: セルごとに建物リストを保持
    private Dictionary<Vector2Int, List<BuildingContext>> spatialGrid;

    // 地理空間データのグリッド
    private Dictionary<Vector2Int, List<RoadContext>> roadGrid;
    private Dictionary<Vector2Int, List<LandUseContext>> landUseGrid;
    private List<GeoSpatialIndex.TsunamiRiskEntry> tsunamiRiskList;
    private List<GeoSpatialIndex.LandslideRiskEntry> landslideRiskList;
    private List<GeoSpatialIndex.TerrainEntry> terrainList;

    // 検索結果のキャッシュ
    private Dictionary<int, CachedSearchResult> searchCache;

    private class CachedSearchResult
    {
        public float timestamp;
        public List<BuildingContext> buildings;
    }
    
    /// <summary>
    /// 建物のコンテキスト情報
    /// </summary>
    [Serializable]
    public class BuildingContext
    {
        public string name;
        public Vector3 position;
        public float distance; // 避難者からの距離（検索時に設定）
        
        // PLATEAU属性
        public string usage;           // 用途（住宅、商業、公共施設など）
        public string majorUsage;      // 主要用途
        public float height;           // 高さ
        public int floors;             // 階数
        public string structureType;   // 構造種別
        public string landUseType;     // 土地利用種別
        public string gmlId;           // GML ID
    }
    
    void Start()
    {
        searchCache = new Dictionary<int, CachedSearchResult>();

        // キャッシュを使用するかどうかで分岐
        if (useCachedIndex && cachedIndex != null && cachedIndex.HasData)
        {
            LoadFromCachedIndex();
        }
        else
        {
            BuildSpatialIndex();

            if (useCachedIndex && cachedIndex == null)
            {
                Debug.LogWarning("[EnvironmentalContext] キャッシュが設定されていないため、動的構築を実行しました。" +
                                 "「Tools → Environment → Bake Spatial Index」でBakeしてください。");
            }
            else if (useCachedIndex && !cachedIndex.HasData)
            {
                Debug.LogWarning("[EnvironmentalContext] キャッシュが空のため、動的構築を実行しました。" +
                                 "「Tools → Environment → Bake Spatial Index」でBakeしてください。");
            }
        }

        if (exportToJsonOnStart)
        {
            ExportToJson();
        }

        // 地理空間インデックスを読み込み
        LoadGeoSpatialIndex();
    }

    /// <summary>
    /// 地理空間インデックス（道路・土地利用・災害リスク等）を読み込む
    /// </summary>
    private void LoadGeoSpatialIndex()
    {
        if (geoSpatialIndex == null || !geoSpatialIndex.HasData)
        {
            Debug.LogWarning("[EnvironmentalContext] 地理空間インデックスが未設定またはデータがありません。" +
                             "「Tools → Environment → Bake Geo Spatial Index」でBakeしてください。");
            return;
        }

        var startTime = Time.realtimeSinceStartup;

        // 道路グリッドを構築
        roadGrid = new Dictionary<Vector2Int, List<RoadContext>>();
        foreach (var entry in geoSpatialIndex.roads)
        {
            Vector2Int gridKey = WorldToGridKey(entry.position);
            if (!roadGrid.ContainsKey(gridKey))
                roadGrid[gridKey] = new List<RoadContext>();

            roadGrid[gridKey].Add(new RoadContext
            {
                name = entry.name,
                position = entry.position,
                distance = 0,
                gmlId = entry.gmlId,
                functionCode = entry.functionCode,
                functionName = entry.functionName,
                sectionType = entry.sectionType,
                sectionTypeName = entry.sectionTypeName
            });
        }

        // 土地利用グリッドを構築
        landUseGrid = new Dictionary<Vector2Int, List<LandUseContext>>();
        foreach (var entry in geoSpatialIndex.landUses)
        {
            Vector2Int gridKey = WorldToGridKey(entry.position);
            if (!landUseGrid.ContainsKey(gridKey))
                landUseGrid[gridKey] = new List<LandUseContext>();

            landUseGrid[gridKey].Add(new LandUseContext
            {
                name = entry.name,
                position = entry.position,
                distance = 0,
                gmlId = entry.gmlId,
                landUseClass = entry.landUseClass,
                landUseClassName = entry.landUseClassName,
                usage = entry.usage,
                area = entry.area
            });
        }

        // 災害リスクデータはリストとして保持（Bounds判定用）
        tsunamiRiskList = geoSpatialIndex.tsunamiRisks;
        landslideRiskList = geoSpatialIndex.landslideRisks;
        terrainList = geoSpatialIndex.terrains;

        var elapsed = Time.realtimeSinceStartup - startTime;
        Debug.Log($"[EnvironmentalContext] 地理空間インデックス読み込み完了: " +
                  $"道路{geoSpatialIndex.RoadCount}件, " +
                  $"土地利用{geoSpatialIndex.LandUseCount}件, " +
                  $"津波リスク{geoSpatialIndex.TsunamiRiskCount}件, " +
                  $"土砂災害リスク{geoSpatialIndex.LandslideRiskCount}件, " +
                  $"地形{geoSpatialIndex.TerrainCount}件 " +
                  $"（処理時間: {elapsed:F3}秒）");
    }

    /// <summary>
    /// キャッシュから空間インデックスを読み込む（高速）
    /// </summary>
    private void LoadFromCachedIndex()
    {
        var startTime = Time.realtimeSinceStartup;
        spatialGrid = new Dictionary<Vector2Int, List<BuildingContext>>();

        // キャッシュのcellSizeを使用
        cellSize = cachedIndex.cellSize;

        foreach (var entry in cachedIndex.buildings)
        {
            Vector2Int gridKey = WorldToGridKey(entry.position);

            if (!spatialGrid.ContainsKey(gridKey))
                spatialGrid[gridKey] = new List<BuildingContext>();

            var context = new BuildingContext
            {
                name = entry.name,
                position = entry.position,
                distance = 0,
                usage = entry.usage,
                majorUsage = entry.majorUsage,
                height = entry.height,
                floors = entry.floors,
                structureType = entry.structureType,
                landUseType = entry.landUseType,
                gmlId = entry.gmlId
            };

            spatialGrid[gridKey].Add(context);
        }

        var elapsed = Time.realtimeSinceStartup - startTime;
        Debug.Log($"[EnvironmentalContext] キャッシュから読み込み完了: {cachedIndex.buildings.Count}件（処理時間: {elapsed:F3}秒）");
    }
    
    /// <summary>
    /// 空間インデックスを構築（シーン起動時に実行）
    /// </summary>
    private void BuildSpatialIndex()
    {
        var startTime = Time.realtimeSinceStartup;
        spatialGrid = new Dictionary<Vector2Int, List<BuildingContext>>();
        
        // 全PLATEAUオブジェクトを検索（ソート不要なのでFindObjectsSortMode.Noneを使用）
        var cityObjects = FindObjectsByType<PLATEAUCityObjectGroup>(FindObjectsSortMode.None);
        int indexedCount = 0;
        int skippedCount = 0;
        
        foreach (var cityObj in cityObjects)
        {
            // 建物のみを対象（道路や地形を除外）
            if (!cityObj.name.StartsWith("bldg_"))
            {
                skippedCount++;
                continue;
            }
            
            var renderer = cityObj.GetComponent<Renderer>();
            if (renderer == null)
            {
                skippedCount++;
                continue;
            }
            
            // PLATEAUオブジェクトは全て同じTransform.positionを持つため、Renderer.bounds.centerを使用
            Vector3 center = renderer.bounds.center;
            Vector2Int gridKey = WorldToGridKey(center);
            
            // セルが存在しなければ作成
            if (!spatialGrid.ContainsKey(gridKey))
                spatialGrid[gridKey] = new List<BuildingContext>();
            
            // 建物情報を抽出してセルに追加
            var context = ExtractBuildingContext(cityObj, center);
            spatialGrid[gridKey].Add(context);
            indexedCount++;
        }
        
        var elapsed = Time.realtimeSinceStartup - startTime;
        
        Debug.Log($"[EnvironmentalContext] 空間インデックス構築完了: {indexedCount}件の建物をインデックス化（処理時間: {elapsed:F3}秒）");
    }
    
    /// <summary>
    /// 指定位置から一定範囲内の建物を取得（キャッシュ対応）
    /// </summary>
    public List<BuildingContext> GetNearbyBuildings(Vector3 position, float radius)
    {
        // キャッシュキーを生成
        int cacheKey = GetCacheKey(position, radius);
        
        // キャッシュをチェック
        if (cacheLifetime > 0 && searchCache.TryGetValue(cacheKey, out var cached))
        {
            if (Time.time - cached.timestamp < cacheLifetime)
            {
                return cached.buildings;
            }
        }
        
        // 新規検索
        var result = SearchNearbyBuildings(position, radius);
        
        // キャッシュに保存
        if (cacheLifetime > 0)
        {
            searchCache[cacheKey] = new CachedSearchResult
            {
                timestamp = Time.time,
                buildings = result
            };
        }
        
        return result;
    }
    
    /// <summary>
    /// 空間検索の本体処理
    /// </summary>
    private List<BuildingContext> SearchNearbyBuildings(Vector3 position, float radius)
    {
        if (spatialGrid == null || spatialGrid.Count == 0)
        {
            Debug.LogWarning("[EnvironmentalContext] 空間インデックスが未構築です");
            return new List<BuildingContext>();
        }
        
        var nearbyBuildings = new List<BuildingContext>();
        
        // 避難者のグリッドセルを計算
        Vector2Int centerCell = WorldToGridKey(position);
        
        // 検索範囲（半径）からセル数を計算
        int cellRadius = Mathf.CeilToInt(radius / cellSize);
        
        // 周辺セルをループ
        for (int x = -cellRadius; x <= cellRadius; x++)
        {
            for (int z = -cellRadius; z <= cellRadius; z++)
            {
                Vector2Int targetCell = centerCell + new Vector2Int(x, z);
                
                // セルが存在するかチェック
                if (spatialGrid.TryGetValue(targetCell, out var buildingsInCell))
                {
                    // このセル内の建物をチェック
                    foreach (var building in buildingsInCell)
                    {
                        // 正確な距離を計算（Y座標は考慮しない）
                        float distance = Vector3.Distance(
                            new Vector3(position.x, 0, position.z),
                            new Vector3(building.position.x, 0, building.position.z)
                        );
                        
                        // 半径内なら追加
                        if (distance <= radius)
                        {
                            // 距離情報を設定（元のオブジェクトは変更しない）
                            var buildingCopy = new BuildingContext
                            {
                                name = building.name,
                                position = building.position,
                                distance = distance,
                                usage = building.usage,
                                majorUsage = building.majorUsage,
                                height = building.height,
                                floors = building.floors,
                                structureType = building.structureType,
                                landUseType = building.landUseType,
                                gmlId = building.gmlId
                            };
                            
                            nearbyBuildings.Add(buildingCopy);
                        }
                    }
                }
            }
        }
        
        // 距離でソート
        nearbyBuildings.Sort((a, b) => a.distance.CompareTo(b.distance));
        
        return nearbyBuildings;
    }
    
    /// <summary>
    /// PLATEAU建物から属性情報を抽出
    /// </summary>
    private BuildingContext ExtractBuildingContext(PLATEAUCityObjectGroup cityObj, Vector3 center)
    {
        var context = new BuildingContext
        {
            name = cityObj.gameObject.name,
            position = center,
            distance = 0,
            usage = "不明",
            majorUsage = "",
            height = 0,
            floors = 0,
            structureType = "",
            landUseType = "",
            gmlId = ""
        };
        
        try
        {
            if (cityObj.CityObjects == null || 
                cityObj.CityObjects.rootCityObjects == null || 
                cityObj.CityObjects.rootCityObjects.Count == 0)
            {
                return context;
            }
            
            var rootObject = cityObj.CityObjects.rootCityObjects[0];
            context.gmlId = rootObject.GmlID;
            
            var jsonStr = JsonConvert.SerializeObject(rootObject);
            var rootData = JsonConvert.DeserializeObject<RootObject>(jsonStr);
            
            if (rootData?.Attributes == null)
            {
                return context;
            }
            
            // 基本属性を抽出
            foreach (var attr in rootData.Attributes)
            {
                if (attr.Key == "bldg:usage")
                {
                    context.usage = attr.Value?.ToString() ?? "不明";
                }
                else if (attr.Key == "bldg:measuredheight")
                {
                    float.TryParse(attr.Value?.ToString(), out context.height);
                }
                else if (attr.Key == "bldg:storeysaboveground")
                {
                    int.TryParse(attr.Value?.ToString(), out context.floors);
                    // 9999は「不明」を意味するため、1階建てとして扱う
                    if (context.floors == 9999)
                    {
                        context.floors = 1;
                    }
                }
                else if (attr.Key == "uro:buildingDetailAttribute")
                {
                    // 詳細属性を抽出（3層構造に対応）
                    if (attr.AttributeSetValue != null)
                    {
                        foreach (var detailAttr in attr.AttributeSetValue)
                        {
                            if (detailAttr.Key == "uro:BuildingDetailAttribute" && 
                                detailAttr.AttributeSetValue != null)
                            {
                                foreach (var uroAttr in detailAttr.AttributeSetValue)
                                {
                                    if (uroAttr.Key == "uro:buildingStructureType")
                                        context.structureType = uroAttr.Value?.ToString() ?? "";
                                    else if (uroAttr.Key == "uro:landUseType")
                                        context.landUseType = uroAttr.Value?.ToString() ?? "";
                                    else if (uroAttr.Key == "uro:majorUsage")
                                        context.majorUsage = uroAttr.Value?.ToString() ?? "";
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EnvironmentalContext] {cityObj.name} の属性抽出に失敗: {ex.Message}");
        }
        
        return context;
    }
    
    /// <summary>
    /// ワールド座標をグリッドセル座標に変換
    /// </summary>
    private Vector2Int WorldToGridKey(Vector3 position)
    {
        return new Vector2Int(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.z / cellSize)
        );
    }
    
    /// <summary>
    /// キャッシュキーを生成
    /// </summary>
    private int GetCacheKey(Vector3 position, float radius)
    {
        // 位置を粗くグリッド化してキーを生成（10mごと）
        int gridX = Mathf.FloorToInt(position.x / 10f);
        int gridZ = Mathf.FloorToInt(position.z / 10f);
        int radiusInt = Mathf.FloorToInt(radius);
        
        return HashCode.Combine(gridX, gridZ, radiusInt);
    }
    
    /// <summary>
    /// デバッグ用：空間インデックスをJSONで出力
    /// </summary>
    private void ExportToJson()
    {
        try
        {
            var data = new
            {
                metadata = new
                {
                    total_buildings = spatialGrid.Sum(g => g.Value.Count),
                    cell_size = cellSize,
                    grid_cells = spatialGrid.Count,
                    timestamp = DateTime.Now.ToString("o")
                },
                buildings = spatialGrid.SelectMany(g => g.Value).ToList()
            };
            
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            System.IO.File.WriteAllText(jsonExportPath, json);
            
            Debug.Log($"[EnvironmentalContext] デバッグ用JSONを出力: {jsonExportPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[EnvironmentalContext] JSON出力に失敗: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 全建物リストを取得（スポーン位置管理用）
    /// </summary>
    public List<BuildingContext> GetAllBuildings()
    {
        if (spatialGrid == null || spatialGrid.Count == 0)
            return new List<BuildingContext>();
        
        var allBuildings = new List<BuildingContext>();
        foreach (var cell in spatialGrid.Values)
        {
            allBuildings.AddRange(cell);
        }
        
        return allBuildings;
    }
    
    /// <summary>
    /// 統計情報を取得（デバッグ用）
    /// </summary>
    public string GetStatistics()
    {
        if (spatialGrid == null)
            return "空間インデックス未構築";

        int totalBuildings = spatialGrid.Sum(g => g.Value.Count);
        float avgBuildingsPerCell = spatialGrid.Count > 0 ? (float)totalBuildings / spatialGrid.Count : 0;
        int maxBuildingsInCell = spatialGrid.Count > 0 ? spatialGrid.Max(g => g.Value.Count) : 0;

        return $"建物総数: {totalBuildings}, " +
               $"セル数: {spatialGrid.Count}, " +
               $"平均: {avgBuildingsPerCell:F1}件/セル, " +
               $"最大: {maxBuildingsInCell}件/セル";
    }

    #region 地理空間データ検索メソッド

    /// <summary>
    /// 指定位置での津波リスク情報を取得
    /// </summary>
    /// <param name="position">検索位置</param>
    /// <returns>津波リスク情報（該当なしの場合はnull）</returns>
    public TsunamiRiskContext GetTsunamiRiskAtPosition(Vector3 position)
    {
        if (tsunamiRiskList == null || tsunamiRiskList.Count == 0)
            return null;

        // 位置が含まれる津波リスク区域を検索（Boundsで判定）
        foreach (var entry in tsunamiRiskList)
        {
            if (entry.bounds.Contains(position))
            {
                return new TsunamiRiskContext
                {
                    name = entry.name,
                    position = entry.position,
                    gmlId = entry.gmlId,
                    description = entry.description,
                    rank = entry.rank,
                    estimatedDepth = entry.estimatedDepth
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 指定位置での土砂災害リスク情報を取得
    /// </summary>
    /// <param name="position">検索位置</param>
    /// <returns>土砂災害リスク情報（該当なしの場合はnull）</returns>
    public LandslideRiskContext GetLandslideRiskAtPosition(Vector3 position)
    {
        if (landslideRiskList == null || landslideRiskList.Count == 0)
            return null;

        // 位置が含まれる土砂災害リスク区域を検索（Boundsで判定）
        // 特別警戒区域を優先して返す
        LandslideRiskContext foundContext = null;

        foreach (var entry in landslideRiskList)
        {
            if (entry.bounds.Contains(position))
            {
                var context = new LandslideRiskContext
                {
                    name = entry.name,
                    position = entry.position,
                    gmlId = entry.gmlId,
                    areaType = entry.areaType,
                    areaTypeName = entry.areaTypeName,
                    descriptionCode = entry.descriptionCode,
                    descriptionName = entry.descriptionName,
                    isSpecialZone = entry.isSpecialZone
                };

                // 特別警戒区域なら即座に返す
                if (entry.isSpecialZone)
                    return context;

                // そうでなければ記録しておく
                if (foundContext == null)
                    foundContext = context;
            }
        }

        return foundContext;
    }

    /// <summary>
    /// 指定位置での標高を取得
    /// </summary>
    /// <param name="position">検索位置</param>
    /// <returns>標高（メートル）。取得できない場合は-1</returns>
    public float GetCurrentElevation(Vector3 position)
    {
        if (terrainList == null || terrainList.Count == 0)
            return -1f;

        // 位置が含まれる地形データを検索
        foreach (var entry in terrainList)
        {
            if (entry.bounds.Contains(position))
            {
                // Boundsの中心Y座標を概算標高として使用
                // より正確には、DEMメッシュへのレイキャストが必要
                return entry.minElevation + (entry.maxElevation - entry.minElevation) * 0.5f;
            }
        }

        // 見つからない場合はpositionのY座標を返す
        return position.y;
    }

    /// <summary>
    /// 指定位置から一定範囲内の道路を取得
    /// </summary>
    /// <param name="position">検索位置</param>
    /// <param name="radius">検索半径（メートル）</param>
    /// <returns>道路リスト（距離順）</returns>
    public List<RoadContext> GetNearbyRoads(Vector3 position, float radius)
    {
        if (roadGrid == null || roadGrid.Count == 0)
            return new List<RoadContext>();

        var nearbyRoads = new List<RoadContext>();
        Vector2Int centerCell = WorldToGridKey(position);
        int cellRadius = Mathf.CeilToInt(radius / cellSize);

        for (int x = -cellRadius; x <= cellRadius; x++)
        {
            for (int z = -cellRadius; z <= cellRadius; z++)
            {
                Vector2Int targetCell = centerCell + new Vector2Int(x, z);

                if (roadGrid.TryGetValue(targetCell, out var roadsInCell))
                {
                    foreach (var road in roadsInCell)
                    {
                        float distance = Vector3.Distance(
                            new Vector3(position.x, 0, position.z),
                            new Vector3(road.position.x, 0, road.position.z)
                        );

                        if (distance <= radius)
                        {
                            nearbyRoads.Add(new RoadContext
                            {
                                name = road.name,
                                position = road.position,
                                distance = distance,
                                gmlId = road.gmlId,
                                functionCode = road.functionCode,
                                functionName = road.functionName,
                                sectionType = road.sectionType,
                                sectionTypeName = road.sectionTypeName
                            });
                        }
                    }
                }
            }
        }

        nearbyRoads.Sort((a, b) => a.distance.CompareTo(b.distance));
        return nearbyRoads;
    }

    /// <summary>
    /// 最寄りの主要道路を取得
    /// </summary>
    /// <param name="position">検索位置</param>
    /// <param name="radius">検索半径（メートル）</param>
    /// <returns>最寄りの主要道路（見つからない場合はnull）</returns>
    public RoadContext GetNearestMajorRoad(Vector3 position, float radius = 200f)
    {
        var roads = GetNearbyRoads(position, radius);
        return roads.FirstOrDefault(r => r.IsMajorRoad);
    }

    /// <summary>
    /// 指定位置での土地利用情報を取得
    /// </summary>
    /// <param name="position">検索位置</param>
    /// <returns>土地利用情報（見つからない場合はnull）</returns>
    public LandUseContext GetLandUseAtPosition(Vector3 position)
    {
        if (landUseGrid == null || landUseGrid.Count == 0)
            return null;

        Vector2Int gridKey = WorldToGridKey(position);

        // 現在のセルと隣接セルを検索
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                Vector2Int targetCell = gridKey + new Vector2Int(x, z);

                if (landUseGrid.TryGetValue(targetCell, out var landUsesInCell))
                {
                    // 最も近い土地利用を返す
                    LandUseContext closest = null;
                    float closestDistance = float.MaxValue;

                    foreach (var landUse in landUsesInCell)
                    {
                        float distance = Vector3.Distance(
                            new Vector3(position.x, 0, position.z),
                            new Vector3(landUse.position.x, 0, landUse.position.z)
                        );

                        if (distance < closestDistance)
                        {
                            closestDistance = distance;
                            closest = new LandUseContext
                            {
                                name = landUse.name,
                                position = landUse.position,
                                distance = distance,
                                gmlId = landUse.gmlId,
                                landUseClass = landUse.landUseClass,
                                landUseClassName = landUse.landUseClassName,
                                usage = landUse.usage,
                                area = landUse.area
                            };
                        }
                    }

                    if (closest != null && closestDistance < 100f) // 100m以内なら返す
                        return closest;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 指定位置周辺の交通状況を取得する。
    /// TrafficStateProviderに委譲する。
    /// </summary>
    /// <param name="position">検索中心位置</param>
    /// <param name="radius">検索半径（メートル）</param>
    /// <returns>周辺道路の交通状況リスト</returns>
    public List<TrafficCondition> GetTrafficConditions(Vector3 position, float radius = 200f)
    {
        var provider = TrafficStateProvider.Instance;
        if (provider != null)
        {
            return provider.GetTrafficConditions(position, radius);
        }
        return new List<TrafficCondition>();
    }

    /// <summary>
    /// 指定位置周辺の全体的な渋滞度を取得する（0-1）
    /// </summary>
    public float GetOverallCongestion(Vector3 position, float radius = 300f)
    {
        var provider = TrafficStateProvider.Instance;
        if (provider != null)
        {
            return provider.GetOverallCongestion(position, radius);
        }
        return 0f;
    }

    /// <summary>
    /// 渋滞情報の自然言語サマリーを取得する
    /// </summary>
    public string GetTrafficSummary(Vector3 position, float radius = 300f)
    {
        var provider = TrafficStateProvider.Instance;
        if (provider != null)
        {
            return provider.GetTrafficSummary(position, radius);
        }
        return "交通情報なし";
    }

    /// <summary>
    /// 地理空間データの統計情報を取得
    /// </summary>
    public string GetGeoSpatialStatistics()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("道路: ");
        sb.Append(roadGrid?.Sum(g => g.Value.Count) ?? 0);
        sb.Append("件, 土地利用: ");
        sb.Append(landUseGrid?.Sum(g => g.Value.Count) ?? 0);
        sb.Append("件, 津波リスク: ");
        sb.Append(tsunamiRiskList?.Count ?? 0);
        sb.Append("件, 土砂災害リスク: ");
        sb.Append(landslideRiskList?.Count ?? 0);
        sb.Append("件, 地形: ");
        sb.Append(terrainList?.Count ?? 0);
        sb.Append("件");
        return sb.ToString();
    }

    #endregion
}
}
