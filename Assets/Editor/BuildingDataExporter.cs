using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PLATEAU.CityInfo;
using Newtonsoft.Json;
using EvacSim.Environment;

/// <summary>
/// シーン内のPLATEAU建物データを抽出してファイル出力するエディタツール
/// </summary>
public class BuildingDataExporter : EditorWindow
{
    private string outputDirectory = "Assets/ExportedData";
    private string fileNamePrefix = "building_data";
    private bool exportJSON = true;
    private bool exportCSV = true;
    private bool includeDetailedAttributes = true;
    private Vector2 scrollPosition;

    [MenuItem("Tools/PLATEAU/Export Building Data")]
    public static void ShowWindow()
    {
        GetWindow<BuildingDataExporter>("Building Data Exporter");
    }

    void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        
        EditorGUILayout.LabelField("Building Data Exporter", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("出力設定", EditorStyles.boldLabel);
        outputDirectory = EditorGUILayout.TextField("出力ディレクトリ", outputDirectory);
        fileNamePrefix = EditorGUILayout.TextField("ファイル名プレフィックス", fileNamePrefix);
        
        EditorGUILayout.Space();
        exportJSON = EditorGUILayout.Toggle("JSON形式で出力", exportJSON);
        exportCSV = EditorGUILayout.Toggle("CSV形式で出力", exportCSV);
        includeDetailedAttributes = EditorGUILayout.Toggle("詳細属性を含める", includeDetailedAttributes);
        
        EditorGUILayout.Space();
        
        if (GUILayout.Button("建物データを出力", GUILayout.Height(40)))
        {
            ExportBuildingData();
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "このツールはシーン内の全PLATEAU建物オブジェクトをスキャンし、\n" +
            "位置座標と属性情報を抽出してファイルに出力します。\n\n" +
            "出力される情報:\n" +
            "- オブジェクト名\n" +
            "- 位置座標（Bounds Center, Min, Max）\n" +
            "- 建物用途、高さ、階数、建築年など\n" +
            "- 土地利用種別、構造種別など（詳細属性ON時）",
            MessageType.Info
        );

        EditorGUILayout.EndScrollView();
    }

    private void ExportBuildingData()
    {
        // ディレクトリが存在しない場合は作成
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // シーン内の全PLATEAUCityObjectGroupを検索（ソート不要）
        PLATEAUCityObjectGroup[] cityObjects = FindObjectsByType<PLATEAUCityObjectGroup>(FindObjectsSortMode.None);
        
        if (cityObjects.Length == 0)
        {
            EditorUtility.DisplayDialog("エラー", "シーン内にPLATEAU建物オブジェクトが見つかりませんでした。", "OK");
            return;
        }

        List<BuildingData> buildingDataList = new List<BuildingData>();
        int processedCount = 0;

        foreach (var cityObj in cityObjects)
        {
            float progress = (float)processedCount / cityObjects.Length;
            EditorUtility.DisplayProgressBar("建物データを抽出中...", $"{processedCount}/{cityObjects.Length}", progress);
            
            try
            {
                BuildingData data = ExtractBuildingData(cityObj);
                if (data != null)
                {
                    buildingDataList.Add(data);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BuildingDataExporter] {cityObj.name} の処理中にエラー: {ex.Message}");
            }
            
            processedCount++;
        }

        EditorUtility.ClearProgressBar();

        // ファイル出力
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        
        if (exportJSON)
        {
            string jsonPath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{timestamp}.json");
            ExportToJSON(buildingDataList, jsonPath);
        }

        if (exportCSV)
        {
            string csvPath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{timestamp}.csv");
            ExportToCSV(buildingDataList, csvPath);
        }

        EditorUtility.DisplayDialog(
            "完了", 
            $"建物データの出力が完了しました。\n\n" +
            $"処理件数: {buildingDataList.Count}件\n" +
            $"出力先: {outputDirectory}", 
            "OK"
        );

        // 出力ディレクトリを開く
        EditorUtility.RevealInFinder(outputDirectory);
        
        Debug.Log($"[BuildingDataExporter] 完了: {buildingDataList.Count}件の建物データを出力しました。");
    }

