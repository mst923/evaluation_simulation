using UnityEngine;
using EvacSim.Evacuees;

namespace EvacSim.Traffic
{
    /// <summary>
    /// ペルソナ属性と状況に基づいて移動モードを判定するクラス。
    /// 年齢、身体状況、家族構成、自宅位置、渋滞情報を考慮する。
    /// </summary>
    public static class TransportModeDecider
    {
        /// <summary>
        /// ペルソナと状況に基づいて移動モードを決定する。
        /// </summary>
        /// <param name="persona">ペルソナデータ</param>
        /// <param name="vehicleUsageRate">実験パラメータの車両利用率</param>
        /// <param name="enableTraffic">交通シミュレーションが有効か</param>
        /// <returns>推奨される移動モード</returns>
        public static Evacuee.TransportMode Decide(
            PersonaData persona,
            float vehicleUsageRate,
            bool enableTraffic)
        {
            // 交通シミュレーションが無効なら常に徒歩
            if (!enableTraffic)
                return Evacuee.TransportMode.WALKING;

            // 車両を持っていない or 運転不可なら徒歩
            if (persona == null || !persona.has_vehicle || !persona.can_drive)
                return Evacuee.TransportMode.WALKING;

            // 身体的に運転が困難な場合
            if (IsPhysicallyUnableToDrive(persona))
                return Evacuee.TransportMode.WALKING;

            // 車両利用率に基づく確率的判定
            // ペルソナのagent_idを種として使用（再現性のため）
            float hash = Mathf.Abs((float)(persona.agent_id * 2654435761 % 4294967296) / 4294967296f);
            if (hash > vehicleUsageRate)
                return Evacuee.TransportMode.WALKING;

            return Evacuee.TransportMode.DRIVING;
        }

        /// <summary>
        /// 身体的に運転が困難かどうかを判定する
        /// </summary>
        private static bool IsPhysicallyUnableToDrive(PersonaData persona)
        {
            if (string.IsNullOrEmpty(persona.physical_condition))
                return false;

            string condition = persona.physical_condition.ToLower();

            // 車椅子、視覚障害、高齢で体が不自由な場合
            if (condition.Contains("車椅子") || condition.Contains("wheelchair"))
                return true;
            if (condition.Contains("視覚障害") || condition.Contains("blind"))
                return true;

            return false;
        }

        /// <summary>
        /// 渋滞状況に基づいて車両放棄を推奨するかどうかを判定する。
        /// </summary>
        /// <param name="congestionLevel">現在の道路の渋滞レベル (1-5)</param>
        /// <param name="remainingDistanceMeters">目的地までの残り距離（メートル）</param>
        /// <param name="walkingTimeMinutes">徒歩での推定所要時間（分）</param>
        /// <returns>車両を放棄すべきかどうか</returns>
        public static bool ShouldAbandonVehicle(
            int congestionLevel,
            float remainingDistanceMeters,
            float walkingTimeMinutes)
        {
            // 大渋滞（レベル5）で残り距離が短い場合は放棄を推奨
            if (congestionLevel >= 5 && remainingDistanceMeters < 500f)
                return true;

            // 渋滞（レベル4以上）で徒歩のほうが早い場合
            if (congestionLevel >= 4 && walkingTimeMinutes < 10f)
                return true;

            return false;
        }

        /// <summary>
        /// 渋滞レベルの日本語ラベルを取得する
        /// </summary>
        public static string GetCongestionLabel(int level)
        {
            return level switch
            {
                1 => "順調",
                2 => "やや混雑",
                3 => "混雑",
                4 => "渋滞",
                5 => "大渋滞",
                _ => "不明",
            };
        }
    }
}
