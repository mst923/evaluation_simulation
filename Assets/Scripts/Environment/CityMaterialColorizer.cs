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

        foreach (var renderer in renderers)
        {
            string objName = renderer.gameObject.name;

            if (objName.StartsWith("tran_"))
            {
                SetColor(renderer, roadColor);
                roadCount++;
            }
            else if (objName.StartsWith("bldg_"))
            {
                // 建物ごとに微妙に色を変えてリアルに
                float variation = buildingColorVariation;
                int hash = objName.GetHashCode();
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
            else if (objName.StartsWith("dem_"))
            {
                SetColor(renderer, terrainColor);
                terrainCount++;
            }
            else if (objName.StartsWith("luse_"))
            {
                SetColor(renderer, landUseColor);
                otherCount++;
            }
        }

        Debug.Log($"[CityMaterialColorizer] 色適用完了: 道路={roadCount}, 建物={buildingCount}, 地形={terrainCount}, その他={otherCount}");
    }

    private static void SetColor(Renderer renderer, Color color)
    {
        // SharedMaterialを変更するとプロジェクト全体に影響するため、
        // ランタイムではmaterial（インスタンス）を使う
        foreach (var mat in renderer.materials)
        {
            if (mat != null)
            {
                mat.color = color;
            }
        }
    }
}
}
