using System;
using UnityEngine;

namespace EvacSim.Decision.LLM
{
    /// <summary>
    /// 避難者の行動タイプ（LLMレスポンス上のaction_type）
    /// </summary>
    public enum ActionType
    {
        EVACUATE,       // 避難所に向かう
        STAY,           // その場で待機
        SEARCH_FAMILY,  // 家族を探す
        CONTACT,        // 家族に連絡を取る
        FOLLOW,         // 周囲の人について行く
        TALK            // 周辺の避難者と会話する
    }

    /// <summary>
    /// 移動速度の選択肢（LLMが選択する4段階の速度）
    /// </summary>
    public enum SpeedChoice
    {
        SLOW,      // ゆっくり
        NORMAL,    // 普通
        FAST,      // 急ぎ足
        RUN        // 走る
    }

    [Serializable]
    public class Vector3Payload
    {
        public float x;
        public float y;
        public float z;

        public Vector3Payload() { }

        public Vector3Payload(Vector3 value)
        {
            x = value.x;
            y = value.y;
            z = value.z;
        }
    }

    [Serializable]
    public class ShelterCandidatePayload
    {
        public string id;
        public string display_name;      // 表示名（例: 豊間小学校）
        public string description;       // 説明（例: 海抜20m、耐震構造）
        public Vector3Payload position;
        public int current_capacity;
        public int max_capacity;
        public float distance_meters;    // NavMeshでの経路距離（メートル）
        public float walking_time_minutes; // 徒歩所要時間（分）
        public string destination_type;  // 避難先タイプ: "shelter" or "tsunami_evacuation_area"
        public float elevation_meters;   // 津波避難地域の場合の海抜（メートル）
    }

    [Serializable]
    public class EvacueePayload
    {
        public string id;
        public Vector3Payload position;
    }

    [Serializable]
    public class SelfStatePayload
    {
        public Vector3Payload position;
        public Vector3Payload velocity;
        public float energy_level;
        public string energy_label;
        public float stress_level;
        public string stress_label;
        public string stress_reason;
        public string current_goal;
        public float stamina;
        public string stamina_label;              // 体力ラベル: "十分"/"普通"/"低下"/"疲労"
        public string current_speed_choice;       // 現在の速度選択: "SLOW"/"NORMAL"/"FAST"/"RUN"
        public string[] available_speed_choices;  // 利用可能な速度選択肢
        public string[] injuries;
        public string injury_notes;
    }

    [Serializable]
    public class TemporalContextPayload
    {
        public float elapsed_time;
        public bool has_time_limit;
        public float time_limit;
    }

    [Serializable]
    public class LLMActionRequest
    {
        public string request_id;
        public float timestamp;
        public ShelterCandidatePayload[] shelter_candidates;
        public EvacueePayload[] evacuees;
    }

    [Serializable]
    public class LLMActionResponse
    {
        public string request_id;
        public int[] actions;
        public string reasoning;
        public float confidence;
    }

    [Serializable]
    public class PersonaPayload
    {
        public int agent_id;
        public string name;
        public string role;
        public string age_group;
        public float speed_multiplier;
        public string mental_state;
        public string priority;
        public string system_prompt_context;

        // 拡張フィールド: 生活背景情報
        public string home_location_category;    // 自宅位置カテゴリ: "coastal", "hill", "center"
        public int home_elevation;               // 自宅の海抜(m)
        public string home_structure;            // 自宅の構造
        public int residence_years;              // 居住年数
        public string local_knowledge_level;     // 土地勘レベル: "newcomer", "resident", "native"
        public string current_location_reason;   // 現在地にいる理由
        public string past_disaster_experience;  // 過去の災害経験
        public string physical_condition;        // 身体状態

        // 交通シミュレーション用
        public bool has_vehicle;                 // 車両を所有しているか
        public bool can_drive;                   // 運転可能か
    }

    [Serializable]
    public class BuildingContextPayload
    {
        public string name;
        public Vector3Payload position;
        public float distance;
        public string usage;
        public string major_usage;
        public float height;
        public int floors;
        public string structure_type;
        public string land_use_type;
    }

