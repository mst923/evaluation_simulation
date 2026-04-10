using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using EvacSim.Core;

namespace EvacSim.Disaster
{
/// <summary>
/// 聴覚的な災害情報（地鳴り、サイレンなど）を管理するクラス
/// 避難者の位置に応じて、聞こえる音の種類と強度を判定する
/// </summary>
public class AudioEventManager : MonoBehaviour
{
    [Header("地鳴り設定")]
    [Tooltip("地鳴りが聞こえる基準となる位置（海岸線ポイント）")]
    public List<Transform> coastlinePoints = new List<Transform>();

    [Tooltip("地鳴りが聞こえる最大距離（海岸からの距離、メートル）")]
    public float maxRumbleDistance = 1000f;

    [Tooltip("地鳴りを自動検出するためのタグ")]
    public string coastlineTag = "Coastline";

    [Header("サイレン設定")]
    [Tooltip("サイレン音源の位置")]
    public List<Transform> sirenSources = new List<Transform>();

    [Tooltip("サイレンが聞こえる最大距離（メートル）")]
    public float maxSirenDistance = 800f;

    [Tooltip("サイレンを自動検出するためのタグ")]
    public string sirenTag = "Siren";

    [Header("現在の聴覚状態（読み取り専用）")]
    [SerializeField] private float _currentRumbleIntensity = 0f;
    [SerializeField] private bool _isSirenActive = false;

    // プロパティ
    public float CurrentRumbleIntensity => _currentRumbleIntensity;
    public bool IsSirenActive => _isSirenActive;

    private EnvManager _envManager;

    void Start()
    {
        _envManager = FindFirstObjectByType<EnvManager>();

        // 海岸線ポイントが未設定の場合、タグで自動検出
        if (coastlinePoints.Count == 0 && !string.IsNullOrEmpty(coastlineTag))
        {
            var coastlineObjs = GameObject.FindGameObjectsWithTag(coastlineTag);
            coastlinePoints = coastlineObjs.Select(o => o.transform).ToList();
            if (coastlinePoints.Count > 0)
            {
                Debug.Log($"[AudioEventManager] 海岸線ポイントを{coastlinePoints.Count}個自動検出しました");
            }
        }

        // サイレン音源が未設定の場合、タグで自動検出
        if (sirenSources.Count == 0 && !string.IsNullOrEmpty(sirenTag))
        {
            var sirenObjs = GameObject.FindGameObjectsWithTag(sirenTag);
            sirenSources = sirenObjs.Select(o => o.transform).ToList();
            if (sirenSources.Count > 0)
            {
                Debug.Log($"[AudioEventManager] サイレン音源を{sirenSources.Count}個自動検出しました");
            }
        }
    }

    /// <summary>
    /// DisasterEventManagerから呼び出される: 聴覚状態を更新
    /// </summary>
    public void UpdateAudioState(float rumbleIntensity, bool sirenActive)
    {
        _currentRumbleIntensity = rumbleIntensity;
        _isSirenActive = sirenActive;
    }

    /// <summary>
    /// サイレンのアクティブ状態を設定
    /// </summary>
    public void SetSirenActive(bool active)
    {
        _isSirenActive = active;
    }

    /// <summary>
    /// 指定位置で聞こえる地鳴りの強度を計算（0-1）
    /// 海岸線からの距離に基づいて減衰
    /// </summary>
    public float GetRumbleIntensityAt(Vector3 listenerPosition)
    {
        if (_currentRumbleIntensity <= 0) return 0;
        if (coastlinePoints.Count == 0) return _currentRumbleIntensity; // 海岸線未設定の場合は減衰なし

        // 最も近い海岸線ポイントまでの距離を計算
        float minDistanceToCoast = float.MaxValue;

        foreach (var coastPoint in coastlinePoints)
        {
            if (coastPoint == null) continue;
            float dist = Vector3.Distance(listenerPosition, coastPoint.position);
            if (dist < minDistanceToCoast)
            {
                minDistanceToCoast = dist;
            }
        }

        // 距離減衰を適用（距離が近いほど強度が高い）
        float attenuation = 1f - Mathf.Clamp01(minDistanceToCoast / maxRumbleDistance);
        return _currentRumbleIntensity * attenuation;
    }

    /// <summary>
    /// 指定位置でサイレンが聞こえるかどうかを判定
    /// </summary>
    public bool CanHearSiren(Vector3 listenerPosition)
    {
        if (!_isSirenActive) return false;
        if (sirenSources.Count == 0) return true; // サイレン音源未設定の場合は常に聞こえる

        foreach (var sirenSource in sirenSources)
        {
            if (sirenSource == null) continue;
            float distance = Vector3.Distance(listenerPosition, sirenSource.position);
            if (distance <= maxSirenDistance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 最も近いサイレン音源からの距離を取得
    /// </summary>
    public float GetNearestSirenDistance(Vector3 listenerPosition)
    {
        if (sirenSources.Count == 0) return 0f;

        float minDistance = float.MaxValue;

        foreach (var sirenSource in sirenSources)
        {
            if (sirenSource == null) continue;
            float distance = Vector3.Distance(listenerPosition, sirenSource.position);
            if (distance < minDistance)
            {
                minDistance = distance;
            }
        }

        return minDistance == float.MaxValue ? 0f : minDistance;
    }

    /// <summary>
    /// 最も近い海岸線ポイントからの距離を取得
    /// </summary>
    public float GetNearestCoastlineDistance(Vector3 position)
    {
        if (coastlinePoints.Count == 0) return float.MaxValue;

        float minDistance = float.MaxValue;

        foreach (var coastPoint in coastlinePoints)
        {
            if (coastPoint == null) continue;
            float distance = Vector3.Distance(position, coastPoint.position);
            if (distance < minDistance)
            {
                minDistance = distance;
            }
        }

        return minDistance;
    }

    /// <summary>
    /// エピソードリセット時の処理
    /// </summary>
    public void OnEpisodeReset()
    {
        _currentRumbleIntensity = 0f;
        _isSirenActive = false;
    }

    void OnDrawGizmosSelected()
    {
        // 海岸線ポイントを可視化
        Gizmos.color = Color.cyan;
        foreach (var point in coastlinePoints)
        {
            if (point != null)
            {
                Gizmos.DrawWireSphere(point.position, 10f);
            }
        }

        // サイレン音源と可聴範囲を可視化
        Gizmos.color = Color.yellow;
        foreach (var source in sirenSources)
        {
            if (source != null)
            {
                Gizmos.DrawWireSphere(source.position, maxSirenDistance);
            }
        }
    }
}
}
