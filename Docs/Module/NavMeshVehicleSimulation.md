# NavMeshベース車両シミュレーション仕様書

## 1. 概要

本ドキュメントは、SUMOベースの車両シミュレーションをNavMeshAgentベースに置き換える設計を定義する。

### 1.1 背景と動機

従来のSUMO統合アーキテクチャでは以下の課題があった：

1. **外部依存**: Python WebSocketサーバー + SUMO TraCIの起動・管理が必要
2. **座標変換の複雑さ**: Unity → JGD2011 → WGS84 → SUMOの多段変換
3. **2D制約**: SUMOは2D平面のみ。PLATEAUの3D地形との統合が不完全
4. **モード切替の非連続性**: 歩行（NavMesh）↔ 車両（SUMO）が別システムのため切替時にWarpが必要

### 1.2 設計目標

- 歩行者と車両を**同一のNavMeshシステム**上で統合
- 車両が**渋滞する**挙動を再現（RVO回避の無効化 + 物理ブロック）
- LLMへの渋滞情報提供パイプラインを維持
- 外部サーバー依存を排除し、Unity内で完結

### 1.3 関連ファイル一覧

#### 新規作成

| ファイル | 役割 |
|---------|------|
| `Assets/Scripts/Traffic/NavMeshVehicleAgent.cs` | NavMeshAgentベースの車両コントローラー |
| `Assets/Scripts/Traffic/NavMeshTrafficCalculator.cs` | エッジごとの渋滞レベル計算（SUMO BPR関数の代替） |

#### 変更

| ファイル | 変更内容 |
|---------|---------|
| `Assets/Scripts/Traffic/VehiclePoolManager.cs` | VehicleController → NavMeshVehicleAgent に置換 |
| `Assets/Scripts/Traffic/TrafficStateProvider.cs` | TrafficClient → NavMeshTrafficCalculator に配線変更 |
| `Assets/Scripts/Traffic/TrafficHeatmapVisualizer.cs` | 同上 |
| `Assets/Scripts/Traffic/TrafficMessages.cs` | SUMO通信用クラス削除、データ構造のみ保持 |
| `Assets/Scripts/Evacuee/Evacuee.cs` | 運転関連メソッド6箇所の改修 |

#### 変更なし（再利用）

| ファイル | 理由 |
|---------|------|
| `RoadNetwork.cs` | 空間インデックス(100mグリッド)を渋滞計算に使用 |
| `RoadNetworkLoader.cs` | GeoJSON道路データ読込 |
| `RoadNetworkVisualizer.cs` | 道路メッシュ描画 |
| `TransportModeDecider.cs` | WALKING/DRIVING判定・車両放棄閾値 |
| `EnvironmentalContextProvider.cs` | TrafficStateProviderへの委譲 |
| `LLMActionMessages.cs` | NearbyTrafficPayload等の構造体 |

#### 削除

| ファイル | 理由 |
|---------|------|
| `Assets/Scripts/Traffic/TrafficClient.cs` | SUMO WebSocketクライアント |
| `Assets/Scripts/Traffic/TrafficServerManager.cs` | SUMOサーバープロセス管理 |
| `Assets/Scripts/Traffic/VehicleController.cs` | SUMO位置適用コントローラー |
| `traffic_server/` | Python SUMOサーバー全体 |

---

## 2. アーキテクチャ

### 2.1 全体構成

```
┌─────────────────────────────────────────────────────┐
│                    Unity (NavMesh)                    │
│                                                       │
│  ┌──────────┐    ┌──────────────────┐                │
│  │ Evacuee  │    │ NavMeshVehicle   │                │
│  │(Walking) │    │    Agent         │                │
│  │NavAgent  │    │  NavAgent        │                │
│  │radius=0.5│    │  radius=2.0      │                │
│  │RVO=High  │    │  RVO=None        │                │
│  │          │    │  +BoxCollider    │                │
│  └──────────┘    └────────┬─────────┘                │
│       │                    │                          │
│       │     position sync  │                          │
│       ├────────────────────┘                          │
│       ▼                                               │
│  ┌──────────────────────┐                             │
│  │  VehiclePoolManager  │ ← プール管理                │
│  └──────────┬───────────┘                             │
│             │ ActiveVehicles                          │
│             ▼                                         │
│  ┌──────────────────────────┐                         │
│  │ NavMeshTrafficCalculator │                         │
│  │  エッジごと密度計算       │                         │
│  │  渋滞レベル 1-5          │                         │
│  └──────────┬───────────────┘                         │
│             │ OnTrafficStateUpdated                   │
│             ▼                                         │
│  ┌──────────────────────────┐                         │
│  │  TrafficStateProvider    │ ← インターフェース不変   │
│  └──────────┬───────────────┘                         │
│             │                                         │
│      ┌──────┴──────┐                                  │
│      ▼             ▼                                  │
│  ┌────────┐  ┌──────────┐                             │
│  │Heatmap │  │   LLM    │                             │
│  │Viz     │  │Pipeline  │                             │
│  └────────┘  └──────────┘                             │
└─────────────────────────────────────────────────────┘
```

