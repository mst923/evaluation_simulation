import asyncio
import json
import os
import random
import re
import math
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Any, Dict, List, Optional

import websockets
from dotenv import load_dotenv
from pydantic import ValidationError

try:
    from openai import AsyncOpenAI
except ImportError:  # pragma: no cover
    AsyncOpenAI = None  # type: ignore

from models import AgentInput
from memory_summarizer import (
    get_recent_actions,
    get_recent_conversations,
    get_recent_contacts,
)
from memory_manager import initialize_memory_manager, get_memory_manager


load_dotenv()

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
OPENAI_MODEL = os.getenv("OPENAI_MODEL", "gpt-4o-mini")
SERVER_HOST = os.getenv("LLM_SERVER_HOST", "127.0.0.1")
SERVER_PORT = int(os.getenv("LLM_SERVER_PORT", "8765"))

PROJECT_ROOT = Path(__file__).resolve().parents[1]
LOG_BASE_DIR = PROJECT_ROOT / "Logs" / "experiment_results"

# 実験IDごとのログディレクトリを管理
_experiment_log_dirs: Dict[str, Path] = {}


def _get_log_dir(experiment_id: Optional[str]) -> Path:
    """実験IDに対応するログディレクトリを取得（なければ作成）"""
    if not experiment_id:
        # experiment_idがない場合はサーバー起動時のタイムスタンプを使用
        experiment_id = datetime.now().strftime("%Y%m%d_%H%M%S")

    if experiment_id not in _experiment_log_dirs:
        log_dir = LOG_BASE_DIR / experiment_id / "llm_decisions"
        log_dir.mkdir(parents=True, exist_ok=True)
        _experiment_log_dirs[experiment_id] = log_dir

    return _experiment_log_dirs[experiment_id]


def _create_client() -> Optional[AsyncOpenAI]:
    if OPENAI_API_KEY and AsyncOpenAI is not None:
        return AsyncOpenAI(api_key=OPENAI_API_KEY)
    return None


OPENAI_CLIENT = _create_client()

# 長期記憶（RAG）の初期化状態を追跡
_memory_initialized = False

# シナリオコンテキストのキャッシュ
_scenario_context: Optional[Dict[str, Any]] = None


def load_scenario_context() -> Optional[Dict[str, Any]]:
    """
    scenario_context.jsonを読み込む（キャッシュ付き）
    """
    global _scenario_context
    if _scenario_context is not None:
        return _scenario_context

    context_path = Path(__file__).parent / "data" / "scenario_context.json"
    if context_path.exists():
        try:
            with open(context_path, "r", encoding="utf-8") as f:
                _scenario_context = json.load(f)
            print(f"[LLM SERVER] シナリオコンテキスト読み込み完了: {context_path}")
        except Exception as e:
            print(f"[LLM SERVER] シナリオコンテキスト読み込みエラー: {e}")
            _scenario_context = None
    else:
        print(
            f"[LLM SERVER] シナリオコンテキストファイルが見つかりません: {context_path}"
        )
    return _scenario_context


def _get_speed_description(speed_multiplier: float) -> str:
    """
    速度倍率を「自信」に関する表現に変換する

    Args:
        speed_multiplier: 速度倍率（1.0が通常速度）

    Returns:
        自信に関する表現（例: "徒歩での移動に自信があります"、"徒歩での移動には自信がありません"）
    """
    if speed_multiplier > 1.0:
        # 速い場合（自信がある）
        if speed_multiplier <= 1.2:
            return "徒歩での移動に自信があります"
        elif speed_multiplier <= 1.5:
            return "徒歩での移動に十分な自信があります"
        elif speed_multiplier <= 2.0:
            return "徒歩での移動に非常に自信があります"
        else:
            return "徒歩での移動に極めて自信があります"
    elif speed_multiplier < 1.0:
        # 遅い場合（自信がない）
        if speed_multiplier >= 0.8:
            return "徒歩での移動には少し不安があります"
        elif speed_multiplier >= 0.5:
            return "徒歩での移動には自信がありません"
        else:
            return "徒歩での移動には全く自信がありません"
    else:
        # 1.0の場合（通常速度）- この場合は何も表示しない
        return None


def _get_distance_description(distance_m: float, walking_time: float) -> str:
    """距離を感覚的な表現に変換する"""
    if distance_m <= 200:
        return "すぐ近く"
    elif distance_m <= 500:
        return "歩いて数分"
    elif distance_m <= 1000:
        return f"歩いて10分くらい"
    elif distance_m <= 2000:
        return f"歩いて20分くらい"
    elif distance_m <= 3000:
        return f"歩いて30分くらい"
    else:
        return f"かなり遠い（歩いて{int(walking_time)}分以上）"


def _get_elevation_description(elevation_m: float) -> str:
    """海抜を感覚的な表現に変換する"""
    if elevation_m >= 30:
        return "高台"
    elif elevation_m >= 20:
        return "やや高い場所"
    elif elevation_m >= 10:
        return "少し高い場所"
    else:
        return "低地"


def _get_capacity_description(current_capacity: int, max_capacity: int) -> str:
    """収容状況を感覚的な表現に変換する"""
    if current_capacity >= 2147483647:  # int.MaxValue
        return "広い場所"

    if max_capacity > 0:
        ratio = current_capacity / max_capacity
        if ratio >= 0.8:
            return "まだ余裕がある"
        elif ratio >= 0.5:
            return "半分くらい埋まっている"
        elif ratio >= 0.2:
            return "かなり混雑している"
        else:
            return "ほぼ満員"

    # max_capacityがない場合は残り人数で判断
    if current_capacity >= 500:
        return "まだ余裕がある"
    elif current_capacity >= 100:
        return "それなりに受け入れ可能"
    elif current_capacity >= 30:
        return "あまり余裕がない"
    else:
        return "ほぼ満員"


def _get_shelter_info_from_context(shelter_name: str) -> Optional[Dict[str, Any]]:
    """シナリオコンテキストから避難所情報を取得する（指定避難所と津波避難場所の両方を検索）"""
    scenario_context = load_scenario_context()
    if not scenario_context:
        return None

    evac_rules = scenario_context.get("evacuation_rules", {})

    # 指定避難所を検索
    designated_shelters = evac_rules.get("designated_shelters", [])
    for shelter in designated_shelters:
        if shelter.get("name") == shelter_name:
            return shelter

    # 津波避難場所を検索
    tsunami_areas = evac_rules.get("tsunami_evacuation_areas", [])
    for area in tsunami_areas:
        if area.get("name") == shelter_name:
            return area

    return None


def _get_shelter_features(shelter_name: str) -> list[str]:
    """シナリオコンテキストから避難所の特徴情報を取得する"""
    shelter_info = _get_shelter_info_from_context(shelter_name)
    if shelter_info:
        return shelter_info.get("features", [])
    return []


def _get_shelter_elevation_from_context(shelter_name: str) -> Optional[float]:
    """シナリオコンテキストから避難所の海抜情報を取得する（JSONを優先）"""
    shelter_info = _get_shelter_info_from_context(shelter_name)
    if shelter_info:
        return shelter_info.get("elevation_m")
    return None


def _get_building_perception(
    building: Dict[str, Any], seismic_intensity: int
) -> tuple[str, str]:
    """
    建物情報を避難者の感覚的表現に変換する

    Args:
        building: 建物情報の辞書
        seismic_intensity: 震度（0-7）

    Returns:
        (外観の感覚的表現, 損傷推定の文章) のタプル
    """
    height = building.get("height", 0)
    structure = building.get("structure_type", "")
    distance = building.get("distance", 0)
    is_wooden = "木造" in structure

    # 距離の感覚的表現
    if distance <= 5:
        dist_desc = "すぐ近く"
    elif distance <= 15:
        dist_desc = f"{distance:.0f}m先"
    elif distance <= 30:
        dist_desc = f"{distance:.0f}m先"
    else:
        dist_desc = f"{distance:.0f}m先"

    # 建物の外観表現
    if height > 15 and not is_wooden:
        appearance = "高くて頑丈そうな建物"
    elif height > 10 and is_wooden:
        appearance = "高い木造の建物"
    elif height > 10 and not is_wooden:
        appearance = "中くらいの鉄筋の建物"
    elif height <= 5 and is_wooden:
        appearance = "低い古そうな建物"
    elif height <= 5 and not is_wooden:
        appearance = "低い鉄筋の建物"
    else:
        # 5m < height <= 10m
        if is_wooden:
            appearance = "木造2階建ての家"
        else:
            appearance = "2階建ての建物"

    # 損傷推定（震度6以上の場合）
    damage = ""
    if seismic_intensity >= 6:
        if is_wooden and height <= 5:
            damage = "傾いているように見える。危険だ"
        elif is_wooden:
            damage = "壁にひびが入っているかもしれない"
        elif height <= 5:
            damage = "窓ガラスが割れている可能性がある"
        else:
            damage = "揺れは大きかったが、建物自体は無事そうだ"

    return f"{dist_desc}に{appearance}がある", damage


def _get_location_description(
    env_context: Optional[Dict[str, Any]], scenario_context: Optional[Dict[str, Any]]
) -> str:
    """
    座標を地名+環境表現に変換する

    Args:
        env_context: 環境コンテキスト
        scenario_context: シナリオコンテキスト

    Returns:
        場所の説明文
    """
    parts = []

    # 地域名を取得
    region_name = ""
    if scenario_context:
        region = scenario_context.get("region", {})
        region_name = region.get("name", "")

    # 土地利用から場所の種類を推定
    land_use = ""
    if env_context:
        land_use = env_context.get("current_land_use", "")

    location_type = ""
    if land_use:
        if "住宅" in land_use:
            location_type = "住宅街"
        elif "商業" in land_use:
            location_type = "商店街付近"
        elif "田" in land_use or "畑" in land_use or "農" in land_use:
            location_type = "農地付近"
        elif "森林" in land_use or "山林" in land_use:
            location_type = "山林付近"
        elif "工業" in land_use:
            location_type = "工業地帯"

    # 地名と場所タイプを組み合わせ
    if region_name and location_type:
        parts.append(f"{region_name}の{location_type}")
    elif region_name:
        parts.append(region_name)
    elif location_type:
        parts.append(location_type)

    # 海抜情報を追加
    if env_context:
        elevation = env_context.get("current_elevation", -1)
        if elevation >= 0:
            parts.append(f"海抜{elevation:.0f}m")

    return "（" + "、".join(parts) + "）" if parts else ""


def build_system_prompt() -> str:
    """
    システムプロンプトを構築する（静的な内容のみ）。
    行動選択肢、出力形式の説明など、毎回変わらない情報を含む。

    2026-01-20改善: トークン効率向上のため以下を実施
    - 人間らしい反応を7項目から3項目に圧縮（重複・ペルソナで表現可能な項目を削除）
    - JSON例を1つの完全例 + 行動別追加フィールド表に変更（約500トークン削減）
    """
    lines = []

    lines.append(
        "あなたは予期せぬ災害に直面した一般市民です。与えられたペルソナに基づいて、その人物として自然に行動してください。"
    )
    lines.append("")

    # 人間らしさの強調（3項目に圧縮）
    lines.append("【重要】")
    lines.append("あなたは突然の災害に直面した普通の人間です：")
    lines.append("- 家族のことが心配で、安全より合流を優先してしまうことがある")
    lines.append("- 情報が不足すると判断に迷い、様子を見てしまうことがある")
    lines.append("- 「まだ大丈夫」と危険を過小評価することがある")
    lines.append("")
    lines.append("「正しい避難行動」ではなく「その人らしい行動」を選択してください。")
    lines.append("")

    # 出力形式（簡潔化）（2026-01-21改善: long_term_goal/mid_term_planの出力を強制）
    lines.append("【出力形式】JSON1つのみ出力。以下の全フィールドを必ず含めてください:")
    lines.append("- action_type: EVACUATE/STAY/SEARCH_FAMILY/CONTACT/FOLLOW/TALK")
    lines.append("- long_term_goal: 今一番やりたいこと（1文）※省略不可")
    lines.append("- mid_term_plan: 次にやること（1文）※省略不可")
    lines.append("- reasoning: 判断理由（2-3文）")
    lines.append("- confidence: 0.0-1.0")
    lines.append("")

    # 2つの完全例（2026-01-21改善: STAY + EVACUATEの例を提示、selected_shelter_id必須を明示）
    lines.append("例1（様子見）:")
    lines.append(
        '{"action_type": "STAY", "long_term_goal": "まず状況を把握したい", "mid_term_plan": "周囲の様子を確認する", "reasoning": "揺れは収まったが、まだ何が起きているかわからない。まず状況を確認したい。", "confidence": 0.5}'
    )
    lines.append("")
    lines.append("例2（避難）:")
    lines.append(
        '{"action_type": "EVACUATE", "selected_shelter_id": "望洋荘", "long_term_goal": "安全な場所に逃げたい", "mid_term_plan": "高台の避難所に向かう", "reasoning": "津波警報が出ている。すぐに高台に避難すべきだ。", "confidence": 0.8, "desired_speed": "急ぎ足"}'
    )
    lines.append("")

    # 行動別追加フィールド（表形式で簡潔に、必須を明示）
    lines.append("【行動別の追加フィールド】")
    lines.append(
        "- EVACUATE: selected_shelter_id（避難所名）※必須, desired_speed（ゆっくり/普通/急ぎ足/走る）"
    )
    lines.append("- SEARCH_FAMILY: target_family_member（家族名）")
    lines.append("- CONTACT: contact_target（連絡先）, contact_message（メッセージ）")
    lines.append("- FOLLOW: target_evacuee_id（避難者ID）")
    lines.append(
        "- TALK: talk_target_id（避難者ID）, talk_topic（話題）, talk_message（内容）"
    )
    lines.append("- STAY: 追加フィールドなし")
    lines.append("")

    # 交通シミュレーション用の追加フィールド（任意）
    lines.append("【交通シミュレーション（任意フィールド）】")
    lines.append(
        "- recommended_transport_mode: WALKING/DRIVING/ABANDON_VEHICLE（必要時のみ）"
    )
    lines.append(
        "  ・WALKING: 徒歩で避難（現状が徒歩ならそのまま）"
    )
    lines.append(
        "  ・DRIVING: 車で避難（車両を保有し、運転可能な場合のみ）"
    )
    lines.append(
        "  ・ABANDON_VEHICLE: 走行中に渋滞がひどく、車を降りて徒歩に切替えたい場合"
    )
    lines.append(
        "  ※【周辺の交通状況】で大渋滞が示されており、現在DRIVING中なら、ABANDON_VEHICLEを検討してください。"
    )
    lines.append("")

    # 注意事項（簡潔化）
    lines.append("【注意】表示されているID・避難所名を正確に使用すること。")

    return "\n".join(lines)


