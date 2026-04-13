using UnityEngine;
using System.Collections.Generic;

namespace EvacSim.Environment
{
/// <summary>
/// PLATEAUの3D都市モデルオブジェクトに、街並みらしいリアルな色合いを自動適用する。
/// シーン内の tran_（道路）, bldg_（建物）, dem_（地形）等を名前プレフィックスで判別し、
/// マテリアルカラーを変更する。
/// </summary>
public class CityMaterialColorizer : MonoBehaviour
{
    [Header("道路 (tran_)")]
    [Tooltip("アスファルト道路の色")]
    [SerializeField] private Color roadColor = new Color(0.25f, 0.25f, 0.27f);  // ダークグレー（アスファルト）

    [Header("建物 (bldg_)")]
    [Tooltip("建物の基本色（ランダムバリエーション付き）")]
    [SerializeField] private Color buildingBaseColor = new Color(0.85f, 0.82f, 0.78f);  // クリームベージュ
    [Tooltip("建物の色のバリエーション幅")]
    [SerializeField] private float buildingColorVariation = 0.08f;

    [Header("地形 (dem_)")]
    [Tooltip("地面・地形の色")]
    [SerializeField] private Color terrainColor = new Color(0.45f, 0.55f, 0.35f);  // 草地の緑

    [Header("土地利用 (luse_)")]
    [SerializeField] private Color landUseColor = new Color(0.5f, 0.6f, 0.4f);  // やや明るい緑

    [Header("設定")]
    [Tooltip("Play開始時に自動適用")]
    [SerializeField] private bool autoApplyOnStart = true;

    private void Start()
    {
        if (autoApplyOnStart)
        {
            ApplyColors();
        }
    }

    /// <summary>
    /// シーン内の全PLATEAUオブジェクトにマテリアルカラーを適用する
    /// </summary>
    [ContextMenu("Apply City Colors")]
    public void ApplyColors()
    {
        int roadCount = 0, buildingCount = 0, terrainCount = 0, otherCount = 0;

        // シーン内の全Rendererを走査
        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);

        // デバッグ: Renderer総数を出力
        Debug.Log($"[CityMaterialColorizer] Renderer総数: {renderers.Length}");
        bool debugDone = false;

        foreach (var renderer in renderers)
        {
            // 自身の名前 or 親の名前で判定（PLATEAUは子にRendererがある場合がある）
            string objName = renderer.gameObject.name;
            string parentName = renderer.transform.parent != null ? renderer.transform.parent.name : "";
            string matchName = objName.StartsWith("tran_") || objName.StartsWith("bldg_") || objName.StartsWith("dem_") || objName.StartsWith("luse_")
                ? objName : parentName;

            if (!debugDone)
            {
                // 最初の5件のRenderer名をログ出力
                Debug.Log($"[DEBUG] renderer例: name={objName}, parent={parentName}");
                if (roadCount + buildingCount + terrainCount + otherCount >= 5) debugDone = true;
            }

            if (matchName.StartsWith("tran_"))
            {
                SetColor(renderer, roadColor);
                roadCount++;
            }
            else if (matchName.StartsWith("bldg_"))
            {
                // 建物ごとに微妙に色を変えてリアルに
                float variation = buildingColorVariation;
                int hash = matchName.GetHashCode();
                float r = buildingBaseColor.r + ((hash & 0xFF) / 255f - 0.5f) * variation;
                float g = buildingBaseColor.g + (((hash >> 8) & 0xFF) / 255f - 0.5f) * variation;
                float b = buildingBaseColor.b + (((hash >> 16) & 0xFF) / 255f - 0.5f) * variation;
                SetColor(renderer, new Color(
                    Mathf.Clamp01(r),
                    Mathf.Clamp01(g),
                    Mathf.Clamp01(b)
                ));
                buildingCount++;
            }
            else if (matchName.StartsWith("dem_"))
            {
                SetColor(renderer, terrainColor);
                terrainCount++;
            }
            else if (matchName.StartsWith("luse_"))
            {
                SetColor(renderer, landUseColor);
                otherCount++;
            }
        }

        Debug.Log($"[CityMaterialColorizer] 色適用完了: 道路={roadCount}, 建物={buildingCount}, 地形={terrainCount}, その他={otherCount}");

        // デバッグ: 最初に見つかった道路のマテリアル情報を出力
        foreach (var renderer in renderers)
        {
            if (renderer.gameObject.name.StartsWith("tran_"))
            {
                var mat = Application.isPlaying ? renderer.material : renderer.sharedMaterial;
                if (mat != null)
                {
                    Debug.Log($"[DEBUG] road obj={renderer.gameObject.name}, shader={mat.shader.name}, props=[{string.Join(", ", GetPropertyNames(mat))}]");
                }
                break;
            }
        }
    }

    private static List<string> GetPropertyNames(Material mat)
    {
        var names = new List<string>();
        var shader = mat.shader;
        for (int i = 0; i < shader.GetPropertyCount(); i++)
        {
            names.Add(shader.GetPropertyName(i));
        }
        return names;
    }

    private static void SetColor(Renderer renderer, Color color)
    {
        var mats = Application.isPlaying ? renderer.materials : renderer.sharedMaterials;
        foreach (var mat in mats)
        {
            if (mat == null) continue;

            // URP Lit / Simple Lit
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            // Standard shader
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
        }
    }
}
}
