using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using EvacSim.Core;
using EvacSim.Evacuee;

namespace EvacSim.Disaster
{
/// <summary>
/// 防災行政無線とJアラートを管理するクラス
/// EnvManagerに統合しても良いが、責務を分離するために独立したコンポーネントとして実装
/// </summary>
public class AlertManager : MonoBehaviour
{
    [Header("行政無線スピーカー")]
    [Tooltip("シーン内の全行政無線スピーカー（自動検出 or 手動設定）")]
    public List<EmergencyBroadcastSpeaker> speakers = new List<EmergencyBroadcastSpeaker>();
    
    [Tooltip("自動的にシーン内のSpeakerを検出するか")]
    public bool autoFindSpeakers = true;
    
    [Header("Jアラート設定")]
    [Tooltip("Jアラートを発信する時刻（シミュレーション開始からの秒数）")]
    public float jAlertSendTimeSeconds = 15f;
    
    [Tooltip("Jアラートのメッセージ内容")]
    [TextArea(3, 6)]
    public string jAlertMessage = "【緊急速報】津波警報が発表されました。海岸から離れて高台に避難してください。";
    
    [Header("シナリオ連動設定")]
    [Tooltip("どのシナリオでJアラートを発信するか")]
    public EnvManager.DisasterScenario[] jAlertScenarios = new EnvManager.DisasterScenario[] 
    { 
        EnvManager.DisasterScenario.Shindo7Tsunami 
    };
    
    private EnvManager _envManager;
    private bool _jAlertSent = false;
    private float _lastCheckTime = 0f;
    private const float CHECK_INTERVAL = 1f; // 1秒ごとにチェック

    // DisasterEventManagerから通知された放送メッセージを一時保存
    private string _pendingBroadcastMessage = null;
    private bool _hasPendingBroadcast = false;
    
    void Start()
    {
        _envManager = GetComponentInParent<EnvManager>();
        if (_envManager == null)
        {
            _envManager = FindFirstObjectByType<EnvManager>();
        }
        
        if (autoFindSpeakers)
        {
            // シーン内の全EmergencyBroadcastSpeakerを自動検出
            speakers = new List<EmergencyBroadcastSpeaker>(
                FindObjectsByType<EmergencyBroadcastSpeaker>(FindObjectsSortMode.None)
            );
            Debug.Log($"[AlertManager] 自動検出: {speakers.Count}個の行政無線スピーカーを発見");
        }
    }
    
    void Update()
    {
        if (_envManager == null) return;
        
        float currentTime = _envManager.CurrentTimeSec;
        
        // 一定間隔でチェック（パフォーマンス最適化）
        if (currentTime - _lastCheckTime < CHECK_INTERVAL)
        {
            return;
        }
        _lastCheckTime = currentTime;
        
        // 行政無線の放送をチェック
        CheckBroadcastSpeakers(currentTime);

        // DisasterEventManagerからの放送メッセージをチェック
        CheckPendingBroadcast();

        // Jアラートの発信をチェック
        CheckJAlert(currentTime);
    }

    /// <summary>
    /// DisasterEventManagerからの保留中の放送メッセージを処理
    /// </summary>
    private void CheckPendingBroadcast()
    {
        if (!_hasPendingBroadcast || string.IsNullOrEmpty(_pendingBroadcastMessage)) return;

        BroadcastToAllEvacuees(_pendingBroadcastMessage);
        _hasPendingBroadcast = false;
        _pendingBroadcastMessage = null;
    }

    /// <summary>
    /// DisasterEventManagerから呼び出される: 緊急放送を行う
    /// 防災無線が動作中の場合のみ、可聴範囲内の避難者に通知
    /// </summary>
    public void BroadcastEmergencyMessage(string message)
    {
        _pendingBroadcastMessage = message;
        _hasPendingBroadcast = true;
        Debug.Log($"[AlertManager] 緊急放送をスケジュール: {message}");
    }

    /// <summary>
    /// 全スピーカーの可聴範囲内の避難者に放送を通知
    /// </summary>
    private void BroadcastToAllEvacuees(string message)
    {
        if (_envManager == null || _envManager.Evacuees == null) return;

        int notifiedCount = 0;

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null || !evacueeObj.activeSelf) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null) continue;

            // いずれかのスピーカーの可聴範囲内かどうかを判定
            bool canHear = false;
            foreach (var speaker in speakers)
            {
                if (speaker != null && speaker.IsWithinAudibleRange(evacueeObj.transform.position))
                {
                    canHear = true;
                    break;
                }
            }