def _get_normalcy_bias_prompt() -> str:
    """正常性バイアスのプロンプト（2026-01-21改善: バイアス表現を削除し、経験・考え方として記述）"""
    return """あなたは以下のような経験や考え方を持っています：
- 過去の経験から「今回も大丈夫かもしれない」と考えることがあります
- 周囲の人々の行動や警報は判断材料の一つですが、楽観視します
- 確実な情報がないまま動くのは非合理的だと考えています
- 危険が明確であったり、周囲の多くが避難し始めたら、自分も行動を見直します"""


def _get_conformity_bias_prompt() -> str:
    """同調バイアスのプロンプト（2026-01-21改善: バイアス表現を削除）"""
    return """あなたは以下のような考え方を持っています：
- 周囲の多くの人が同じ方向に動いている場合、その判断を参考にします
- ただし、避難先が明確な場合は自分で避難先を選択できます
- 周囲が避難を始めたら、自分も避難を開始する傾向があります
- 判断に迷った場合のみ、他者について行くことを選びます"""


def _get_combined_bias_prompt() -> str:
    """複合バイアスのプロンプト（2026-01-21追加: 正常性+同調を統合した自然な表現）"""
    return """あなたは以下のような経験や考え方を持っています：
- 過去の経験から「今回も大丈夫かもしれない」と考えることがあります
- 確実な情報がないまま動くのは非合理的だと考えています
- 周囲の人々の行動は重要な判断材料です。多くの人が避難していなければ様子を見ます
- 一方で、周囲の多くが避難し始めたら、自分も行動を見直します
- 判断に迷った場合は、周囲の動きを参考にして決めます"""


