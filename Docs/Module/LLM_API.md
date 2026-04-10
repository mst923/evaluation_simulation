# LLM API 入出力仕様書

## 概要

UnityシミュレーションとLLM間の通信プロトコルを定義します。
避難者が避難所を選択する際に、LLMに問い合わせて意思決定を行います。

---

## アーキテクチャ

```text
Unity (C#)                          Python Server
┌─────────────────┐                ┌──────────────────┐
│  Evacuee.cs     │  WebSocket     │  LLM Decision    │
│                 │ ───────────>   │  Server          │
│  意思決定が     │  JSON Request  │                  │
│  必要になった   │                │  OpenAI API      │
│  タイミング     │ <───────────   │  Anthropic API   │
│                 │  JSON Response │                  │
└─────────────────┘                └──────────────────┘
```

---

## 1. LLMへの入力（Unity → Python）

### 1.0 互換サブセット（必須）

ML-Agents の既存観測をそのまま利用するため、まずは以下の最小構造のみを必須とします。  
この情報で各避難所候補に対する 0/1 の選択を返すことで、従来のポリシーと置き換えられます。

```json
{
  "request_id": "run-0001-evacuee-001",
  "timestamp": 12.3,

  "shelter_candidates": [
    {
      "id": "bldg_a",
      "position": { "x": 180.2, "y": 0.7, "z": 244.2 },
      "current_capacity": 45,
      "max_capacity": 100
    },
    {
      "id": "bldg_b",
      "position": { "x": 210.8, "y": 0.7, "z": 198.4 },
      "current_capacity": 12,
      "max_capacity": 80
    }
  ]
}
```

> **拡張フィールド**（ペルソナやハザードなど）は任意。後述の 1.1 以降に定義しています。

### 1.1 JSON形式（拡張版）

```json
{
  "evacuee_id": "evacuee_001",
  "request_type": "shelter_selection",
  "timestamp": 15.3,
  
  "evacuee_state": {
    "position": {
      "x": 123.45,
      "y": 10.0,
      "z": 678.90
    },
    "persona": {
      "age": 68,
      "physical_ability": 0.6,
      "risk_aversion": 0.8,
      "normalcy_bias": 0.5,
      "social_influence": 0.7,
      "background": "浪江町に40年住んでいる元教師。足腰がやや弱い。"
    },
    "current_energy": 0.75,
    "stress_level": 0.6
  },
  
  "available_shelters": [
    {
      "id": "shelter_a",
      "name": "浪江町役場",
      "distance": 250.5,
      "direction": "北東",
      "current_capacity": 45,
      "max_capacity": 100,
      "capacity_ratio": 0.45,
      "safety_rating": 0.9,
      "crowd_density": 0.6
    },
    {
      "id": "shelter_b",
      "name": "浪江小学校",
      "distance": 380.2,
      "direction": "南西",
      "current_capacity": 12,
      "max_capacity": 80,
      "capacity_ratio": 0.15,
      "safety_rating": 0.95,
      "crowd_density": 0.2
    }
  ],
  
  "environment": {
    "visible_hazards": [
      {
        "type": "flooding",
        "distance": 150.0,
        "severity": 0.7
      }
    ],
    "nearby_evacuees_count": 23,
    "nearby_evacuees_direction": "北東方向に多数",
    "time_elapsed": 15.3,
    "weather": "雨"
  },
  
  "memory": {
    "past_actions": [
      "10秒前: 避難開始",
      "5秒前: 北東方向に移動開始"
    ],
    "local_knowledge": [
      "浪江町役場は津波時には危険",
      "浪江小学校は高台で安全"
    ]
  }
}
```

### 1.2 Pythonでのデータクラス定義

最小構成を扱う場合は以下だけで十分です。

```python
from dataclasses import dataclass
from typing import List

@dataclass
class Vec3:
    x: float
    y: float
    z: float

@dataclass
class ShelterCandidate:
    id: str
    position: Vec3
    current_capacity: int
    max_capacity: int

@dataclass
class EvacueePosition:
    id: str
    position: Vec3

@dataclass
class MinimalRequest:
    request_id: str
    timestamp: float
    evacuee: EvacueePosition
    shelter_candidates: List[ShelterCandidate]
```

以下は将来的な拡張で利用できるリッチな構造です。