### 2.2 車両の渋滞メカニズム

従来のNavMeshAgentはRVO（Reciprocal Velocity Obstacles）により他エージェントを回避する。車両シミュレーションでは**回避せず詰まる**必要がある。

```
歩行者エージェント              車両エージェント
├── obstacleAvoidance: High    ├── obstacleAvoidance: None
├── 他エージェントを避ける      ├── 他エージェントを避けない
├── すり抜けて進む              ├── 物理Colliderでブロックされる
└── 自由に移動                 └── 前が詰まれば停止 → 渋滞
```

**ブロック方式:**
- 各車両に `BoxCollider`（isTrigger=false）+ `Rigidbody`（isKinematic=true）
- NavMeshAgentはRVO=Noneのため回避しない
- 物理エンジンのCollider衝突でブロック
- フォールバック: `NavMeshObstacle`（carve=true）を停車時のみ有効化

### 2.3 Single NavMesh方式

既存NavMeshはPLATEAUの道路面（`tran_*`）上にBakeされている。歩行者と車両で同一のNavMeshを共有し、**エージェント設定の差異**で挙動を分ける。

| パラメータ | 歩行者 | 車両 |
|-----------|--------|------|
| `speed` | 3.0 m/s × persona倍率 | 11.1 m/s（40km/h） |
| `radius` | 0.5 | 2.0 |
| `obstacleAvoidanceType` | HighQuality | None |
| `acceleration` | 8.0（デフォルト） | 3.0 |
| `angularSpeed` | 120（デフォルト） | 120 |
| `autoBraking` | false | true |
| Collider | なし（NavMesh RVOのみ） | BoxCollider + kinematic Rigidbody |

---

## 3. コンポーネント詳細

### 3.1 NavMeshVehicleAgent

車両1台に対応するMonoBehaviourコンポーネント。

```
NavMeshVehicleAgent
├── NavMeshAgent（自動付与）
│   ├── speed: 道路speedLimit / 3.6
│   ├── radius: 2.0
│   ├── obstacleAvoidanceType: None
│   ├── acceleration: 3.0
│   └── autoBraking: true
├── BoxCollider（4m × 2m × 8m, isTrigger=false）
├── Rigidbody（isKinematic=true）
│
├── プロパティ
│   ├── VehicleId: string
│   ├── OwnerEvacueeId: string
│   ├── CurrentEdgeId: string（定期更新）
│   ├── CurrentSpeed: float（velocity.magnitude）
│   └── IsStopped: bool（speed < 0.1）
│
├── メソッド
│   ├── Activate(vehicleId, origin, destination)
│   │   ├── NavMesh.SamplePositionでoriginをNavMesh上に補正
│   │   ├── agent.Warp(position)
│   │   └── agent.SetDestination(destination)
│   ├── UpdateDestination(destination)
│   └── Deactivate()
│       ├── agent.ResetPath()
│       ├── agent.enabled = false
│       └── gameObject.SetActive(false)
│
└── Update（毎フレーム）
    └── CurrentEdgeIdの更新（1秒間隔でGetNearestEdge）
```

### 3.2 NavMeshTrafficCalculator

全アクティブ車両を走査し、エッジごとの渋滞データを算出するシングルトン。

