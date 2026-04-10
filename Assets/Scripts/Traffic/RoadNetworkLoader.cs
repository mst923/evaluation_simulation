using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// GeoJSONファイルから道路ネットワークを読み込むクラス。
    /// 座標はPython側で事前にPLATEAU Unity座標に変換済みであることを前提とする。
    /// </summary>
    public class RoadNetworkLoader : MonoBehaviour
    {
        private const string Tag = "[RoadNetworkLoader]";

        [Header("GeoJSONファイルパス")]
        [Tooltip("Assets/からの相対パスまたは絶対パス")]
        [SerializeField] private string geoJsonPath = "Config/iwaki_road_network.geojson";

        [Header("読み込み設定")]
        [Tooltip("シーン起動時に自動読み込み")]
        [SerializeField] private bool autoLoadOnStart = true;

        /// <summary>読み込まれた道路ネットワーク</summary>
        public RoadNetwork Network { get; private set; }

        /// <summary>読み込み完了フラグ</summary>
        public bool IsLoaded { get; private set; }

        public static RoadNetworkLoader Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (autoLoadOnStart)
            {
                LoadNetwork();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// GeoJSONから道路ネットワークを読み込む
        /// </summary>
        public void LoadNetwork()
        {
            string fullPath = ResolvePath(geoJsonPath);
            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"{Tag} GeoJSONファイルが見つかりません: {fullPath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(fullPath);
                Network = ParseGeoJson(json);
                IsLoaded = true;
                Debug.Log($"{Tag} 道路ネットワーク読み込み完了: ノード={Network.NodeCount}, エッジ={Network.EdgeCount}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{Tag} GeoJSON読み込みエラー: {ex.Message}");
            }
        }

        /// <summary>
        /// GeoJSON文字列を解析してRoadNetworkを構築する
        /// </summary>
        private RoadNetwork ParseGeoJson(string json)
        {
            var network = new RoadNetwork();
            // JsonUtilityはGeoJSONの多態的構造を正しくパースできないため、
            // 常にMiniJsonベースの手動パーサーを使用する
            ParseGeoJsonManual(json, network);
            return network;
        }

        /// <summary>
        /// MiniJSONスタイルの手動パース（JsonUtilityの制約を回避）
        /// </summary>
        private void ParseGeoJsonManual(string json, RoadNetwork network)
        {
            // Unity標準のJsonUtilityは多態的なGeoJSONに非対応のため、
            // 簡易的なパーサーで処理する
            Debug.Log($"{Tag} GeoJSON手動パース開始 (jsonLength={json.Length})");
            var parsed = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (parsed == null)
            {
                Debug.LogError($"{Tag} MiniJson.Deserialize failed - returned null (jsonLength={json.Length}, first100={json.Substring(0, System.Math.Min(100, json.Length))})");
                return;
            }

            var features = parsed.ContainsKey("features") ? parsed["features"] as List<object> : null;
            if (features == null)
            {
                Debug.LogError($"{Tag} features is null. Keys in parsed: {string.Join(", ", parsed.Keys)}");
                return;
            }
            Debug.Log($"{Tag} GeoJSON features count: {features.Count}");

            foreach (var featureObj in features)
            {
                var feature = featureObj as Dictionary<string, object>;
                if (feature == null) continue;

                var properties = feature["properties"] as Dictionary<string, object>;
                if (properties == null) continue;

                string featureType = properties["type"] as string;

                if (featureType == "node")
                {
                    var node = ParseNodeFeature(properties);
                    if (node != null)
                        network.AddNode(node);
                }
                else if (featureType == "edge")
                {
                    var edge = ParseEdgeFeature(properties);
                    if (edge != null)
                        network.AddEdge(edge);
                }
            }
        }

        private RoadNode ParseNodeFeature(Dictionary<string, object> properties)
        {
            try
            {
                return new RoadNode
                {
                    id = Convert.ToInt64(properties["id"]),
                    position = new Vector3(
                        Convert.ToSingle(properties["unity_x"]),
                        Convert.ToSingle(properties["unity_y"]),
                        Convert.ToSingle(properties["unity_z"])
                    ),
                    lon = Convert.ToSingle(properties["lon"]),
                    lat = Convert.ToSingle(properties["lat"]),
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} ノードのパースに失敗: {ex.Message}");
                return null;
            }
        }

        private RoadEdge ParseEdgeFeature(Dictionary<string, object> properties)
        {
            try
            {
                var edge = new RoadEdge
                {
                    id = properties["id"] as string ?? "",
                    sourceNodeId = Convert.ToInt64(properties["source_node"]),
                    targetNodeId = Convert.ToInt64(properties["target_node"]),
                    lengthMeters = Convert.ToSingle(properties["length_meters"]),
                    lanes = Convert.ToInt32(properties["lanes"]),
                    speedLimitKmh = Convert.ToSingle(properties["speed_limit_kmh"]),
                    oneway = Convert.ToBoolean(properties["oneway"]),
                    highwayType = properties["highway_type"] as string ?? "",
                    roadName = properties["name"] as string ?? "",
                };

                // geometry_unityからポイント列を復元
                if (properties.TryGetValue("geometry_unity", out var geomObj))
                {
                    var geomList = geomObj as List<object>;
                    if (geomList != null)
                    {
                        foreach (var pointObj in geomList)
                        {
                            var point = pointObj as Dictionary<string, object>;
                            if (point != null)
                            {
                                edge.geometry.Add(new Vector3(
                                    Convert.ToSingle(point["x"]),
                                    Convert.ToSingle(point["y"]),
                                    Convert.ToSingle(point["z"])
                                ));
                            }
                        }
                    }
                }

                return edge;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} エッジのパースに失敗: {ex.Message}");
                return null;
            }
        }

        private string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path))
                return path;

            // Application.dataPath = "<ProjectRoot>/Assets"
            return Path.Combine(Application.dataPath, path);
        }

        /// <summary>
        /// JsonUtility用のダミーRoot（実際にはMiniJsonを使用）
        /// </summary>
        [Serializable]
        private class GeoJsonRoot
        {
            public string type;
            public List<GeoJsonFeature> features;
        }

        [Serializable]
        private class GeoJsonFeature
        {
            public string type;
        }
    }

    /// <summary>
    /// 軽量JSONパーサー（Unity標準JsonUtilityの代替）
    /// GeoJSONの多態的構造をDictionary/Listとしてパースする
    /// </summary>
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return ParseValue(json, 0, out _);
        }

        private static object ParseValue(string json, int index, out int nextIndex)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) { nextIndex = index; return null; }

            char c = json[index];
            if (c == '{') return ParseObject(json, index, out nextIndex);
            if (c == '[') return ParseArray(json, index, out nextIndex);
            if (c == '"') return ParseString(json, index, out nextIndex);
            if (c == 't' || c == 'f') return ParseBool(json, index, out nextIndex);
            if (c == 'n') return ParseNull(json, index, out nextIndex);
            return ParseNumber(json, index, out nextIndex);
        }

        private static Dictionary<string, object> ParseObject(string json, int index, out int nextIndex)
        {
            var dict = new Dictionary<string, object>();
            index++; // skip '{'
            SkipWhitespace(json, ref index);

            while (index < json.Length && json[index] != '}')
            {
                string key = (string)ParseString(json, index, out index);
                SkipWhitespace(json, ref index);
                index++; // skip ':'
                SkipWhitespace(json, ref index);
                object value = ParseValue(json, index, out index);
                dict[key] = value;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') index++;
                SkipWhitespace(json, ref index);
            }

            nextIndex = index + 1; // skip '}'
            return dict;
        }

        private static List<object> ParseArray(string json, int index, out int nextIndex)
        {
            var list = new List<object>();
            index++; // skip '['
            SkipWhitespace(json, ref index);

            while (index < json.Length && json[index] != ']')
            {
                object value = ParseValue(json, index, out index);
                list.Add(value);
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') index++;
                SkipWhitespace(json, ref index);
            }

            nextIndex = index + 1; // skip ']'
            return list;
        }

        private static string ParseString(string json, int index, out int nextIndex)
        {
            index++; // skip opening '"'
            var sb = new System.Text.StringBuilder();
            while (index < json.Length)
            {
                char c = json[index];
                if (c == '\\' && index + 1 < json.Length)
                {
                    char escaped = json[index + 1];
                    switch (escaped)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(escaped); break;
                    }
                    index += 2;
                }
                else if (c == '"')
                {
                    nextIndex = index + 1;
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                    index++;
                }
            }
            nextIndex = index;
            return sb.ToString();
        }

        private static object ParseNumber(string json, int index, out int nextIndex)
        {
            int start = index;
            while (index < json.Length && "0123456789+-.eE".IndexOf(json[index]) >= 0)
                index++;
            string numStr = json.Substring(start, index - start);
            nextIndex = index;

            if (numStr.Contains('.') || numStr.Contains('e') || numStr.Contains('E'))
                return double.Parse(numStr, System.Globalization.CultureInfo.InvariantCulture);

            if (long.TryParse(numStr, out long longVal))
                return longVal;
            return double.Parse(numStr, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object ParseBool(string json, int index, out int nextIndex)
        {
            if (json.Substring(index, 4) == "true") { nextIndex = index + 4; return true; }
            if (json.Substring(index, 5) == "false") { nextIndex = index + 5; return false; }
            nextIndex = index;
            return null;
        }

        private static object ParseNull(string json, int index, out int nextIndex)
        {
            if (json.Substring(index, 4) == "null") { nextIndex = index + 4; return null; }
            nextIndex = index;
            return null;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && " \t\n\r".IndexOf(json[index]) >= 0)
                index++;
        }
    }
}