```python
from dataclasses import dataclass
from typing import List, Dict, Optional

@dataclass
class Position:
    x: float
    y: float
    z: float

@dataclass
class Persona:
    age: int
    physical_ability: float  # 0-1
    risk_aversion: float  # 0-1
    normalcy_bias: float  # 0-1
    social_influence: float  # 0-1
    background: str

@dataclass
class EvacueeState:
    position: Position
    persona: Persona
    current_energy: float  # 0-1
    stress_level: float  # 0-1

@dataclass
class Shelter:
    id: str
    name: str
    distance: float
    direction: str
    current_capacity: int
    max_capacity: int
    capacity_ratio: float
    safety_rating: float
    crowd_density: float

@dataclass
class Hazard:
    type: str
    distance: float
    severity: float

@dataclass
class Environment:
    visible_hazards: List[Hazard]
    nearby_evacuees_count: int
    nearby_evacuees_direction: str
    time_elapsed: float
    weather: str

@dataclass
class Memory:
    past_actions: List[str]
    local_knowledge: List[str]

@dataclass
class LLMRequest:
    evacuee_id: str
    request_type: str
    timestamp: float
    evacuee_state: EvacueeState
    available_shelters: List[Shelter]
    environment: Environment
    memory: Memory
```

---

## 2. LLMからの出力（Python → Unity）

### 2.0 互換サブセット（必須）

避難者ごとに 1 つの避難先を返すのが必須フォーマットです。

```json
{
  "request_id": "run-0001-evacuee-001",
  "evacuee_id": "evacuee_001",
  "selected_shelter_id": "bldg_b",
  "reasoning": "bldg_b は最寄りかつ空き容量が十分",
  "confidence": 0.82
}
```

Unity 側では `selected_shelter_id` を検索して NavMeshAgent の目的地に設定し、`reasoning` や `confidence` はログ・デバッグ用途に利用します。

### 2.1 JSON形式（シンプル版・拡張）

```json
{
  "evacuee_id": "evacuee_001",
  "decision": {
    "selected_shelter_id": "shelter_b",
    "selected_shelter_name": "浪江小学校",
    "movement_speed": 0.9,
    "urgency": 0.7,
    "reasoning": "浪江小学校は距離は遠いが、高台で安全性が高く、収容にも余裕がある。足腰は弱いが、安全を優先して移動する。",
    "confidence": 0.85
  },
  "timestamp": 15.3,
  "processing_time_ms": 1250
}
```

### 2.2 JSON形式（詳細版 - オプション）

より詳細な行動制御が必要な場合：

```json
{
  "evacuee_id": "evacuee_001",
  "decision": {
    "selected_shelter_id": "shelter_b",
    "selected_shelter_name": "浪江小学校",
    
    "movement": {
      "speed_multiplier": 0.9,
      "movement_type": "cautious",
      "urgency": 0.7,
      "rest_needed": false
    },
    
    "route_preferences": {
      "prefer_main_roads": 0.7,
      "avoid_crowds": 0.6,
      "avoid_hazards": 0.9
    },
    
    "alternative_plan": {
      "if_full": "shelter_a",
      "if_blocked": "wait_for_info"
    },
    
    "reasoning": "浪江小学校は距離は遠いが、高台で安全性が高く、収容にも余裕がある。足腰は弱いが、安全を優先して移動する。洪水エリアを避けて主要道路を通る。",
    
    "confidence": 0.85,
    "emotional_state": {
      "anxiety": 0.6,
      "hope": 0.7
    }
  },
  "timestamp": 15.3,
  "processing_time_ms": 1250
}
```

### 2.3 Pythonでのレスポンスクラス

```python
@dataclass
class Movement:
    speed_multiplier: float  # 0-1
    movement_type: str  # "direct", "cautious", "exploratory"
    urgency: float  # 0-1
    rest_needed: bool

@dataclass
class RoutePreferences:
    prefer_main_roads: float  # 0-1
    avoid_crowds: float  # 0-1
    avoid_hazards: float  # 0-1

@dataclass
class AlternativePlan:
    if_full: str  # 代替避難所ID
    if_blocked: str  # "wait_for_info", "return_home", etc.

@dataclass
class EmotionalState:
    anxiety: float  # 0-1
    hope: float  # 0-1

@dataclass
class Decision:
    selected_shelter_id: str
    selected_shelter_name: str
    movement: Movement
    route_preferences: Optional[RoutePreferences] = None
    alternative_plan: Optional[AlternativePlan] = None
    reasoning: str = ""
    confidence: float = 0.5
    emotional_state: Optional[EmotionalState] = None

@dataclass
class LLMResponse:
    evacuee_id: str
    decision: Decision
    timestamp: float
    processing_time_ms: float
```