def build_user_prompt(
    payload: Dict[str, Any], agent_input: Optional[AgentInput]
) -> str:
    """
    ユーザープロンプトを構築する（動的な内容）。
    ペルソナ、現在の状況、避難所情報など、リクエストごとに変わる情報を含む。

    セクション構成:
    A. あなた自身に関する情報（WHO）
    B. 災害に関する情報（WHAT）
    C. 場所に関する情報（WHERE）
    D. 家族に関する情報（FAMILY）
    E. 記憶・履歴（MEMORY）
    F. 行動選択肢（OPTIONS）
    """
    shelters = payload.get("shelter_candidates", [])
    evacuee = payload.get("evacuee", {})
    persona = payload.get("persona")
    scenario_id = payload.get("scenario_id", "mild_quake")

    lines = []

    # ========================================
    # A. あなた自身に関する情報（WHO）
    # ========================================

    # A-1. ペルソナ情報（2026-01-20改善: 名前・年齢・役割を1行に統合、土地勘・家族概要を削除）
    if persona:
        lines.append("【あなたのペルソナ】")
        # 名前・年齢・役割を1行に統合
        name = persona.get("name", "不明")
        age_group = persona.get("age_group", "不明")
        role = persona.get("role", "不明")
        lines.append(f"{name}（{age_group}・{role}）")

        lines.append(f"心理状態: {persona.get('mental_state', '不明')}")
        lines.append(f"優先事項: {persona.get('priority', '不明')}")

        # 拡張フィールド: 生活背景情報（自宅情報は維持）
        home_category = persona.get("home_location_category")
        home_elevation = persona.get("home_elevation", 0)
        home_structure = persona.get("home_structure")
        if home_category or home_elevation or home_structure:
            home_category_ja = {
                "coastal": "海岸沿い",
                "hill": "高台",
                "center": "地区中心部",
            }.get(home_category, home_category or "不明")
            structure_ja = {
                "wooden_1story": "木造平屋",
                "wooden_2story": "木造2階建て",
                "rc_2story": "鉄筋コンクリート2階建て",
                "rc_3story": "鉄筋コンクリート3階建て",
            }.get(home_structure, home_structure or "")
            home_desc = f"自宅: {home_category_ja}、海抜{home_elevation}m"
            if structure_ja:
                home_desc += f"、{structure_ja}"
            lines.append(home_desc)

        # 居住歴（維持 - 土地勘の代わりとして機能）
        residence_years = persona.get("residence_years", 0)
        if residence_years > 0:
            lines.append(f"居住歴: この地域に{residence_years}年")

        # 土地勘フィールドは削除（居住歴で代替）

        current_reason = persona.get("current_location_reason")
        if current_reason:
            lines.append(f"現在ここにいる理由: {current_reason}")

        # 家族の状況（概要）は削除（セクションD「あなたの家族情報」と重複のため）

        past_disaster = persona.get("past_disaster_experience")
        if past_disaster:
            lines.append(f"災害経験: {past_disaster}")

        physical_condition = persona.get("physical_condition")
        if physical_condition and physical_condition != "健康":
            lines.append(f"身体状態: {physical_condition}")

        lines.append("")
        lines.append(persona.get("system_prompt_context", ""))
        lines.append("")

        # 2026-01-21改善: 災害経験をより強調（正常性バイアスの自然な再現）
        if past_disaster:
            lines.append("【あなたの過去の経験から】")
            lines.append(past_disaster)
            lines.append("この経験を踏まえて、今回も同じように考えるかもしれません。")
            lines.append("")

    # A-2. あなたの状態（2026-01-20改善: エネルギー削除、移動速度を1行に統合）
    if agent_input is not None:
        self_state = agent_input.self_state
        temporal = agent_input.temporal_context
        lines.append("【あなたの状態】")

        # 体力（stamina）情報
        stamina = getattr(self_state, "stamina", 1.0) if self_state else 1.0

        # 2026-01-21: ストレス関連（stress_label, stress_reason）を削除し、体力のみ表示
        lines.append(f"体力: {stamina:.0%}")

        # 移動速度を1行に統合
        current_speed = getattr(self_state, "current_speed_choice", "NORMAL")
        available_speeds = getattr(self_state, "available_speed_choices", None)
        if available_speeds is None:
            # デフォルトの選択肢を体力に基づいて生成
            available_speeds = ["ゆっくり", "普通"]
            if stamina >= 0.3:
                available_speeds.append("急ぎ足")
            if stamina >= 0.5:
                available_speeds.append("走る")

        speed_names = {
            "SLOW": "ゆっくり",
            "NORMAL": "普通",
            "FAST": "急ぎ足",
            "RUN": "走る",
        }
        current_speed_display = speed_names.get(current_speed, current_speed)
        lines.append(
            f"移動速度: {current_speed_display}（選択可能: {'/'.join(available_speeds)}）"
        )

        # 2026-01-21: stress_reasonを削除
        if self_state.current_goal:
            lines.append(f"現在の目標: {self_state.current_goal}")
        if temporal.time_limit is not None:
            remaining = max(temporal.time_limit - temporal.elapsed_time, 0.0)
            lines.append(
                f"時間制約: 残り{remaining:.1f}秒 / 総量 {temporal.time_limit:.1f}秒"
            )
        lines.append("")

    # A-3. 実験条件による心理的傾向（条件付き）
    bias_condition = payload.get("bias_condition", "none")
    override_bias = payload.get("override_persona_bias", False)

    if override_bias and bias_condition != "none":
        lines.append("【実験条件による心理的傾向】")

        if bias_condition == "normalcy_bias":
            lines.append(_get_normalcy_bias_prompt())
        elif bias_condition == "conformity_bias":
            lines.append(_get_conformity_bias_prompt())
        elif bias_condition == "combined":
            lines.append(_get_combined_bias_prompt())

        lines.append("")
        lines.append("上記の心理的傾向を踏まえて、あなたらしい行動を選択してください。")
        lines.append("")

    # A-4. 移動能力の注釈（条件付き）
    if persona:
        speed_multiplier = persona.get("speed_multiplier", 1.0)
        speed_desc = _get_speed_description(speed_multiplier)
        if speed_desc is not None:
            if speed_multiplier < 1.0:
                lines.append(f"※ {speed_desc}")
            elif speed_multiplier > 1.0:
                lines.append(f"※ {speed_desc}")
            lines.append("")

    # ========================================
    # B. 災害に関する情報（WHAT）
    # 2026-01-20改善: 行動指示を削除、体感表現のみに、地域説明をセクションCに移動
    # ========================================

    # B-1. 状況（体感表現のみ、数値は警報で）
    lines.append("【状況】")

    # 地域名のみ（説明・標高範囲はセクションCに移動）
    scenario_context = load_scenario_context()
    region_description = None  # セクションCで使用するため保存
    region_elevation_range = None
    if scenario_context:
        region = scenario_context.get("region", {})
        if region:
            region_name = region.get("name", "不明")
            lines.append(f"あなたは現在、{region_name}にいます。")
            # 地域説明・標高範囲はセクションCに移動（ここでは保存のみ）
            region_description = region.get("description")
            geography = region.get("geography", {})
            region_elevation_range = geography.get("elevation_range")

    # B-1.5 災害フェーズを先に取得（状況描写で使用するため）
    environment_state = payload.get("environment_state")
    disaster_phase = (
        environment_state.get("disaster_phase", "") if environment_state else ""
    )

    # 体感表現（災害フェーズに連動）
    if scenario_id == "shindo_2":
        lines.append("地震が発生しました。少し揺れを感じる程度です。")
    elif scenario_id == "shindo_6":
        lines.append(
            "強い揺れが発生しました。室内の物が大きく揺れ、一部は棚から落ちています。"
        )
        lines.append("建物自体は大きく損壊していませんが、余震の可能性もあります。")
    elif scenario_id == "shindo_7_tsunami":
        # 災害フェーズに応じて状況描写を変化させる
        if disaster_phase == "Shaking":
            lines.append(
                "非常に強い揺れが長く続き、身動きが取れませんでしたが、ようやく収まりました。"
            )
            lines.append(
                "棚から物が落ちて散乱しています。まだ余震があるかもしれません。"
            )
        elif disaster_phase == "InfoGap":
            lines.append("揺れは収まりましたが、棚から物が落ちて散乱しています。")
            lines.append(
                "停電しているようで、テレビもつきません。何が起きているのかよくわかりません。"
            )
        elif disaster_phase in [
            "WarningIssued",
            "PreArrival",
            "FirstWave",
            "MainWave",
            "Aftermath",
        ]:
            lines.append("非常に強い揺れが発生しました。棚から物が落ちています。")
            lines.append("津波警報が発令されているようです。")
        else:
            # デフォルト（フェーズ不明の場合）
            lines.append(
                "非常に強い揺れが発生しました。立っていられないほどの激しい揺れで、棚から物が落ちています。"
            )
    else:
        lines.append("地震が発生しました。揺れはそれほど大きくありません。")

    # B-2. 災害フェーズ（簡潔に1-2行）
    # environment_stateとdisaster_phaseは上で取得済み
    if environment_state:
        phase_display = environment_state.get("disaster_phase_display")

        # フェーズに応じた簡潔な説明
        phase_descriptions = {
            "Shaking": "安全な場所で身を守ってください",
            "InfoGap": "情報が入りにくい状況です",
            "WarningIssued": "津波警報発表中",
            "PreArrival": "津波が接近しています",
            "FirstWave": "津波の第1波が到達中",
            "MainWave": "津波の本波が到達中",
        }
        desc = phase_descriptions.get(disaster_phase, "")
        if phase_display and desc:
            lines.append("")
            lines.append(f"【災害フェーズ】{phase_display} - {desc}")

        # 停電・防災無線故障の状態のみ表示（震度・津波高さはJアラートに移動）
        is_power_on = environment_state.get("is_power_on", True)
        if not is_power_on:
            lines.append("※ 現在停電中")

        is_radio_working = environment_state.get("is_radio_working", True)
        if not is_radio_working:
            lines.append("※ 防災無線が故障中")

    # B-2.5. 情報状況（Shaking/InfoGapフェーズのみ）
    # 2026-01-21改善: 情報がないことを明示的に伝える
    if disaster_phase == "Shaking":
        lines.append("")
        lines.append("【情報状況】")
        lines.append("まだ何が起きているのかわかりません。")
        lines.append("テレビもラジオも確認できていません。")
    elif disaster_phase == "InfoGap":
        lines.append("")
        lines.append("【情報状況】")
        lines.append("停電でテレビがつきません。")
        lines.append("スマホを見ても、まだ詳しい情報は入ってきていません。")

    # B-3. Jアラート（スマホの緊急速報）- 震度・津波高さを含める
    # 2026-01-21改善: 警報はWarningIssued以降のフェーズでのみ表示
    has_received_j_alert = payload.get("has_received_j_alert", False)
    last_j_alert_message = payload.get("last_j_alert_message", "")
    should_show_warning = disaster_phase in [
        "WarningIssued",
        "PreArrival",
        "FirstWave",
        "MainWave",
        "Aftermath",
    ]
    if has_received_j_alert and last_j_alert_message and should_show_warning:
        lines.append("")
        lines.append("【スマホの緊急速報（Jアラート）】")
        lines.append("あなたのスマートフォンに、次のような警報が届きました:")
        # 震度・津波高さをJアラートメッセージに追加
        alert_parts = [last_j_alert_message]
        if environment_state:
            seismic = environment_state.get("seismic_intensity", 0)
            tsunami_height = environment_state.get("tsunami_height", 0)
            if seismic > 0:
                alert_parts.append(f"震度{seismic}")
            if tsunami_height > 0:
                alert_parts.append(f"予想津波高さ{tsunami_height}m")
        lines.append(" ".join(alert_parts))

    # B-4. 防災行政無線の放送（条件付き）- 別セクションで維持
    # 2026-01-21改善: 警報はWarningIssued以降のフェーズでのみ表示
    has_heard_broadcast = payload.get("has_heard_broadcast", False)
    last_broadcast_message = payload.get("last_broadcast_message", "")
    if has_heard_broadcast and last_broadcast_message and should_show_warning:
        lines.append("")
        lines.append("【防災行政無線の放送】")
        lines.append("あなたは屋外スピーカーからの放送を聞きました:")
        lines.append(last_broadcast_message)

    # B-6. 行政からの追加情報（条件付き・実験3用）
    # 2026-01-21改善: 警報はWarningIssued以降のフェーズでのみ表示
    information_strategy = payload.get("information_strategy", "standard")
    administrative_guidance = payload.get("administrative_guidance")
    if administrative_guidance and should_show_warning:
        lines.append("")
        lines.append("【行政からの追加情報】")

        # 避難ガイダンスを表示
        evacuation_guidance = administrative_guidance.get("evacuation_guidance", "")
        if evacuation_guidance:
            lines.append(evacuation_guidance)

        # 詳細情報（具体性が高い場合）
        if information_strategy in ["detailed", "detailed_urgent"]:
            # 津波高さ情報
            initial_height = administrative_guidance.get("initial_tsunami_height", 0)
            updated_height = administrative_guidance.get("updated_tsunami_height", 0)
            exceeds_assumption = administrative_guidance.get(
                "exceeds_assumption", False
            )

            if updated_height > initial_height:
                lines.append("")
                lines.append(f"【津波高さ情報の更新】")
                lines.append(
                    f"当初予想: {initial_height}m → 最新予想: {updated_height}m"
                )
                if exceeds_assumption:
                    lines.append("※ ハザードマップの想定を超える可能性があります。")

            # 推奨避難所
            recommended_shelters = administrative_guidance.get(
                "recommended_shelters", []
            )
            if recommended_shelters:
                lines.append("")
                lines.append("《推奨される避難先》")
                for shelter in recommended_shelters[:5]:  # 上位5件
                    lines.append(f"・{shelter}")

            # 不安全な避難所
            unsafe_shelters = administrative_guidance.get("unsafe_shelters", [])
            if unsafe_shelters:
                lines.append("")
                lines.append("《高さが不十分な可能性のある避難所》")
                for shelter in unsafe_shelters[:3]:  # 上位3件
                    lines.append(f"・{shelter}")

        # 追加警告（切迫感が高い場合）
        additional_warning = administrative_guidance.get("additional_warning")
        if additional_warning:
            lines.append("")
            lines.append(f"【重要な警告】")
            lines.append(additional_warning)

    # B-7. 聴覚情報（知覚状態から）（条件付き）
    perception_state = payload.get("perception_state")
    if perception_state:
        audio_info = []

        # 地鳴り
        if perception_state.get("has_heard_rumble", False):
            rumble_intensity = perception_state.get("rumble_intensity", 0)
            if rumble_intensity > 0.7:
                audio_info.append(
                    "非常に強い地鳴りが聞こえます。津波が近づいている可能性があります。"
                )
            elif rumble_intensity > 0.3:
                audio_info.append("地鳴りのような音が聞こえます。")

        # サイレン
        if perception_state.get("has_heard_siren", False):
            audio_info.append("津波警報のサイレンが鳴り響いています。")

        # 消防団の呼びかけ
        if perception_state.get("has_heard_fire_truck", False):
            fire_truck_msg = perception_state.get("last_fire_truck_message", "")
            if fire_truck_msg:
                audio_info.append(
                    f"消防団の呼びかけが聞こえました: 「{fire_truck_msg}」"
                )
            else:
                audio_info.append("消防団の呼びかけが聞こえました。")

        if audio_info:
            lines.append("")
            lines.append("【聞こえた音・声】")
            for info in audio_info:
                lines.append(f"- {info}")

    # ========================================
    # C. 場所に関する情報（WHERE）
    # 2026-01-20改善: 地域説明・標高範囲をセクションBから移動
    # ========================================

    # C-0. 地域の特性（セクションBから移動）
    if region_description or region_elevation_range:
        lines.append("")
        lines.append("【この地域について】")
        if region_description:
            lines.append(region_description)
        if region_elevation_range:
            lines.append(f"標高範囲: {region_elevation_range}")

    # C-1. あなたの現在位置（2026-01-20改善: 座標→地名+環境表現）
    # 環境コンテキスト（周辺建物情報）を取得（先に取得して場所説明に使用）
    env_context = payload.get("environmental_context")

    lines.append("")
    lines.append("【あなたの現在位置】")
    # 地名+環境表現に変換
    location_desc = _get_location_description(env_context, scenario_context)
    if location_desc:
        lines.append(location_desc)

    # デバッグログ: environmental_contextの有無を確認
    if env_context is None:
        print("[LLM SERVER] WARNING: environmental_context is None")
    elif not env_context.get("nearby_buildings"):
        print(
            f"[LLM SERVER] WARNING: environmental_context exists but nearby_buildings is empty or None: {env_context}"
        )
    else:
        print(
            f"[LLM SERVER] INFO: environmental_context received with {len(env_context.get('nearby_buildings', []))} buildings"
        )

    # C-3. 今いる場所の様子
    # 2026-01-21改善: Shaking/InfoGapフェーズでは津波リスク情報を表示しない
    if env_context:
        lines.append("")
        lines.append("【今いる場所の様子】")

        terrain_perceptions = []
        location_awareness = []

        # 標高に基づく環境認識（数値ではなく感覚的な表現）
        elevation = env_context.get("current_elevation", -1)
        if elevation >= 0:
            if disaster_phase in ["Shaking", "InfoGap"]:
                # 揺れ直後/情報空白期は津波への危機感を煽る情報を表示しない
                if elevation < 5:
                    terrain_perceptions.append("低い場所にいるようだ")
                elif elevation < 15:
                    terrain_perceptions.append("それほど高くない場所にいる")
                elif elevation < 30:
                    terrain_perceptions.append("少し高い場所にいるようだ")
                else:
                    terrain_perceptions.append("かなり高い場所にいる")
            else:
                # WarningIssued以降は詳細な危機情報を表示
                if elevation < 5:
                    terrain_perceptions.append("海や川がすぐ近くに見える低い場所だ")
                    terrain_perceptions.append(
                        "津波が来たらすぐに水に浸かりそうな気がする"
                    )
                elif elevation < 15:
                    terrain_perceptions.append("それほど高くない場所にいる")
                    terrain_perceptions.append(
                        "海からの距離を考えると、津波の時は不安だ"
                    )
                elif elevation < 30:
                    terrain_perceptions.append("少し高い場所にいるようだ")
                else:
                    terrain_perceptions.append("かなり高い場所にいる")
                    terrain_perceptions.append("ここなら津波の心配は少なそうだ")

        # 津波リスク区域の認識（地元民としての土地勘）
        # 2026-01-21改善: Shaking/InfoGapフェーズでは表示しない
        if env_context.get("is_in_tsunami_zone") and disaster_phase not in [
            "Shaking",
            "InfoGap",
        ]:
            location_awareness.append(
                "この辺りは昔から「津波が来たら危ない場所」と言われている"
            )
            depth = env_context.get("tsunami_estimated_depth", 0)
            if depth >= 5:
                location_awareness.append(
                    "大津波が来れば、建物の2階でも危ないかもしれない"
                )
            elif depth >= 2:
                location_awareness.append("津波が来れば1階は水に浸かるだろう")

        # 土砂災害リスクの認識（地震による二次災害の判断）
        if env_context.get("is_in_special_landslide_zone"):
            landslide_type = env_context.get("landslide_type", "")
            terrain_perceptions.append("すぐ近くに急な崖や斜面が見える")
            location_awareness.append("さっきの地震で地盤が緩んでいるかもしれない")
            location_awareness.append(
                "余震が来たら崩れる危険がある。ここは早く離れた方がいい"
            )
            if "急傾斜" in landslide_type:
                location_awareness.append(
                    "この崖は地震の揺れで崩れやすくなっているはずだ"
                )
                terrain_perceptions.append(
                    "崖の上から小石がパラパラ落ちてきている気がする"
                )
            elif "土石流" in landslide_type:
                location_awareness.append("山の上から土砂が流れてくるかもしれない")
                location_awareness.append("この谷筋は危険だ。横方向に逃げた方がいい")
            elif "地すべり" in landslide_type:
                location_awareness.append("この斜面全体が動き出すかもしれない")
                location_awareness.append("地面にひび割れがないか注意しながら進もう")
        elif env_context.get("is_in_landslide_zone"):
            landslide_type = env_context.get("landslide_type", "")
            terrain_perceptions.append("近くに斜面や崖がある")
            location_awareness.append("地震の後は土砂崩れが起きやすい。注意が必要だ")
            if "急傾斜" in landslide_type:
                location_awareness.append("あの崖沿いは通らない方が安全だろう")
            elif "土石流" in landslide_type:
                location_awareness.append("沢沿いの道は避けた方がいいかもしれない")
            elif "地すべり" in landslide_type:
                location_awareness.append("斜面の様子に気をつけながら進もう")

        # 土地利用の認識（見てわかる情報）
        land_use = env_context.get("current_land_use", "")
        if land_use:
            if "住宅" in land_use:
                terrain_perceptions.append("住宅が立ち並ぶ場所だ")
            elif "商業" in land_use:
                terrain_perceptions.append("お店や建物が多い場所だ")
            elif "田" in land_use or "畑" in land_use or "農" in land_use:
                terrain_perceptions.append("田畑が広がる開けた場所だ")
            elif "森林" in land_use or "山林" in land_use:
                terrain_perceptions.append("木々に囲まれた場所だ")

        # 道路の認識（見てわかる情報）
        road_type = env_context.get("nearest_road_type", "")
        if road_type:
            if "国道" in road_type or "高速" in road_type:
                terrain_perceptions.append("大きな道路が近くにある")
            elif "県道" in road_type or "都道府県道" in road_type:
                terrain_perceptions.append("県道沿いにいる")

        # 環境認識を出力
        if terrain_perceptions:
            for perception in terrain_perceptions:
                lines.append(f"- {perception}")

        # C-4. この場所について知っていること（条件付き）
        if location_awareness:
            lines.append("")
            lines.append("【この場所について知っていること】")
            for awareness in location_awareness:
                lines.append(f"- {awareness}")

    # C-5. 周辺環境の情報（建物情報）
    # 2026-01-20改善: 10件→3件に削減、感覚的表現+損傷推定に変換、総数は維持
    if env_context and env_context.get("nearby_buildings"):
        lines.append("")
        lines.append("【周辺の建物】")
        # 総数を明示（検索範囲の詳細は削除）
        total_buildings = env_context.get(
            "total_buildings_in_area", len(env_context["nearby_buildings"])
        )
        lines.append(f"周囲に{total_buildings}件の建物があります。")
        lines.append("")

        # 震度を取得（損傷推定に使用）
        seismic_intensity = 0
        if environment_state:
            seismic_intensity = environment_state.get("seismic_intensity", 0)

        # 上位3件のみ表示
        for idx, building in enumerate(env_context["nearby_buildings"][:3], 1):
            appearance, damage = _get_building_perception(building, seismic_intensity)
            lines.append(f"{idx}. {appearance}")
            if damage:
                lines.append(f"   → {damage}")

    # ========================================
    # C-EXT. 交通状況（TRAFFIC）
    # ========================================
    nearby_traffic = payload.get("nearby_traffic")
    transport_mode = payload.get("transport_mode", "WALKING")

    if transport_mode == "DRIVING":
        lines.append("")
        lines.append("【あなたの移動手段】")
        lines.append("現在、車で避難中です。")

    if nearby_traffic:
        summary = nearby_traffic.get("summary", "")
        if summary:
            lines.append("")
            lines.append("【周辺の交通状況】")
            lines.append(summary)

        nearby_roads = nearby_traffic.get("nearby_roads", [])
        congested_roads = [r for r in nearby_roads if r.get("congestion_level", 1) >= 3]
        if congested_roads:
            for road in congested_roads[:3]:
                name = road.get("road_name") or road.get("edge_id", "不明")
                label = road.get("congestion_label", "不明")
                avg_speed = road.get("average_speed_kmh", 0)
                ratio = 1.0
                fft = road.get("free_flow_travel_time", 0)
                tt = road.get("estimated_travel_time", 0)
                if fft > 0:
                    ratio = tt / fft
                lines.append(
                    f"  - {name}: {label}（平均{avg_speed:.0f}km/h、通常の{ratio:.1f}倍）"
                )

        overall = nearby_traffic.get("overall_congestion", 0)
        if overall > 0.5 and transport_mode == "DRIVING":
            lines.append("")
            lines.append("※ 渋滞がひどい場合は、車を降りて徒歩で避難することも検討してください。")

    # ========================================
    # D. 家族に関する情報（FAMILY）
    # ========================================

    # D-1. あなたの家族情報（詳細）
    family_members = payload.get("family_members", [])
    if family_members and len(family_members) > 0:
        lines.append("")
        lines.append("【あなたの家族情報】")

        # シーン内・シーン外で分類
        in_scene_members = [
            m for m in family_members if m.get("exists_in_scene", False)
        ]
        out_of_scene_members = [
            m for m in family_members if not m.get("exists_in_scene", False)
        ]

        for member in in_scene_members:
            name = member.get("name", "不明")
            relation = member.get("relation", "不明")
            location = member.get("likely_location", "不明")
            distance = member.get("distance_meters", 0)
            has_phone = member.get("has_phone", False)
            is_reunited = member.get("is_reunited", False)

            contact_status = "連絡可能" if has_phone else "連絡手段なし"
            if is_reunited:
                member_info = f"- {relation}: {name}（★合流済み★、距離: 約{distance:.0f}m、一緒に行動中）"
            else:
                member_info = f"- {relation}: {name}（{location}にいる可能性、距離: 約{distance:.0f}m、{contact_status}）"
            lines.append(member_info)

        # D-2. この地域外にいる家族（条件付き）
        if out_of_scene_members:
            lines.append("")
            lines.append("【この地域外にいる家族】（探索の対象外、連絡は可能）")
            for member in out_of_scene_members:
                name = member.get("name", "不明")
                relation = member.get("relation", "不明")
                location = member.get("likely_location", "不明")
                has_phone = member.get("has_phone", False)
                contact_status = "連絡可能" if has_phone else "連絡手段なし"
                member_info = (
                    f"- {relation}: {name}（{location}付近、{contact_status}）"
                )
                lines.append(member_info)

    # D-3. 直近の家族からの返信（条件付き）
    # 2026-01-20改善: 導入文を削除
    last_contact_message = payload.get("last_contact_message")
    if last_contact_message:
        lines.append("")
        lines.append("【直近の家族からの返信】")
        lines.append(last_contact_message)

    # ========================================
    # E. 記憶・履歴（MEMORY）
    # ========================================

    # E-1. 現在の長期目標
    current_goal = payload.get("current_long_term_goal")
    current_plan = payload.get("current_mid_term_plan")

    if current_goal:
        lines.append("")
        lines.append("【現在の長期目標】")
        primary_goal = current_goal.get("primary_goal", "未設定")
        if primary_goal and primary_goal != "未設定":
            lines.append(f"主要目標: {primary_goal}")
        else:
            lines.append("主要目標: 未設定")

        secondary_goals = current_goal.get("secondary_goals")
        if secondary_goals and len(secondary_goals) > 0:
            lines.append(f"副次目標: {', '.join(secondary_goals)}")

        constraints = current_goal.get("constraints")
        if constraints and len(constraints) > 0:
            lines.append(f"制約条件: {', '.join(constraints)}")

    # E-2. 現在の中期計画
    if current_plan:
        lines.append("")
        lines.append("【現在の中期計画】")
        steps = current_plan.get("steps")
        if steps and len(steps) > 0:
            for idx, step in enumerate(steps, 1):
                lines.append(f"{idx}. {step}")
        else:
            lines.append("手順: 未設定")

        contingency = current_plan.get("contingency")
        if contingency:
            lines.append(f"緊急時の代替案: {contingency}")

    # E-2.5. 判断の一貫性（長期目標または中期計画がある場合のみ表示）
    # 2026-01-21追加: 前回の判断を維持しやすくする
    has_goal = (
        current_goal
        and current_goal.get("primary_goal")
        and current_goal.get("primary_goal") != "未設定"
    )
    has_plan = (
        current_plan
        and current_plan.get("steps")
        and len(current_plan.get("steps", [])) > 0
    )
    if has_goal or has_plan:
        lines.append("")
        lines.append("【判断の一貫性について】")
        lines.append("前回の判断を変える場合は、明確な理由が必要です。")
        lines.append(
            "「長期目標・中期計画」と「今回の行動の理由」を比較して、本当に変更が必要か考えてください。"
        )

    # E-3. 直近の行動履歴（条件付き）
    # 2026-01-20改善: 行動回数表示を削除
    action_history = payload.get("action_history")
    if action_history:
        all_actions = action_history.get("recent_actions", [])

        # 直近3件のみ取得
        recent_actions = get_recent_actions(all_actions, max_count=3)

        if recent_actions and len(recent_actions) > 0:
            lines.append("")
            lines.append("【直近の行動】")
            lines.append("")

            for action in recent_actions:
                timestamp = action.get("timestamp", 0)
                action_type = action.get("action_type", "不明")
                target = action.get("target", "")
                reasoning = action.get("reasoning", "")
                result = action.get("result", "completed")

                action_desc = {
                    "EVACUATE": "避難",
                    "STAY": "待機",
                    "SEARCH_FAMILY": "家族探索",
                    "CONTACT": "連絡",
                    "FOLLOW": "追従",
                    "TALK": "会話",
                }.get(action_type, action_type)

                line = f"- {timestamp:.0f}秒: {action_desc}"
                if target:
                    line += f"（{target}）"
                if reasoning:
                    # 長い理由は省略
                    short_reasoning = (
                        reasoning[:50] + "..." if len(reasoning) > 50 else reasoning
                    )
                    line += f" - {short_reasoning}"
                if result != "completed":
                    line += f" [結果: {result}]"

                lines.append(line)

    # E-4. 直近の会話履歴（条件付き）
    conversation_history = payload.get("conversation_history")
    if conversation_history:
        all_conversations = conversation_history.get("recent_conversations", [])
        total_count = conversation_history.get("total_conversation_count", 0)

        # 直近5件のみ取得
        recent_conversations = get_recent_conversations(all_conversations, max_count=5)

        if recent_conversations and len(recent_conversations) > 0:
            lines.append("")
            lines.append("【直近の会話履歴】")
            lines.append(f"これまでに{total_count}回の会話を行っています。直近5件:")
            lines.append("")
            for conv in recent_conversations:
                partner_name = conv.get("partner_name", "不明")
                topic = conv.get("topic", "一般")
                my_message = conv.get("my_message", "")
                partner_response = conv.get("partner_response", "")
                i_initiated = conv.get("i_initiated", True)

                topic_desc = {
                    "ShelterInfo": "避難所情報",
                    "CurrentSituation": "現状確認",
                    "ActionAdvice": "行動相談",
                    "SafetyConfirm": "安全確認",
                    "General": "一般",
                }.get(topic, topic)

                lines.append(f"会話相手: {partner_name}（話題: {topic_desc}）")
                if i_initiated:
                    lines.append(f"自分: {my_message}")
                    lines.append(f"{partner_name}: {partner_response}")
                else:
                    lines.append(f"{partner_name}: {my_message}")
                    lines.append(f"自分: {partner_response}")
                lines.append("")

    # E-5. あなたの記憶・知識（長期記憶）（条件付き）
    # 2026-01-20改善: 導入文を削除
    long_term_memories = payload.get("long_term_memories", [])
    if long_term_memories and len(long_term_memories) > 0:
        lines.append("")
        lines.append("【あなたの記憶・知識】")
        lines.append("")

        for mem in long_term_memories:
            memory_type = mem.get("memory_type", "unknown")
            content = mem.get("content", "")

            type_label = {
                "regional_knowledge": "地域知識",
                "persona_knowledge": "自己認識",
                "disaster_experience": "過去の経験",
            }.get(memory_type, "記憶")

            lines.append(f"[{type_label}] {content}")

    # ========================================
    # F. 行動選択肢（OPTIONS）
    # ========================================

    # F-1. 周囲の状況（2026-01-21改善: FOLLOW誘発を抑制するため簡素化）
    nearby_evacuees = payload.get("nearby_evacuees", [])
    nearby_evacuees_count = payload.get("nearby_evacuees_count", 0)

    if nearby_evacuees_count > 0:
        lines.append("")
        lines.append("【周囲の状況】")

        # 人数は抽象的に表示（具体的な数は非表示）
        if nearby_evacuees_count <= 3:
            lines.append("周囲にはほとんど人がいません。")
        elif nearby_evacuees_count <= 10:
            lines.append("周囲に数人の人が見えます。")
        else:
            lines.append("周囲にはそれなりの人数がいます。")

        # 周囲の人の行動傾向を要約（実際のデータに基づく）
        evacuating_count = sum(
            1 for e in nearby_evacuees if e.get("current_action") == "EVACUATE"
        )
        staying_count = sum(
            1 for e in nearby_evacuees if e.get("current_action") == "STAY"
        )

        if evacuating_count > staying_count:
            lines.append("避難を始めている人もいるようです。")
        elif staying_count > evacuating_count:
            lines.append("まだ様子を見ている人が多いようです。")
        else:
            lines.append("人々の動きはまちまちです。")

        # 周囲の人の属性（役割・年齢層）と行動状態は表示するが、名前・目標避難所は非表示
        lines.append("")
        lines.append("周囲にいる人:")
        for idx, nearby_evacuee in enumerate(nearby_evacuees[:3], 1):
            evacuee_id = nearby_evacuee.get("id", "不明")
            evacuee_role = nearby_evacuee.get("role", "")
            evacuee_age = nearby_evacuee.get("age_group", "")
            distance = nearby_evacuee.get("distance_meters", 0)
            action = nearby_evacuee.get("current_action", "")

            # 役割・年齢層のみ表示（名前は非表示）
            attr_display = evacuee_role if evacuee_role else evacuee_age
            if not attr_display:
                attr_display = "人"

            # 行動状態を簡潔に表示（目標避難所は非表示）
            action_desc = {
                "EVACUATE": "急いで避難中",
                "STAY": "様子を見ている",
                "SEARCH_FAMILY": "家族を探している",
                "CONTACT": "連絡を取っている",
                "FOLLOW": "誰かについて行っている",
                "TALK": "話している",
            }.get(action, "")

            lines.append(
                f"{idx}. {attr_display} [ID: {evacuee_id}] - 約{distance:.0f}m先、{action_desc}"
            )

        lines.append("")
        lines.append("※話しかける(TALK)、ついていく(FOLLOW)場合はIDを指定")

    # F-2. 避難先候補（2026-01-20改善: 避難所と津波避難場所を統合）
    # 2026-01-21改善: Shaking/InfoGapフェーズでは避難先情報を制限
    lines.append("")
    lines.append("【避難先候補】")

    if disaster_phase in ["Shaking", "InfoGap"]:
        # 揺れ直後や情報空白期は避難先が明確でない
        lines.append("まだ情報がなく、どこに避難すべきかはっきりしません。")
        lines.append(
            "近くに高い場所や頑丈な建物があるかもしれませんが、確認できていません。"
        )
    else:
        # WarningIssued以降は詳細表示
        # 全避難所を統合して海抜でソート
        all_shelters = shelters.copy()
        # 海抜情報を追加してソート
        for shelter in all_shelters:
            display_name = shelter.get("display_name", shelter.get("id", "不明"))
            elevation_from_json = _get_shelter_elevation_from_context(display_name)
            shelter["_elevation"] = (
                elevation_from_json
                if elevation_from_json is not None
                else shelter.get("elevation_meters", 0)
            )
        all_shelters.sort(key=lambda s: s.get("_elevation", 0), reverse=True)

        for idx, shelter in enumerate(all_shelters[:5], 1):
            display_name = shelter.get("display_name", shelter.get("id", "不明"))
            distance_m = shelter.get("distance_meters", 0)
            walking_time = shelter.get("walking_time_minutes", 0)
            elevation_meters = shelter.get("_elevation", 0)
            features = _get_shelter_features(display_name)

            # 距離表示を「距離m/時間分」形式に変更
            if distance_m < 1000:
                distance_str = f"{distance_m:.0f}m"
            else:
                distance_str = f"{distance_m/1000:.1f}km"
            distance_time_str = f"{distance_str}/{walking_time:.0f}分"

            # 海抜は常に表示
            elevation_str = f"海抜{elevation_meters:.0f}m"

            # 高台かどうか
            elevation_desc = ""
            if elevation_meters >= 30:
                elevation_desc = "高台"
            elif elevation_meters >= 20:
                elevation_desc = "やや高台"

            # 特徴をまとめる
            characteristics = [distance_time_str, elevation_str]
            if elevation_desc:
                characteristics.append(elevation_desc)
            if features:
                characteristics.append(features[0])

            lines.append(f"{idx}. {display_name} - {' / '.join(characteristics)}")

    # F-4. 選択可能な行動（2026-01-21改善: 提示順序変更、説明文を中立的に）
    available_actions = payload.get("available_actions", [])
    if available_actions and len(available_actions) > 0:
        lines.append("")
        lines.append("【選択可能な行動】※この中から必ず1つ選んでください")

        action_descriptions = {
            "EVACUATE": "避難所・高台へ避難する",
            "STAY": "その場で状況を確認する",
            "SEARCH_FAMILY": "家族を探しに行く",
            "CONTACT": "家族へ連絡（メール等）",
            "FOLLOW": "近くの人について行く",
            "TALK": "近くの人に状況を聞く",
        }

        # 行動の提示順序を変更（EVACUATE優先、FOLLOW最後）
        action_order = [
            "EVACUATE",
            "CONTACT",
            "SEARCH_FAMILY",
            "TALK",
            "STAY",
            "FOLLOW",
        ]
        for action in action_order:
            if action in available_actions:
                desc = action_descriptions.get(action, action)
                lines.append(f"- {action}: {desc}")

        # 選択できない行動を明示（2026-01-20改善: 具体的理由を追加）
        all_actions = ["EVACUATE", "STAY", "SEARCH_FAMILY", "CONTACT", "FOLLOW", "TALK"]
        unavailable = [a for a in all_actions if a not in available_actions]
        if unavailable:
            lines.append("")
            unavailable_reasons = {
                "FOLLOW": "周囲30m以内に適切な対象がいない",
                "TALK": "周囲30m以内に会話可能な対象がいない",
                "CONTACT": "連絡可能な家族がいない",
                "SEARCH_FAMILY": "探索対象の家族がいない",
                "EVACUATE": "避難先が設定されていない",
                "STAY": "待機できない状況",
            }
            unavailable_str = ", ".join(
                [
                    f"{a}（{unavailable_reasons.get(a, '条件未達')}）"
                    for a in unavailable
                ]
            )
            lines.append(f"※選択不可: {unavailable_str}")

    return "\n".join(lines)


