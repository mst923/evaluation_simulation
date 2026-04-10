# 交通ネットワーク仕様書

## 1. 概要

本ドキュメントは、避難シミュレーションにおける交通ネットワーク（車両シミュレーション）の仕様を定義する。

OSMの道路ネットワークとSUMO交通シミュレータを統合し、車両の追従モデル・車線変更・渋滞伝播を含むリアルな交通流を再現する。

### 1.1 目的

2011年東日本大震災では車両避難による渋滞が大きな被害要因となった。歩行者（NavMesh）のみの既存シミュレーションに車両交通を追加し、車両＋歩行者の統合的な避難シミュレーションを実現する。

### 1.2 関連ファイル一覧

#### Python側（`traffic_server/`）

| ファイル | 役割 |
|---------|------|
| `traffic_server/pyproject.toml` | 依存パッケージ定義 |
| `traffic_server/coordinate_transformer.py` | WGS84→JGD2011→PLATEAU Unity座標変換 |
| `traffic_server/osm_network_builder.py` | OSMから道路ネットワーク抽出・GeoJSON出力 |
| `traffic_server/sumo_config_generator.py` | OSM→SUMO設定ファイル生成 |
| `traffic_server/server.py` | WebSocketサーバー（SUMO TraCIブリッジ） |
| `traffic_server/models.py` | Pydanticデータモデル |

#### Unity側（`Assets/Scripts/Traffic/`）

| ファイル | 役割 |
|---------|------|
| `RoadNetwork.cs` | 道路ネットワークのデータ構造（ノード・エッジ・グラフ探索） |
| `RoadNetworkLoader.cs` | GeoJSONからネットワーク読み込み |
| `RoadNetworkVisualizer.cs` | Gizmoでのネットワーク描画 |
| `TrafficClient.cs` | traffic_serverとのWebSocket通信 |
| `TrafficServerManager.cs` | サーバープロセスの自動起動・停止 |
| `VehicleController.cs` | 個別車両のGameObject制御 |
| `VehiclePoolManager.cs` | 車両オブジェクトプール（500台規模） |
| `TrafficMessages.cs` | JSON通信メッセージ定義 |
| `TrafficStateProvider.cs` | リアルタイム渋滞データの取得・提供 |
| `TransportModeDecider.cs` | ペルソナ属性に基づく移動モード判定 |
| `TrafficHeatmapVisualizer.cs` | 渋滞ヒートマップの可視化 |

---

## 2. アーキテクチャ

```
┌─────────────────────────────────────────────────────────────────┐
│                        Unity (C#)                                │
│                                                                  │
│  Evacuee (歩行者)  ←→  NavMeshAgent (既存)                      │
│  Evacuee (車両)    ←→  VehicleController (新規)                 │
│       ↕                        ↕                                 │
│  LLMDecisionClient        TrafficClient                          │
│       ↕                        ↕                                 │
│  LLMServerManager         TrafficServerManager                   │
└───────┼────────────────────────┼────────────────────────────────┘
        │ WebSocket :8765        │ WebSocket :8766
        ↓                        ↓
┌───────────────┐    ┌────────────────────────────────────────────┐
│ llm_server/   │    │ traffic_server/                             │
│ server.py     │    │                                             │
│ (LLM意思決定) │    │  server.py  ←→  SUMO (TraCI)               │
│               │    │  osm_network_builder.py                     │
│               │    │  coordinate_transformer.py                  │
└───────────────┘    └────────────────────────────────────────────┘
```

---

## 3. 道路ネットワークデータ

### 3.1 データソース

OSM (OpenStreetMap) → osmnx で抽出

### 3.2 ノード（交差点）

| フィールド | 型 | 説明 |
|-----------|-----|------|
| id | long | OSMノードID |
| position | Vector3 | Unity座標 |
| lon, lat | float | WGS84座標（デバッグ用） |
| connectedEdgeIds | List\<string\> | 接続エッジIDリスト |

### 3.3 エッジ（道路区間）