---

## 3. LLMプロンプト設計

### 3.1 システムプロンプト

```python
SYSTEM_PROMPT = """
あなたは避難シミュレーションの意思決定エージェントです。
避難者のペルソナと状況に基づいて、現実的な避難行動を決定してください。

重要な考慮事項：
1. 安全性を最優先にする
2. ペルソナの特性（年齢、身体能力、リスク認識）を反映する
3. 人間らしい多様な判断をする（必ずしも最適解でなくてもよい）
4. 推論プロセスを明確に説明する
"""
```

### 3.2 ユーザープロンプト（テンプレート）

```python
def create_prompt(request: LLMRequest) -> str:
    persona = request.evacuee_state.persona
    shelters_desc = "\n".join([
        f"  - {s.name}: 距離{s.distance:.0f}m、収容率{s.capacity_ratio:.0%}、安全度{s.safety_rating:.0%}"
        for s in request.available_shelters
    ])
    
    hazards_desc = "\n".join([
        f"  - {h.type}: {h.distance:.0f}m先、危険度{h.severity:.0%}"
        for h in request.environment.visible_hazards
    ]) if request.environment.visible_hazards else "  - なし"
    
    prompt = f"""
## あなたのプロフィール
- 年齢: {persona.age}歳
- 身体能力: {'高い' if persona.physical_ability > 0.7 else '普通' if persona.physical_ability > 0.4 else '低い'}
- 性格: {'慎重で安全重視' if persona.risk_aversion > 0.7 else '柔軟で状況判断型' if persona.risk_aversion > 0.4 else '楽観的で行動的'}
- 背景: {persona.background}

## 現在の状況
- 経過時間: {request.environment.time_elapsed:.1f}秒
- 体力: {request.evacuee_state.current_energy:.0%}
- ストレスレベル: {request.evacuee_state.stress_level:.0%}
- 天候: {request.environment.weather}

## 選択可能な避難所
{shelters_desc}

## 周囲の状況
- 近くの避難者: {request.environment.nearby_evacuees_count}人（{request.environment.nearby_evacuees_direction}）
- 危険要因:
{hazards_desc}

## あなたの知識
{chr(10).join([f"  - {k}" for k in request.memory.local_knowledge])}

## 指示
上記の情報を踏まえて、どの避難所に向かうか決定してください。
必ず以下のJSON形式で回答してください：

{{
  "selected_shelter_id": "避難所のID",
  "selected_shelter_name": "避難所の名前",
  "movement_speed": 0.0-1.0の速度倍率,
  "urgency": 0.0-1.0の切迫度,
  "reasoning": "この判断をした理由（100文字程度）",
  "confidence": 0.0-1.0の確信度
}}
"""
    return prompt
```

---

## 4. Python実装例

### 4.0 サーバー起動と環境変数

`llm_server/` フォルダにサンプル実装を追加しました。`.env` に `OPENAI_API_KEY` と `OPENAI_MODEL` を記述すると自動で読み込まれます。

#### 手動起動（uv推奨）

```bash
cd llm_server
cp .env.example .env  # APIキーを設定
uv run server.py      # 仮想環境の作成・依存解決・起動を自動で行う
```

> `uv` がインストールされていない場合: `curl -LsSf https://astral.sh/uv/install.sh | sh`

従来の `pip` でも起動可能です:

```bash
pip install -r requirements.txt
python server.py
```

APIキーが未設定の場合は安全にヒューリスティックで応答します。

#### 自動起動（LLMServerManager）

`LLMServerManager` コンポーネントをシーン上の GameObject にアタッチすると、シミュレーション開始時にサーバーを自動起動し、停止時に自動終了します。

| Inspector 設定 | デフォルト値 | 説明 |
|---|---|---|
| `commandPath` | `uv` | サーバー起動コマンドのパス（`uv run server.py` として実行される） |
| `autoStart` | `true` | 再生開始時にサーバーを自動起動するか |
| `serverHost` | `127.0.0.1` | サーバーのホスト（環境変数 `LLM_SERVER_HOST` にも反映） |
| `serverPort` | `8765` | サーバーのポート（環境変数 `LLM_SERVER_PORT` にも反映） |
| `startupTimeoutSeconds` | `30` | サーバー起動待ちのタイムアウト秒数 |
| `retryIntervalSeconds` | `1` | 接続テストのリトライ間隔秒数 |