def _position_tuple(info: Dict[str, Any]) -> tuple[float, float, float]:
    pos = info.get("position", {}) if info else {}
    return (
        float(pos.get("x", 0.0)),
        float(pos.get("y", 0.0)),
        float(pos.get("z", 0.0)),
    )


def _extract_float(value: Any) -> Optional[float]:
    if value is None:
        return None
    try:
        number = float(value)
        if math.isnan(number) or math.isinf(number):
            return None
        return number
    except (TypeError, ValueError):
        return None


def _normalize_speed_choice(value: Any) -> Optional[str]:
    """
    速度選択肢を正規化する（日本語/英語両方対応）

    Args:
        value: LLMからのdesired_speed値（"ゆっくり", "SLOW", "急ぎ足", "FAST"など）

    Returns:
        正規化された速度選択肢（"SLOW", "NORMAL", "FAST", "RUN"）またはNone
    """
    if value is None:
        return "NORMAL"

    value_str = str(value).strip()
    if not value_str:
        return "NORMAL"

    # 日本語名と英語名のマッピング（大文字小文字を区別しない）
    speed_map = {
        "ゆっくり": "SLOW",
        "普通": "NORMAL",
        "急ぎ足": "FAST",
        "走る": "RUN",
        "slow": "SLOW",
        "normal": "NORMAL",
        "fast": "FAST",
        "run": "RUN",
    }

    normalized = speed_map.get(value_str.lower())
    if normalized:
        return normalized

    # 完全一致しない場合、大文字に変換して確認
    upper_value = value_str.upper()
    if upper_value in ["SLOW", "NORMAL", "FAST", "RUN"]:
        return upper_value

    return "NORMAL"


def heuristic_selection(payload: Dict[str, Any]) -> Optional[str]:
    shelters = payload.get("shelter_candidates", [])
    evacuee = payload.get("evacuee", {})
    if not shelters or not evacuee:
        return None

    ex, ey, ez = _position_tuple(evacuee)
    best_id = None
    best_distance = float("inf")

    for entry in shelters:
        capacity = entry.get("current_capacity", 0)
        if capacity <= 0:
            continue
        sx, sy, sz = _position_tuple(entry)
        distance = (ex - sx) ** 2 + (ez - sz) ** 2 + (ey - sy) ** 2
        if distance < best_distance:
            best_distance = distance
            best_id = entry.get("id")

    if best_id is None and shelters:
        best_id = random.choice(shelters).get("id")

    return best_id


def _normalize_temporal(raw: Dict[str, Any]) -> Dict[str, Any]:
    normalized = dict(raw)
    has_limit = normalized.pop("has_time_limit", None)
    if has_limit is not None and not has_limit:
        normalized["time_limit"] = None
    return normalized


def _build_agent_input(payload: Dict[str, Any]) -> Optional[AgentInput]:
    self_state = payload.get("self_state")
    temporal = payload.get("temporal_context") or payload.get("temporal")
    if self_state is None or temporal is None:
        return None
    try:
        return AgentInput.model_validate(
            {
                "self_state": self_state,
                "temporal_context": _normalize_temporal(temporal),
            }
        )
    except ValidationError as exc:  # pragma: no cover - logging only
        print(f"[LLM SERVER] AgentInput validation failed: {exc}")
        return None