```
NavMeshTrafficCalculator（Singleton）
├── 設定
│   ├── updateInterval: 1.0秒
│   └── edgeSearchRadius: 30f
│
├── イベント
│   └── OnTrafficStateUpdated: Action<Dictionary<string, EdgeTrafficUpdate>>
│
├── 計算フロー（1秒ごと）
│   ├── 1. VehiclePoolManager.ActiveVehiclesを取得
│   ├── 2. 各車両のCurrentEdgeIdで集計
│   │   ├── vehicleCount（エッジ上の車両数）
│   │   └── speedSum（速度の合計）
│   ├── 3. エッジごとの指標計算
│   │   ├── averageSpeed = speedSum / vehicleCount
│   │   ├── density = vehicleCount / edge.EstimatedCapacity
│   │   ├── freeFlowTravelTime = edge.length / (speedLimit / 3.6)
│   │   ├── travelTime = freeFlowTravelTime × (1 + 0.15 × density^4)  ← BPR関数
│   │   └── congestionLevel = DensityToLevel(density)
│   └── 4. OnTrafficStateUpdated発火
│
└── 渋滞レベル判定
    ├── density < 0.3 → Level 1（順調）
    ├── density < 0.5 → Level 2（やや混雑）
    ├── density < 0.7 → Level 3（混雑）
    ├── density < 0.9 → Level 4（渋滞）
    └── density >= 0.9 → Level 5（大渋滞）
```

### 3.3 VehiclePoolManager（改修）

変更点:
- `VehicleController` → `NavMeshVehicleAgent` に型変更
- TrafficClientイベント購読を削除
- 新しいパブリックAPI:

```
VehiclePoolManager（Singleton）
├── SpawnVehicle(vehicleId, origin, destination) → NavMeshVehicleAgent
│   ├── プールからGet
│   ├── NavMeshVehicleAgent.Activate(vehicleId, origin, destination)
│   └── _activeVehiclesに登録
├── ReturnToPool(vehicleId)
│   ├── NavMeshVehicleAgent.Deactivate()
│   └── プールに返却
├── GetVehicle(vehicleId) → NavMeshVehicleAgent
├── ActiveVehicles → IReadOnlyDictionary<string, NavMeshVehicleAgent>
└── ReturnAllToPool()
```

---

## 4. モード切替フロー

### 4.1 歩行 → 車両

```
Evacuee.SwitchToDriving(destination)
├── NavAgent.isStopped = true
├── NavAgent.enabled = false
├── vehicleId = $"veh_{uniqueId}"
├── vehicle = VehiclePoolManager.SpawnVehicle(vehicleId, transform.position, destination)
├── vehicle.OwnerEvacueeId = uniqueId
├── _vehicleAgent = vehicle
└── CurrentTransportMode = DRIVING
```

### 4.2 車両 → 歩行（車両放棄）

```
Evacuee.SwitchToWalking()
├── VehiclePoolManager.ReturnToPool(_vehicleAgentId)
├── _vehicleAgent = null
├── _hasAbandonedVehicle = true
├── NavAgent.enabled = true
├── NavMesh.SamplePosition → NavAgent.Warp（NavMesh上に復帰）
├── CurrentTransportMode = WALKING
└── NavAgent.speed = DefaultSpeed × persona倍率
```

### 4.3 車両放棄判定

```
EvaluateAbandonVehicle()（5秒ごと）
├── conditions = TrafficStateProvider.GetTrafficConditions(position, 200f)
├── maxCongestion = conditions.Max(c => c.congestionLevel)
├── walkingTime = CalculateNavMeshDistance(position, Target) / walkingSpeed
├── if TransportModeDecider.ShouldAbandonVehicle(maxCongestion, remainingDist, walkingTime)
│   └── SwitchToWalking()
└── ※ TrafficStateProviderのインターフェースは不変
```

### 4.4 目的地変更時

```
CheckDrivingTargetChange()
├── if Target changed while DRIVING
│   └── _vehicleAgent.UpdateDestination(Target.transform.position)
│       ← 従来: SUMO vehicle remove → 再spawn
│       → 新: NavMeshAgent.SetDestination()を呼ぶだけ
```

---

## 5. LLMパイプライン（変更なし）

渋滞データのLLMへの提供パイプラインは完全に維持される。

```
NavMeshTrafficCalculator
  ↓ OnTrafficStateUpdated（EdgeTrafficUpdate形式）
TrafficStateProvider._edgeTrafficStates
  ↓ GetTrafficConditions(position, radius)
EnvironmentalContextProvider
  ↓ GetTrafficConditions() / GetOverallCongestion() / GetTrafficSummary()
Evacuee.BuildNearbyTrafficPayload()
  ↓ NearbyTrafficPayload（top5道路、全体渋滞度、自然言語サマリー）
LLMEvacDecisionRequest.nearby_traffic
  ↓
LLM → recommended_transport_mode: "ABANDON_VEHICLE" / "WALKING" / "DRIVING"
```