- サーバーの stdout/stderr は Unity Console にリダイレクトされます
- `LLMDecisionClient` は接続前に `LLMServerManager.IsServerReady` を自動的に待機します
- `OnApplicationQuit` / `OnDisable` でプロセスツリーごと確実に終了します

### 4.1 LLM決定サーバー（OpenAI使用）

```python
import asyncio
import websockets
import json
from openai import AsyncOpenAI
from typing import Dict, Any

class EvacuationLLMServer:
    def __init__(self, api_key: str, model: str = "gpt-4o-mini"):
        self.client = AsyncOpenAI(api_key=api_key)
        self.model = model
        
    async def decide_action(self, request_data: Dict[str, Any]) -> Dict[str, Any]:
        """LLMで避難行動を決定"""
        import time
        start_time = time.time()
        
        # リクエストをパース
        request = self._parse_request(request_data)
        
        # プロンプト作成
        prompt = create_prompt(request)
        
        # LLM呼び出し
        response = await self.client.chat.completions.create(
            model=self.model,
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": prompt}
            ],
            temperature=0.7,  # ペルソナによって調整可能
            response_format={"type": "json_object"}  # JSON出力を強制
        )
        
        # レスポンスをパース
        decision = json.loads(response.choices[0].message.content)
        
        # 処理時間を計算
        processing_time = (time.time() - start_time) * 1000
        
        # レスポンス構築
        return {
            "evacuee_id": request_data["evacuee_id"],
            "decision": decision,
            "timestamp": request_data["timestamp"],
            "processing_time_ms": processing_time
        }
    
    def _parse_request(self, data: Dict[str, Any]) -> LLMRequest:
        """JSONをLLMRequestオブジェクトに変換"""
        # 実装は省略（dataclassを使って変換）
        pass

async def handle_client(websocket, llm_server: EvacuationLLMServer):
    """クライアントからのリクエストを処理"""
    async for message in websocket:
        try:
            # リクエストをパース
            request_data = json.loads(message)
            
            # LLMで意思決定
            response = await llm_server.decide_action(request_data)
            
            # レスポンスを送信
            await websocket.send(json.dumps(response))
            
        except Exception as e:
            error_response = {
                "error": str(e),
                "evacuee_id": request_data.get("evacuee_id", "unknown")
            }
            await websocket.send(json.dumps(error_response))

async def main():
    """サーバー起動"""
    llm_server = EvacuationLLMServer(api_key="your-openai-api-key")
    
    async with websockets.serve(
        lambda ws: handle_client(ws, llm_server),
        "localhost",
        8765
    ):
        print("LLM Decision Server started on ws://localhost:8765")
        await asyncio.Future()  # 永続実行

if __name__ == "__main__":
    asyncio.run(main())
```

---

## 5. Unity実装例（C#）

### 5.1 LLMクライアント

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using NativeWebSocket;
using Newtonsoft.Json;

[Serializable]
public class LLMRequest
{
    public string evacuee_id;
    public string request_type;
    public float timestamp;
    public EvacueeStateData evacuee_state;
    public List<ShelterData> available_shelters;
    public EnvironmentData environment;
    public MemoryData memory;
}

[Serializable]
public class LLMResponse
{
    public string evacuee_id;
    public DecisionData decision;
    public float timestamp;
    public float processing_time_ms;
}

[Serializable]
public class DecisionData
{
    public string selected_shelter_id;
    public string selected_shelter_name;
    public float movement_speed;
    public float urgency;
    public string reasoning;
    public float confidence;
}

public class LLMDecisionClient : MonoBehaviour
{
    private WebSocket websocket;
    private Dictionary<string, TaskCompletionSource<LLMResponse>> pendingRequests;
    
    async void Start()
    {
        pendingRequests = new Dictionary<string, TaskCompletionSource<LLMResponse>>();
        
        websocket = new WebSocket("ws://localhost:8765");
        
        websocket.OnMessage += (bytes) =>
        {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            var response = JsonConvert.DeserializeObject<LLMResponse>(message);
            
            if (pendingRequests.ContainsKey(response.evacuee_id))
            {
                pendingRequests[response.evacuee_id].SetResult(response);
                pendingRequests.Remove(response.evacuee_id);
            }
        };
        
        await websocket.Connect();
    }
    
