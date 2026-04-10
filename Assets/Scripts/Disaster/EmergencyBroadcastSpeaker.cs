using UnityEngine;

namespace EvacSim.Disaster
{
/// <summary>
/// 防災行政無線（屋外スピーカー）のコンポーネント
/// シーン内のGameObjectにアタッチして、位置と可聴範囲を設定する
/// </summary>
public class EmergencyBroadcastSpeaker : MonoBehaviour
{
    [Header("可聴範囲設定")]
    [Tooltip("このスピーカーから聞こえる最大距離（メートル）")]
    public float audibleRadiusMeters = 500f;
    
    [Header("放送内容")]
    [Tooltip("このスピーカーから流す放送内容（日本語）")]
    [TextArea(3, 6)]
    public string broadcastMessage = "津波警報が発表されました。海岸から離れて高台に避難してください。";
    
    [Header("放送タイミング（シミュレーション開始からの秒数）")]
    [Tooltip("このスピーカーが放送を開始する時刻（秒）。0 = 開始直後")]
    public float broadcastStartTimeSeconds = 10f;
    
    [Tooltip("放送が継続する時間（秒）。0 = 1回だけ放送")]
    public float broadcastDurationSeconds = 30f;
    
    [Header("デバッグ表示")]
    [Tooltip("Gizmosで可聴範囲を表示するか")]
    public bool showGizmos = true;
    
    private bool _hasBroadcasted = false;
    private float _broadcastEndTime = 0f;
    
    void Start()
    {
        // 放送終了時刻を計算
        _broadcastEndTime = broadcastStartTimeSeconds + broadcastDurationSeconds;
    }
    
    /// <summary>
    /// 指定時刻に放送中かどうかを判定
    /// </summary>
    public bool IsBroadcasting(float currentTimeSeconds)
    {
        if (_hasBroadcasted && broadcastDurationSeconds <= 0f)
        {
            // 1回だけ放送する設定の場合、既に放送済みならfalse
            return false;
        }
        
        return currentTimeSeconds >= broadcastStartTimeSeconds && 
               currentTimeSeconds <= _broadcastEndTime;
    }
    
    /// <summary>
    /// 指定位置がこのスピーカーの可聴範囲内かどうかを判定
    /// </summary>
    public bool IsWithinAudibleRange(Vector3 position)
    {
        float distance = Vector3.Distance(transform.position, position);
        return distance <= audibleRadiusMeters;
    }
    
    /// <summary>
    /// 放送を開始したことを記録（1回だけ放送の場合）
    /// </summary>
    public void MarkAsBroadcasted()
    {
        _hasBroadcasted = true;
    }
    
    /// <summary>
    /// エピソード開始時に放送状態をリセット
    /// </summary>
    public void ResetBroadcastState()
    {
        _hasBroadcasted = false;
        _broadcastEndTime = broadcastStartTimeSeconds + broadcastDurationSeconds;
    }
    
    /// <summary>
    /// 放送内容を取得
    /// </summary>
    public string GetBroadcastMessage()
    {
        return broadcastMessage;
    }
    
    /// <summary>
    /// スピーカーの位置を取得
    /// </summary>
    public Vector3 GetPosition()
    {
        return transform.position;
    }
    
    /// <summary>
    /// 可聴半径を取得
    /// </summary>
    public float GetAudibleRadius()
    {
        return audibleRadiusMeters;
    }
    
    void OnDrawGizmos()
    {
        if (showGizmos)
        {
            // 可聴範囲を視覚的に表示（黄色のワイヤースフィア）
            Gizmos.color = new Color(1f, 1f, 0f, 0.3f); // 黄色、半透明
            Gizmos.DrawWireSphere(transform.position, audibleRadiusMeters);
            
            // スピーカーの位置を赤い球で表示
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.position, 2f);
        }
    }
}
}