    [Serializable]
    public class EnvironmentalContextPayload
    {
        public BuildingContextPayload[] nearby_buildings;
        public float search_radius;
        public int total_buildings_in_area;

        // 災害リスク情報（現在地のリスクのみ、簡潔版）
        public bool is_in_tsunami_zone;           // 津波浸水想定区域内か
        public string tsunami_risk_rank;          // 浸水深ランク（例: "5m以上10m未満"）
        public float tsunami_estimated_depth;     // 推定浸水深(m)
        public bool is_in_landslide_zone;         // 土砂災害警戒区域内か
        public bool is_in_special_landslide_zone; // 土砂災害特別警戒区域内か
        public string landslide_type;             // 災害種別（急傾斜地の崩落、土石流、地すべり）

        // 地形情報
        public float current_elevation;           // 現在地の標高(m)

        // 道路情報
        public string nearest_major_road;         // 最寄りの主要道路名
        public string nearest_road_type;          // 道路種別（高速、国道、県道、市道）

        // 土地利用情報
        public string current_land_use;           // 現在地の土地利用種別
    }

    /// <summary>
    /// 家族1人分の情報（LLMに渡すための簡略版）
    /// </summary>
    [Serializable]
    public class FamilyMemberPayload
    {
        public string name;              // 名前
        public string relation;          // 続柄（妻、夫、息子、娘など）
        public string likely_location;   // 想定される場所（"自宅"、"小学校"など）
        public Vector3Payload search_position; // 探索する座標
        public float distance_meters;    // 所有者からの距離
        public bool has_phone;           // 連絡手段の有無
        public bool exists_in_scene;     // シーン内に実在するか
        public bool is_reunited;         // 合流済みかどうか
    }

    /// <summary>
    /// 周辺の避難者情報（FOLLOW/TALK行動用）
    /// </summary>
    [Serializable]
    public class NearbyEvacueePayload
    {
        public string id;                // 避難者のID（GameObject名またはuniqueId）
        public Vector3Payload position;  // 現在位置
        public float distance_meters;    // 距離（メートル）
        public string current_action;    // 現在の行動（"EVACUATE", "STAY", "SEARCH_FAMILY", "CONTACT", "FOLLOW", "TALK"）
        public string target_shelter_id; // 目標避難所ID（EVACUATEの場合）
        public string name;              // 避難者の名前（ペルソナ名）
        public string role;              // 役割
        public string age_group;         // 年齢層
    }

    /// <summary>
    /// 会話履歴エントリ（TALK行動用）
    /// </summary>
    [Serializable]
    public class ConversationLogEntry
    {
        public float timestamp;           // 会話時刻
        public string partner_id;         // 会話相手のID
        public string partner_name;       // 会話相手の名前
        public string topic;              // 話題
        public string my_message;         // 自分のメッセージ
        public string partner_response;   // 相手の返答
        public bool i_initiated;          // 自分から話しかけたか
    }

    /// <summary>
    /// 会話履歴ペイロード（LLMリクエスト用）
    /// </summary>
    [Serializable]
    public class ConversationHistoryPayload
    {
        public ConversationLogEntry[] recent_conversations;
        public int total_conversation_count;
    }

    /// <summary>
    /// 会話リクエスト（話しかける側 → 話しかけられる側）
    /// </summary>
    [Serializable]
    public class ConversationRequest
    {
        public string initiator_id;       // 話しかけた人のID
        public string initiator_name;     // 話しかけた人の名前
        public string topic;              // 話題
        public string message;            // メッセージ内容
        public string initiator_action;   // 話しかけた人の現在行動
        public string initiator_target;   // 話しかけた人の目標避難所
    }

    /// <summary>
    /// 会話応答（話しかけられる側 → 話しかける側）
    /// </summary>
    [Serializable]
    public class ConversationResponse
    {
        public string responder_id;       // 返答者のID
        public string responder_name;     // 返答者の名前
        public string response_message;   // 返答内容
        public bool willing_to_share;     // 情報共有の意思
        public bool want_to_continue;     // 会話継続意思
    }

