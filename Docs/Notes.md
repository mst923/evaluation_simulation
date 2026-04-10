# Notes - セットアップガイド

## 前提条件

### Unity バージョン
- **Unity 2023.2.19f1** が必要（`ProjectSettings/ProjectVersion.txt` で指定）
- Unity Hub からインストールする場合、正確なバージョンを選択すること
- URP (Universal Render Pipeline) 16.0.6 を使用しているため、ビルトインRPでは動作しない

### Python 環境
- **Python 3.10 以上** が必要
- パッケージマネージャとして **[uv](https://astral.sh/uv)** を使用
  - macOS: `brew install uv` または `curl -LsSf https://astral.sh/uv/install.sh | sh`

### OpenAI API キー
- LLMエージェント機能を使用する場合、OpenAI APIキーが必要

---

## git clone 後のセットアップ手順

### 1. Unity プロジェクトを開く

```bash
git clone <repository-url>
```

Unity Hub で `evacuation_simulation` フォルダを開く。初回はパッケージの解決に時間がかかる。

### 2. PLATEAU SDK の手動インストール

PLATEAU SDK (`com.synesthesias.plateau-unity-sdk`) は `.gitignore` で除外されており、git clone だけでは含まれない。
`Packages/com.synesthesias.plateau-unity-sdk/` ディレクトリにSDKを手動で配置する必要がある。

**インストール方法:**
1. [PLATEAU SDK for Unity](https://github.com/Project-PLATEAU/PLATEAU-SDK-for-Unity) のリリースページから `.tgz` をダウンロード
2. 解凍して `Packages/com.synesthesias.plateau-unity-sdk/` に配置する

> **経緯:** 当初は `manifest.json` でローカルの絶対パス（`file:/Users/harukidoi/Downloads/...`）を指定しており、他の環境で動作しなかった。その後 GitHub URL 参照（`git#v2.3.2`）に変更したが、最終的にGit管理外のローカルパッケージとして手動管理する方式に落ち着いた。

### 3. LLM サーバーの設定（LLMエージェント使用時）

```bash
cd llm_server
cp .env.example .env
```

`.env` ファイルを編集し、以下を設定:

| 変数 | デフォルト | 説明 |
|------|-----------|------|
| `OPENAI_API_KEY` | （必須） | OpenAI API キー |
| `OPENAI_MODEL` | `gpt-4o-mini` | 使用するLLMモデル |
| `LLM_SERVER_HOST` | `127.0.0.1` | WebSocket サーバーアドレス |
| `LLM_SERVER_PORT` | `8765` | WebSocket サーバーポート |

Python 依存関係のインストール:
```bash
cd llm_server
uv sync
```

> **Note:** `LLMServerManager` コンポーネントがシーンに配置されていれば、Unityの再生ボタンを押した際に `uv run server.py` が自動で起動される。手動起動も可能: `uv run server.py`

### 4. シーンファイルについて

以下のシーンファイルはサイズが大きく（100MB超）、Git管理外のため別途入手が必要:
- `Assets/Scenes/Iwaki*.unity`（いわき市シーン）
- `Assets/Scenes/namie*.unity`（浪江町シーン）
- `Assets/Scenes/MInamiSoumaLOD2.unity`（南相馬市シーン）

---

## 主要パッケージ一覧

| パッケージ | バージョン | 用途 |
|-----------|-----------|------|
| `com.synesthesias.plateau-unity-sdk` | ローカル管理 | 3D都市モデル |
| `com.unity.ai.navigation` | 2.0.6 | NavMesh による経路探索 |
| `com.unity.ml-agents` | 2.0.2 | 強化学習エージェント |
| `com.unity.render-pipelines.universal` | 16.0.6 | URP レンダリング |
| `com.unity.inputsystem` | 1.7.0 | 新入力システム |
| `com.boxqkrtm.ide.cursor` | GitHub | Cursor エディタ対応 |
| `com.singularitygroup.hotreload` | ローカル管理 | Hot Reload（開発時） |

---

## これまでに発生した主要なエラーと解決策

### 1. PLATEAU SDK の参照エラー

**問題:** `manifest.json` で PLATEAU SDK をローカルの絶対パス（`file:/Users/harukidoi/Downloads/PLATEAU-SDK-for-Unity-v4.0.0.1-alpha.tgz`）で参照していたため、他の開発環境では SDK が見つからずパッケージ解決に失敗した。

**解決策の変遷:**
1. `commit 6218502d`: GitHub URL (`https://github.com/Project-PLATEAU/PLATEAU-SDK-for-Unity.git#v2.3.2`) に変更 → 環境非依存に
2. `commit 65fe1b08`: ローカルパッケージ参照 (`file:com.synesthesias.plateau-unity-sdk`) に変更 → バージョン固定のため
3. `commit 67e9bee5`: 再度 GitHub URL に変更
4. 最終的に `.gitignore` に `Packages/com.synesthesias.plateau-unity-sdk/` を追加し、手動管理方式に統一

### 2. 建物がピンク色になるバグ（マテリアル欠落）

**問題:** ビルトインレンダーパイプラインのマテリアルが使用されており、PLATEAU SDK で読み込んだ建物モデルがピンク色（シェーダーエラー）で表示された。

**解決策（`commit 65fe1b08`, `commit 67e9bee5`）:**
- レンダーパイプラインを **URP (Universal Render Pipeline) 16.0.6** に変更
- `manifest.json` に `com.unity.render-pipelines.universal` を追加
- URP 用の Render Pipeline Asset を作成し、`GraphicsSettings` / `QualitySettings` を更新
- 全マテリアル（Easy Primitive People、建物など）を URP 対応シェーダーに変換

### 3. PLATEAU RoadNetwork のコンパイルエラー

**問題:** PLATEAU SDK Toolkit が `PLATEAU.Editor.RoadNetwork.RoadNetworkEditMode` という型を参照していたが、使用していた SDK バージョンにはこの型が存在せず、コンパイルエラーが発生した。

**解決策（`commit 591dbdf3`）:**
- `Assets/Editor/PLATEAU/RoadNetwork/RoadNetworkEditModeStub.cs` にスタブ（仮の型定義）を作成
- `None`, `Edit`, `View` の3値を持つ enum として定義し、コンパイルを通るようにした

### 4. NavMesh で道路が地形に埋まる問題

**問題:** PLATEAU の道路メッシュ（tran）が DEM（地形）より低い位置にあり、NavMesh で経路探索すると地面に埋まった状態で移動していた。

**解決策（`commit 6b0de632`）:**
- `Assets/Editor/SnapToGround.cs` エディタスクリプトを作成
- 道路メッシュの各頂点から地形に向けてレイキャストし、地形の高さ + クリアランス（3m）に道路を自動調整する `Tools > Snap Roads to Ground` メニューを追加
- NavMesh を再作成

### 5. シェルターの capacity 関連エラー

**問題:** シェルター（避難所）の収容人数管理で、capacity のチェックやカウントに不整合がありランタイムエラーが発生していた。

**解決策（`commit 5655e65a`）:**
- `Shelter.cs` と `ShelterEnvManager.cs` の収容人数管理ロジックを修正
- `Evacuee.cs` での避難先選択時のバリデーションを強化

### 6. ML-Agents エージェントの不可解な挙動

**問題:** 強化学習で訓練したエージェントが意図しない行動（目的地に到達しない、同じ場所を回り続ける等）を取っていた。

**解決策（`commit e2ab3cdd`）:**
- `ShelterEnvManager.cs` と `Utils.cs` のロジックを修正
- Evacuee プレハブのスケール設定を調整

### 7. LLM レスポンス処理の警告とエピソードリセット不備

**問題:** 複数エピソードを連続実行する際、前エピソードの状態（会話履歴、家族状態、行動履歴など）がリセットされず、LLMレスポンスの警告やエージェントの不正な振る舞いが発生した。

**解決策（`commit c6126367`）:**
- `Evacuee.cs` に `ResetForNewEpisode()` メソッドを追加し、全動的状態をクリアするように変更
- `ShelterEnvManager.cs` でエピソードリセット時にこのメソッドを呼び出すよう修正
- UIに `episodeInfoText` を追加し、現在のエピソード状況を表示

### 8. ログが正常に記録されない問題

**問題:** シミュレーション実行時のログ（アクションログ、避難率CSV等）が正しく出力されない、または不完全な状態で終了していた。

**解決策（`commit e6122df1`）:**
- `SimulationMetrics.cs` を大幅に改修し、ログ出力ロジックを安定化
- `Evacuee.cs` のログ記録タイミングを修正
- `ExperimentConfig.cs` にバッチ実験用の設定機能を追加

---

## ディレクトリ構成（主要部分）

```
evacuation_simulation/
├── Assets/
│   ├── Config/              # ペルソナ・家族・シナリオ設定CSV
│   ├── Scripts/             # C# スクリプト
│   │   ├── Evacuee.cs       # 避難者エージェント
│   │   ├── ShelterEnvManager.cs  # シミュレーション管理
│   │   ├── LLM/             # LLM連携クライアント
│   │   └── ...
│   └── Scenes/              # Unity シーン（大部分はGit管理外）
├── Docs/
│   ├── Module/              # 機能仕様書
│   └── Notes.md             # 本ファイル
├── llm_server/              # Python WebSocket サーバー
│   ├── server.py            # メインサーバー
│   ├── memory_manager.py    # 長期記憶（RAG）
│   ├── memory_summarizer.py # 短期記憶
│   ├── models.py            # データモデル
│   ├── .env.example         # 環境変数テンプレート
│   └── data/                # メモリ・シナリオデータ
├── Packages/                # Unity パッケージ
├── ProjectSettings/         # Unity プロジェクト設定
└── Logs/                    # シミュレーション結果（Git管理外）
```

---

## メモ