データ構造（不変）:
- `EdgeTrafficUpdate`: edge_id, vehicle_count, average_speed, congestion_level, travel_time, free_flow_travel_time
- `NearbyTrafficPayload`: nearby_roads[], overall_congestion, summary
- `RoadTrafficPayload`: edge_id, road_name, congestion_level, congestion_label, average_speed_kmh, etc.

---

## 6. 渋滞レベルの計算方式

### 6.1 従来（SUMO）

```
ratio = actual_travel_time / free_flow_travel_time
→ ratio閾値で5段階に分類
```

### 6.2 新方式（NavMesh密度ベース）

```
density = edge上の車両数 / edge.EstimatedCapacity
EstimatedCapacity = floor(edge.lengthMeters × edge.lanes / 7.5)
  ※ 7.5m = 車両1台あたりの占有長（車長5m + 車間2.5m）

travelTime = freeFlowTravelTime × (1 + α × density^β)
  α = 0.15, β = 4.0（標準BPR関数パラメータ）
```

| 密度 | レベル | ラベル |
|------|--------|--------|
| < 0.3 | 1 | 順調 |
| < 0.5 | 2 | やや混雑 |
| < 0.7 | 3 | 混雑 |
| < 0.9 | 4 | 渋滞 |
| >= 0.9 | 5 | 大渋滞 |

---

## 7. パフォーマンス考慮

| 項目 | 想定値 | 対策 |
|------|--------|------|
| 車両エージェント数 | 最大500 | VehiclePoolManagerのmaxPoolSize |
| 歩行者エージェント数 | 最大500 | 既存設計のまま |
| NavMeshTrafficCalculator更新頻度 | 1秒 | エッジ特定にRoadNetworkの空間グリッド使用（O(1)） |
| NavMeshObstacle carving | 停車車両のみ | 移動中は無効化でNavMesh再計算を抑制 |
| CurrentEdgeId更新 | 車両ごとに1秒間隔 | GetNearestEdge(30m)の空間グリッド探索 |

---

## 8. リスクと対策

| リスク | 影響 | 対策 |
|--------|------|------|
| kinematic Rigidbody同士がすり抜ける | 渋滞が発生しない | NavMeshObstacle(carve)をフォールバック実装 |
| 500台carvingのパフォーマンス | フレームレート低下 | 停車中のみcarving有効、移動中は無効 |
| 車両radius(2.0)でNavMeshに配置不可 | スポーン失敗 | SamplePosition探索半径50m |
| 歩行者が車両をすり抜ける | 非現実的な挙動 | レイヤー設定で物理衝突制御 |
| NavMeshの分断箇所 | 車両が目的地に到達不能 | PathPartial検知で代替ルート/歩行切替 |

---

## 9. 実装フェーズ

| Phase | 内容 | 依存 |
|-------|------|------|
| 1 | NavMeshVehicleAgent.cs 新規作成 | なし |
| 2 | NavMeshTrafficCalculator.cs 新規作成 | RoadNetwork（既存） |
| 3 | VehiclePoolManager.cs 改修 | Phase 1 |
| 4 | TrafficStateProvider.cs 配線変更 | Phase 2 |
| 5 | Evacuee.cs 運転メソッド改修 | Phase 1, 3 |
| 6 | TrafficHeatmapVisualizer.cs 配線変更 | Phase 2 |
| 7 | SUMO関連コード削除 | Phase 1-6完了後 |

---

## 10. 検証項目

1. 車両1台のspawn → NavMesh上で目的地へ移動完了
2. 狭い道路に10台spawn → 物理ブロックにより速度≈0（渋滞）
3. NavMeshTrafficCalculator → 渋滞時Level 4-5を出力
4. EvaluateAbandonVehicle → 渋滞検知でSwitchToWalking発火
5. BuildNearbyTrafficPayload → LLMに渋滞情報が到達
6. 歩行↔車両の切替 → NavMeshAgent復帰が正常
7. 500歩行者 + 300車両 → 安定したフレームレート