def _classify_rate_limit_error(error_message: str) -> str:
    """
    レートリミットエラーの種類を判別する。

    Args:
        error_message: エラーメッセージ文字列

    Returns:
        エラー種類: "RPM" (リクエスト/分), "TPM" (トークン/分), "RPD" (リクエスト/日), "UNKNOWN"
    """
    error_str = str(error_message).upper()

    # デバッグ: 実際のエラーメッセージを出力
    print(f"[LLM SERVER] RateLimitError message for classification: {error_message}")

    # RPM (リクエスト/分) のパターン
    if (
        "RPM" in error_str
        or "REQUESTS PER MIN" in error_str
        or "REQUEST PER MINUTE" in error_str
    ):
        return "RPM"
    # TPM (トークン/分) のパターン
    elif (
        "TPM" in error_str
        or "TOKENS PER MIN" in error_str
        or "TOKEN PER MINUTE" in error_str
    ):
        return "TPM"
    # RPD (リクエスト/日) のパターン
    elif (
        "RPD" in error_str
        or "REQUESTS PER DAY" in error_str
        or "REQUEST PER DAY" in error_str
    ):
        return "RPD"
    # その他のレートリミット関連パターン
    elif "RATE LIMIT" in error_str or "RATELIMIT" in error_str:
        # 一般的なレートリミット（種類が特定できない場合）
        # 追加のヒントを探す
        if "REQUEST" in error_str and "MINUTE" in error_str:
            return "RPM"
        elif "TOKEN" in error_str and "MINUTE" in error_str:
            return "TPM"
        elif "REQUEST" in error_str and "DAY" in error_str:
            return "RPD"
        return "UNKNOWN"
    else:
        return "UNKNOWN"


def _get_rate_limit_description(limit_type: str) -> str:
    """
    レートリミットの種類に応じた日本語説明を返す。

    Args:
        limit_type: エラー種類 ("RPM", "TPM", "RPD", "UNKNOWN")

    Returns:
        日本語説明文
    """
    descriptions = {
        "RPM": "1分あたりのリクエスト数上限に達しました",
        "TPM": "1分あたりのトークン数上限に達しました",
        "RPD": "1日あたりのリクエスト数上限に達しました",
        "UNKNOWN": "レートリミットに達しました（種類不明）",
    }
    return descriptions.get(limit_type, descriptions["UNKNOWN"])


async def _call_openai_with_retry(
    system_prompt: str, user_prompt: str, max_retries: int = 3, base_delay: float = 1.0
) -> tuple[Optional[str], Optional[Dict[str, Any]]]:
    """
    Exponential backoffによるリトライ機能付きでOpenAI APIを呼び出す。

    Args:
        system_prompt: システムプロンプト（静的な指示）
        user_prompt: ユーザープロンプト（動的な状況情報）
        max_retries: 最大リトライ回数
        base_delay: 初回リトライまでの待機時間（秒）

    Returns:
        (LLMの応答テキスト, エラー情報) のタプル
        - 成功時: (content, None)
        - 失敗時: (None, {"error_type": str, "error_message": str, "rate_limit_type": str})
    """
    from openai import RateLimitError, APIError, APIConnectionError

    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]

    last_exception = None
    last_error_info = None
    for attempt in range(max_retries + 1):
        try:
            response = await OPENAI_CLIENT.chat.completions.create(
                model=OPENAI_MODEL,
                messages=messages,
                response_format={"type": "json_object"},
            )
            return response.choices[0].message.content, None
        except RateLimitError as e:
            last_exception = e
            limit_type = _classify_rate_limit_error(str(e))
            limit_description = _get_rate_limit_description(limit_type)
            last_error_info = {
                "error_type": "RateLimitError",
                "error_message": str(e),
                "rate_limit_type": limit_type,
                "rate_limit_description": limit_description,
            }
            if attempt < max_retries:
                delay = base_delay * (2**attempt)  # exponential backoff
                print(
                    f"[LLM SERVER] RateLimitError ({limit_type}: {limit_description}), retrying in {delay:.1f}s (attempt {attempt + 1}/{max_retries})"
                )
                await asyncio.sleep(delay)
            else:
                print(
                    f"[LLM SERVER] RateLimitError ({limit_type}: {limit_description}), max retries exceeded: {e}"
                )
        except (APIError, APIConnectionError) as e:
            last_exception = e
            last_error_info = {
                "error_type": "APIError",
                "error_message": str(e),
                "rate_limit_type": None,
                "rate_limit_description": None,
            }
            if attempt < max_retries:
                delay = base_delay * (2**attempt)
                print(
                    f"[LLM SERVER] API error, retrying in {delay:.1f}s (attempt {attempt + 1}/{max_retries}): {e}"
                )
                await asyncio.sleep(delay)
            else:
                print(f"[LLM SERVER] API error, max retries exceeded: {e}")
        except Exception as e:
            # その他の例外はリトライしない
            print(f"[LLM SERVER] Unexpected error: {e}")
            return None, {
                "error_type": "UnexpectedError",
                "error_message": str(e),
                "rate_limit_type": None,
                "rate_limit_description": None,
            }

    return None, last_error_info


async def call_openai(
    payload: Dict[str, Any], agent_input: Optional[AgentInput]
) -> tuple[Optional[Dict[str, Any]], str, str, Optional[Dict[str, Any]]]:
    """
    OpenAI APIを呼び出して避難行動の決定を取得する。

    Returns:
        (decision, system_prompt, user_prompt, error_info) のタプル
        - error_info: エラー発生時のエラー情報（正常時はNone）
    """
    system_prompt = build_system_prompt()
    user_prompt = build_user_prompt(payload, agent_input)

    if OPENAI_CLIENT is None:
        return None, system_prompt, user_prompt, None

    try:
        content, error_info = await _call_openai_with_retry(system_prompt, user_prompt)
        if content is None:
            return None, system_prompt, user_prompt, error_info
        decision = _safe_load_json(content)

        # action_typeが存在するか、または従来の形式（selected_shelter_idのみ）か確認
        action_type = decision.get("action_type", "EVACUATE")

        # EVACUATEの場合はselected_shelter_idが必須（2026-01-21改善: ない場合は自動補完）
        if action_type == "EVACUATE":
            if "selected_shelter_id" not in decision:
                # shelter_idがない場合は最寄りの避難所を自動選択（heuristicフォールバック防止）
                decision["selected_shelter_id"] = heuristic_selection(payload)
                print(
                    f"[LLM SERVER] EVACUATE行動のshelter_idを自動補完: {decision['selected_shelter_id']}"
                )
            return decision, system_prompt, user_prompt, None

        # STAY, SEARCH_FAMILY, CONTACT, FOLLOW の場合はaction_typeがあればOK
        if "action_type" in decision:
            action_type = decision.get("action_type")
            # FOLLOWの場合はtarget_evacuee_idが必要（2026-01-21改善: ない場合はSTAYに変更）
            if action_type == "FOLLOW":
                if "target_evacuee_id" not in decision:
                    print(
                        f"[LLM SERVER] FOLLOW行動だがtarget_evacuee_idがありません、STAYに変更: {decision}"
                    )
                    decision["action_type"] = "STAY"
                    reasoning = decision.get("reasoning", "")
                    decision["reasoning"] = (
                        reasoning + " (FOLLOW対象不明のためSTAYに変更)"
                    )

            # TALKの場合はtalk_target_idとtalk_messageが必要（2026-01-21改善: ない場合はSTAYに変更）
            if action_type == "TALK":
                if "talk_target_id" not in decision or "talk_message" not in decision:
                    missing = []
                    if "talk_target_id" not in decision:
                        missing.append("talk_target_id")
                    if "talk_message" not in decision:
                        missing.append("talk_message")
                    print(
                        f"[LLM SERVER] TALK行動だが{', '.join(missing)}がありません、STAYに変更: {decision}"
                    )
                    decision["action_type"] = "STAY"
                    reasoning = decision.get("reasoning", "")
                    decision["reasoning"] = (
                        reasoning + " (TALK情報不足のためSTAYに変更)"
                    )

            # 2026-01-21改善: LLMが文字列で返した場合、オブジェクト形式に変換
            # Unity側はLongTermGoalPayload/MidTermPlanPayload型を期待している
            if isinstance(decision.get("long_term_goal"), str):
                decision["long_term_goal"] = {
                    "primary_goal": decision["long_term_goal"],
                    "secondary_goals": [],
                    "constraints": [],
                }
                decision["should_update_goal"] = True

            if isinstance(decision.get("mid_term_plan"), str):
                decision["mid_term_plan"] = {
                    "steps": [decision["mid_term_plan"]],
                    "contingency": "",
                }
                decision["should_update_plan"] = True

            # 長期目標と中期計画の検証
            if decision.get("should_update_goal"):
                goal = decision.get("long_term_goal")
                if goal and not goal.get("primary_goal"):
                    print(
                        f"[LLM SERVER] WARNING: should_update_goal=true but long_term_goal.primary_goal is missing"
                    )

            if decision.get("should_update_plan"):
                plan = decision.get("mid_term_plan")
                if plan and not plan.get("steps"):
                    print(
                        f"[LLM SERVER] WARNING: should_update_plan=true but mid_term_plan.steps is missing"
                    )

            return decision, system_prompt, user_prompt, None

        # 従来の形式（action_typeなし、selected_shelter_idあり）もサポート
        if "selected_shelter_id" in decision:
            decision["action_type"] = "EVACUATE"  # デフォルトで追加
            return decision, system_prompt, user_prompt, None

    except Exception as exc:  # pragma: no cover
        print(f"[LLM SERVER] OpenAI呼び出しで例外: {exc}")
    return None, system_prompt, user_prompt, None


def _safe_load_json(text: str) -> Dict[str, Any]:
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        match = re.search(r"\{.*\}", text, re.DOTALL)
        if match:
            return json.loads(match.group(0))
        raise


def _sanitize_filename(name: Optional[str]) -> str:
    if not name:
        return "unknown"
    safe = re.sub(r"[^a-zA-Z0-9_.-]", "_", name)
    return safe or "unknown"


def _log_decision(
    evacuee_id: Optional[str],
    request_id: str,
    source: str,
    input_snapshot: Dict[str, Any],
    output_snapshot: Dict[str, Any],
    system_prompt: Optional[str] = None,
    user_prompt: Optional[str] = None,
    experiment_id: Optional[str] = None,
    episode_id: Optional[int] = None,
    episode_elapsed_time: Optional[float] = None,
) -> None:
    try:
        sanitized_id = _sanitize_filename(evacuee_id)

        # 日本時間（JST = UTC+9）で秒までのタイムスタンプを生成
        jst = timezone(timedelta(hours=9))
        timestamp_jst = datetime.now(jst).strftime("%Y-%m-%d %H:%M:%S")

        # 実験IDに対応するログディレクトリを取得
        log_dir = _get_log_dir(experiment_id)

        # 読みやすい形式のログファイル（.txt）
        log_filename = log_dir / f"{sanitized_id}.txt"

        # プロンプトを除いたメタデータ
        log_meta = {
            "timestamp": timestamp_jst,
            "episode_id": episode_id,
            "episode_elapsed_time": (
                round(episode_elapsed_time, 2)
                if episode_elapsed_time is not None
                else None
            ),
            "request_id": request_id,
            "source": source,
            "input": input_snapshot,
            "output": output_snapshot,
        }

        with log_filename.open("a", encoding="utf-8") as fp:
            # 区切り線
            fp.write("=" * 80 + "\n")

            # メタデータをJSON形式で出力
            fp.write(json.dumps(log_meta, ensure_ascii=False, indent=2))
            fp.write("\n\n")

            # システムプロンプトを出力
            if system_prompt:
                fp.write("-" * 80 + "\n")
                fp.write("[SYSTEM PROMPT]\n")
                fp.write("-" * 80 + "\n")
                fp.write(system_prompt)
                fp.write("\n\n")

            # ユーザープロンプトを出力
            if user_prompt:
                fp.write("-" * 80 + "\n")
                fp.write("[USER PROMPT]\n")
                fp.write("-" * 80 + "\n")
                fp.write(user_prompt)
                fp.write("\n\n")
    except Exception as exc:  # pragma: no cover
        print(f"[LLM SERVER] failed to write log: {exc}")


