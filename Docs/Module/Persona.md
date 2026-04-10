# 避難シミュレーション ペルソナ仕様書

## 1. 概要

本ドキュメントは、避難シミュレーションにおけるペルソナ（避難者キャラクター）の設計仕様を定義する。

### 1.1 ペルソナシステムの目的
- LLMベースの避難者エージェントに個性と背景を付与
- 現実的な避難行動パターンの再現（正常性バイアス、同調行動等）
- 家族の安否確認・探索行動のシミュレーション

### 1.2 関連ファイル一覧

| ファイル | 説明 |
|---------|------|
| `Assets/Config/personas.csv` | ペルソナ基本情報・背景設定 |
| `Assets/Config/family_groups.csv` | 家族グループ情報（新形式・双方向対応） |
| `Assets/Config/families.csv` | 家族情報（旧形式・後方互換用） |
| `Assets/Scripts/PersonaData.cs` | ペルソナデータの読み込み処理 |
| `Assets/Scripts/FamilyGroupManager.cs` | 家族グループ管理（双方向関係の自動導出） |
| `Assets/Scripts/FamilyData.cs` | 家族データの読み込み処理（互換レイヤー） |
| `llm_server/server.py` | LLMプロンプト生成（家族情報の動的生成含む） |

---

## 2. personas.csv 仕様

### 2.1 カラム定義

| カラム名 | 型 | 説明 | 例 |
|---------|-----|------|-----|
| agent_id | int | エージェント識別子（1始まり） | 1, 2, 3 |
| name | string | 名前（日本語フルネーム） | 佐藤 健太 |
| role | string | 役割・職業 | 施設管理者, 一般社員, 高齢者 |
| age_group | string | 年齢層と性別 | 30s_Male, 70s_Female |
| speed_multiplier | float | 移動速度係数（0.4〜1.2） | 1.0 |
| mental_state | string | 心理状態 | 冷静・合理的 |
| priority | string | 避難時の優先事項 | 安全確保, 子供の捜索 |
| system_prompt_context | string | LLMへのキャラクター設定（ダブルクォートで囲む） | "あなたは..." |
| home_location_category | string | 自宅位置カテゴリ | coastal, hill, center |
| home_elevation | int | 自宅の海抜(m) | 15 |
| home_structure | string | 自宅の構造 | wooden_2story |
| residence_years | int | 居住年数 | 10 |
| local_knowledge_level | string | 土地勘レベル | native |
| current_location_reason | string | 現在地にいる理由 | 職場で施設の管理業務中 |
| past_disaster_experience | string | 過去の災害経験 | 2011年の津波を経験した |
| physical_condition | string | 身体状態 | 健康, 杖を使用 |
| has_smartphone | bool | スマートフォン所有 | true, false |
| has_vehicle | bool | 車両を所有しているか | true, false |
| can_drive | bool | 運転可能か（免許・身体状況） | true, false |

### 2.2 交通関連属性の説明

`has_vehicle` と `can_drive` は交通シミュレーション（SUMO統合）で使用される。

- `has_vehicle=true, can_drive=true`: 車両避難が可能
- `has_vehicle=true, can_drive=false`: 車両を持っているが自分では運転できない（高齢者、子供等）
- `has_vehicle=false`: 徒歩避難のみ

交通シミュレーションが無効（`ExperimentConfig.EnableTrafficSimulation=false`）の場合、これらの属性は無視され全員が徒歩で避難する。

### 2.3 有効な値リスト

#### mental_state（心理状態）
| 値 | 説明 |
|-----|------|
| 冷静・合理的 | 情報に基づき適切に判断 |
| 冷静・分析的 | 全体状況を分析して行動 |
| 正常性バイアス・楽観的 | 「大丈夫だろう」と避難を遅らせる |
| 同調性・集団追従 | 周囲の行動に追従 |
| 同調性・パニック気味 | 周囲に追従しつつ不安定 |
| 焦燥・目的外行動 | 家族探索など避難以外を優先 |
| 慎重・人混み回避 | 混雑や段差を避ける |

#### home_location_category（自宅位置）
| 値 | 説明 |
|-----|------|
| coastal | 海岸沿い（津波リスク高） |
| hill | 高台・丘陵地 |
| center | 市街地中心部 |

#### home_structure（自宅構造）
| 値 | 説明 |
|-----|------|
| wooden_1story | 木造1階建て |
| wooden_2story | 木造2階建て |
| rc_2story | RC造2階建て |
| rc_3story | RC造3階建て |