    /// <summary>
    /// LLMへの会話応答生成リクエスト
    /// </summary>
    [Serializable]
    public class LLMConversationResponseRequest
    {
        public string request_id;
        public string request_type;       // "conversation_response"
        public PersonaPayload persona;
        public EnvironmentPayload environment;
        public ConversationRequest incoming_conversation;
        public string current_action;
        public string current_target;

        // ログ用メタデータ
        public string evacuee_id;         // 応答者のID
        public string experiment_id;      // 実験ID
        public int? episode_id;           // エピソードID
        public float? episode_elapsed_time; // エピソード経過時間
    }

    /// <summary>
    /// 環境情報ペイロード（会話応答用の簡略版）
    /// </summary>
    [Serializable]
    public class EnvironmentPayload
    {
        public string scenario_id;
        public bool has_heard_broadcast;
        public string last_broadcast_message;
        public bool has_received_j_alert;
        public string last_j_alert_message;
    }

    /// <summary>
    /// LLMからの会話応答
    /// </summary>
    [Serializable]
    public class LLMConversationResponseResponse
    {
        public string request_id;
        public string response_message;   // 生成された返答
        public bool willing_to_share;     // 情報共有の意思
        public bool want_to_continue;     // 会話継続意思
        public string reasoning;          // 返答理由
    }

    /// <summary>
    /// 家族への連絡リクエスト（連絡する側 → 家族）
    /// </summary>
    [Serializable]
    public class FamilyContactRequest
    {
        public string sender_id;          // 連絡者のID
        public string sender_name;        // 連絡者の名前
        public string sender_relation;    // 連絡者の続柄（夫、妻など）
        public string message;            // メッセージ内容
        public string sender_action;      // 連絡者の現在行動
        public string sender_target;      // 連絡者の目標避難所
        public string sender_location;    // 連絡者の現在位置の説明
    }

    /// <summary>
    /// 家族からの返信（家族 → 連絡する側）
    /// </summary>
    [Serializable]
    public class FamilyContactResponse
    {
        public string responder_id;       // 返答者のID
        public string responder_name;     // 返答者の名前
        public string response_message;   // 返答内容
        public string current_status;     // 現在の状況（無事、怪我など）
        public string current_location;   // 現在位置の説明
        public string planned_action;     // 今後の行動予定
        public bool want_to_continue;     // 会話継続意思
    }

    /// <summary>
    /// LLMへの家族連絡応答生成リクエスト
    /// </summary>
    [Serializable]
    public class LLMFamilyContactResponseRequest
    {
        public string request_id;
        public string request_type;       // "family_contact_response"
        public PersonaPayload persona;    // 応答する家族のペルソナ
        public EnvironmentPayload environment;
        public FamilyContactRequest incoming_contact;
        public string current_action;
        public string current_target;
        public string family_relationship; // 家族関係の説明

        // ログ用メタデータ
        public string evacuee_id;         // 応答者のID（家族）
        public string experiment_id;      // 実験ID
        public int? episode_id;           // エピソードID
        public float? episode_elapsed_time; // エピソード経過時間
    }

    /// <summary>
    /// LLMからの家族連絡応答
    /// </summary>
    [Serializable]
    public class LLMFamilyContactResponseResponse
    {
        public string request_id;
        public string response_message;   // 生成された返答
        public string current_status;     // 現在の状況
        public string current_location;   // 現在位置
        public string planned_action;     // 今後の行動
        public bool want_to_continue;     // 会話継続意思
        public string reasoning;          // 返答理由
    }

    /// <summary>
    /// 会話セッションコンテキスト（マルチターン会話の状態管理）
    /// </summary>
    [Serializable]
    public class ConversationSessionContext
    {
        public string partner_id;                 // 会話相手のID
        public string partner_name;               // 会話相手の名前
        public int turn_count;                    // 現在のターン数
        public string current_topic;              // 現在の話題
        public bool partner_wants_to_continue;    // 相手の継続意思
        public float session_start_time;          // セッション開始時刻
        public bool is_family_contact;            // 家族連絡かどうか
    }