            if (canHear)
            {
                evacuee.OnHeardBroadcast(message);
                notifiedCount++;
            }
        }

        Debug.Log($"[AlertManager] 緊急放送を{notifiedCount}人に通知しました: {message}");
    }

    /// <summary>
    /// DisasterEventManagerから呼び出される: Jアラートを送信
    /// </summary>
    public void SendJAlertFromEvent(string message)
    {
        if (_envManager == null || _envManager.Evacuees == null) return;

        int sentCount = 0;

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null || !evacueeObj.activeSelf) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null) continue;

            // スマホを持っている避難者にのみ送信
            if (evacuee.HasSmartphone)
            {
                evacuee.OnReceivedJAlert(message);
                sentCount++;
            }
        }

        Debug.Log($"[AlertManager] Jアラートを{sentCount}人のスマホ保有者に送信しました（イベントから）\n" +
                  $"送信内容: {message}");
    }
    
    /// <summary>
    /// 行政無線スピーカーの放送をチェックし、範囲内の避難者に通知
    /// </summary>
    private void CheckBroadcastSpeakers(float currentTime)
    {
        if (_envManager == null || _envManager.Evacuees == null) return;
        
        foreach (var speaker in speakers)
        {
            if (speaker == null) continue;
            
            // 放送中かどうかをチェック
            if (!speaker.IsBroadcasting(currentTime))
            {
                continue;
            }
            
            // 1回だけ放送する設定の場合、放送開始時にマーク
            if (speaker.broadcastDurationSeconds <= 0f && 
                currentTime >= speaker.broadcastStartTimeSeconds && 
                currentTime <= speaker.broadcastStartTimeSeconds + 1f) // 1秒以内の初回のみ
            {
                speaker.MarkAsBroadcasted();
            }
            
            // 全避難者をチェック
            foreach (var evacueeObj in _envManager.Evacuees)
            {
                if (evacueeObj == null || !evacueeObj.activeSelf) continue;
                
                var evacuee = evacueeObj.GetComponent<Evacuee>();
                if (evacuee == null) continue;
                
                // 可聴範囲内かどうかを判定
                if (speaker.IsWithinAudibleRange(evacueeObj.transform.position))
                {
                    // 避難者に「放送を聞いた」ことを通知
                    evacuee.OnHeardBroadcast(speaker.GetBroadcastMessage());
                }
            }
        }
    }
    
    /// <summary>
    /// Jアラートの発信をチェック
    /// </summary>
    private void CheckJAlert(float currentTime)
    {
        if (_envManager == null || _jAlertSent) return;
        
        // 指定時刻に達したか、またはシナリオ条件を満たしたか
        bool shouldSend = currentTime >= jAlertSendTimeSeconds;
        
        if (shouldSend)
        {
            // シナリオ条件をチェック
            bool scenarioMatches = jAlertScenarios.Length == 0 || 
                                   jAlertScenarios.Contains(_envManager.Scenario);
            
            if (scenarioMatches)
            {
                SendJAlert();
                _jAlertSent = true;
            }
        }
    }
    
    /// <summary>
    /// Jアラートを全スマホ保有者に送信
    /// </summary>
    private void SendJAlert()
    {
        if (_envManager == null || _envManager.Evacuees == null) return;
        
        int sentCount = 0;
        
        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null || !evacueeObj.activeSelf) continue;
            
            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null) continue;
            
            // スマホを持っている避難者にのみ送信
            if (evacuee.HasSmartphone)
            {
                evacuee.OnReceivedJAlert(jAlertMessage);
                sentCount++;
            }
        }
        
        Debug.Log($"[AlertManager] Jアラートを{sentCount}人のスマホ保有者に送信しました\n" +
                  $"送信内容: {jAlertMessage}");
    }
    
    /// <summary>
    /// エピソード開始時にリセット
    /// </summary>
    public void OnEpisodeStart()
    {
        _jAlertSent = false;
        _lastCheckTime = 0f;

        // 保留中の放送メッセージをリセット
        _pendingBroadcastMessage = null;
        _hasPendingBroadcast = false;

        // 全スピーカーの放送状態をリセット
        foreach (var speaker in speakers)
        {
            if (speaker != null)
            {
                speaker.ResetBroadcastState();
            }
        }

        Debug.Log("[AlertManager] エピソード状態をリセットしました");
    }
}
}