    void Update()
    {
        #if !UNITY_WEBGL || UNITY_EDITOR
        websocket?.DispatchMessageQueue();
        #endif
    }
    
    public async Task<LLMResponse> RequestDecision(LLMRequest request)
    {
        var tcs = new TaskCompletionSource<LLMResponse>();
        pendingRequests[request.evacuee_id] = tcs;
        
        string json = JsonConvert.SerializeObject(request);
        await websocket.SendText(json);
        
        return await tcs.Task;
    }
    
    async void OnDestroy()
    {
        await websocket.Close();
    }
}
```

### 5.2 Evacueeクラスの拡張

```csharp
public class Evacuee : MonoBehaviour {
    
    [Header("LLM Integration")]
    public LLMDecisionClient llmClient;
    public bool useLLM = true;
    
    [Header("Persona")]
    public EvacueePersona persona;
    
    private NavMeshAgent NavAgent;
    
    void Awake() {
        NavAgent = GetComponent<NavMeshAgent>();
        
        if (persona == null) {
            persona = GenerateRandomPersona();
        }
        
        // LLMクライアントを取得
        if (useLLM) {
            llmClient = FindObjectOfType<LLMDecisionClient>();
        }
    }
    
    // エージェントが建物を選択したときに呼ばれる
    private async void OnShelterSelectionNeeded()
    {
        if (useLLM && llmClient != null)
        {
            // LLMに問い合わせ
            var decision = await RequestLLMDecision();
            
            // 決定に従って行動
            ApplyDecision(decision);
        }
        else
        {
            // 従来のロジック（最短距離）
            SearchAndMoveToNearestShelter();
        }
    }
    
    private async Task<DecisionData> RequestLLMDecision()
    {
        // 利用可能な避難所を取得
        List<ShelterData> shelters = GetAvailableShelters();
        
        // リクエストを構築
        var request = new LLMRequest
        {
            evacuee_id = gameObject.name,
            request_type = "shelter_selection",
            timestamp = Time.time,
            evacuee_state = new EvacueeStateData
            {
                position = new Vector3Data(transform.position),
                persona = persona.ToData(),
                current_energy = 0.75f,  // 実際の値を使用
                stress_level = 0.6f
            },
            available_shelters = shelters,
            environment = GetEnvironmentData(),
            memory = GetMemoryData()
        };
        
        // LLMに問い合わせ
        var response = await llmClient.RequestDecision(request);
        
        // デバッグログ
        Debug.Log($"[{gameObject.name}] LLM Decision: {response.decision.selected_shelter_name}");
        Debug.Log($"Reasoning: {response.decision.reasoning}");
        
        return response.decision;
    }
    
    private void ApplyDecision(DecisionData decision)
    {
        // 選択された避難所を見つける
        GameObject selectedShelter = GameObject.Find(decision.selected_shelter_id);
        
        if (selectedShelter != null)
        {
            // 目標を設定
            Target = selectedShelter.transform.GetChild(0).gameObject;
            NavAgent.SetDestination(Target.transform.position);
            
            // 速度を調整
            NavAgent.speed = persona.GetMovementSpeed() * decision.movement_speed;
        }
    }
    
    private List<ShelterData> GetAvailableShelters()
    {
        GameObject[] shelters = GameObject.FindGameObjectsWithTag("Shelter");
        List<ShelterData> shelterDataList = new List<ShelterData>();
        
        foreach (var shelter in shelters)
        {
            var shelterComponent = shelter.GetComponent<Shelter>();
            float distance = Vector3.Distance(transform.position, shelter.transform.position);
            
            shelterDataList.Add(new ShelterData
            {
                id = shelter.name,
                name = shelter.name,
                distance = distance,
                direction = GetDirection(shelter.transform.position),
                current_capacity = shelterComponent.NowAccCount,
                max_capacity = shelterComponent.MaxCapacity,
                capacity_ratio = (float)shelterComponent.NowAccCount / shelterComponent.MaxCapacity,
                safety_rating = 0.9f,  // 実際の値を使用
                crowd_density = 0.5f
            });
        }
        
        return shelterDataList;
    }
    
    private string GetDirection(Vector3 targetPos)
    {
        Vector3 dir = (targetPos - transform.position).normalized;
        
        if (dir.z > 0.7f) return "北";
        if (dir.z < -0.7f) return "南";
        if (dir.x > 0.7f) return "東";
        if (dir.x < -0.7f) return "西";
        if (dir.z > 0 && dir.x > 0) return "北東";
        if (dir.z > 0 && dir.x < 0) return "北西";
        if (dir.z < 0 && dir.x > 0) return "南東";
        return "南西";
    }
}
```

---

## 6. データフロー全体図

```text
1. Unity: 避難者が意思決定を必要とする
   ↓