    /// <summary>
    /// LLMへの会話継続判断リクエスト
    /// </summary>
    [Serializable]
    public class LLMConversationContinuationRequest
    {
        public string request_id;
        public string request_type;               // "conversation_continuation"
        public PersonaPayload persona;
        public ConversationSessionContext session;
        public string partner_last_message;       // 相手の最後の発言

        // ログ用メタデータ
        public string evacuee_id;                 // 判断者のID
        public string experiment_id;              // 実験ID
        public int? episode_id;                   // エピソードID
        public float? episode_elapsed_time;       // エピソード経過時間
    }

    /// <summary>
    /// LLMからの会話継続判断レスポンス
    /// </summary>
    [Serializable]
    public class LLMConversationContinuationResponse
    {
        public string request_id;
        public bool want_to_continue;             // 会話を継続したいか
        public string message;                    // 継続なら次の発言、終了なら締めの言葉
        public string reasoning;                  // 判断理由
    }

    /// <summary>
    /// 知覚状態ペイロード（聴覚・放送情報）
    /// </summary>
    [Serializable]
    public class PerceptionStatePayload
    {
        // 聴覚情報
        public bool has_heard_rumble;          // 地鳴りを聞いたか
        public float rumble_intensity;         // 地鳴りの強度（0-1）
        public bool has_heard_siren;           // サイレンを聞いたか
        public bool has_heard_fire_truck;      // 消防団の呼びかけを聞いたか
        public string last_fire_truck_message; // 最後に聞いた消防団のメッセージ

        // 放送・Jアラート情報
        public bool has_heard_broadcast;       // 行政無線の放送を聞いたか
        public string last_broadcast_message;  // 最後に聞いた放送内容
        public bool has_received_j_alert;      // Jアラートを受信したか
        public string last_j_alert_message;    // 最後に受信したJアラート内容
    }

    /// <summary>
    /// 環境状態ペイロード（災害フェーズ・停電等）
    /// </summary>
    [Serializable]
    public class EnvironmentStatePayload
    {
        public string disaster_phase;          // 災害フェーズ（enum名）
        public string disaster_phase_display;  // 災害フェーズの日本語表示名
        public int seismic_intensity;          // 現在の震度
        public float tsunami_height;           // 津波予想高さ（m）
        public bool is_power_on;               // 停電状態
        public bool is_radio_working;          // 防災無線動作状態
        public float rumble_intensity;         // 現在の地鳴り強度（0-1）
        public bool siren_active;              // サイレン鳴動中
    }

    /// <summary>
    /// 長期目標（Long-term Goal）- 避難所到達、家族合流、安全確保などの長期目標
    /// </summary>
    [Serializable]
    public class LongTermGoalPayload
    {
        public string primary_goal;        // 主要目標（例: "家族全員で○○避難所に到達する"）
        public string[] secondary_goals;    // 副次目標（例: ["途中で怪我をしない", "できるだけ早く到達する"]）
        public string[] constraints;       // 制約条件（例: ["高齢の母を連れているため、階段の多い経路は避ける"]）
    }

    /// <summary>
    /// 中期計画（Mid-term Plan）- 長期目標を達成するための具体的な手順
    /// </summary>
    [Serializable]
    public class MidTermPlanPayload
    {
        public string[] steps;             // 手順のリスト（例: ["まず自宅から○○小学校に移動し、子供を迎える", "次に○○交差点を経由して△△避難所に向かう"]）
        public string contingency;         // 緊急時の代替案（例: "もし津波が早く来そうなら、子供を迎えずに最寄りの高台に避難する"）
    }

