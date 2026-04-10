using System;
using System.Collections.Generic;
using UnityEngine;

namespace Traffic
{
    /// <summary>
    /// 交通サーバーとの通信用メッセージの種類
    /// </summary>
    public static class TrafficMessageType
    {
        // クライアント → サーバー
        public const string SPAWN_VEHICLE = "spawn_vehicle";
        public const string REMOVE_VEHICLE = "remove_vehicle";
        public const string SET_ROUTE = "set_route";
        public const string GET_STATE = "get_state";
        public const string GET_TRAFFIC = "get_traffic";
        public const string STEP = "step";

        // サーバー → クライアント
        public const string VEHICLE_UPDATES = "vehicle_updates";
        public const string TRAFFIC_STATE = "traffic_state";
        public const string ERROR = "error";
    }

    /// <summary>
    /// 車両スポーンリクエスト
    /// </summary>
    [Serializable]
    public class VehicleSpawnRequest
    {
        public string message_type = TrafficMessageType.SPAWN_VEHICLE;
        public string request_id;
        public SpawnVehicleData spawn_vehicle;
    }

    [Serializable]
    public class SpawnVehicleData
    {
        public string vehicle_id;
        public string origin_edge;
        public string destination_edge;
        public LLM.Vector3Payload origin_pos;       // Unity座標（サーバー側でSUMO edge自動解決）
        public LLM.Vector3Payload destination_pos;   // Unity座標（サーバー側でSUMO edge自動解決）
        public float depart_speed;
        public string vehicle_type = "passenger";
    }

    /// <summary>
    /// 車両削除リクエスト
    /// </summary>
    [Serializable]
    public class VehicleRemoveRequest
    {
        public string message_type = TrafficMessageType.REMOVE_VEHICLE;
        public string request_id;
        public RemoveVehicleData remove_vehicle;
    }

    [Serializable]
    public class RemoveVehicleData
    {
        public string vehicle_id;
    }

    /// <summary>
    /// ルート変更リクエスト
    /// </summary>
    [Serializable]
    public class RouteChangeRequest
    {
        public string message_type = TrafficMessageType.SET_ROUTE;
        public string request_id;
        public SetRouteData set_route;
    }

    [Serializable]
    public class SetRouteData
    {
        public string vehicle_id;
        public string[] edge_ids;
    }

    /// <summary>
    /// シミュレーションステップリクエスト
    /// </summary>
    [Serializable]
    public class StepRequest
    {
        public string message_type = TrafficMessageType.STEP;
        public string request_id;
    }

    /// <summary>
    /// 渋滞情報取得リクエスト
    /// </summary>
    [Serializable]
    public class TrafficStateRequest
    {
        public string message_type = TrafficMessageType.GET_TRAFFIC;
        public string request_id;
        public string[] edge_ids;
    }

    /// <summary>
    /// サーバーからのレスポンス
    /// </summary>
    [Serializable]
    public class TrafficServerResponse
    {
        public string message_type;
        public string request_id;
        public VehicleUpdate[] vehicle_states;
        public EdgeTrafficUpdate[] edge_traffic;
        public float sim_time;
        public string error;
    }

    /// <summary>
    /// 車両の位置更新データ
    /// </summary>
    [Serializable]
    public class VehicleUpdate
    {
        public string vehicle_id;
        public LLM.Vector3Payload position;
        public float speed;
        public float angle;
        public string edge_id;
        public int lane_index;
        public string route_id;
        public bool is_stopped;
    }

    /// <summary>
    /// エッジごとの交通状態データ
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