| フィールド | 型 | 説明 |
|-----------|-----|------|
| id | string | "sourceNode-targetNode-key" |
| sourceNodeId | long | 始点ノードID |
| targetNodeId | long | 終点ノードID |
| lengthMeters | float | 道路長（メートル） |
| lanes | int | 車線数 |
| speedLimitKmh | float | 制限速度（km/h） |
| oneway | bool | 一方通行フラグ |
| highwayType | string | 道路種別（motorway, primary, residential等） |
| roadName | string | 道路名 |
| geometry | List\<Vector3\> | 線形のポイント列 |

### 3.4 座標変換パイプライン

```
WGS84 (EPSG:4326)
  ↓ pyproj
JGD2011 平面直角座標系 第9系 (EPSG:6677)
  ↓ PLATEAUオフセット適用
PLATEAU Unity座標 (X=東, Y=上, Z=北)
```

PLATEAUの変換式: `Unity座標 = JGD2011平面直角座標 - referencePoint`

| パラメータ | 値 | 説明 |
|-----------|-----|------|
| referencePoint.x | 105341.68 | namie.unityシーンのPLATEAU X オフセット |
| referencePoint.y | 0.0 | 高さオフセット |
| referencePoint.z | 168959.62 | namie.unityシーンのPLATEAU Z オフセット |
| EPSG | 6677 | JGD2011 平面直角座標系 第9系 |

### 3.5 対象エリアとファイルパス

| 項目 | 値 |
|------|-----|
| 対象エリア | 浪江町周辺 (37.50°N-37.54°N, 140.995°E-141.055°E) |
| GeoJSON出力先 | `Assets/Config/iwaki_road_network.geojson` |
| SUMO設定ディレクトリ | `traffic_server/sumo_data/` |
| SUMO設定ファイル | `traffic_server/sumo_data/simulation.sumocfg` |

---

## 4. WebSocket通信プロトコル

### 4.1 メッセージ種別

| 方向 | メッセージ | 説明 |
|------|-----------|------|
| → | `step` | シミュレーション1ステップ進行 |
| → | `spawn_vehicle` | 車両スポーン |
| → | `remove_vehicle` | 車両削除 |
| → | `set_route` | ルート変更 |
| → | `get_state` | 全車両状態取得 |
| → | `get_traffic` | エッジ交通状態取得 |
| ← | `vehicle_updates` | 車両位置更新 |
| ← | `traffic_state` | 渋滞データ |
| ← | `error` | エラー |

### 4.2 レスポンス例（vehicle_updates）

```json
{
  "message_type": "vehicle_updates",
  "request_id": "traffic-000001",
  "vehicle_states": [
    {
      "vehicle_id": "veh_1",
      "position": { "x": 180.2, "y": 0.7, "z": 244.2 },
      "speed": 8.3,
      "angle": 45.0,
      "edge_id": "12345-67890-0",
      "is_stopped": false
    }
  ],
  "sim_time": 120.5
}
```

---

## 5. 渋滞モデル

### 5.1 渋滞レベル（BPR関数ベース）

travel_time / free_flow_travel_time の比率で分類:

| レベル | ラベル | 比率 |
|--------|--------|------|
| 1 | 順調 | < 1.2 |
| 2 | やや混雑 | 1.2 - 1.5 |
| 3 | 混雑 | 1.5 - 2.0 |
| 4 | 渋滞 | 2.0 - 3.0 |
| 5 | 大渋滞 | > 3.0 |

### 5.2 LLMへの渋滞情報提供

渋滞情報はLLMプロンプトの【周辺の交通状況】セクションに自然言語で含まれる:

```
【周辺の交通状況】
国道6号は渋滞（平均15km/h、通常の2.8倍）。
県道382号は混雑（平均25km/h、通常の1.7倍）。

※ 渋滞がひどい場合は、車を降りて徒歩で避難することも検討してください。
```

---

## 6. 移動モード

### 6.1 TransportMode

