using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace EvacSim.Disaster
{
/// <summary>
/// 災害イベントの種類
/// </summary>
public enum DisasterEventType
{
    // 地震関連
    MainQuake,          // 本震
    Aftershock,         // 余震

    // 警報関連
    TsunamiWarning,     // 津波警報発表
    EvacuationOrder,    // 避難指示
    WarningUpdate,      // 警報更新（予想高さ変更など）

    // 環境変化
    PowerOutage,        // 停電発生
    PowerRestore,       // 停電復旧
    RadioMalfunction,   // 防災無線故障
    RadioRestore,       // 防災無線復旧

    // 津波関連
    Backwash,           // 引き波
    TsunamiArrival,     // 津波到達
    TsunamiPeak,        // 津波最大

    // 人的情報
    FireTruckPatrol,    // 消防団巡回開始
    SirenStart,         // サイレン鳴動
    SirenEnd,           // サイレン停止

    // フェーズ変更
    PhaseChange         // 災害フェーズ変更
}

/// <summary>
/// 災害フェーズ
/// </summary>
public enum DisasterPhase
{
    BeforeDisaster,     // 発災前
    Shaking,            // 揺れ継続中
    InfoGap,            // 情報空白期
    WarningIssued,      // 警報発表後
    PreArrival,         // 津波到達前
    FirstWave,          // 第1波
    Interval,           // 波間
    MainWave,           // 本波（最大）
    Aftermath           // 収束期
}

/// <summary>
/// 単一の災害イベント定義
/// </summary>
[Serializable]
public class DisasterEvent
{
    public float timeSeconds;           // 発災からの経過時間（秒）
    public DisasterEventType eventType; // イベント種別
    public DisasterPhase phase;         // この時点での災害フェーズ

    // 媒体別の情報
    public string broadcastMessage;     // 行政無線メッセージ
    public string jAlertMessage;        // Jアラートメッセージ

    // 環境パラメータ
    public int seismicIntensity;        // 震度（余震時に変化）
    public float tsunamiHeightEstimate; // 津波予想高さ（m）
    public bool isPowerOn;              // 停電状態
    public bool isRadioWorking;         // 防災無線動作状態

    // 聴覚イベント
    public float groundRumbleIntensity; // 地鳴りの強度（0-1）
    public bool sirenActive;            // サイレン鳴動中

    /// <summary>
    /// イベントが発火済みかどうか
    /// </summary>
    [NonSerialized]
    public bool hasFired = false;
}

/// <summary>
/// 災害シナリオCSVを読み込み、イベントリストを管理するクラス
/// </summary>
public class DisasterTimeline
{
    public string scenarioName;
    public List<DisasterEvent> events = new List<DisasterEvent>();

    /// <summary>
    /// CSVファイルからシナリオを読み込む
    /// </summary>
    /// <param name="csvPath">CSVファイルのパス（Resourcesフォルダからの相対パス、または絶対パス）</param>
    /// <returns>読み込んだDisasterTimeline</returns>
    public static DisasterTimeline LoadFromCSV(string csvPath)
    {
        var timeline = new DisasterTimeline();
        timeline.scenarioName = Path.GetFileNameWithoutExtension(csvPath);

        string fullPath = csvPath;

        // Resourcesフォルダ内のTextAssetとして読み込みを試みる
        TextAsset csvAsset = Resources.Load<TextAsset>(csvPath);
        string csvContent;

        if (csvAsset != null)
        {
            csvContent = csvAsset.text;
        }
        else
        {
            // 直接ファイルパスとして読み込む
            if (!File.Exists(fullPath))
            {
                // Assets/Config/ からの相対パスを試す
                fullPath = Path.Combine(Application.dataPath, "Config", csvPath);
            }

            if (!File.Exists(fullPath))
            {
                Debug.LogError($"[DisasterTimeline] CSVファイルが見つかりません: {csvPath}");
                return timeline;
            }

            csvContent = File.ReadAllText(fullPath);
        }

        var lines = csvContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 2)
        {
            Debug.LogWarning("[DisasterTimeline] CSVにデータがありません");
            return timeline;
        }

        // ヘッダー行をスキップしてパース
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var evt = ParseCSVLine(line);
            if (evt != null)
            {
                timeline.events.Add(evt);
            }
        }

        // 時刻順にソート
        timeline.events.Sort((a, b) => a.timeSeconds.CompareTo(b.timeSeconds));

        Debug.Log($"[DisasterTimeline] シナリオ '{timeline.scenarioName}' を読み込みました。イベント数: {timeline.events.Count}");

        return timeline;
    }

    /// <summary>
    /// CSV行をパースしてDisasterEventを生成
    /// </summary>
    private static DisasterEvent ParseCSVLine(string line)
    {
        // CSVの列: time_sec,event_type,phase,broadcast_message,j_alert_message,seismic_intensity,tsunami_height,power_on,radio_working,rumble_intensity,siren_active
        var parts = SplitCSVLine(line);

        if (parts.Length < 11)
        {
            Debug.LogWarning($"[DisasterTimeline] CSV行の列数が不足しています: {line}");
            return null;
        }

        try
        {
            var evt = new DisasterEvent
            {
                timeSeconds = ParseFloat(parts[0], 0f),
                eventType = ParseEnum<DisasterEventType>(parts[1], DisasterEventType.PhaseChange),
                phase = ParseEnum<DisasterPhase>(parts[2], DisasterPhase.BeforeDisaster),
                broadcastMessage = parts[3].Trim('"'),
                jAlertMessage = parts[4].Trim('"'),
                seismicIntensity = ParseInt(parts[5], 0),
                tsunamiHeightEstimate = ParseFloat(parts[6], 0f),
                isPowerOn = ParseBool(parts[7], true),
                isRadioWorking = ParseBool(parts[8], true),
                groundRumbleIntensity = ParseFloat(parts[9], 0f),
                sirenActive = ParseBool(parts[10], false)
            };

            return evt;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DisasterTimeline] CSV行のパースに失敗: {line}\n{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// CSV行をカンマで分割（引用符内のカンマを考慮）
    /// </summary>
    private static string[] SplitCSVLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());

        return result.ToArray();
    }

    private static float ParseFloat(string value, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return float.TryParse(value, out float result) ? result : defaultValue;
    }

    private static int ParseInt(string value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return int.TryParse(value, out int result) ? result : defaultValue;
    }

    private static bool ParseBool(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        value = value.ToLower().Trim();
        if (value == "true" || value == "1" || value == "yes") return true;
        if (value == "false" || value == "0" || value == "no") return false;
        return defaultValue;
    }

    private static T ParseEnum<T>(string value, T defaultValue) where T : struct
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return Enum.TryParse<T>(value, true, out T result) ? result : defaultValue;
    }

    /// <summary>
    /// 全イベントの発火状態をリセット
    /// </summary>
    public void ResetAllEvents()
    {
        foreach (var evt in events)
        {
            evt.hasFired = false;
        }
    }
}
}