async def process_payload(payload: Dict[str, Any]) -> Dict[str, Any]:
    request_id = payload.get("request_id", f"req-{random.randint(0, 1_000_000)}")

    # 長期記憶（RAG）検索
    memory_manager = get_memory_manager()
    if memory_manager and memory_manager.is_initialized:
        try:
            # 現在の状況からクエリを構築
            evacuee = payload.get("evacuee", {})
            persona = payload.get("persona", {})
            scenario_id = payload.get("scenario_id", "")
            agent_id = evacuee.get("id")

            # agent_idを整数に変換（可能な場合）
            agent_id_int = None
            if agent_id is not None:
                try:
                    # "evacuee_1" のような形式からIDを抽出
                    if isinstance(agent_id, str) and "_" in agent_id:
                        agent_id_int = int(agent_id.split("_")[-1])
                    elif isinstance(agent_id, (int, float)):
                        agent_id_int = int(agent_id)
                except (ValueError, IndexError):
                    pass

            # 検索クエリを構築（避難者の目標・計画を優先的に使用）
            query_parts = []

            # 1. 長期目標をクエリに含める（最優先）
            current_goal = payload.get("current_long_term_goal")
            if current_goal:
                primary_goal = current_goal.get("primary_goal", "")
                if primary_goal and primary_goal != "未設定":
                    query_parts.append(primary_goal)

            # 2. 中期計画をクエリに含める
            current_plan = payload.get("current_mid_term_plan")
            if current_plan:
                steps = current_plan.get("steps", [])
                if steps and len(steps) > 0:
                    query_parts.append(steps[0])  # 最初のステップのみ

            # 3. シナリオに応じた基本文（目標・計画がない場合のフォールバック）
            if not query_parts:
                if scenario_id == "shindo_7_tsunami":
                    query_parts.append("津波から避難して安全な高台に逃げたい")
                elif scenario_id == "shindo_6":
                    query_parts.append("地震から身を守り安全を確保したい")
                else:
                    query_parts.append("災害から避難したい")

            query = " ".join(query_parts)

            # 長期記憶を検索（閾値を0.5に設定）
            memories = await memory_manager.search(
                query=query, agent_id=agent_id_int, top_k=3, threshold=0.5
            )

            if memories:
                payload["long_term_memories"] = memories
                print(f"[LLM SERVER] 長期記憶を{len(memories)}件取得しました")

        except Exception as e:
            print(f"[LLM SERVER] 長期記憶検索でエラー: {e}")

    # デバッグログ: 受信したペイロードのキーを確認
    print(f"[LLM SERVER] Received payload keys: {list(payload.keys())}")
    if "environmental_context" in payload:
        env_ctx = payload["environmental_context"]
        if env_ctx:
            print(
                f"[LLM SERVER] environmental_context type: {type(env_ctx)}, keys: {list(env_ctx.keys()) if isinstance(env_ctx, dict) else 'N/A'}"
            )
        else:
            print(
                f"[LLM SERVER] environmental_context is present but value is: {env_ctx}"
            )
    else:
        print("[LLM SERVER] environmental_context NOT in payload")

    agent_input = _build_agent_input(payload)

    # LLMを呼び出し（システムプロンプトとユーザープロンプトも返される）
    decision, system_prompt, user_prompt, error_info = await call_openai(
        payload, agent_input
    )
    evacuee = payload.get("evacuee", {})

    # input_snapshotは最小限に（shelter_candidatesとpersonaはpromptに含まれているため省略）
    # 2026-01-21: 未使用フィールドをログから除外
    # - energy_level, energy_label: 未使用
    # - injuries, injury_notes: 未使用
    # - stress_level, stress_label, stress_reason: プロンプトから除外したため未使用
    if agent_input:
        agent_input_dict = agent_input.model_dump()
        # self_stateから未使用フィールドを削除
        # 2026-01-21: stress_label, stress_reasonも削除（プロンプトから除外したため）
        if "self_state" in agent_input_dict:
            for unused_field in [
                "energy_level",
                "energy_label",
                "injuries",
                "injury_notes",
                "stress_level",
                "stress_label",
                "stress_reason",
            ]:
                agent_input_dict["self_state"].pop(unused_field, None)
        input_snapshot = {"agent_input": agent_input_dict}
    else:
        input_snapshot = {"agent_input": None}

    if decision is None:
        # フォールバック: LLMが失敗した場合（2026-01-21改善: フェーズに応じて行動を変更）
        environment_state = payload.get("environment_state", {})
        disaster_phase = (
            environment_state.get("disaster_phase", "") if environment_state else ""
        )

        if disaster_phase in ["Shaking", "InfoGap"]:
            # 揺れ直後・情報空白期はSTAY（現実的な行動）
            action_type = "STAY"
            selected_id = None
            reasoning = "Fallback: 情報がないため様子を見る"
        else:
            # 警報発表後はEVACUATE
            action_type = "EVACUATE"
            selected_id = heuristic_selection(payload)
            reasoning = "Fallback: 警報が出ているため避難する"
        confidence = 0.5
        source = "heuristic"
        desired_speed = None
    else:
        # LLMの決定を取得
        action_type = decision.get("action_type", "EVACUATE")
        reasoning = decision.get("reasoning", "LLM decision")
        confidence = float(decision.get("confidence", 0.5))
        source = "llm"
        desired_speed = _normalize_speed_choice(decision.get("desired_speed"))

        # EVACUATEの場合はselected_shelter_idが必要
        if action_type == "EVACUATE":
            selected_id = decision.get("selected_shelter_id") or heuristic_selection(
                payload
            )
        else:
            # STAY等の場合はselected_shelter_idは不要
            selected_id = decision.get("selected_shelter_id")

    # EVACUATEだがselected_idがない場合のフォールバック
    if (
        action_type == "EVACUATE"
        and selected_id is None
        and payload.get("shelter_candidates")
    ):
        selected_id = payload["shelter_candidates"][0].get("id")

    response_payload = {
        "request_id": request_id,
        "evacuee_id": evacuee.get("id"),
        "action_type": action_type,
        "selected_shelter_id": selected_id,
        "reasoning": reasoning,
        "confidence": confidence,
        "desired_speed": desired_speed,
    }

    # エラー情報がある場合はレスポンスに含める（Unity側で赤色警告表示用）
    if error_info is not None:
        response_payload["llm_error"] = error_info

    # TALKの場合は追加フィールドを含める
    if decision and action_type == "TALK":
        response_payload["talk_target_id"] = decision.get("talk_target_id")
        response_payload["talk_topic"] = decision.get("talk_topic", "General")
        response_payload["talk_message"] = decision.get("talk_message")

    # FOLLOWの場合は追加フィールドを含める
    if decision and action_type == "FOLLOW":
        response_payload["target_evacuee_id"] = decision.get("target_evacuee_id")

    # 2026-01-21追加: long_term_goal/mid_term_planをUnityに返却
    if decision:
        if decision.get("long_term_goal"):
            response_payload["long_term_goal"] = decision["long_term_goal"]
            response_payload["should_update_goal"] = decision.get(
                "should_update_goal", False
            )
        if decision.get("mid_term_plan"):
            response_payload["mid_term_plan"] = decision["mid_term_plan"]
            response_payload["should_update_plan"] = decision.get(
                "should_update_plan", False
            )

        # 交通シミュレーション: 推奨される移動手段を返却
        rec_mode = decision.get("recommended_transport_mode")
        if rec_mode:
            rec_mode_upper = str(rec_mode).upper()
            if rec_mode_upper in ("WALKING", "DRIVING", "ABANDON_VEHICLE"):
                response_payload["recommended_transport_mode"] = rec_mode_upper
        if decision.get("recommended_route_change"):
            response_payload["recommended_route_change"] = decision[
                "recommended_route_change"
            ]

    # 注: 行動履歴の要約（LLM API呼び出し）は削除
    # レート制限を回避するため、直近3件の履歴をそのまま使用する方式に変更

    output_snapshot = {
        "action_type": action_type,
        "reasoning": reasoning,
        "confidence": confidence,
    }
    if selected_id is not None:
        output_snapshot["selected_shelter_id"] = selected_id
    if desired_speed is not None:
        output_snapshot["desired_speed"] = desired_speed
    # 2026-01-21追加: long_term_goal/mid_term_plan及び行動別追加フィールドをログに記録
    if decision:
        if decision.get("long_term_goal"):
            output_snapshot["long_term_goal"] = decision["long_term_goal"]
        if decision.get("mid_term_plan"):
            output_snapshot["mid_term_plan"] = decision["mid_term_plan"]
        # 行動別追加フィールド
        if decision.get("target_family_member"):  # SEARCH_FAMILY
            output_snapshot["target_family_member"] = decision["target_family_member"]
        if decision.get("contact_target"):  # CONTACT
            output_snapshot["contact_target"] = decision["contact_target"]
        if decision.get("contact_message"):
            output_snapshot["contact_message"] = decision["contact_message"]
        if decision.get("target_evacuee_id"):  # FOLLOW
            output_snapshot["target_evacuee_id"] = decision["target_evacuee_id"]
        if decision.get("talk_target_id"):  # TALK
            output_snapshot["talk_target_id"] = decision["talk_target_id"]
        if decision.get("talk_topic"):
            output_snapshot["talk_topic"] = decision["talk_topic"]
        if decision.get("talk_message"):
            output_snapshot["talk_message"] = decision["talk_message"]

    _log_decision(
        evacuee_id=evacuee.get("id"),
        request_id=request_id,
        source=source,
        input_snapshot=input_snapshot,
        output_snapshot=output_snapshot,
        system_prompt=system_prompt,
        user_prompt=user_prompt,
        experiment_id=payload.get("experiment_id"),
        episode_id=payload.get("episode_id"),
        episode_elapsed_time=payload.get("episode_elapsed_time"),
    )

    return response_payload


def build_conversation_response_prompts(payload: Dict[str, Any]) -> tuple[str, str]:
    """
    会話応答生成用のsystem_promptとuser_promptを構築する。
    話しかけられた避難者が、相手にどう返答するかを決定するためのプロンプト。
    意思決定プロンプト（build_system_prompt / build_user_prompt）のパターンに準拠。
    """
    # ========================================
    # system_prompt（静的）
    # ========================================
    system_prompt = """あなたは予期せぬ災害に直面した一般市民です。他の避難者から話しかけられました。
与えられたペルソナに基づいて、その人物として自然に返答してください。

【重要】
- 家族のことが心配で、会話を急いで切り上げたいかもしれません
- 自分の知っている情報があれば適切に共有してください
- 自分も避難中で時間がない場合は、簡潔に答えてください

【出力形式】JSON1つのみ出力:
{
  "response_message": "返答内容（口語的な日本語）",
  "willing_to_share": true/false（情報を共有する意思があるか）,
  "want_to_continue": true/false（会話を続けたいか。falseなら締めの言葉で返答）,
  "reasoning": "この返答をした理由"
}"""

    # ========================================
    # user_prompt（動的）
    # ========================================
    lines = []

    persona = payload.get("persona", {})
    environment = payload.get("environment", {})
    incoming = payload.get("incoming_conversation", {})
    current_action = payload.get("current_action", "不明")
    current_target = payload.get("current_target", "")

    # A-1. ペルソナ情報
    lines.append("【あなたのペルソナ】")
    name = persona.get("name", "不明")
    age_group = persona.get("age_group", "不明")
    role = persona.get("role", "不明")
    lines.append(f"{name}（{age_group}・{role}）")

    mental_state = persona.get("mental_state")
    if mental_state:
        lines.append(f"心理状態: {mental_state}")

    priority = persona.get("priority")
    if priority:
        lines.append(f"優先事項: {priority}")

    past_disaster = persona.get("past_disaster_experience")
    if past_disaster:
        lines.append(f"災害経験: {past_disaster}")

    physical_condition = persona.get("physical_condition")
    if physical_condition and physical_condition != "健康":
        lines.append(f"身体状態: {physical_condition}")

    system_prompt_context = persona.get("system_prompt_context")
    if system_prompt_context:
        lines.append("")
        lines.append(system_prompt_context)
    lines.append("")

    # A-2. あなたの状態
    lines.append("【あなたの状態】")
    stamina = payload.get("stamina", 1.0)
    lines.append(f"体力: {stamina:.0%}")

    # 現在の行動
    action_desc = {
        "EVACUATE": "避難所に向かっている",
        "STAY": "その場で待機している",
        "SEARCH_FAMILY": "家族を探している",
        "CONTACT": "家族に連絡を取っている",
        "FOLLOW": "他の避難者について行っている",
        "TALK": "他の避難者と会話中",
    }.get(current_action, current_action)
    lines.append(f"現在の行動: {action_desc}")

    current_goal = payload.get("current_goal")
    if current_goal:
        lines.append(f"現在の目標: {current_goal}")
    lines.append("")

    # B. 状況
    lines.append("【状況】")
    scenario_id = environment.get("scenario_id", "")
    disaster_phase = environment.get("disaster_phase", "")

    if scenario_id == "shindo_7_tsunami":
        if disaster_phase in ["Shaking", "InfoGap"]:
            lines.append("強い地震が発生しました。まだ詳しい情報は入っていません。")
        else:
            lines.append("震度7クラスの地震が発生し、津波警報が発令中です。")
    elif scenario_id == "shindo_6":
        lines.append("強い揺れが発生しました。")
    else:
        lines.append("地震が発生しました。")

    # Jアラート
    has_received_j_alert = payload.get("has_received_j_alert", False)
    last_j_alert_message = payload.get("last_j_alert_message", "")
    if has_received_j_alert and last_j_alert_message:
        lines.append(f"スマホの緊急速報: {last_j_alert_message}")

    # 防災無線
    has_heard_broadcast = payload.get("has_heard_broadcast", False)
    last_broadcast_message = payload.get("last_broadcast_message", "")
    if has_heard_broadcast and last_broadcast_message:
        lines.append(f"防災無線: {last_broadcast_message}")
    lines.append("")

    # C. 現在位置
    location_description = payload.get("location_description")
    if location_description:
        lines.append("【あなたの現在位置】")
        lines.append(location_description)
        lines.append("")

    # E-1. 長期目標
    current_long_term_goal = payload.get("current_long_term_goal", {})
    if current_long_term_goal:
        primary_goal = current_long_term_goal.get("primary_goal")
        if primary_goal and primary_goal != "未設定":
            lines.append("【現在の長期目標】")
            lines.append(f"主要目標: {primary_goal}")
            secondary_goals = current_long_term_goal.get("secondary_goals", [])
            if secondary_goals:
                lines.append(f"副次目標: {', '.join(secondary_goals)}")
            lines.append("")

    # E-2. 中期計画
    current_mid_term_plan = payload.get("current_mid_term_plan", {})
    if current_mid_term_plan:
        steps = current_mid_term_plan.get("steps", [])
        if steps:
            lines.append("【現在の中期計画】")
            for idx, step in enumerate(steps, 1):
                lines.append(f"{idx}. {step}")
            lines.append("")

    # E-3. 直近の行動履歴
    action_history = payload.get("action_history", {})
    recent_actions = action_history.get("recent_actions", [])
    if recent_actions:
        lines.append("【直近の行動】")
        for action in recent_actions[:3]:
            timestamp = action.get("timestamp", 0)
            action_type = action.get("action_type", "不明")
            reasoning = action.get("reasoning", "")
            action_type_ja = {
                "EVACUATE": "避難",
                "STAY": "待機",
                "SEARCH_FAMILY": "家族探索",
                "CONTACT": "連絡",
                "FOLLOW": "追従",
                "TALK": "会話",
            }.get(action_type, action_type)
            line = f"- {timestamp:.0f}秒: {action_type_ja}"
            if reasoning:
                short_reasoning = reasoning[:40] + "..." if len(reasoning) > 40 else reasoning
                line += f" - {short_reasoning}"
            lines.append(line)
        lines.append("")

    # E-4. 直近の会話履歴
    conversation_history = payload.get("conversation_history", {})
    recent_conversations = conversation_history.get("recent_conversations", [])
    if recent_conversations:
        lines.append("【直近の会話履歴】")
        for conv in recent_conversations[:5]:
            partner_name = conv.get("partner_name", "不明")
            my_message = conv.get("my_message", "")
            partner_response = conv.get("partner_response", "")
            lines.append(f"相手: {partner_name}")
            if my_message:
                lines.append(f"自分: {my_message}")
            if partner_response:
                lines.append(f"{partner_name}: {partner_response}")
            lines.append("")

    # E-5. 長期記憶
    long_term_memories = payload.get("long_term_memories", [])
    if long_term_memories:
        lines.append("【あなたの記憶・知識】")
        for mem in long_term_memories[:5]:
            content = mem.get("content", "")
            if content:
                lines.append(f"- {content}")
        lines.append("")

    # ========================================
    # 会話の状況（ここから会話固有の情報）
    # ========================================
    lines.append("---以下は会話の状況---")
    lines.append("")

    # 話しかけてきた人の情報
    lines.append("【話しかけてきた人】")
    lines.append(f"名前: {incoming.get('initiator_name', '不明')}")
    lines.append(f"メッセージ: 「{incoming.get('message', '')}」")

    topic = incoming.get("topic", "General")
    topic_desc = {
        "ShelterInfo": "避難所情報",
        "CurrentSituation": "現状確認",
        "ActionAdvice": "行動相談",
        "SafetyConfirm": "安全確認",
        "General": "一般",
    }.get(topic, topic)
    lines.append(f"話題: {topic_desc}")
    lines.append("")

    # ターン情報
    turn_count = payload.get("turn_count", 1)
    if turn_count > 1:
        lines.append(f"これは相手との会話の{turn_count}回目のやり取りです。")
        lines.append("")

    if turn_count >= 7:
        lines.append(
            "会話が長くなっています。区切りの良いところで終わりにしても構いません。"
        )

    user_prompt = "\n".join(lines)
    return system_prompt, user_prompt


