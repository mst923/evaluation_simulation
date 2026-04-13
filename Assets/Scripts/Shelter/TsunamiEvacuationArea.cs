using UnityEngine;
using UnityEngine.AI;
using EvacSim.Core;
using EvacSim.Evacuees;

namespace EvacSim.Shelters
{
/// <summary>
/// 津波避難地域（高台など）のコンポーネント
/// Shelterとは異なり、収容人数に制限がないエリア
/// </summary>
public class TsunamiEvacuationArea : MonoBehaviour
{
    [Header("避難地域情報")]
    [Tooltip("避難地域の表示名（例: 高台エリアA）")]
    public string displayName = "";

    [Tooltip("避難地域の説明（例: 海抜35m、広い平地）")]
    [TextArea(2, 4)]
    public string description = "";

    [Tooltip("海抜（メートル）")]
    public float elevationMeters = 0f;

    /// <summary>
    /// 津波避難地域の識別子
    /// </summary>
    public string uuid;

    /// <summary>
    /// 現在の収容人数（メトリクス用）
    /// </summary>
    public int CurrentOccupancy = 0;

    private EnvManager _env;

    // NavMesh上の位置キャッシュ（パフォーマンス最適化のため）
    private Vector3? _cachedNavMeshPosition = null;
    private bool _navMeshPositionCalculated = false;

    void Start()
    {
        _env = GetComponentInParent<EnvManager>();
        if (_env != null)
        {
            _env.OnEndEpisode += (float _, float __) => Reset();
        }

        if (string.IsNullOrEmpty(uuid))
        {
            uuid = System.Guid.NewGuid().ToString();
        }

        Debug.Log($"[TsunamiEvacuationArea] {gameObject.name}: 初期化完了 - " +
                  $"DisplayName: {displayName}, Elevation: {elevationMeters}m");
    }

    /// <summary>
    /// EvacuationPoint（子オブジェクト）のNavMesh上の位置を取得
    /// キャッシュを使用してパフォーマンスを最適化
    /// </summary>
    /// <returns>NavMesh上の位置、取得できない場合はnull</returns>
    public Vector3? GetNavMeshPosition()
    {
        try
        {
            if (_navMeshPositionCalculated)
            {
                return _cachedNavMeshPosition;
            }

            Transform pointTransform = transform.childCount > 0 ? transform.GetChild(0) : transform;
            if (pointTransform == null)
            {
                _navMeshPositionCalculated = true;
                return null;
            }

            Vector3 targetPosition = pointTransform.position;

            NavMeshHit hit;
            if (NavMesh.SamplePosition(targetPosition, out hit, 50f, NavMesh.AllAreas))
            {
                _cachedNavMeshPosition = hit.position;
            }
            else
            {
                _cachedNavMeshPosition = null;
            }

            _navMeshPositionCalculated = true;
            return _cachedNavMeshPosition;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[TsunamiEvacuationArea] {gameObject.name}: GetNavMeshPosition failed - {ex.Message}");
            _navMeshPositionCalculated = true;
            _cachedNavMeshPosition = null;
            return null;
        }
    }

    /// <summary>
    /// エピソードリセット時の処理
    /// </summary>
    public void Reset()
    {
        CurrentOccupancy = 0;
        _navMeshPositionCalculated = false;
        _cachedNavMeshPosition = null;
        Debug.Log($"[TsunamiEvacuationArea] {gameObject.name}: エピソード終了 - 収容人数をリセットしました。");
    }

    /// <summary>
    /// 避難者オブジェクトが避難地域に到達したときに呼び出される
    /// </summary>
    void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[TsunamiEvacuationArea] OnTriggerEnter called on {gameObject.name} - Colliding with: {other.name}, Tag: {other.tag}");

        if (other.CompareTag("Evacuee"))
        {
            Debug.Log($"[TsunamiEvacuationArea] Evacuee detected! Area: {gameObject.name}, Evacuee: {other.name}");
            Evacuee evacuee = other.GetComponent<Evacuee>();
            if (evacuee != null)
            {
                // 避難処理を実行（記録はEvacuee.EvacuationToArea内で避難成功時にのみ行う）
                evacuee.EvacuationToArea(this);
                CurrentOccupancy++;
            }
            else
            {
                Debug.LogError($"[TsunamiEvacuationArea] Evacuee component not found on {other.name}");
            }
        }
    }
}
}