2. Evacuee.cs: 現在の状態と利用可能な避難所情報を収集
   ↓
3. LLMDecisionClient: JSONリクエストを構築
   ↓
4. WebSocket: Python サーバーにリクエスト送信
   ↓
5. Python Server: リクエストを受信
   ↓
6. Prompt作成: ペルソナと状況に基づいたプロンプト生成
   ↓
7. OpenAI API: LLMで意思決定
   ↓
8. JSON Response: 決定をJSON形式で返す
   ↓
9. WebSocket: Unityにレスポンス送信
   ↓
10. Evacuee.cs: 決定に従って行動
    ↓
11. NavMeshAgent: 選択された避難所に移動
```

---

## 7. 重要な考慮事項

### 7.1 パフォーマンス

- **LLM呼び出しは遅い**（1-3秒）
- 全避難者を同時に問い合わせない
- バッチ処理または順次処理を検討

```csharp
// 例: 避難者を5秒ごとに5人ずつ処理
IEnumerator ProcessEvacueesInBatches()
{
    int batchSize = 5;
    for (int i = 0; i < Evacuees.Count; i += batchSize)
    {
        for (int j = i; j < Mathf.Min(i + batchSize, Evacuees.Count); j++)
        {
            Evacuees[j].OnShelterSelectionNeeded();
        }
        yield return new WaitForSeconds(5f);
    }
}
```

### 7.2 コスト管理

- GPT-4o-mini使用推奨（安価で高速）
- 1リクエスト約500トークン → 約$0.0001
- 100人 × 1回 = $0.01
- 実験10回でも$0.10程度

### 7.3 エラーハンドリング

```python
try:
    decision = await llm_server.decide_action(request_data)
except Exception as e:
    # エラー時はデフォルト動作（最短距離）にフォールバック
    decision = {
        "selected_shelter_id": "default_shelter",
        "movement_speed": 1.0,
        "reasoning": f"LLMエラー: {str(e)}"
    }
```

---

## 8. まとめ

### 入力（Unity → LLM）の要点

✅ 必須: 避難所候補の位置・収容数  
✅ 必須: 現在の避難者位置リスト  
✅ 任意: ペルソナ、ハザード、環境・記憶情報（将来拡張）

### 出力（LLM → Unity）の要点

✅ 必須: 各候補に対応する 0/1 配列  
✅ 任意: 理由付け、確信度、移動速度などのメタ情報

### 実装の現実性

⭐ WebSocket通信：標準的な技術
⭐ JSON形式：シンプルで実装容易
⭐ 非同期処理：Unityでも対応可能
⭐ コスト：実験規模なら数十円〜数百円

**これで実装可能なLLM統合が実現できます！**

---

## 交通シミュレーション関連フィールド（拡張）

### 入力（Unity → LLM）に追加されるフィールド

| フィールド | 型 | 説明 |
|-----------|-----|------|
| `transport_mode` | string | 移動手段: "WALKING" / "DRIVING" |
| `nearby_traffic` | object | 周辺の交通状況 |
| `nearby_traffic.nearby_roads` | array | 周辺道路ごとの交通データ |
| `nearby_traffic.overall_congestion` | float | 全体的な渋滞度 (0-1) |
| `nearby_traffic.summary` | string | 自然言語での渋滞サマリー |
| `persona.has_vehicle` | bool | 車両所有フラグ |
| `persona.can_drive` | bool | 運転可能フラグ |

### 出力（LLM → Unity）に追加されるフィールド

| フィールド | 型 | 説明 |
|-----------|-----|------|
| `recommended_transport_mode` | string | 推奨移動手段: "WALKING" / "DRIVING" / "ABANDON_VEHICLE" |
| `recommended_route_change` | string | ルート変更の推奨理由 |

### nearby_traffic.nearby_roads の要素

```json
{
  "edge_id": "12345-67890-0",
  "road_name": "国道6号",
  "congestion_level": 4,
  "congestion_label": "渋滞",
  "average_speed_kmh": 15.0,
  "estimated_travel_time": 180.0,
  "free_flow_travel_time": 60.0,
  "distance_meters": 50.0
}
```