async def process_conversation_response(payload: Dict[str, Any]) -> Dict[str, Any]:
    """
    会話応答生成リクエストを処理する。
    話しかけられた避難者のLLMが返答を生成する。
    """
    request_id = payload.get("request_id", f"conv-{random.randint(0, 1_000_000)}")
    persona = payload.get("persona", {})
    incoming = payload.get("incoming_conversation", {})

    if OPENAI_CLIENT is None:
        # LLMが利用できない場合はデフォルト応答
        result = {
            "request_id": request_id,
            "response_message": "すみません、今は急いでいるので...",
            "willing_to_share": False,
            "want_to_continue": False,
            "reasoning": "LLM unavailable - default response",
        }
        # ログ出力（デフォルト応答）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="conversation_response_default",
            input_snapshot={
                "initiator_name": incoming.get("initiator_name"),
                "message": incoming.get("message"),
                "topic": incoming.get("topic"),
            },
            output_snapshot=result,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )
        return result

    system_prompt, user_prompt = build_conversation_response_prompts(payload)

    try:
        content, _ = await _call_openai_with_retry(system_prompt, user_prompt)
        if content is None:
            result = {
                "request_id": request_id,
                "response_message": "すみません、今は急いでいるので...",
                "willing_to_share": False,
                "want_to_continue": False,
                "reasoning": "LLM call failed - default response",
            }
            # ログ出力（LLM失敗）
            _log_decision(
                evacuee_id=payload.get("evacuee_id") or persona.get("name"),
                request_id=request_id,
                source="conversation_response_failed",
                input_snapshot={
                    "initiator_name": incoming.get("initiator_name"),
                    "message": incoming.get("message"),
                    "topic": incoming.get("topic"),
                },
                output_snapshot=result,
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                experiment_id=payload.get("experiment_id"),
                episode_id=payload.get("episode_id"),
                episode_elapsed_time=payload.get("episode_elapsed_time"),
            )
            return result

        response = _safe_load_json(content)

        result = {
            "request_id": request_id,
            "response_message": response.get("response_message", "..."),
            "willing_to_share": response.get("willing_to_share", True),
            "want_to_continue": response.get("want_to_continue", False),
            "reasoning": response.get("reasoning", ""),
        }

        # ログ出力（成功）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="conversation_response",
            input_snapshot={
                "initiator_name": incoming.get("initiator_name"),
                "message": incoming.get("message"),
                "topic": incoming.get("topic"),
                "turn_count": payload.get("turn_count", 1),
            },
            output_snapshot=result,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )

        return result

    except Exception as exc:
        print(f"[LLM SERVER] 会話応答生成で例外: {exc}")
        result = {
            "request_id": request_id,
            "response_message": "すみません、今は急いでいるので...",
            "willing_to_share": False,
            "want_to_continue": False,
            "reasoning": f"Exception: {exc}",
        }
        # ログ出力（例外）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="conversation_response_exception",
            input_snapshot={
                "initiator_name": incoming.get("initiator_name"),
                "message": incoming.get("message"),
                "topic": incoming.get("topic"),
                "exception": str(exc),
            },
            output_snapshot=result,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )
        return result


def build_family_contact_response_prompts(payload: Dict[str, Any]) -> tuple[str, str]:
    """
    家族連絡応答生成用のsystem_promptとuser_promptを構築する。
    連絡を受けた家族メンバーが、送信者にどう返答するかを決定するためのプロンプト。
    意思決定プロンプト（build_system_prompt / build_user_prompt）のパターンに準拠。
    """
    # ========================================
    # system_prompt（静的）
    # ========================================
    system_prompt = """あなたは予期せぬ災害に直面した一般市民です。家族から緊急連絡（メール）を受けました。
与えられたペルソナに基づいて、その人物として自然に返信してください。

【重要】
- 家族を安心させたい、または不安を共有したいかもしれません
- あなたの現在の状況を正直に伝えてください
- 今後の行動予定も伝えてあげてください

以下の点を含めて返信を作成してください:
- あなたの現在の状況（無事かどうか、怪我はないか）
- あなたの現在位置
- 今後の行動予定（どこに避難するか、どう行動するか）

【出力形式】JSON1つのみ出力:
{
  "response_message": "返信内容（メールの本文、口語的な日本語）",
  "current_status": "無事/軽傷/重傷など現在の状況",
  "current_location": "現在位置の説明",
  "planned_action": "今後の行動予定",
  "want_to_continue": true/false（やり取りを続けたいか）,
  "reasoning": "この返信をした理由"
}"""

    # ========================================
    # user_prompt（動的）
    # ========================================
    lines = []

    persona = payload.get("persona", {})
    environment = payload.get("environment", {})
    incoming = payload.get("incoming_contact", {})
    current_action = payload.get("current_action", "不明")
    current_target = payload.get("current_target", "")
    family_relationship = payload.get("family_relationship", "")

    # A-1. ペルソナ情報
    lines.append("【あなたのペルソナ】")
    name = persona.get("name", "不明")
    age_group = persona.get("age_group", "不明")
    role = persona.get("role", "不明")
    lines.append(f"{name}（{age_group}・{role}）")

    if family_relationship:
        lines.append(f"家族関係: {family_relationship}")

    mental_state = persona.get("mental_state")
    if mental_state:
        lines.append(f"心理状態: {mental_state}")

    priority = persona.get("priority")
    if priority:
        lines.append(f"優先事項: {priority}")

    past_disaster = persona.get("past_disaster_experience")
    if past_disaster:
        lines.append(f"災害経験: {past_disaster}")

    physical_condition = persona.get("physical_condition")
    if physical_condition and physical_condition != "健康":
        lines.append(f"身体状態: {physical_condition}")

    system_prompt_context = persona.get("system_prompt_context")
    if system_prompt_context:
        lines.append("")
        lines.append(system_prompt_context)
    lines.append("")

    # A-2. あなたの状態
    lines.append("【あなたの状態】")
    stamina = payload.get("stamina", 1.0)
    lines.append(f"体力: {stamina:.0%}")

    # 現在の行動
    action_desc = {
        "EVACUATE": "避難所に向かっている",
        "STAY": "その場で待機している",
        "SEARCH_FAMILY": "家族を探している",
        "CONTACT": "家族に連絡を取っている",
        "FOLLOW": "他の避難者について行っている",
        "TALK": "他の避難者と会話中",
    }.get(current_action, current_action)
    lines.append(f"現在の行動: {action_desc}")

    if current_target:
        lines.append(f"目標避難所: {current_target}")

    current_goal = payload.get("current_goal")
    if current_goal:
        lines.append(f"現在の目標: {current_goal}")
    lines.append("")

    # B. 状況
    lines.append("【状況】")
    scenario_id = environment.get("scenario_id", "")
    disaster_phase = environment.get("disaster_phase", "")

    if scenario_id == "shindo_7_tsunami":
        if disaster_phase in ["Shaking", "InfoGap"]:
            lines.append("強い地震が発生しました。まだ詳しい情報は入っていません。")
        else:
            lines.append("震度7クラスの地震が発生し、津波警報が発令中です。")
    elif scenario_id == "shindo_6":
        lines.append("強い揺れが発生しました。")
    else:
        lines.append("地震が発生しました。")

    # Jアラート
    has_received_j_alert = payload.get("has_received_j_alert", False)
    last_j_alert_message = payload.get("last_j_alert_message", "")
    if has_received_j_alert and last_j_alert_message:
        lines.append(f"スマホの緊急速報: {last_j_alert_message}")
    lines.append("")

    # C. 現在位置
    location_description = payload.get("location_description")
    if location_description:
        lines.append("【あなたの現在位置】")
        lines.append(location_description)
        lines.append("")

    # D. 家族情報
    family_members = payload.get("family_members", [])
    if family_members:
        lines.append("【あなたの家族情報】")
        for member in family_members[:5]:
            member_name = member.get("name", "不明")
            relation = member.get("relation", "不明")
            location = member.get("likely_location", "不明")
            is_reunited = member.get("is_reunited", False)
            if is_reunited:
                lines.append(f"- {relation}: {member_name}（★合流済み★）")
            else:
                lines.append(f"- {relation}: {member_name}（{location}にいる可能性）")
        lines.append("")

    # 直近の家族からの返信
    last_contact_message = payload.get("last_contact_message")
    if last_contact_message:
        lines.append("【直近の家族からの返信】")
        lines.append(last_contact_message)
        lines.append("")

    # E-1. 長期目標
    current_long_term_goal = payload.get("current_long_term_goal", {})
    if current_long_term_goal:
        primary_goal = current_long_term_goal.get("primary_goal")
        if primary_goal and primary_goal != "未設定":
            lines.append("【現在の長期目標】")
            lines.append(f"主要目標: {primary_goal}")
            secondary_goals = current_long_term_goal.get("secondary_goals", [])
            if secondary_goals:
                lines.append(f"副次目標: {', '.join(secondary_goals)}")
            lines.append("")

    # E-2. 中期計画
    current_mid_term_plan = payload.get("current_mid_term_plan", {})
    if current_mid_term_plan:
        steps = current_mid_term_plan.get("steps", [])
        if steps:
            lines.append("【現在の中期計画】")
            for idx, step in enumerate(steps, 1):
                lines.append(f"{idx}. {step}")
            lines.append("")

    # E-3. 直近の行動履歴
    action_history = payload.get("action_history", {})
    recent_actions = action_history.get("recent_actions", [])
    if recent_actions:
        lines.append("【直近の行動】")
        for action in recent_actions[:3]:
            timestamp = action.get("timestamp", 0)
            action_type = action.get("action_type", "不明")
            reasoning = action.get("reasoning", "")
            action_type_ja = {
                "EVACUATE": "避難",
                "STAY": "待機",
                "SEARCH_FAMILY": "家族探索",
                "CONTACT": "連絡",
                "FOLLOW": "追従",
                "TALK": "会話",
            }.get(action_type, action_type)
            line = f"- {timestamp:.0f}秒: {action_type_ja}"
            if reasoning:
                short_reasoning = reasoning[:40] + "..." if len(reasoning) > 40 else reasoning
                line += f" - {short_reasoning}"
            lines.append(line)
        lines.append("")

    # E-5. 長期記憶
    long_term_memories = payload.get("long_term_memories", [])
    if long_term_memories:
        lines.append("【あなたの記憶・知識】")
        for mem in long_term_memories[:5]:
            content = mem.get("content", "")
            if content:
                lines.append(f"- {content}")
        lines.append("")

    # ========================================
    # 連絡の状況（ここから連絡固有の情報）
    # ========================================
    lines.append("---以下は連絡の状況---")
    lines.append("")

    # 連絡してきた家族の情報
    lines.append("【連絡してきた家族】")
    lines.append(f"名前: {incoming.get('sender_name', '不明')}")
    if incoming.get("sender_relation"):
        lines.append(f"続柄: {incoming.get('sender_relation')}")
    lines.append(f"メッセージ: 「{incoming.get('message', '')}」")
    if incoming.get("sender_location"):
        lines.append(f"現在位置: {incoming.get('sender_location')}")
    if incoming.get("sender_action"):
        sender_action_desc = {
            "EVACUATE": "避難所に向かっている",
            "STAY": "その場で待機している",
            "SEARCH_FAMILY": "家族を探している",
            "CONTACT": "連絡を取っている",
            "FOLLOW": "他の人について行っている",
            "TALK": "他の人と話している",
        }.get(incoming.get("sender_action"), incoming.get("sender_action"))
        lines.append(f"相手の行動: {sender_action_desc}")
    lines.append("")

    # ターン情報
    turn_count = payload.get("turn_count", 1)
    if turn_count > 1:
        lines.append(f"これは家族との連絡の{turn_count}回目のやり取りです。")
        lines.append("")

    if turn_count >= 7:
        lines.append(
            "やり取りが長くなっています。区切りの良いところで終わりにしても構いません。"
        )

    user_prompt = "\n".join(lines)
    return system_prompt, user_prompt


async def process_family_contact_response(payload: Dict[str, Any]) -> Dict[str, Any]:
    """
    家族連絡応答生成リクエストを処理する。
    連絡を受けた家族のLLMが返答を生成する。
    """
    request_id = payload.get("request_id", f"fam-{random.randint(0, 1_000_000)}")
    persona = payload.get("persona", {})
    incoming = payload.get("incoming_contact", {})

    if OPENAI_CLIENT is None:
        # LLMが利用できない場合はデフォルト応答
        result = {
            "request_id": request_id,
            "response_message": "無事だよ。今は自宅付近にいる。様子を見ている。",
            "current_status": "無事",
            "current_location": "自宅付近",
            "planned_action": "様子を見て避難する",
            "want_to_continue": False,
            "reasoning": "LLM unavailable - default response",
        }
        # ログ出力（デフォルト応答）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="family_contact_response_default",
            input_snapshot={
                "sender_name": incoming.get("sender_name"),
                "sender_relation": incoming.get("sender_relation"),
                "message": incoming.get("message"),
            },
            output_snapshot=result,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )
        return result

    system_prompt, user_prompt = build_family_contact_response_prompts(payload)

    try:
        content, _ = await _call_openai_with_retry(system_prompt, user_prompt)
        if content is None:
            result = {
                "request_id": request_id,
                "response_message": "無事だよ。今は自宅付近にいる。様子を見ている。",
                "current_status": "無事",
                "current_location": "自宅付近",
                "planned_action": "様子を見て避難する",
                "want_to_continue": False,
                "reasoning": "LLM call failed - default response",
            }
            # ログ出力（LLM失敗）
            _log_decision(
                evacuee_id=payload.get("evacuee_id") or persona.get("name"),
                request_id=request_id,
                source="family_contact_response_failed",
                input_snapshot={
                    "sender_name": incoming.get("sender_name"),
                    "sender_relation": incoming.get("sender_relation"),
                    "message": incoming.get("message"),
                },
                output_snapshot=result,
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                experiment_id=payload.get("experiment_id"),
                episode_id=payload.get("episode_id"),
                episode_elapsed_time=payload.get("episode_elapsed_time"),
            )
            return result

        response = _safe_load_json(content)

        result = {
            "request_id": request_id,
            "response_message": response.get("response_message", "無事だよ。"),
            "current_status": response.get("current_status", "不明"),
            "current_location": response.get("current_location", "不明"),
            "planned_action": response.get("planned_action", ""),
            "want_to_continue": response.get("want_to_continue", False),
            "reasoning": response.get("reasoning", ""),
        }

        # ログ出力（成功）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="family_contact_response",
            input_snapshot={
                "sender_name": incoming.get("sender_name"),
                "sender_relation": incoming.get("sender_relation"),
                "message": incoming.get("message"),
                "turn_count": payload.get("turn_count", 1),
            },
            output_snapshot=result,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )

        return result

    except Exception as exc:
        print(f"[LLM SERVER] 家族連絡応答生成で例外: {exc}")
        result = {
            "request_id": request_id,
            "response_message": "無事だよ。今は自宅付近にいる。",
            "current_status": "無事",
            "current_location": "自宅付近",
            "planned_action": "様子を見て避難する",
            "want_to_continue": False,
            "reasoning": f"Exception: {exc}",
        }
        # ログ出力（例外）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="family_contact_response_exception",
            input_snapshot={
                "sender_name": incoming.get("sender_name"),
                "sender_relation": incoming.get("sender_relation"),
                "message": incoming.get("message"),
                "exception": str(exc),
            },
            output_snapshot=result,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )
        return result