#### local_knowledge_level（土地勘レベル）
| 値 | 居住年数目安 | 説明 |
|-----|------------|------|
| newcomer | 1〜3年 | 新住民、土地勘が浅い |
| resident | 4〜15年 | 中堅居住者、主要施設は把握 |
| native | 16年以上 | 古参・地元出身、詳細な土地勘 |

---

## 3. family_groups.csv 仕様（新形式）

family_groups.csvは家族情報のSingle Source of Truth（唯一の情報源）である。
**双方向の家族関係を自動導出**できる正規化された形式。

### 3.1 カラム定義

| カラム名 | 型 | 説明 | 例 |
|---------|-----|------|-----|
| family_id | string | 家族グループID（苗字ベース） | yamamoto, tanaka |
| member_id | string | グループ内の識別子 | m1, m2, m3 |
| member_name | string | 名前 | 山本 剛 |
| role_in_family | string | 家族内での役割 | 父, 母, 子, 本人 |
| agent_id | int | シーン内エージェントID（-1=NPC） | 3, -1 |
| spawn_category | string | スポーンカテゴリ | Office, School |
| has_phone | bool | 連絡手段の有無 | true, false |
| exists_in_scene | bool | シーン内に存在するか | true, false |
| dependency | string | 依存関係 | none, requires_assistance |

### 3.2 role_in_family（家族内役割）
| 値 | 説明 |
|-----|------|
| 父 | 父親 |
| 母 | 母親 |
| 子 | 子供 |
| 本人 | 単身者 |
| 祖父 | 祖父 |
| 祖母 | 祖母 |

### 3.3 dependency（依存関係）
| 値 | 説明 |
|-----|------|
| none | 依存関係なし |
| requires_assistance | 支援が必要（高齢者、障害者等） |
| caregiver:mX | mXの介護者 |
| dependent:mX | mXに依存（子供等） |

### 3.4 双方向関係の自動導出

family_groups.csvでは、各メンバーは1行のみ定義。
続柄は`role_in_family`から自動的に導出される。

**続柄変換テーブル:**
| 自分の役割 | 相手の役割 | 相手の続柄 |
|-----------|-----------|----------|
| 父 | 母 | 妻 |
| 父 | 子 | 子供 |
| 母 | 父 | 夫 |
| 母 | 子 | 子供 |
| 子 | 父 | 父 |
| 子 | 母 | 母 |
| 子 | 子 | 兄弟 |

**例: 山本家**
```csv
family_id,member_id,member_name,role_in_family,agent_id,...
yamamoto,m1,山本 剛,父,3,...
yamamoto,m2,山本 美咲,母,-1,...
yamamoto,m3,山本 健,子,-1,...
```

導出結果:
- 山本剛(父)から見た山本美咲(母) → **妻**
- 山本剛(父)から見た山本健(子) → **子供**
- 山本美咲(母)から見た山本剛(父) → **夫**
- 山本健(子)から見た山本剛(父) → **父**

### 3.5 spawn_category（配置カテゴリ）
| 値 | 説明 | 日本語表示 |
|-----|------|----------|
| Office | オフィス・会社 | 勤務先 |
| School | 学校 | 学校 |
| Residential | 住宅地 | 自宅 |
| Commercial | 商業施設 | 商業施設 |
| Hospital | 病院 | 病院 |
| PublicFacility | 公共施設 | 公共施設 |
| Other | その他 | その他 |

---

## 4. families.csv 仕様（旧形式・後方互換）

family_groups.csvが存在しない場合のフォールバック用。
**一方向のみ**の家族関係を定義。

### 4.1 カラム定義

| カラム名 | 型 | 説明 | 例 |
|---------|-----|------|-----|
| agent_id | int | 本人のエージェントID | 3 |
| relation | string | 続柄 | self, 妻, 子供 |
| target_agent_id | int | 家族のエージェントID（-1=シーン外） | -1 |
| target_name | string | 家族の名前 | 山本 美咲 |
| spawn_category | string | 配置カテゴリ | School |
| has_phone | bool | 連絡手段の有無 | true, false |
| exists_in_scene | bool | シーン内に存在するか | true, false |

---

## 5. ファイル間の整合性ルール

### 5.1 ファイル選択の優先順位
1. `family_groups.csv`が存在する場合 → 新形式を使用（双方向対応）
2. 存在しない場合 → `families.csv`を使用（旧形式・一方向のみ）

### 5.2 必須の整合性
- **personas.csv の agent_id** と **family_groups.csv の agent_id > 0 のメンバー** は対応すること

### 5.3 家族情報の動的生成
LLMプロンプト内の「家族の状況」はfamily_groups.csv（またはfamilies.csv）から動的に生成される。