    [Serializable]
    public class LLMEvacDecisionRequest
    {
        public string request_id;
        public string experiment_id;                     // 実験セッションID（ログ出力先ディレクトリ用）
        public int episode_id;                           // エピソード番号
        public float episode_elapsed_time;               // エピソード内経過時間（秒）
        public float timestamp;
        public EvacueePayload evacuee;
        public ShelterCandidatePayload[] shelter_candidates;
        public SelfStatePayload self_state;
        public TemporalContextPayload temporal_context;
        public PersonaPayload persona;                 // ペルソナ情報
        public EnvironmentalContextPayload environmental_context; // 環境コンテキスト
        public FamilyMemberPayload[] family_members;   // 家族情報
        public NearbyEvacueePayload[] nearby_evacuees; // 周辺の避難者情報（FOLLOW行動用、最大5人）
        public int nearby_evacuees_count;              // 周辺避難者の総数（検出範囲内の全人数）
        public float nearby_evacuees_density;          // 周辺避難者の混雑度（人数/100m²）
        public string last_contact_message;            // 直近の家族からの返信内容
        public string scenario_id;                     // 災害シナリオID（例: "shindo_2"）
        public bool has_heard_broadcast;               // 行政無線の放送を聞いたか
        public string last_broadcast_message;          // 最後に聞いた放送内容
        public bool has_received_j_alert;              // Jアラートを受信したか
        public string last_j_alert_message;           // 最後に受信したJアラート内容
        public LongTermGoalPayload current_long_term_goal;  // 現在の長期目標（更新判断用）
        public MidTermPlanPayload current_mid_term_plan;   // 現在の中期計画（更新判断用）
        public ConversationHistoryPayload conversation_history;  // 会話履歴（TALK行動用）
        public string[] available_actions;                // 利用可能なアクション一覧（条件に基づいて動的に設定）

        // 災害シナリオシステム用の拡張フィールド
        public PerceptionStatePayload perception_state;    // 知覚状態（聴覚情報等）
        public EnvironmentStatePayload environment_state;  // 環境状態（災害フェーズ等）

        // 短期記憶（行動履歴）
        public ActionHistoryPayload action_history;        // 行動履歴（圧縮・要約用）

        // 交通シミュレーション用
        public string transport_mode;                      // 移動手段: "WALKING", "DRIVING"
        public NearbyTrafficPayload nearby_traffic;        // 周辺の交通状況

        // 実験2-2: 認知バイアス条件
        public string bias_condition;                      // バイアス条件: "none", "normalcy_bias", "conformity_bias", "combined"
        public bool override_persona_bias;                 // ペルソナのmental_stateをオーバーライドするか

        // 実験3: 情報提供戦略
        public string information_strategy;                // 情報提供戦略: "standard", "urgent", "detailed", "detailed_urgent"
        public AdministrativeGuidancePayload administrative_guidance;  // 行政からの追加情報
    }

    [Serializable]
    public class LLMEvacDecisionResponse
    {
        public string request_id;
        public string evacuee_id;
        public string action_type;           // 行動タイプ: "EVACUATE", "STAY", "SEARCH_FAMILY", "CONTACT", "FOLLOW", "TALK"
        public string selected_shelter_id;   // action_type="EVACUATE" の場合に使用

        // SEARCH_FAMILY 用
        public string target_family_member;  // 探索対象の家族名または続柄
        public Vector3Payload target_location; // 目標位置（任意）

        // CONTACT 用
        public string contact_target;        // 連絡先の家族名または続柄
        public string contact_message;       // 家族からの返信内容（あるいはその想定）

        // FOLLOW 用
        public string target_evacuee_id;     // 追従対象の避難者ID

        // TALK 用
        public string talk_target_id;        // 話しかける相手のID
        public string talk_topic;            // 会話の話題（ShelterInfo, CurrentSituation, ActionAdvice, SafetyConfirm, General）
        public string talk_message;          // 話しかける内容

        // 階層的意思決定用
        public LongTermGoalPayload long_term_goal;       // 長期目標（オプショナル、更新時のみ）
        public MidTermPlanPayload mid_term_plan;         // 中期計画（オプショナル、更新時のみ）
        public bool should_update_goal;                  // 長期目標を更新すべきか（LLMが判断）
        public bool should_update_plan;                  // 中期計画を更新すべきか（LLMが判断）

        public string reasoning;
        public float confidence;
        public string desired_speed;  // 速度選択肢: "SLOW"/"NORMAL"/"FAST"/"RUN" または日本語