def build_conversation_continuation_prompts(payload: Dict[str, Any]) -> tuple[str, str]:
    """
    会話継続判断用のsystem_promptとuser_promptを構築する。
    自分が会話を続けるかどうか、続ける場合は次の発言を生成する。
    意思決定プロンプト（build_system_prompt / build_user_prompt）のパターンに準拠。
    """
    # ========================================
    # system_prompt（静的）
    # ========================================
    system_prompt = """あなたは予期せぬ災害に直面した一般市民です。他の避難者と会話中です。
与えられたペルソナに基づいて、その人物として自然に行動してください。

相手の発言を受けて、会話を続けるかどうか判断してください。
続ける場合は次の発言を、終わる場合は締めの言葉を考えてください。

【重要】
- 家族のことが心配で、会話を急いで切り上げたいかもしれません
- 避難中なので、長話は避けた方が良いかもしれません
- 必要な情報が得られたら切り上げても構いません

【出力形式】JSON1つのみ出力:
{
  "want_to_continue": true/false,
  "message": "次の発言（続ける場合）または締めの言葉（終わる場合）",
  "reasoning": "判断理由"
}"""

    # ========================================
    # user_prompt（動的）
    # ========================================
    lines = []

    persona = payload.get("persona", {})
    session = payload.get("session", {})
    partner_last_message = payload.get("partner_last_message", "")
    environment = payload.get("environment", {})

    # A-1. ペルソナ情報
    lines.append("【あなたのペルソナ】")
    name = persona.get("name", "不明")
    age_group = persona.get("age_group", "不明")
    role = persona.get("role", "不明")
    lines.append(f"{name}（{age_group}・{role}）")

    mental_state = persona.get("mental_state")
    if mental_state:
        lines.append(f"心理状態: {mental_state}")

    priority = persona.get("priority")
    if priority:
        lines.append(f"優先事項: {priority}")

    past_disaster = persona.get("past_disaster_experience")
    if past_disaster:
        lines.append(f"災害経験: {past_disaster}")

    physical_condition = persona.get("physical_condition")
    if physical_condition and physical_condition != "健康":
        lines.append(f"身体状態: {physical_condition}")

    system_prompt_context = persona.get("system_prompt_context")
    if system_prompt_context:
        lines.append("")
        lines.append(system_prompt_context)
    lines.append("")

    # A-2. あなたの状態
    lines.append("【あなたの状態】")
    stamina = payload.get("stamina", 1.0)
    lines.append(f"体力: {stamina:.0%}")

    current_goal = payload.get("current_goal")
    if current_goal:
        lines.append(f"現在の目標: {current_goal}")
    lines.append("")

    # B. 状況
    scenario_id = environment.get("scenario_id", "")
    disaster_phase = environment.get("disaster_phase", "")
    if scenario_id:
        lines.append("【状況】")
        if scenario_id == "shindo_7_tsunami":
            if disaster_phase in ["Shaking", "InfoGap"]:
                lines.append("強い地震が発生しました。まだ詳しい情報は入っていません。")
            else:
                lines.append("震度7クラスの地震が発生し、津波警報が発令中です。")
        elif scenario_id == "shindo_6":
            lines.append("強い揺れが発生しました。")
        else:
            lines.append("地震が発生しました。")

        # Jアラート
        has_received_j_alert = payload.get("has_received_j_alert", False)
        last_j_alert_message = payload.get("last_j_alert_message", "")
        if has_received_j_alert and last_j_alert_message:
            lines.append(f"スマホの緊急速報: {last_j_alert_message}")
        lines.append("")

    # C. 現在位置
    location_description = payload.get("location_description")
    if location_description:
        lines.append("【あなたの現在位置】")
        lines.append(location_description)
        lines.append("")

    # E-1. 長期目標
    current_long_term_goal = payload.get("current_long_term_goal", {})
    if current_long_term_goal:
        primary_goal = current_long_term_goal.get("primary_goal")
        if primary_goal and primary_goal != "未設定":
            lines.append("【現在の長期目標】")
            lines.append(f"主要目標: {primary_goal}")
            lines.append("")

    # E-2. 中期計画
    current_mid_term_plan = payload.get("current_mid_term_plan", {})
    if current_mid_term_plan:
        steps = current_mid_term_plan.get("steps", [])
        if steps:
            lines.append("【現在の中期計画】")
            for idx, step in enumerate(steps, 1):
                lines.append(f"{idx}. {step}")
            lines.append("")

    # E-3. 直近の行動履歴
    action_history = payload.get("action_history", {})
    recent_actions = action_history.get("recent_actions", [])
    if recent_actions:
        lines.append("【直近の行動】")
        for action in recent_actions[:3]:
            timestamp = action.get("timestamp", 0)
            action_type = action.get("action_type", "不明")
            reasoning = action.get("reasoning", "")
            action_type_ja = {
                "EVACUATE": "避難",
                "STAY": "待機",
                "SEARCH_FAMILY": "家族探索",
                "CONTACT": "連絡",
                "FOLLOW": "追従",
                "TALK": "会話",
            }.get(action_type, action_type)
            line = f"- {timestamp:.0f}秒: {action_type_ja}"
            if reasoning:
                short_reasoning = reasoning[:40] + "..." if len(reasoning) > 40 else reasoning
                line += f" - {short_reasoning}"
            lines.append(line)
        lines.append("")

    # E-4. 直近の会話履歴
    conversation_history = payload.get("conversation_history", {})
    recent_conversations = conversation_history.get("recent_conversations", [])
    if recent_conversations:
        lines.append("【直近の会話履歴】")
        for conv in recent_conversations[:5]:
            partner_name = conv.get("partner_name", "不明")
            my_message = conv.get("my_message", "")
            partner_response = conv.get("partner_response", "")
            lines.append(f"相手: {partner_name}")
            if my_message:
                lines.append(f"自分: {my_message}")
            if partner_response:
                lines.append(f"{partner_name}: {partner_response}")
            lines.append("")

    # E-5. 長期記憶
    long_term_memories = payload.get("long_term_memories", [])
    if long_term_memories:
        lines.append("【あなたの記憶・知識】")
        for mem in long_term_memories[:5]:
            content = mem.get("content", "")
            if content:
                lines.append(f"- {content}")
        lines.append("")

    # ========================================
    # 会話の状況（ここから会話固有の情報）
    # ========================================
    lines.append("---以下は会話の状況---")
    lines.append("")

    lines.append("【会話の状況】")
    lines.append(f"相手: {session.get('partner_name', '不明')}")

    topic = session.get("current_topic", "General")
    topic_desc = {
        "ShelterInfo": "避難所情報",
        "CurrentSituation": "現状確認",
        "ActionAdvice": "行動相談",
        "SafetyConfirm": "安全確認",
        "General": "一般",
    }.get(topic, topic)
    lines.append(f"話題: {topic_desc}")

    turn_count = session.get("turn_count", 1)
    lines.append(f"これまでのやり取り: {turn_count}回")
    lines.append(f"相手の最後の発言: 「{partner_last_message}」")
    lines.append("")

    if turn_count >= 7:
        lines.append(
            "会話が長くなっています。区切りの良いところで終わりにしても構いません。"
        )

    user_prompt = "\n".join(lines)
    return system_prompt, user_prompt


async def process_conversation_continuation(payload: Dict[str, Any]) -> Dict[str, Any]:
    """
    会話継続判断リクエストを処理する。
    自分が会話を続けるか判断し、続ける場合は次のメッセージを生成する。
    """
    request_id = payload.get("request_id", f"cont-{random.randint(0, 1_000_000)}")
    persona = payload.get("persona", {})
    session = payload.get("session", {})
    partner_last_message = payload.get("partner_last_message", "")

    if OPENAI_CLIENT is None:
        # LLMが利用できない場合はデフォルト応答（会話終了）
        result = {
            "request_id": request_id,
            "want_to_continue": False,
            "message": "すみません、急いでいるのでこの辺で...",
            "reasoning": "LLM unavailable - default response",
        }
        # ログ出力（デフォルト応答）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="conversation_continuation_default",
            input_snapshot={
                "partner_name": session.get("partner_name"),
                "partner_last_message": partner_last_message,
                "turn_count": session.get("turn_count", 1),
            },
            output_snapshot=result,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )
        return result

    system_prompt, user_prompt = build_conversation_continuation_prompts(payload)

    try:
        content, _ = await _call_openai_with_retry(system_prompt, user_prompt)
        if content is None:
            result = {
                "request_id": request_id,
                "want_to_continue": False,
                "message": "すみません、急いでいるのでこの辺で...",
                "reasoning": "LLM call failed - default response",
            }
            # ログ出力（LLM失敗）
            _log_decision(
                evacuee_id=payload.get("evacuee_id") or persona.get("name"),
                request_id=request_id,
                source="conversation_continuation_failed",
                input_snapshot={
                    "partner_name": session.get("partner_name"),
                    "partner_last_message": partner_last_message,
                    "turn_count": session.get("turn_count", 1),
                },
                output_snapshot=result,
                system_prompt=system_prompt,
                user_prompt=user_prompt,
                experiment_id=payload.get("experiment_id"),
                episode_id=payload.get("episode_id"),
                episode_elapsed_time=payload.get("episode_elapsed_time"),
            )
            return result

        response = _safe_load_json(content)

        result = {
            "request_id": request_id,
            "want_to_continue": response.get("want_to_continue", False),
            "message": response.get("message", "..."),
            "reasoning": response.get("reasoning", ""),
        }

        # ログ出力（成功）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="conversation_continuation",
            input_snapshot={
                "partner_name": session.get("partner_name"),
                "partner_last_message": partner_last_message,
                "turn_count": session.get("turn_count", 1),
                "topic": session.get("current_topic"),
            },
            output_snapshot=result,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )

        return result

    except Exception as exc:
        print(f"[LLM SERVER] 会話継続判断で例外: {exc}")
        result = {
            "request_id": request_id,
            "want_to_continue": False,
            "message": "すみません、急いでいるのでこの辺で...",
            "reasoning": f"Exception: {exc}",
        }
        # ログ出力（例外）
        _log_decision(
            evacuee_id=payload.get("evacuee_id") or persona.get("name"),
            request_id=request_id,
            source="conversation_continuation_exception",
            input_snapshot={
                "partner_name": session.get("partner_name"),
                "partner_last_message": partner_last_message,
                "turn_count": session.get("turn_count", 1),
                "exception": str(exc),
            },
            output_snapshot=result,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
            experiment_id=payload.get("experiment_id"),
            episode_id=payload.get("episode_id"),
            episode_elapsed_time=payload.get("episode_elapsed_time"),
        )
        return result


async def _handle_single_message(
    websocket: websockets.WebSocketServerProtocol, message: str
) -> None:
    """
    1つのメッセージ処理を独立したタスクとして実行するヘルパー。
    これにより、同一WebSocket上でも複数のリクエストを並列に処理できる。
    """
    try:
        payload = json.loads(message)

        # request_typeによって処理を分岐
        request_type = payload.get("request_type", "evac_decision")

        if request_type == "conversation_response":
            # 会話応答生成リクエスト
            response = await process_conversation_response(payload)
        elif request_type == "family_contact_response":
            # 家族連絡応答生成リクエスト
            response = await process_family_contact_response(payload)
        elif request_type == "conversation_continuation":
            # 会話継続判断リクエスト
            response = await process_conversation_continuation(payload)
        else:
            # 通常の避難意思決定リクエスト
            response = await process_payload(payload)

        await websocket.send(json.dumps(response))
    except json.JSONDecodeError:
        await websocket.send(json.dumps({"error": "invalid_json"}))
    except Exception as exc:  # pragma: no cover
        await websocket.send(json.dumps({"error": str(exc)}))


async def handler(websocket: websockets.WebSocketServerProtocol) -> None:
    # 各受信メッセージごとに独立したタスクを起動し、並列に処理する
    async for message in websocket:
        # fire-and-forget タスクとして起動（エラーは各タスク内でハンドリング）
        asyncio.create_task(_handle_single_message(websocket, message))


async def main() -> None:
    global _memory_initialized

    print(f"[LLM SERVER] starting on ws://{SERVER_HOST}:{SERVER_PORT}")

    # 長期記憶（RAG）の初期化
    if OPENAI_CLIENT and not _memory_initialized:
        print("[LLM SERVER] 長期記憶（RAG）を初期化中...")
        memory_manager = await initialize_memory_manager(OPENAI_CLIENT)
        if memory_manager and memory_manager.is_initialized:
            _memory_initialized = True
            stats = memory_manager.get_statistics()
            print(f"[LLM SERVER] 長期記憶初期化完了: {stats}")
        else:
            print("[LLM SERVER] 長期記憶の初期化に失敗しました（RAG機能は無効）")

    # ping_timeout を延長（デフォルト20秒 → 120秒）
    # LLM API呼び出しが長時間かかる場合の接続切断を防ぐ
    async with websockets.serve(
        handler,
        SERVER_HOST,
        SERVER_PORT,
        ping_timeout=120,
        ping_interval=30,
    ):
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
