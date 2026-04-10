using System;
using System.Collections.Generic;
using UnityEngine;
using EvacSim.Core;

namespace EvacSim.Disaster
{
/// <summary>
/// 災害シナリオの時系列イベントを管理・発火するコンポーネント
/// CSVから読み込んだシナリオに基づき、時刻に応じてイベントを発火し、
/// 各媒体マネージャー（AlertManager、AudioEventManager等）に通知する
/// </summary>
public class DisasterEventManager : MonoBehaviour
{
    [Header("シナリオ設定")]
    [Tooltip("シナリオCSVファイルのパス（Assets/Config/ からの相対パス）")]
    public string scenarioCSVPath = "scenario.csv";

    [Tooltip("シナリオを自動読み込みするか")]
    public bool autoLoadScenario = true;

    [Header("現在の災害状態（読み取り専用）")]
    [SerializeField] private DisasterPhase _currentPhase = DisasterPhase.BeforeDisaster;
    [SerializeField] private int _currentSeismicIntensity = 0;
    [SerializeField] private float _currentTsunamiHeight = 0f;
    [SerializeField] private bool _isPowerOn = true;
    [SerializeField] private bool _isRadioWorking = true;
    [SerializeField] private float _groundRumbleIntensity = 0f;
    [SerializeField] private bool _isSirenActive = false;

    // プロパティ
    public DisasterPhase CurrentPhase => _currentPhase;
    public int CurrentSeismicIntensity => _currentSeismicIntensity;
    public float CurrentTsunamiHeight => _currentTsunamiHeight;
    public bool IsPowerOn => _isPowerOn;
    public bool IsRadioWorking => _isRadioWorking;
    public float GroundRumbleIntensity => _groundRumbleIntensity;
    public bool IsSirenActive => _isSirenActive;

    // イベント
    public event Action<DisasterEvent> OnDisasterEvent;
    public event Action<DisasterPhase> OnPhaseChange;

    // 内部状態
    private DisasterTimeline _timeline;
    private int _nextEventIndex = 0;
    private EnvManager _envManager;

    // 各媒体マネージャーへの参照
    private AlertManager _alertManager;
    private AudioEventManager _audioEventManager;
    private FireTruckAgent[] _fireTruckAgents;

    void Start()
    {
        _envManager = FindFirstObjectByType<EnvManager>();

        if (_envManager == null)
        {
            Debug.LogError("[DisasterEventManager] EnvManagerが見つかりません");
            return;
        }

        // 各媒体マネージャーを取得
        _alertManager = FindFirstObjectByType<AlertManager>();
        _audioEventManager = FindFirstObjectByType<AudioEventManager>();
        _fireTruckAgents = FindObjectsByType<FireTruckAgent>(FindObjectsSortMode.None);

        // シナリオを自動読み込み
        if (autoLoadScenario && !string.IsNullOrEmpty(scenarioCSVPath))
        {
            LoadScenario(scenarioCSVPath);
        }

        // エピソード開始イベントに登録
        _envManager.OnStartEpisode += OnEpisodeReset;
    }

    void OnDestroy()
    {
        if (_envManager != null)
        {
            _envManager.OnStartEpisode -= OnEpisodeReset;
        }
    }

    void Update()
    {
        if (_timeline == null || _envManager == null) return;

        float currentTime = _envManager.CurrentTimeSec;

        // 発火すべきイベントをチェック
        while (_nextEventIndex < _timeline.events.Count)
        {
            var evt = _timeline.events[_nextEventIndex];
            if (evt.timeSeconds <= currentTime && !evt.hasFired)
            {
                FireEvent(evt);
                evt.hasFired = true;
                _nextEventIndex++;
            }
            else if (evt.timeSeconds > currentTime)
            {
                break;
            }
            else
            {
                _nextEventIndex++;
            }
        }
    }

    /// <summary>
    /// シナリオCSVを読み込む
    /// </summary>
    public void LoadScenario(string csvPath)
    {
        _timeline = DisasterTimeline.LoadFromCSV(csvPath);

        if (_timeline != null && _timeline.events.Count > 0)
        {
            // 初期状態を設定（最初のイベントの環境状態を適用）
            ApplyEnvironmentState(_timeline.events[0]);
            Debug.Log($"[DisasterEventManager] シナリオを読み込みました: {_timeline.scenarioName}, イベント数: {_timeline.events.Count}");
        }
        else
        {
            Debug.LogWarning("[DisasterEventManager] シナリオの読み込みに失敗したか、イベントがありません");
        }
    }

    /// <summary>
    /// イベントを発火する
    /// </summary>
    private void FireEvent(DisasterEvent evt)
    {
        Debug.Log($"[DisasterEvent] {evt.timeSeconds}秒: {evt.eventType} - Phase: {evt.phase}");

        // 環境状態を更新
        ApplyEnvironmentState(evt);

        // フェーズ変更を通知
        if (_currentPhase != evt.phase)
        {
            _currentPhase = evt.phase;
            OnPhaseChange?.Invoke(evt.phase);
        }

        // 各媒体に通知
        NotifyMediaManagers(evt);

        // イベント発火通知
        OnDisasterEvent?.Invoke(evt);
    }

