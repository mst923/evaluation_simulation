# LLM駆動エージェント・ベース・モデル（ABM）避難シミュレーション

Unity上で動作する3D避難シミュレーションシステム。大規模言語モデル（LLM）をエージェントの意思決定エンジンとして活用し、人間らしい避難行動を再現する。東日本大震災を想定した福島県いわき市平豊間地区・平薄磯地区の実環境を対象としている。

## 目次

- [研究の背景と目的](#研究の背景と目的)
- [技術スタック](#技術スタック)
- [アーキテクチャ](#アーキテクチャ)
- [プロジェクト構造](#プロジェクト構造)
- [セットアップ](#セットアップ)
- [主要コンポーネント](#主要コンポーネント)
- [シミュレーションの仕組み](#シミュレーションの仕組み)
- [設定ファイル](#設定ファイル)
- [入出力仕様](#入出力仕様)
- [実験設計](#実験設計)
- [計測指標](#計測指標)
- [分析ツール](#分析ツール)
- [ドキュメント一覧](#ドキュメント一覧)

---

## 研究の背景と目的

### 背景

従来の避難シミュレーションは「合理的な最適避難行動」を前提としているが、2011年東日本大震災では以下のような**非合理的だが人間らしい行動**が多数観測された：

- **正常性バイアス**: 「大したことない」と危険を過小評価し避難が遅れる
- **家族探索行動**: 合理的な避難より家族の安否確認を優先する
- **同調行動**: 周囲が動かないため自分も避難しない
- **情報の過小評価**: 初報の津波予想高さ（3m）を信じ、実際（6m超）に対応できない

### 目的

LLMを認知エンジンとして活用し、**「その人らしい判断」** を生成することで、現実的な避難行動の多様性を再現するシミュレーションを構築する。

### 研究課題

1. LLMベースのエージェントは人間らしい多様な避難行動を再現できるか？
2. 心理的バイアス（正常性バイアス、同調バイアス）が避難行動に与える影響は？
3. 家族関係や社会的相互作用が避難の遅延・促進にどう寄与するか？
4. 情報提供戦略（警報の具体性・切迫感）の違いが避難完了率に与える影響は？

---

## 技術スタック

| カテゴリ | 技術 |
|---------|------|
| ゲームエンジン | Unity 2022.3+ (C#) |
| 3D都市モデル | PLATEAU SDK for Unity（国土交通省） |
| LLM | OpenAI API (GPT-4o-mini) |
| 通信 | WebSocket（双方向リアルタイム） |
| サーバー | Python 3.10+ (asyncio) |
| データ検証 | Pydantic |
| 経路探索 | Unity NavMesh |
| 交通シミュレーション | SUMO (TraCI) + OSMnx |
| パッケージ管理 | uv (Python) |

---

## アーキテクチャ

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Unityシミュレーション環境                         │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐  │
│  │    環境管理       │    │   避難者群        │    │   実験制御       │  │
│  ├──────────────────┤    ├──────────────────┤    ├──────────────────┤  │
│  │ ・避難所管理     │───▶│ ・避難者モデル    │◀───│ ・実験条件設定   │  │
│  │ ・警報発令管理   │    │ ・LLM/ルール判断 │    │ ・結果計測       │  │
│  │ ・災害進行管理   │    │ ・家族関係管理    │    │ ・メトリクス     │  │
│  │ ・PLATEAU建物    │    │ ・個人属性       │    │                  │  │
│  └──────────────────┘    └────────┬─────────┘    └──────────────────┘  │
│                                   │                                      │
│  ┌──────────────────┐             │                                      │
│  │  空間情報管理     │             │ WebSocket通信                        │
│  ├──────────────────┤             │                                      │
│  │ ・周辺建物検索   │             ▼                                      │
│  │ ・PLATEAU地形情報│    ┌──────────────────┐                           │
│  │ ・危険区域判定   │    │ LLM通信クライアント│                           │
│  └──────────────────┘    └────────┬─────────┘                           │
│                                   │                                      │
│                          ┌────────┴─────────┐                           │
│                          │ LLMサーバー管理   │                           │
│                          │ (自動起動/停止)   │                           │
│                          └────────┬─────────┘                           │
└───────────────────────────────────┼─────────────────────────────────────┘
                                    │ リアルタイム双方向通信
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                       Python 意思決定サーバー                             │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐  │
│  │   メインサーバー  │    │   記憶管理       │    │   データ検証     │  │
│  │                  │    │                  │    │                  │  │
│  │ ・WebSocket受信  │◀──▶│ ・長期記憶       │    │ ・入出力検証     │  │
│  │ ・プロンプト生成 │    │ ・行動履歴要約   │    │ ・型安全保証     │  │
│  │ ・LLM問い合わせ │    │ ・地域知識       │    │                  │  │
│  │ ・応答解析      │    └──────────────────┘    └──────────────────┘  │
│  └────────┬─────────┘                                                   │
│           │                                                              │
│           ▼                                                              │
│  ┌──────────────────┐                                                   │
│  │   OpenAI API     │                                                   │
│  │  （GPT-4o-mini） │                                                   │
│  └──────────────────┘                                                   │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## プロジェクト構造

```
evacuation_simulation/
├── Assets/
│   ├── Scripts/                        # C# シミュレーションコード
│   │   ├── LLM/                        # LLM連携モジュール
│   │   │   ├── LLMDecisionClient.cs    #   WebSocketクライアント
│   │   │   ├── LLMActionMessages.cs    #   リクエスト/レスポンスJSON定義
│   │   │   └── LLMServerManager.cs     #   Pythonサーバー自動起動/停止
│   │   ├── Evacuee.cs                  # 避難者エージェント（メイン）
│   │   ├── ShelterEnvManager.cs        # シミュレーション環境管理
│   │   ├── Shelter.cs                  # 避難所モデル
│   │   ├── ExperimentConfig.cs         # 実験設定
│   │   ├── SimulationMetrics.cs        # 計測・メトリクス収集
│   │   ├── EnvironmentalContextProvider.cs  # 空間情報提供
│   │   ├── RuleBasedDecisionMaker.cs   # ルールベース比較用エージェント
│   │   ├── AlertManager.cs             # 警報・放送管理
│   │   ├── DisasterEventManager.cs     # 災害イベント発火
│   │   ├── DisasterTimeline.cs         # シナリオCSVパーサー
│   │   ├── PersonaData.cs              # ペルソナ属性データ
│   │   ├── FamilyData.cs               # 家族関係データ
│   │   ├── FamilyGroupManager.cs       # 双方向家族関係管理
│   │   ├── SpawnLocationManager.cs     # スポーン位置管理
│   │   ├── BuildingCategorizer.cs      # PLATEAU建物分類
│   │   ├── BuildingSpatialIndex.cs     # 建物空間インデックス
│   │   ├── GeoSpatialIndex.cs          # 地理空間インデックス
│   │   ├── GeoSpatialTypes.cs          # 地理空間型定義
│   │   ├── CityObjectTypes.cs          # 都市オブジェクト型定義
│   │   ├── TsunamiEvacuationArea.cs    # 津波避難区域判定
│   │   ├── AudioEventManager.cs        # 音声イベント管理
│   │   ├── EmergencyBroadcastSpeaker.cs # 防災無線スピーカー
│   │   ├── FireTruckAgent.cs           # 消防車エージェント
│   │   ├── ShelterManagementAgent.cs   # 避難所管理エージェント
│   │   ├── EvacueeSpawnPoint.cs        # スポーンポイント定義
│   │   └── Utils.cs                    # ユーティリティ
│   ├── Config/                         # 設定データ
│   │   ├── personas.csv                #   ペルソナ定義（30人）
│   │   ├── fukushima_evacuation_personas_500.csv  # 拡張ペルソナ（500人）
│   │   ├── families.csv                #   家族関係定義
│   │   ├── scenario.csv                #   災害シナリオ（時系列イベント）
│   │   ├── scenario.md                 #   シナリオ説明
│   │   ├── BuildingSpatialIndex.asset  #   建物空間インデックス（事前計算）
│   │   └── GeoSpatialIndex.asset       #   地理空間インデックス（事前計算）
│   └── Scenes/                         # Unityシーン
│       ├── Iwaki/                      #   いわき市平豊間・平薄磯地区
│       ├── namie/                      #   浪江町付近
│       └── SampleScene/                #   開発・テスト用
├── llm_server/                         # Python LLMサーバー
│   ├── server.py                       #   WebSocketサーバー本体
│   ├── models.py                       #   Pydanticデータモデル
│   ├── memory_manager.py               #   長期記憶管理
│   ├── memory_summarizer.py            #   行動履歴要約
│   ├── persona_maker.py                #   ペルソナ生成ツール
│   ├── makeCSV.py                      #   CSV生成ツール
│   ├── analyze_evacuation_start_time.py #  避難開始時間分析
│   ├── analysis/                       #   事後分析ツール群
│   │   ├── analyze_reasoning.py        #     意思決定理由分析
│   │   ├── analyze_shelter_selection.py #     避難所選択パターン分析
│   │   ├── analyze_social_network.py   #     社会ネットワーク分析
│   │   ├── analyze_social_interaction.py #    社会的相互作用分析
│   │   ├── analyze_qualitative.py      #     定性分析
│   │   └── aggregate_experiment_results.py #  実験結果集約
│   ├── pyproject.toml                  #   Python依存定義（uv用）
│   ├── requirements.txt                #   Python依存（pip用）
│   ├── uv.lock                         #   依存ロックファイル
│   ├── .env                            #   環境変数（APIキー等）
│   └── .env.example                    #   環境変数テンプレート
├── Docs/                               # ドキュメント
│   ├── Architecture.md                 #   システムアーキテクチャ詳細
│   ├── Module/                         #   機能仕様書
│   │   ├── LLM_API.md                  #     LLM通信プロトコル仕様
│   │   ├── Persona.md                  #     ペルソナ仕様
│   │   ├── Connection.md               #     社会的相互作用仕様
│   │   ├── Memory.md                   #     記憶システム仕様
│   │   ├── RuleBasedAgent.md           #     ルールベースエージェント仕様
│   │   ├── prompt.md                   #     LLMプロンプト設計
│   │   ├── Evaluation.md               #     評価指標仕様
│   │   ├── TrafficNetwork.md           #     交通ネットワーク仕様
│   │   └── TrafficNetwork_OpenQuestions.md # 交通ネットワーク検討事項
│   ├── Experiment.md                   #   実験計画
│   ├── ChangeLog.md                    #   変更履歴
│   ├── FutureWork.md                   #   今後の課題
│   ├── ImplementationPlan.md           #   実装計画
│   ├── ActionPlan.md                   #   アクションプラン
│   ├── Priority.md                     #   優先度整理
│   └── Tables/                         #   実験結果テーブル
├── Logs/                               # 実行ログ
├── results/                            # シミュレーション結果
├── Packages/                           # 外部パッケージ（PLATEAU SDK等）
├── ProjectSettings/                    # Unity設定
└── CLAUDE.md                           # AI開発ガイドライン
```

---

## セットアップ

### 必要環境

- Unity 2022.3以降
- Python 3.10以降
- OpenAI API Key（オプション — なくてもヒューリスティックモードで動作）
- SUMO（オプション — 交通シミュレーション使用時に必要）

### Pythonサーバー起動

#### LLMサーバー

```bash
cd llm_server
cp .env.example .env  # APIキーを設定

# uv を使用する場合（推奨）
uv run server.py

# pip を使用する場合
pip install -r requirements.txt
python server.py
```

#### 交通シミュレーションサーバー（オプション）

```bash
cd traffic_server

# 1. 依存パッケージをインストール
uv sync

# 2. OSM道路ネットワークを構築（初回のみ）
uv run osm_network_builder.py

# 3. SUMO設定ファイルを生成（SUMO使用時のみ）
uv run sumo_config_generator.py

# 4. 交通サーバーを起動
uv run server.py
```

### 環境変数 (.env)

```
# LLMサーバー
OPENAI_API_KEY=sk-...
OPENAI_MODEL=gpt-4o-mini
LLM_SERVER_HOST=localhost
LLM_SERVER_PORT=8765

# 交通サーバー（オプション）
TRAFFIC_SERVER_HOST=localhost
TRAFFIC_SERVER_PORT=8766
SUMO_CONFIG=sumo_data/simulation.sumocfg  # SUMOなしの場合は空
SUMO_BINARY=sumo  # sumo-gui でGUI表示
```

APIキー未設定時はヒューリスティック（最短距離避難所選択）で自動的に動作する。
交通サーバーはSUMOなしでも起動可能（モックモード）。

### Unityでの実行

1. `Assets/Scenes/Iwaki/` シーンを開く
2. `LLMServerManager` コンポーネントが有効であればPythonサーバーは自動起動される
3. Playボタンで実行
4. Console / Scene / Game ビューで避難行動を確認
5. 結果は `Logs/` および `results/` に出力される

---

## 主要コンポーネント

### Unity側（C#）

#### シミュレーションコア

| ファイル | 役割 |
|---------|------|
| `Evacuee.cs` | 避難者エージェント本体。LLM/ルールベースでの行動決定、NavMeshによる移動、体力・ストレス管理、家族探索・会話処理 |
| `ShelterEnvManager.cs` | シミュレーション全体制御。避難者のスポーン、時間管理、イベント発火、シミュレーションライフサイクル管理 |
| `Shelter.cs` | 避難所モデル。収容人数・現在の占有率・安全度・海抜・アクセス情報を管理 |
| `ExperimentConfig.cs` | 実験パラメータ設定。ペルソナ分布、シナリオ選択、出力設定 |
| `SimulationMetrics.cs` | 避難完了率、避難時間分布、避難所占有率推移、ストレスレベル等のメトリクス収集 |

#### LLM連携

| ファイル | 役割 |
|---------|------|
| `LLMDecisionClient.cs` | WebSocketクライアント。Pythonサーバーとの非同期通信を管理 |
| `LLMActionMessages.cs` | リクエスト/レスポンスのJSONデータ構造定義（EvacueeState、ShelterData、EnvironmentData等） |
| `LLMServerManager.cs` | Pythonサーバーの自動起動・停止。環境変数管理 |

#### 環境・災害

| ファイル | 役割 |
|---------|------|
| `EnvironmentalContextProvider.cs` | グリッドベースの空間インデックスで周辺の建物・避難者情報を高速検索 |
| `AlertManager.cs` | 防災行政無線、Jアラートの発令・受信管理 |
| `DisasterEventManager.cs` | 地震・津波・余震イベントのトリガー |
| `DisasterTimeline.cs` | scenario.csvを解析し、時系列でイベントを発火 |
| `TsunamiEvacuationArea.cs` | 津波浸水想定区域の判定 |
| `EmergencyBroadcastSpeaker.cs` | 防災無線スピーカーの空間配置・音声再生 |
| `AudioEventManager.cs` | サイレン等の音声イベント管理 |

#### 空間・地理

| ファイル | 役割 |
|---------|------|
| `BuildingSpatialIndex.cs` | PLATEAU建物の空間インデックス（事前計算） |
| `BuildingCategorizer.cs` | PLATEAU建物を用途別（住宅・商業・学校等）に分類 |
| `GeoSpatialIndex.cs` | PLATEAUデータによる地理空間検索 |
| `SpawnLocationManager.cs` | 建物カテゴリに基づくスポーン位置選定 |

#### 社会的関係

| ファイル | 役割 |
|---------|------|
| `PersonaData.cs` | ペルソナCSVの解析・属性保持 |
| `FamilyData.cs` | 家族関係データ（後方互換） |
| `FamilyGroupManager.cs` | 双方向家族関係の管理、依存関係追跡 |

#### その他エージェント

| ファイル | 役割 |
|---------|------|
| `RuleBasedDecisionMaker.cs` | 比較実験用ヒューリスティックエージェント |
| `FireTruckAgent.cs` | 消防車エージェント |
| `ShelterManagementAgent.cs` | 避難所管理エージェント |

### Python側

| ファイル | 役割 |
|---------|------|
| `server.py` | WebSocketサーバー本体。プロンプト生成→OpenAI API呼び出し→応答解析→返却。ヒューリスティックフォールバック機能搭載 |
| `models.py` | Pydanticベースの入出力データ検証モデル |
| `memory_manager.py` | 避難者ごとの長期記憶（地域知識・避難所知識・過去経験）をRAG方式で管理 |
| `memory_summarizer.py` | 直近の行動履歴・会話履歴を要約し、プロンプトのコンテキストとして提供 |
| `persona_maker.py` | いわき市の人口統計に基づくペルソナ自動生成 |
| `makeCSV.py` | CSV生成ユーティリティ |

---

## シミュレーションの仕組み

### 全体フロー

```
1. スポーン     30〜500人の避難者が現実的な位置（自宅・職場・学校）に配置
       ↓
2. イベント発火  scenario.csv に基づく時系列イベント（地震・津波警報・余震）
       ↓
3. 意思決定     5〜10秒間隔でLLMサーバーに判断を要求（またはルールベース）
       ↓
4. 行動実行     NavMeshベースの経路探索で移動、会話・連絡・待機等を実行
       ↓
5. 社会的行動   家族探索・情報交換・集団行動
       ↓
6. 計測・記録   避難完了率・時間分布・避難所占有率をCSV/JSONで出力
```

### 災害シナリオ（タイムライン例）

| 経過時間 | イベント | 内容 |
|----------|----------|------|
| 0秒 | 本震 | M7クラスの地震発生 |
| 60秒 | 緊急地震速報 | Jアラート |
| 120秒 | 行政無線 | 避難指示の放送 |
| 180秒 | 情報空白期 | 正確な情報が不足 |
| 240秒 | 津波警報 | 予想高さ3m（実際は6m超） |
| 600秒 | 余震 | 追加の揺れ |
| 1200秒 | 最終警告 | 到達前の最後の警告 |
| 1500秒 | 津波第一波 | 津波到達 |
| 1800秒 | 本波 | 主要津波波 |

### 行動タイプ

避難者が取りうる6種類の行動：

| ActionType | 説明 | 具体例 |
|------------|------|--------|
| `EVACUATE` | 指定避難所へ移動 | 豊間小学校に向かう |
| `STAY` | その場で待機 | 状況を見守る、情報を待つ |
| `SEARCH_FAMILY` | 家族を探しに行く | 学校へ子供を迎えに行く |
| `CONTACT` | 家族に連絡を試みる | 電話・メッセージで安否確認 |
| `FOLLOW` | 他者について行く | 合流した家族と一緒に避難 |
| `TALK` | 近くの避難者と会話 | 情報交換・避難所の相談 |

### 階層的意思決定

LLMは3層構造で思考する：

| 層 | 内容 | 例 |
|----|------|-----|
| 長期目標 | 最終的に達成したいこと | 「家族全員で高台の避難所に到達する」 |
| 中期計画 | 目標達成のための手順 | 「まず学校で子供を迎えに行く」 |
| 即時行動 | 今この瞬間に取る行動 | 「学校に向かって早歩きで移動する」 |

### 移動速度

| 速度 | 説明 | 体力消耗 |
|------|------|---------|
| `SLOW` | 高齢者・身体制約者 | 低 |
| `NORMAL` | 一般的な歩行 | 低 |
| `FAST` | 急ぎの歩行 | 中 |
| `RUN` | 全力で走る | 高 |

---

## 設定ファイル

### ペルソナCSV (`personas.csv`)

避難者の個人属性を定義。いわき市の人口統計に基づく。

| 属性 | 例 | 意思決定への影響 |
|------|-----|-----------------|
| 名前 | 山田 太郎 | 識別用 |
| 年齢層・性別 | 60代・男性 | 移動速度、判断傾向 |
| 移動速度係数 | 0.3〜1.3 | 実際の移動速度 |
| 心理状態 | 正常性バイアス・楽観的 | 危険の認識度合い |
| 行動優先度 | 様子見・現状維持 | 初期行動の傾向 |
| 自宅位置カテゴリ | 沿岸部/丘陵部/中心部 | スポーン位置、津波リスク認識 |
| 自宅海抜 | 5m | 津波リスクの認識 |
| 地域熟知度 | 長年の居住者 | 避難所・地形の知識 |
| 災害経験 | 2011年の津波を経験 | 危機感の度合い |
| 身体状況 | 杖を使用 | 移動制約 |

#### 心理状態の分布

| 心理状態 | 割合 | 特徴 |
|---------|------|------|
| 冷静系（calm/analytical） | 30% | 状況を客観的に判断 |
| 正常性バイアス（optimism bias） | 25% | 危険を過小評価しがち |
| 同調系（social conformity） | 20% | 周囲に合わせて行動 |
| 焦燥（panic） | 10% | パニックになりやすい |
| 慎重（cautious） | 15% | 石橋を叩いて渡る |

### 家族CSV (`families.csv`)

家族グループの構成を定義。シミュレーション内・外の区別あり。

| 分類 | 特徴 |
|------|------|
| シミュレーション内の家族 | Unity上に存在し、位置追跡・合流が可能 |
| シミュレーション外の家族 | 仮想的な存在。連絡のみ可能で、探索・合流は不可 |

### シナリオCSV (`scenario.csv`)

時間経過に伴うイベント・アラート発信タイミングを定義。

| カラム | 説明 |
|--------|------|
| time_sec | イベント発生時刻（秒） |
| event_type | MainQuake / TsunamiWarning / Aftershock 等 |
| tsunami_height | 津波予想高さ（m） |
| seismic_intensity | 震度 |
| broadcast_message | 防災無線の放送内容 |

---

## 入出力仕様

### 入力（Unity → LLMサーバー）

WebSocket JSON形式で以下の情報を送信：

- **避難者の状態**: ペルソナ属性、現在位置、体力・ストレス、現在の行動
- **利用可能な避難所**: 各避難所の名称、距離、収容率、安全度、海抜
- **環境情報**: 周辺建物、危険区域、混雑状況、現在地の海抜
- **家族情報**: 家族メンバーの位置・合流状況・連絡状態
- **記憶**: 地域知識、過去の行動履歴、会話履歴

### 出力（LLMサーバー → Unity）

- **行動タイプ**: EVACUATE / STAY / SEARCH_FAMILY / CONTACT / FOLLOW / TALK
- **行動対象**: 避難所ID / 家族名 / 追従対象ID / 会話相手ID
- **長期目標・中期計画**: 階層的意思決定の上位層
- **移動速度**: SLOW / NORMAL / FAST / RUN
- **判断理由と確信度**: LLMの思考プロセス

詳細は [Docs/Module/LLM_API.md](Docs/Module/LLM_API.md) を参照。

---

## 社会的相互作用

### 家族探索

- シミュレーション内の家族のみ探索可能
- 10秒間隔で相手の位置を追跡
- 10m以内に近づくと「合流」と判定
- 合流後は一緒に行動（追従）

### 連絡（電話・メッセージ）

- シミュレーション内外の家族と連絡可能
- 最大10往復の会話が可能
- 通信遅延あり（3秒）、会話中は移動停止

### 会話（近隣者との情報交換）

- 30m以内の他の避難者と会話可能
- 最大10往復、通信遅延あり（5秒）
- 会話を通じて避難行動が変化する可能性

### 追従

- 家族合流後に自動的に開始
- 5mの距離を維持しながら移動
- 循環追従（A→B→A）の検出・防止機能あり

詳細は [Docs/Module/Connection.md](Docs/Module/Connection.md) を参照。

---

## PLATEAUデータの活用

PLATEAU（プラトー）は国土交通省が主導する3D都市モデル整備プロジェクト。本システムでは福島県いわき市のPLATEAUデータを活用。

| データ種別 | シミュレーションでの用途 |
|-----------|------------------------|
| 建物モデル（LOD1/LOD2） | 避難者の初期配置、建物内外判定 |
| 建物用途 | スポーン位置の分類（住宅地、オフィス街、学校） |
| 建物高さ・構造 | 垂直避難可能性・耐震性の判定 |
| 床面積 | 避難所の収容人数計算 |
| 地形モデル（DEM） | 海抜の取得、津波リスク判定 |

### 避難所収容人数の計算

```
収容人数 = 床面積 × 0.8（有効利用率）÷ 1.65m²（1人あたり面積）
```

### 避難所（18施設）

| 種類 | 数 | 例 |
|------|-----|-----|
| 指定避難所 | 8施設 | 豊間小学校、豊間中学校 |
| 津波避難場所 | 10施設 | 諏訪神社、八幡神社 |

---

## 実験設計

### 実験1: LLM vs ルールベース比較

| 項目 | 内容 |
|------|------|
| 目的 | LLMによる意思決定とルールベースの行動比較 |
| 仮説 | LLMは人間らしい多様な行動を再現し、ルールベースは合理的だが画一的 |
| 条件 | 2条件（LLM / ルールベース） |
| 指標 | 避難完了率、避難完了時間、行動の多様性 |

### 実験2: 認知バイアスの影響

| 条件 | 説明 |
|------|------|
| バイアスなし | 基準条件 |
| 正常性バイアスのみ | 危険を過小評価する傾向 |
| 同調バイアスのみ | 周囲に合わせる傾向 |
| 両バイアス | 両方の傾向を持つ |

### 実験3: 情報提供戦略の検証

| 条件 | 情報の具体性 | 表現の切迫感 |
|------|-------------|-------------|
| 標準 | 一般的 | 通常 |
| 切迫強調 | 一般的 | 強く警告 |
| 詳細情報 | 避難所名・海抜を明示 | 通常 |
| 詳細+切迫 | 避難所名・海抜を明示 | 強く警告 |

---

## 計測指標

### 主要指標

| 指標 | 説明 |
|------|------|
| 避難完了率 | 避難所に到達した避難者の割合 |
| 平均避難時間 | 避難所到達までの平均時間 |
| 避難所別利用率 | 各避難所の選択割合 |
| 行動種別頻度 | 各行動（避難/待機/探索等）の発生回数 |
| 生存率 | 津波到達時に安全な高度にいた避難者の割合 |

### 生存判定

津波到達時の位置の海抜が「**津波の高さ × 2**」以上であれば生存と判定（2011年の実際の生存データに基づく安全マージン）。

### 出力ファイル

| 出力先 | 内容 |
|--------|------|
| `Logs/llm_decisions/` | LLM意思決定ログ（JSON形式） |
| `results/` | 避難完了率、避難時間、避難所混雑度推移（CSV形式） |

---

## 分析ツール

`llm_server/analysis/` 配下のPythonスクリプトで事後分析を行う：

| ファイル | 分析内容 |
|---------|---------|
| `analyze_reasoning.py` | LLMの意思決定理由を抽出・分類 |
| `analyze_shelter_selection.py` | 避難所選択パターン（距離 vs 安全度 vs 混雑度） |
| `analyze_social_network.py` | 家族再会率・社会ネットワーク構造 |
| `analyze_social_interaction.py` | 会話・連絡の発生パターンと影響 |
| `analyze_qualitative.py` | 定性的な行動パターン分析 |
| `aggregate_experiment_results.py` | 複数回の実験結果を集約・統計処理 |
| `analyze_evacuation_start_time.py` | 避難開始時間の分布分析 |

```bash
# 使用例
cd llm_server
python analysis/analyze_reasoning.py
python analysis/aggregate_experiment_results.py
```

---

## ドキュメント一覧

### 設計ドキュメント

| ファイル | 内容 |
|---------|------|
| [Docs/Architecture.md](Docs/Architecture.md) | システムアーキテクチャ全体像、設計思想、データフロー |
| [Docs/Experiment.md](Docs/Experiment.md) | 実験計画の詳細 |
| [Docs/FutureWork.md](Docs/FutureWork.md) | 今後の課題・拡張計画 |
| [Docs/ChangeLog.md](Docs/ChangeLog.md) | 変更履歴 |

### 機能仕様書（Docs/Module/）

| ファイル | 内容 |
|---------|------|
| [LLM_API.md](Docs/Module/LLM_API.md) | WebSocket通信プロトコル、JSONスキーマ定義 |
| [Persona.md](Docs/Module/Persona.md) | ペルソナCSV仕様、LLMプロンプトへの統合方法 |
| [Connection.md](Docs/Module/Connection.md) | 社会的行動（家族探索・会話・追従・連絡）の仕様 |
| [Memory.md](Docs/Module/Memory.md) | 長期記憶・会話履歴・要約システムの仕様 |
| [RuleBasedAgent.md](Docs/Module/RuleBasedAgent.md) | ルールベース意思決定ロジックの仕様 |
| [prompt.md](Docs/Module/prompt.md) | LLMシステムプロンプト・ユーザープロンプトの設計 |
| [Evaluation.md](Docs/Module/Evaluation.md) | 評価指標の定義と計測方法 |
| [TrafficNetwork.md](Docs/Module/TrafficNetwork.md) | 道路ネットワーク・経路制約の仕様 |

### 計画・管理

| ファイル | 内容 |
|---------|------|
| [Docs/ImplementationPlan.md](Docs/ImplementationPlan.md) | 実装計画の詳細ステップ |
| [Docs/ActionPlan.md](Docs/ActionPlan.md) | アクションプラン |
| [Docs/Priority.md](Docs/Priority.md) | 機能の優先度整理 |

---

## 性能

### 検証済みの規模

- 同時避難者数: **500人**

### 高速化の工夫

| 工夫 | 説明 |
|------|------|
| PLATEAU空間情報の事前計算 | `BuildingSpatialIndex.asset` / `GeoSpatialIndex.asset` として事前キャッシュ |
| グリッドベース空間検索 | `EnvironmentalContextProvider.cs` による O(1) 近似の近傍検索 |
| LLMリクエストの分散 | 同時リクエストを段階的に発行し、API負荷を分散 |
| 状態変化時のみリクエスト | 不要なLLM呼び出しを削減 |
| 履歴の制限 | 直近の行動履歴のみ保持（3-5件） |

### LLMの応答時間とコスト

| 項目 | 値 |
|------|-----|
| 応答時間 | 0.5〜3秒/リクエスト |
| コスト | 約0.01円/リクエスト（GPT-4o-mini） |