    private BuildingData ExtractBuildingData(PLATEAUCityObjectGroup cityObj)
    {
        BuildingData data = new BuildingData
        {
            object_name = cityObj.gameObject.name,
            tag = cityObj.gameObject.tag
        };

        // Renderer.boundsから位置情報を取得
        Renderer renderer = cityObj.GetComponent<Renderer>();
        if (renderer != null)
        {
            data.bounds_center = new Vector3Data(renderer.bounds.center);
            data.bounds_min = new Vector3Data(renderer.bounds.min);
            data.bounds_max = new Vector3Data(renderer.bounds.max);
            data.bounds_size = new Vector3Data(renderer.bounds.size);
        }

        // Transform情報も取得
        data.transform_position = new Vector3Data(cityObj.transform.position);
        data.transform_local_position = new Vector3Data(cityObj.transform.localPosition);

        // PLATEAU属性情報を取得
        try
        {
            var rootCityObject = cityObj.CityObjects.rootCityObjects[0];
            data.gml_id = rootCityObject.GmlID;
            data.city_object_type = rootCityObject.CityObjectType.ToString();

            // 属性情報をJSON化して解析
            var cityObjectJsonStr = JsonConvert.SerializeObject(rootCityObject);
            var attributes = JsonConvert.DeserializeObject<RootObject>(cityObjectJsonStr).Attributes;

            ExtractBasicAttributes(attributes, data);
            
            if (includeDetailedAttributes)
            {
                ExtractDetailedAttributes(attributes, data);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[BuildingDataExporter] {cityObj.name}: 属性情報の取得に失敗 - {ex.Message}");
        }

        return data;
    }

    private void ExtractBasicAttributes(List<AttributeValue> attributes, BuildingData data)
    {
        foreach (var attr in attributes)
        {
            switch (attr.Key)
            {
                case "bldg:usage":
                    data.usage = attr.Value?.ToString() ?? "不明";
                    break;
                case "bldg:measuredheight":
                    if (float.TryParse(attr.Value?.ToString(), out float height))
                        data.measured_height = height;
                    break;
                case "bldg:storeysaboveground":
                    if (int.TryParse(attr.Value?.ToString(), out int floors))
                        data.storeys_above_ground = floors;
                    break;
                case "bldg:yearofconstruction":
                    if (int.TryParse(attr.Value?.ToString(), out int year))
                        data.year_of_construction = year;
                    break;
                case "core:creationdate":
                    data.creation_date = attr.Value?.ToString();
                    break;
            }
        }
    }

    private void ExtractDetailedAttributes(List<AttributeValue> attributes, BuildingData data)
    {
        foreach (var attr in attributes)
        {
            if (attr.Key == "uro:buildingDetailAttribute")
            {
                foreach (var detailAttr in attr.AttributeSetValue)
                {
                    if (detailAttr.Key == "uro:BuildingDetailAttribute")
                    {
                        foreach (var uroAttr in detailAttr.AttributeSetValue)
                        {
                            switch (uroAttr.Key)
                            {
                                case "uro:totalFloorArea":
                                    data.total_floor_area = uroAttr.Value?.ToString();
                                    break;
                                case "uro:buildingStructureType":
                                    data.building_structure_type = uroAttr.Value?.ToString();
                                    break;
                                case "uro:areaClassificationType":
                                    data.area_classification_type = uroAttr.Value?.ToString();
                                    break;
                                case "uro:landUseType":
                                    data.land_use_type = uroAttr.Value?.ToString();
                                    break;
                                case "uro:districtsAndZonesType":
                                    data.districts_and_zones_type = uroAttr.Value?.ToString();
                                    break;
                                case "uro:buildingFootprintArea":
                                    data.building_footprint_area = uroAttr.Value?.ToString();
                                    break;
                                case "uro:surveyYear":
                                    if (int.TryParse(uroAttr.Value?.ToString(), out int surveyYear))
                                        data.survey_year = surveyYear;
                                    break;
                                case "uro:majorUsage":
                                    data.major_usage = uroAttr.Value?.ToString();
                                    break;
                            }
                        }
                    }
                }
            }
            else if (attr.Key == "uro:buildingIDAttribute")
            {
                foreach (var idAttr in attr.AttributeSetValue)
                {
                    if (idAttr.Key == "uro:BuildingIDAttribute")
                    {
                        foreach (var uroAttr in idAttr.AttributeSetValue)
                        {
                            switch (uroAttr.Key)
                            {
                                case "uro:buildingID":
                                    data.building_id = uroAttr.Value?.ToString();
                                    break;
                                case "uro:city":
                                    data.city = uroAttr.Value?.ToString();
                                    break;
                                case "uro:prefecture":
                                    data.prefecture = uroAttr.Value?.ToString();
                                    break;
                            }
                        }
                    }
                }
            }
        }
    }

    private void ExportToJSON(List<BuildingData> buildingDataList, string filePath)
    {
        var wrapper = new BuildingDataCollection
        {
            export_datetime = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            total_count = buildingDataList.Count,
            buildings = buildingDataList
        };

        string json = JsonConvert.SerializeObject(wrapper, Formatting.Indented);
        File.WriteAllText(filePath, json, Encoding.UTF8);
        
        Debug.Log($"[BuildingDataExporter] JSON出力完了: {filePath}");
    }

    private void ExportToCSV(List<BuildingData> buildingDataList, string filePath)
    {
        StringBuilder csv = new StringBuilder();
        
        // ヘッダー行
        csv.AppendLine(
            "object_name,gml_id,city_object_type,tag," +
            "bounds_center_x,bounds_center_y,bounds_center_z," +
            "bounds_min_x,bounds_min_y,bounds_min_z," +
            "bounds_max_x,bounds_max_y,bounds_max_z," +
            "bounds_size_x,bounds_size_y,bounds_size_z," +
            "transform_position_x,transform_position_y,transform_position_z," +
            "usage,measured_height,storeys_above_ground,year_of_construction," +
            "total_floor_area,building_structure_type,area_classification_type," +
            "land_use_type,districts_and_zones_type,major_usage," +
            "building_id,city,prefecture,creation_date,survey_year"
        );

        // データ行
        foreach (var data in buildingDataList)
        {
            csv.AppendLine(
                $"{EscapeCSV(data.object_name)}," +
                $"{EscapeCSV(data.gml_id)}," +
                $"{EscapeCSV(data.city_object_type)}," +
                $"{EscapeCSV(data.tag)}," +
                $"{data.bounds_center?.x ?? 0},{data.bounds_center?.y ?? 0},{data.bounds_center?.z ?? 0}," +
                $"{data.bounds_min?.x ?? 0},{data.bounds_min?.y ?? 0},{data.bounds_min?.z ?? 0}," +
                $"{data.bounds_max?.x ?? 0},{data.bounds_max?.y ?? 0},{data.bounds_max?.z ?? 0}," +
                $"{data.bounds_size?.x ?? 0},{data.bounds_size?.y ?? 0},{data.bounds_size?.z ?? 0}," +
                $"{data.transform_position?.x ?? 0},{data.transform_position?.y ?? 0},{data.transform_position?.z ?? 0}," +
                $"{EscapeCSV(data.usage)}," +
                $"{data.measured_height}," +
                $"{data.storeys_above_ground}," +
                $"{data.year_of_construction}," +
                $"{EscapeCSV(data.total_floor_area)}," +
                $"{EscapeCSV(data.building_structure_type)}," +
                $"{EscapeCSV(data.area_classification_type)}," +
                $"{EscapeCSV(data.land_use_type)}," +
                $"{EscapeCSV(data.districts_and_zones_type)}," +
                $"{EscapeCSV(data.major_usage)}," +
                $"{EscapeCSV(data.building_id)}," +
                $"{EscapeCSV(data.city)}," +
                $"{EscapeCSV(data.prefecture)}," +
                $"{EscapeCSV(data.creation_date)}," +
                $"{data.survey_year}"
            );
        }

        File.WriteAllText(filePath, csv.ToString(), Encoding.UTF8);
        
        Debug.Log($"[BuildingDataExporter] CSV出力完了: {filePath}");
    }

    private string EscapeCSV(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}

// データ構造定義

[System.Serializable]
public class BuildingDataCollection
{
    public string export_datetime;
    public int total_count;
    public List<BuildingData> buildings;
}

[System.Serializable]
public class BuildingData
{
    // 基本情報
    public string object_name;
    public string gml_id;
    public string city_object_type;
    public string tag;

    // 位置情報（Bounds）
    public Vector3Data bounds_center;
    public Vector3Data bounds_min;
    public Vector3Data bounds_max;
    public Vector3Data bounds_size;

    // Transform情報
    public Vector3Data transform_position;
    public Vector3Data transform_local_position;

    // 建物基本属性
    public string usage = "不明";
    public float measured_height = 0;
    public int storeys_above_ground = 0;
    public int year_of_construction = 0;
    public string creation_date;

    // 詳細属性
    public string total_floor_area;
    public string building_structure_type;
    public string area_classification_type;
    public string land_use_type;
    public string districts_and_zones_type;
    public string building_footprint_area;
    public string major_usage;
    public int survey_year = 0;

    // ID情報
    public string building_id;
    public string city;
    public string prefecture;
}

[System.Serializable]
public class Vector3Data
{
    public float x;
    public float y;
    public float z;

    public Vector3Data() { }

    public Vector3Data(Vector3 vector)
    {
        x = vector.x;
        y = vector.y;
        z = vector.z;
    }
}