| モード | 説明 | 移動手段 |
|--------|------|----------|
| WALKING | 徒歩 | NavMeshAgent |
| DRIVING | 車両 | SUMO → VehicleController |

### 6.2 移動モードの決定ロジック

`TransportModeDecider.Decide()` が以下の順序で判定:
1. 交通シミュレーション無効 → WALKING
2. 車両未所有 or 運転不可 → WALKING
3. 身体的に運転困難 → WALKING
4. VehicleUsageRate に基づく確率的判定

### 6.3 車両放棄（Vehicle Abandonment）

渋滞がひどい場合、車両を放棄して徒歩に切り替えることが可能。
`ShouldAbandonVehicle()` で判定:
- 大渋滞（レベル5）かつ残り距離 < 500m → 放棄推奨
- 渋滞（レベル4以上）かつ徒歩所要時間 < 10分 → 放棄推奨

---

## 7. ペルソナ属性（交通関連）

`personas.csv` に以下のカラムを追加:

| カラム | 型 | 説明 | デフォルト |
|--------|-----|------|-----------|
| has_vehicle | bool | 車両を所有しているか | false |
| can_drive | bool | 運転可能か（免許・身体） | true |

---

## 8. 実験パラメータ

`ExperimentConfig` に以下のフィールドを追加:

| フィールド | 型 | 説明 | デフォルト |
|-----------|-----|------|-----------|
| VehicleUsageRate | float (0-1) | 車両利用率 | 0.6 |
| EnableTrafficSimulation | bool | 交通シミュレーション有効化 | false |

---

---

## 9. Evacuee 連携フロー（実装配線）

Phase A〜C で `Assets/Scripts/Evacuee.cs` に追加した車両避難者ロジックの実装ガイド。

### 9.1 初期化（SetEvacueeId → InitializeTransportMode）

ペルソナ読み込み直後に `Traffic.TransportModeDecider.Decide()` を呼び、
`ExperimentConfig.VehicleUsageRate` と `EnableTrafficSimulation` に基づいて `CurrentTransportMode` を決定する。
DRIVING と判定された場合は `_pendingDriveStart = true` を立てて保留し、Target（避難先）確定後に実スポーンする。

### 9.2 車両スポーン（TryBeginPendingDrive）

`Update()` 内で `_pendingDriveStart` が立っていれば毎フレーム試行。
以下の条件が揃った時点でスポーン:

- `Target != null`
- `TrafficClient.Instance.IsConnected == true`
- `RoadNetworkLoader.Instance.IsLoaded == true`

`RoadNetwork.GetNearestEdge()` で出発/到着ポジションから最寄りの SUMO edge ID を解決し、
既存の `SwitchToDriving(originEdge, destEdge)` を呼ぶ。エッジ解決失敗時は WALKING にフォールバック。

### 9.3 車両位置同期（SyncPositionWithVehicle）

`VehiclePoolManager` は SUMO の vehicle_updates から**別 GameObject** を生成するため、
Evacuee 本体をそのまま放置すると駐車位置に取り残される。
`Update()` で `IsDriving` の間、`VehiclePoolManager.GetVehicle(SumoVehicleId)` から
対応する `VehicleController` を引き、その transform.position/rotation を Evacuee 本体にコピーする。
初回解決時に `VehicleController.OwnerEvacueeId` も書き込む。

### 9.4 渋滞による車両放棄（EvaluateAbandonVehicle）

`Update()` 内、`IsDriving` かつ `ABANDON_CHECK_INTERVAL_SEC = 5秒` 間隔でスロットル評価。
`TrafficStateProvider.GetTrafficConditions(pos, 100f)` で現在位置に最も近いエッジの渋滞レベルを取得し、
`TransportModeDecider.ShouldAbandonVehicle(level, remainingDist, walkMin)` が true なら `SwitchToWalking()`。

### 9.5 目的地変更時の再スポーン（CheckDrivingTargetChange）

`_lastDrivenTarget` で DRIVING 中の Target 参照を監視。
Target が差し替わった場合は SUMO TraCI の `setRoute` が完全エッジパスを要するため、
**削除＋再スポーン**方式で対応:

