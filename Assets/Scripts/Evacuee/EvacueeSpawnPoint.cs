using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EvacSim.Evacuee
{
/// <summary>
/// カスタムスポーン用のポイント
/// </summary>
public class EvacueeSpawnPoint : MonoBehaviour {
    
    public GameObject EvacueePrefab; // 避難者のプレハブ
    public float SpawnRadius = 10f; // 避難者の生成範囲
    public int SpawnSize = 50; //避難者の生成数
    private GameObject rangeIndicator; // スポーン範囲の表示オブジェクト

    void Start() {
        ShowRangeOff(); // 初期状態では非表示
    }

    void OnDrawGizmos() {
        Gizmos.color = new Color(1, 0, 0, 1.0f);
        Gizmos.DrawSphere(transform.position, SpawnRadius);
    }

    public void SpawnEvacuee() {
        Vector3 spawnPos = transform.position + Random.insideUnitSphere * SpawnRadius;
        spawnPos.y = transform.position.y; // 地面に沿わせる
        GameObject evacuee = Instantiate(EvacueePrefab, spawnPos, Quaternion.identity);
        evacuee.transform.parent = transform.parent;
        evacuee.tag = "Evacuee";
    }

    /// <summary>
    /// ランタイムでスポーン範囲を半透明で表示
    /// </summary>
    public void ShowRangeOn() {
        if (rangeIndicator == null) {
            rangeIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rangeIndicator.transform.SetParent(transform);
            rangeIndicator.transform.localPosition = Vector3.zero;
            rangeIndicator.transform.localScale = new Vector3(SpawnRadius * 2, SpawnRadius * 2, SpawnRadius * 2);


            // マテリアルの設定（半透明）
            Material transparentMaterial = new Material(Shader.Find("Standard"));
            transparentMaterial.color = new Color(200f, 0f, 0f, 0.7f); // 半透明の緑色
            transparentMaterial.SetFloat("_Mode", 3); // 透過設定
            transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            transparentMaterial.SetInt("_ZWrite", 0);
            transparentMaterial.DisableKeyword("_ALPHATEST_ON");
            transparentMaterial.EnableKeyword("_ALPHABLEND_ON");
            transparentMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            transparentMaterial.renderQueue = 3000;

            rangeIndicator.GetComponent<Renderer>().material = transparentMaterial;
            rangeIndicator.GetComponent<Collider>().enabled = false; // 当たり判定を無効化
        }
    }

    /// <summary>
    /// スポーン範囲を非表示
    /// </summary>  
    public void ShowRangeOff() {
        if (rangeIndicator != null) {
            Destroy(rangeIndicator);
            rangeIndicator = null;
        }
    }
}
}