    /// <summary>
    /// 環境状態を更新
    /// </summary>
    private void ApplyEnvironmentState(DisasterEvent evt)
    {
        _currentPhase = evt.phase;
        _currentSeismicIntensity = evt.seismicIntensity;
        _currentTsunamiHeight = evt.tsunamiHeightEstimate;
        _isPowerOn = evt.isPowerOn;
        _isRadioWorking = evt.isRadioWorking;
        _groundRumbleIntensity = evt.groundRumbleIntensity;
        _isSirenActive = evt.sirenActive;
    }

    /// <summary>
    /// 各媒体マネージャーにイベントを通知
    /// </summary>
    private void NotifyMediaManagers(DisasterEvent evt)
    {
        // 行政無線への通知（故障状態を考慮）
        if (!string.IsNullOrEmpty(evt.broadcastMessage) && _isRadioWorking)
        {
            if (_alertManager != null)
            {
                _alertManager.BroadcastEmergencyMessage(evt.broadcastMessage);
            }
        }

        // Jアラートへの通知（停電でも届く）
        if (!string.IsNullOrEmpty(evt.jAlertMessage))
        {
            if (_alertManager != null)
            {
                _alertManager.SendJAlertFromEvent(evt.jAlertMessage);
            }
        }

        // 聴覚イベント
        if (_audioEventManager != null)
        {
            _audioEventManager.UpdateAudioState(evt.groundRumbleIntensity, evt.sirenActive);
        }

        // 消防団巡回開始
        if (evt.eventType == DisasterEventType.FireTruckPatrol)
        {
            foreach (var fireTruck in _fireTruckAgents)
            {
                if (fireTruck != null)
                {
                    fireTruck.StartPatrol();
                }
            }
        }

        // サイレン開始/停止
        if (evt.eventType == DisasterEventType.SirenStart)
        {
            if (_audioEventManager != null)
            {
                _audioEventManager.SetSirenActive(true);
            }
        }
        else if (evt.eventType == DisasterEventType.SirenEnd)
        {
            if (_audioEventManager != null)
            {
                _audioEventManager.SetSirenActive(false);
            }
        }
    }

    /// <summary>
    /// エピソードリセット時の処理
    /// </summary>
    public void OnEpisodeReset()
    {
        _nextEventIndex = 0;

        // タイムラインのイベント発火状態をリセット
        if (_timeline != null)
        {
            _timeline.ResetAllEvents();

            // 初期状態を再適用
            if (_timeline.events.Count > 0)
            {
                ApplyEnvironmentState(_timeline.events[0]);
            }
        }

        // 消防団の巡回を停止
        if (_fireTruckAgents != null)
        {
            foreach (var fireTruck in _fireTruckAgents)
            {
                if (fireTruck != null)
                {
                    fireTruck.StopPatrol();
                }
            }
        }

        Debug.Log("[DisasterEventManager] エピソードをリセットしました");
    }

    /// <summary>
    /// 災害フェーズの日本語名を取得
    /// </summary>
    public string GetPhaseDisplayName()
    {
        return GetPhaseDisplayName(_currentPhase);
    }

    /// <summary>
    /// 災害フェーズの日本語名を取得
    /// </summary>
    public static string GetPhaseDisplayName(DisasterPhase phase)
    {
        switch (phase)
        {
            case DisasterPhase.BeforeDisaster:
                return "発災前";
            case DisasterPhase.Shaking:
                return "揺れ継続中";
            case DisasterPhase.InfoGap:
                return "情報空白期";
            case DisasterPhase.WarningIssued:
                return "警報発表後";
            case DisasterPhase.PreArrival:
                return "津波接近中";
            case DisasterPhase.FirstWave:
                return "第1波到達";
            case DisasterPhase.Interval:
                return "波間";
            case DisasterPhase.MainWave:
                return "本波到達";
            case DisasterPhase.Aftermath:
                return "収束期";
            default:
                return "不明";
        }
    }

    /// <summary>
    /// 現在の環境状態をLLMリクエスト用のPayloadとして取得
    /// </summary>
    public LLM.EnvironmentStatePayload GetEnvironmentStatePayload()
    {
        return new LLM.EnvironmentStatePayload
        {
            disaster_phase = _currentPhase.ToString(),
            disaster_phase_display = GetPhaseDisplayName(),
            seismic_intensity = _currentSeismicIntensity,
            tsunami_height = _currentTsunamiHeight,
            is_power_on = _isPowerOn,
            is_radio_working = _isRadioWorking,
            rumble_intensity = _groundRumbleIntensity,
            siren_active = _isSirenActive
        };
    }
}
}