**生成ロジック（server.py内）:**
```python
family_members = payload.get("family_members", [])
if family_members and len(family_members) > 0:
    family_descriptions = []
    for member in family_members:
        relation = member.get("relation", "")
        location = member.get("likely_location", "")
        if relation and location:
            family_descriptions.append(f"{relation}が{location}にいる")
    if family_descriptions:
        lines.append(f"家族の状況: {' / '.join(family_descriptions)}")
elif not family_members:
    lines.append("家族の状況: 一人暮らし")
```

---

## 6. 行動パターンとの連携

### 6.1 SEARCH_FAMILY 行動
家族を探しに行く行動。family_groups.csv の情報を使用。

**処理フロー:**
1. family_groups.csv から当該agent_idが属する家族グループを特定
2. 同一グループの他メンバーについて、spawn_categoryに基づき探索対象建物を決定
3. 該当カテゴリの建物からランダムまたは最寄りを選択
4. エージェントが探索位置へ移動

### 6.2 CONTACT 行動
家族に連絡を取る行動。

**条件と処理:**
- `has_phone=true` の家族のみ連絡可能
- `exists_in_scene=true`: シーン内の別エージェントとして応答
- `exists_in_scene=false`: LLMが家族役として応答を生成

---

## 7. LLMプロンプトへの反映

### 7.1 ペルソナ情報の提供
personas.csv の各フィールドは、LLMプロンプトの「あなたのペルソナ」セクションとして提供される。

含まれる情報:
- 名前、年齢、役割
- 自宅の位置・構造・海抜
- 居住年数と土地勘レベル
- 現在地にいる理由
- 家族の状況（family_groups.csvから動的生成）
- 過去の災害経験
- 身体状態

### 7.2 家族情報の提供
family_groups.csv の情報は、「あなたの家族情報」セクションとして別途提供される。

含まれる情報:
- 家族メンバーの名前と続柄（**双方向で正しい続柄が表示**）
- 各家族の推定所在地（spawn_categoryの日本語表示）
- 連絡手段の有無（has_phone）
- 現在位置からの距離

### 7.3 プロンプト構成例
```
【あなたのペルソナ】
名前: 山本 剛（40代男性）
役割: 父親
自宅: 海岸沿い、海抜8m、木造2階建て
居住歴: この地域に12年
土地勘: 数年住んでおり、ある程度土地勘がある
現在ここにいる理由: 職場で仕事中だった
家族の状況: 妻が自宅にいる / 子供が学校にいる
過去の災害経験: 2011年の津波で家族と離れ離れになった経験がある
身体状態: 健康だが焦りで判断力が低下

あなたは子供とはぐれてしまった父親です...

【あなたの家族情報】
- 妻（山本 美咲）: 自宅にいる可能性、距離: 約500m、連絡可能
- 子供（山本 健）: 学校にいる可能性、距離: 約800m、連絡手段なし

※ 家族の安全も考慮して行動を選択してください。
```

---

## 8. 実験用ペルソナ（personas_experiment2.csv）

実験2用のペルソナセットは、心理状態の影響を検証するため以下のグループ構成となっている:

| グループ | agent_id | mental_state | 人数 |
|---------|----------|--------------|------|
| 実験A | 1-10 | 冷静・合理的 | 10人 |
| 実験B | 11-20 | 正常性バイアス・楽観的 | 10人 |
| 実験C | 21-30 | 同調性・集団追従 | 10人 |

各グループは年齢・性別・居住地のバランスを考慮して設計されている。

---

## 9. 設計原則

### 9.1 Single Source of Truth
家族情報はfamily_groups.csvを唯一の情報源とし、他ファイルへの重複記載を避ける。
これにより整合性維持の負担を軽減し、データ不整合を防止する。

### 9.2 データ正規化
各家族メンバーは1行のみ定義。双方向の関係はロジックで自動導出。
これにより:
- 行数削減（旧形式の約1/3）
- 重複データの排除
- 整合性維持の自動化

### 9.3 動的生成
LLMプロンプト内のテキスト表現（「妻が自宅にいる」等）は、
構造化されたデータ（family_groups.csv）から実行時に動的生成する。

### 9.4 後方互換性
旧形式（families.csv）もサポートし、移行期間中も既存データで動作可能。
family_groups.csvが存在すれば優先使用。

### 9.5 拡張性
dependencyフィールドにより、以下の拡張が可能:
- 要介護者と介護者の関係
- 親子の依存関係
- 将来的にはペット等も追加可能