1. `TrafficClient.RemoveVehicleAsync(SumoVehicleId)`
2. `VehiclePoolManager.ReturnToPool()`
3. `_pendingDriveStart = true` を立て次 tick で新 Target へ再スポーン

### 9.6 LLM 意思決定への統合

#### リクエスト側（BuildDecisionRequest）

`LLMEvacDecisionRequest` に以下を投入:

- `transport_mode`: `"DRIVING"` / `"WALKING"`
- `nearby_traffic` (NearbyTrafficPayload): `BuildNearbyTrafficPayload()` が `EnvironmentalContextProvider.GetTrafficConditions/GetOverallCongestion/GetTrafficSummary` を呼び、上位5件の道路データ + 全体渋滞度 + 自然言語要約を詰める。交通シミュ無効時は null。

#### レスポンス側（ApplyRecommendedTransportMode）

`LLMEvacDecisionResponse.recommended_transport_mode` を `ApplyLLMDecision` 末尾でハンドル:

- `"ABANDON_VEHICLE"` / `"WALKING"` → 走行中なら `SwitchToWalking()`
- `"DRIVING"` → 徒歩中かつ `HasVehicle && CanDrive && !_hasAbandonedVehicle` なら `_pendingDriveStart = true`

### 9.7 実装フロー図

```
┌───────────────────────────────────────────────────────────────┐
│  SetEvacueeId                                                 │
│    ↓                                                           │
│  InitializeTransportMode → DRIVING判定 → _pendingDriveStart=true │
│    ↓                                                           │
│  (Target確定)                                                   │
│    ↓                                                           │
│  Update() → TryBeginPendingDrive                              │
│    ├─ GetNearestEdge(pos) → originEdge                        │
│    ├─ GetNearestEdge(Target.pos) → destEdge                   │
│    └─ SwitchToDriving(origin, dest) → SUMO spawn              │
│    ↓                                                           │
│  Update() [IsDriving時 毎フレーム]                               │
│    ├─ SyncPositionWithVehicle  … VehicleControllerから位置コピー  │
│    ├─ EvaluateAbandonVehicle (5s間隔) … 渋滞判定→放棄             │
│    └─ CheckDrivingTargetChange … Target変更→削除+再スポーン       │
│    ↓                                                           │
│  BuildDecisionRequest                                         │
│    └─ transport_mode + nearby_traffic を投入                    │
│    ↓                                                           │
│  ApplyLLMDecision                                             │
│    └─ ApplyRecommendedTransportMode                           │
│        ├─ ABANDON_VEHICLE → SwitchToWalking                   │
│        └─ DRIVING → _pendingDriveStart=true                    │
└───────────────────────────────────────────────────────────────┘
```

### 9.8 未対応（Phase C+ 残課題）

- **SUMO 不在時の Unity 側フォールバック**: 現状 EnableTrafficSimulation=true で SUMO 未接続の場合、車両スポーンは失敗するが fallback はない
- **車両プレハブの見た目**: `VehiclePoolManager.vehiclePrefab` 未設定時はランタイムで青い Cube を生成する既定実装
- **TrafficStateProvider と EnvironmentalContextProvider の統合**: 現在は EnvContext が Provider へ委譲する薄いラッパー。必要に応じ統合を検討
- **setRoute による動的リルート**: SUMO TraCI の `setRoute` は完全エッジパスを要するため、経路探索結果を用いた真のリルートは未実装（現状は削除+再スポーンで代替）

---

## 更新履歴

| 日付 | 内容 |
|------|------|
| 2026-03-05 | 初版作成（現状分析） |
| 2026-03-24 | SUMO統合アーキテクチャに全面改訂 |
| 2026-03-24 | PLATEAUオフセット修正（浪江町）、対象エリア・ファイルパス情報追加 |
| 2026-04-07 | §9 Evacuee連携フロー追加（Phase A/B/C 実装配線: モード決定→スポーン→位置同期→放棄→LLM統合） |