        // 交通シミュレーション用
        public string recommended_transport_mode;         // 推奨移動手段: "WALKING"/"DRIVING"/"ABANDON_VEHICLE"
        public string recommended_route_change;           // 推奨ルート変更理由

        // 短期記憶（行動履歴要約）
        public string summarized_action_history;          // サーバーで生成された行動履歴の要約

        // エラー情報（LLM API呼び出し失敗時）
        public LLMErrorInfo llm_error;                    // エラー情報（正常時はnull）
    }

    [Serializable]
    internal class ResponseEnvelope
    {
        public string request_id;
    }

    /// <summary>
    /// LLMサーバーからのエラー情報（レートリミット等）
    /// </summary>
    [Serializable]
    public class LLMErrorInfo
    {
        public string error_type;              // エラータイプ: "RateLimitError", "APIError", "UnexpectedError"
        public string error_message;           // エラーメッセージ全文
        public string rate_limit_type;         // レートリミット種類: "RPM", "TPM", "RPD", "UNKNOWN" (RateLimitErrorの場合のみ)
        public string rate_limit_description;  // レートリミットの日本語説明
    }

    /// <summary>
    /// 行政からの追加情報ペイロード（実験3: 情報提供戦略用）
    /// </summary>
    [Serializable]
    public class AdministrativeGuidancePayload
    {
        public string information_strategy;           // 情報提供戦略: "standard", "urgent", "detailed", "detailed_urgent"
        public float initial_tsunami_height;          // 当初の津波予想高さ（m）
        public float updated_tsunami_height;          // 更新後の津波予想高さ（m）
        public bool exceeds_assumption;               // 想定を超えているか
        public string urgency_level;                  // 緊急度: "normal", "high", "critical"
        public string evacuation_guidance;            // 避難先に関するガイダンス
        public string[] recommended_shelters;         // 推奨避難所リスト（海抜情報付き）
        public string[] unsafe_shelters;              // 高さが不十分な可能性のある避難所
        public string additional_warning;             // 追加の警告メッセージ
    }

    /// <summary>
    /// 道路ごとの交通状況データ（LLMリクエスト用）
    /// </summary>
    [Serializable]
    public class RoadTrafficPayload
    {
        public string edge_id;                // 道路エッジID
        public string road_name;              // 道路名
        public int congestion_level;          // 渋滞レベル (1-5)
        public string congestion_label;       // 渋滞レベルの日本語ラベル（"順調"/"やや混雑"/"混雑"/"渋滞"/"大渋滞"）
        public float average_speed_kmh;       // 平均速度 (km/h)
        public float estimated_travel_time;   // 推定走行時間（秒）
        public float free_flow_travel_time;   // 自由流走行時間（秒）
        public float distance_meters;         // この位置からの距離
    }

    /// <summary>
    /// 周辺の交通状況ペイロード
    /// </summary>
    [Serializable]
    public class NearbyTrafficPayload
    {
        public RoadTrafficPayload[] nearby_roads; // 周辺道路の交通状況
        public float overall_congestion;          // 全体的な渋滞度 (0-1)
        public string summary;                    // 自然言語での要約
    }

    /// <summary>
    /// 行動履歴エントリ（短期記憶用）
    /// </summary>
    [Serializable]
    public class ActionHistoryEntry
    {
        public float timestamp;           // 行動時刻（シミュレーション内秒数）
        public string action_type;        // 行動タイプ（EVACUATE, STAY, etc.）
        public string target;             // 対象（避難所名、家族名など）
        public string reasoning;          // LLMの判断理由
        public string result;             // 結果（completed, failed, interrupted）
    }

    /// <summary>
    /// 行動履歴ペイロード（LLMリクエスト用）
    /// </summary>
    [Serializable]
    public class ActionHistoryPayload
    {
        public ActionHistoryEntry[] recent_actions;   // 直近の行動（最大5件）
        public string summarized_history;             // 要約済み過去履歴
        public int total_action_count;                // 総行動回数
    }
}
