using System;
using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// エッジごとの交通状態データ。
    /// NavMeshTrafficCalculatorが生成し、TrafficStateProviderが消費する。
    /// </summary>
    [Serializable]
    public class EdgeTrafficUpdate
    {
        public string edge_id;
        public int vehicle_count;
        public float average_speed;
        public float occupancy;
        public int congestion_level;
        public float travel_time;
        public float free_flow_travel_time;
    }
}
