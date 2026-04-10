using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EvacSim.Core
{
public class Utils : MonoBehaviour {
    
    /// <summary>
    /// 避難者のランダムスポーン範囲を描画する
    /// </summary>
    public static void DrawWireCircle(Vector3 center, float radius, int segments = 36) {
        float angle = 0f;
        float angleStep = 360f / segments;

        Vector3 prevPoint = center + new Vector3(radius, 0, 0); // 初期点

        for (int i = 1; i <= segments; i++) {
            angle += angleStep;
            float rad = Mathf.Deg2Rad * angle;

            Vector3 newPoint = center + new Vector3(Mathf.Cos(rad) * radius, 0, Mathf.Sin(rad) * radius);
            Gizmos.DrawLine(prevPoint, newPoint);

            prevPoint = newPoint; // 次の線を描画するために現在の点を更新
        }
    }


    /// <summary> 汎用的なCSV保存関数 </summary>
    public static void SaveResultCSV<T>(string[] header, List<T> dataList, Func<T, string[]> convertToCSVRow, string filePath = null, bool append = true) {

        if (filePath == null) {
            filePath = "result.csv";
        }
        // パスの先頭に指定パスを付与
        var logRoot = Path.Combine(Application.dataPath, "..", "Logs");
        filePath = Path.Combine(logRoot, filePath);
        // フォルダが存在しない場合は作成
        string dir = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(dir)) {
            Directory.CreateDirectory(dir);
        }

        bool writeHeader = !File.Exists(filePath) || !append;
        using (StreamWriter writer = new StreamWriter(filePath, append)) {
            if (writeHeader) writer.WriteLine(string.Join(",", header));

            foreach (T data in dataList) {
                string[] row = convertToCSVRow(data);
                writer.WriteLine(string.Join(",", row));
            }
        }
        Debug.Log($"CSV saved: {filePath}");
    }

}
}