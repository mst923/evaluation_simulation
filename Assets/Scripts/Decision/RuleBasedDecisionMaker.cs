using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using EvacSim.Core;
using EvacSim.Decision.LLM;
using EvacSim.Evacuee;
using EvacSim.Shelter;

namespace EvacSim.Decision
{
/// <summary>
/// ルールベースエージェントの意思決定を行うクラス（Level 3）
/// 実験1でLLMエージェントとの比較に使用
///
/// Level 3のルール:
/// - 行動タイプは EVACUATE, STAY, SEARCH_FAMILY, FOLLOW
/// - mental_stateに応じた行動分化（7タイプ）
/// - 確率的要素の導入（正常性バイアスによる避難遅延等）
/// - 避難先として避難所と津波避難場所の両方を選択可能
/// - FOLLOW行動による同調行動の実装
/// </summary>
public class RuleBasedDecisionMaker : MonoBehaviour
{
    [Header("Rule Parameters")]
    [Tooltip("待機から避難に切り替える最大時間（秒）")]
    public float MaxStayDuration = 600f;

    [Tooltip("危険と判定する震度の閾値（現在未使用：津波警報のみで判定）")]
    public int DangerousSeismicIntensity = 6;

    [Tooltip("ルール再評価の間隔（秒）")]
    public float ReevaluationInterval = 10f;

    [Tooltip("家族探索の距離閾値（メートル）")]
    public float SearchFamilyDistanceThreshold = 500f;

    [Header("Level 3: Conformity Parameters")]
    [Tooltip("同調チェック半径（メートル）")]
    public float ConformityCheckRadius = 30f;

    [Tooltip("同調バイアス発動の人数閾値（社交的・周囲配慮型）")]
    public int ConformityThreshold = 3;

    [Tooltip("強い同調バイアス発動の人数閾値（不安傾向・サポート希求型）")]
    public int StrongConformityThreshold = 1;

    [Header("Level 3: Information Diffusion Parameters")]
    [Tooltip("情報伝播の確率（0.0〜1.0）")]
    public float InformationDiffusionProbability = 0.3f;

    [Tooltip("情報伝播が発生する距離（メートル）")]
    public float ProximityRadius = 5f;

    private Evacuee _evacuee;
    private EnvManager _envManager;
    private NavMeshAgent _navAgent;
    private float _stayStartTime = -1f;
    private float _lastEvaluationTime = 0f;
    private bool _hasHeardBroadcast = false;
    private bool _hasReceivedJAlert = false;
    private bool _hasHeardFireTruck = false;

    // Level 2: 家族対応用フィールド
    private FamilyData _familyData;

    // Level 3: ペルソナベースの行動分化用フィールド
    private PersonaData _persona;
    private System.Random _decisionRandom;
    private int _alertReceivedCount = 0;
    private float _preparationTime = 0f;
    private bool _preparationComplete = false;
    private float _preparationStartTime = -1f;
    private float _effectiveMaxStayDuration;
    private Evacuee _followTargetEvacuee;

    // Level 3: 避難先ロック用フィールド（一度選択した避難先を維持）
    private GameObject _lockedDestination = null;

    // Level 3: 情報伝播（Pseudo-TALK）用フィールド
    private bool _hasHeardFromNearbyAgent = false;
    private string _informationSourceName = null;

    /// <summary>
    /// 初期化
    /// </summary>
    public void Initialize(Evacuee evacuee, EnvManager envManager, NavMeshAgent navAgent)
    {
        _evacuee = evacuee;
        _envManager = envManager;
        _navAgent = navAgent;

        // ペルソナは後で取得（SetEvacueeId()後に利用可能になるため）
        // EnsurePersonaLoaded()で遅延取得する

        // 乱数生成器は仮のシードで初期化（ペルソナ読み込み時に再初期化）
        _decisionRandom = new System.Random(0);

        // 準備時間と最大待機時間はペルソナ読み込み後に計算
        // EnsurePersonaLoaded()で実行される
    }

    /// <summary>
    /// ペルソナが読み込まれていなければ読み込む（遅延初期化）
    /// </summary>
    private void EnsurePersonaLoaded()
    {
        if (_persona != null) return;

        _persona = _evacuee.GetPersona();

        if (_persona != null)
        {
            // 乱数生成器を再初期化（エージェントIDでシード）
            if (int.TryParse(_evacuee.EvacueeId, out int id))
            {
                _decisionRandom = new System.Random(id);
            }

            // 準備時間を計算
            CalculatePreparationTime();

            // 有効な最大待機時間を計算
            _effectiveMaxStayDuration = CalculateEffectiveMaxStayDuration();

            Debug.Log($"[RuleBased] {_evacuee.gameObject.name}: ペルソナ読み込み完了 - {_persona.mental_state}");
        }
    }

    /// <summary>
    /// 拡張初期化（Level 2: 家族情報を受け取る）
    /// </summary>
    public void InitializeExtended(FamilyData familyData)
    {
        _familyData = familyData;
    }

    /// <summary>
    /// 放送を聞いたことを記録
    /// </summary>
    public void OnHeardBroadcast()
    {
        if (!_hasHeardBroadcast)
        {
            _hasHeardBroadcast = true;
            _alertReceivedCount++;
        }
    }

    /// <summary>
    /// Jアラートを受信したことを記録
    /// </summary>
    public void OnReceivedJAlert()
    {
        if (!_hasReceivedJAlert)
        {
            _hasReceivedJAlert = true;
            _alertReceivedCount++;
        }
    }

    /// <summary>
    /// 消防団の呼びかけを聞いたことを記録
    /// </summary>
    public void OnHeardFireTruck()
    {
        if (!_hasHeardFireTruck)
        {
            _hasHeardFireTruck = true;
            _alertReceivedCount++;
        }
    }

    /// <summary>
    /// ルールベースで行動を決定（Level 1: 後方互換性のため維持）
    /// </summary>
    /// <returns>決定された行動タイプと目標避難所</returns>
    public (ActionType action, GameObject targetShelter, string reasoning) MakeDecision()
    {
        var (action, shelter, reasoning, _) = MakeDecisionExtended();
        return (action, shelter, reasoning);
    }

    /// <summary>
    /// ルールベースで行動を決定（Level 3: mental_stateベースの行動分化）
    /// </summary>
    /// <returns>決定された行動タイプ、目標避難先、理由、家族対象</returns>
    public (ActionType action, GameObject targetShelter, string reasoning, FamilyMember familyTarget) MakeDecisionExtended()
    {
        // ペルソナの遅延読み込み
        EnsurePersonaLoaded();

        float currentTime = _envManager != null ? _envManager.CurrentTimeSec : Time.time;

        // 最小評価間隔のチェック
        if (currentTime - _lastEvaluationTime < ReevaluationInterval && _lastEvaluationTime > 0)
        {
            // 前回と同じ行動を継続
            return (_evacuee.CurrentAction, _evacuee.Target, "前回の判断を継続", null);
        }
        _lastEvaluationTime = currentTime;

        // 準備時間のチェック（避難開始前の遅延）
        if (!_preparationComplete && _preparationTime > 0)
        {
            if (_preparationStartTime < 0)
            {
                _preparationStartTime = currentTime;
            }

            float elapsed = currentTime - _preparationStartTime;
            if (elapsed < _preparationTime)
            {
                return (ActionType.STAY, null, $"避難準備中（残り{(_preparationTime - elapsed):F0}秒）", null);
            }
            _preparationComplete = true;
        }

        // 優先度0: 焦燥・目的外行動の家族探索（津波警報より優先）
        if (HasFamilyPriority() && ShouldSearchFamily(out var urgentSearchTarget))
        {
            return (ActionType.SEARCH_FAMILY, null,
                $"家族が心配！{urgentSearchTarget.relation}（{urgentSearchTarget.name}）を探しに行く", urgentSearchTarget);
        }

        // 優先度1: 津波警報受信時
        bool alertReceived = _hasReceivedJAlert || _hasHeardBroadcast || _hasHeardFireTruck || _hasHeardFromNearbyAgent;
        if (alertReceived)
        {
            // 同調バイアス系: 先にFOLLOWを検討
            if (HasConformityBias() || HasStrongConformityBias())
            {
                var followResult = TryConformityFollow();
                if (followResult.action == ActionType.FOLLOW)
                {
                    return followResult;
                }

                // FOLLOWできない場合は周囲の状況を確認
                int stayingCount = CountNearbyStayingAgents();
                int evacuatingCount = CountNearbyEvacuatingAgents();

                // 周囲に避難中が多ければ自分も避難（確率判定をスキップ）
                if (evacuatingCount > stayingCount)
                {
                    var (dest, _) = SelectDestination();
                    return (ActionType.EVACUATE, dest, $"周囲が避難を始めたので避難（避難中{evacuatingCount}人）", null);
                }

                // 周囲が待機多数なら待機継続
                if (stayingCount >= 2)
                {
                    return (ActionType.STAY, null, $"周囲も様子を見ているので待機（待機中{stayingCount}人）", null);
                }

                // 周囲に人がいない場合、不安傾向は動けない
                if (HasStrongConformityBias())
                {
                    return (ActionType.STAY, null, "周囲に人がいないので不安で動けない", null);
                }
            }

            // 全ペルソナ共通: 確率的判定
            float evacuationProbability = GetEvacuationProbability();
            if (ProbabilisticCheck(evacuationProbability))
            {
                var (dest, _) = SelectDestination();
                string reason = BuildEvacuateReason() + $"（確率{evacuationProbability:P0}で判断）";
                return (ActionType.EVACUATE, dest, reason, null);
            }

            // 確率を満たさない場合はSTAY
            string mentalStateInfo = _persona?.mental_state ?? "不明";
            return (ActionType.STAY, null, $"まだ様子を見る（{mentalStateInfo}、避難確率{evacuationProbability:P0}）", null);
        }

        // 優先度2: 同調バイアス判定（FOLLOW）
        if (HasConformityBias() || HasStrongConformityBias())
        {
            var followResult = TryConformityFollow();
            if (followResult.action == ActionType.FOLLOW)
            {
                return followResult;
            }

            // 周囲が待機多数の場合はSTAY傾向
            int stayingCount = CountNearbyStayingAgents();
            int evacuatingCount = CountNearbyEvacuatingAgents();
            if (stayingCount > evacuatingCount && stayingCount >= 2)
            {
                return (ActionType.STAY, null, $"周囲も様子を見ているので待機（待機中{stayingCount}人）", null);
            }
        }

        // 優先度3: 家族対応（既存ロジック）
        if (ShouldSearchFamily(out var searchTarget))
        {
            return (ActionType.SEARCH_FAMILY, null,
                $"{searchTarget.relation}（{searchTarget.name}）を探しに行く", searchTarget);
        }

        // 優先度4: 待機（津波警報なし・家族対応なし）
        if (_stayStartTime < 0)
        {
            _stayStartTime = currentTime;
        }

        float stayDuration = currentTime - _stayStartTime;

        // 待機時間上限を超えた場合のみ避難（安全のため）
        if (stayDuration >= _effectiveMaxStayDuration)
        {
            var (dest, _) = SelectDestination();
            return (ActionType.EVACUATE, dest, $"待機時間が{_effectiveMaxStayDuration:F0}秒を超過したため避難", null);
        }

        // 待機を継続
        return (ActionType.STAY, null, $"津波警報未受信のため様子見（経過{stayDuration:F0}秒）", null);
    }

    #region mental_state判定メソッド

    /// <summary>
    /// 正常性バイアスを持つか判定（楽観的・自己信頼型）
    /// </summary>
    private bool HasNormalcyBias()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state)) return false;
        return _persona.mental_state.Contains("楽観") ||
               _persona.mental_state.Contains("自己信頼");
    }

    /// <summary>
    /// 同調バイアスを持つか判定（社交的・周囲配慮型）
    /// </summary>
    private bool HasConformityBias()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state)) return false;
        return _persona.mental_state.Contains("社交的") ||
               _persona.mental_state.Contains("周囲配慮");
    }

    /// <summary>
    /// 強い同調バイアスを持つか判定（不安傾向・サポート希求型）
    /// </summary>
    private bool HasStrongConformityBias()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state)) return false;
        return _persona.mental_state.Contains("不安傾向") ||
               _persona.mental_state.Contains("サポート希求");
    }

    /// <summary>
    /// 混雑回避傾向を持つか判定（慎重・人混み恐怖）
    /// </summary>
    private bool HasCrowdAvoidance()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state)) return false;
        return _persona.mental_state.Contains("慎重") ||
               _persona.mental_state.Contains("人混み恐怖");
    }

    /// <summary>
    /// 家族優先傾向を持つか判定（焦燥・目的外行動）
    /// </summary>
    private bool HasFamilyPriority()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state)) return false;
        return _persona.mental_state.Contains("焦燥") ||
               _persona.mental_state.Contains("目的外行動");
    }

    /// <summary>
    /// 冷静系か判定（冷静・合理的 / 冷静・分析的）
    /// </summary>
    private bool IsRational()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state)) return true; // デフォルトは冷静
        return _persona.mental_state.Contains("冷静");
    }

    /// <summary>
    /// 高齢者か判定
    /// </summary>
    private bool IsElderly()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.age_group)) return false;
        return _persona.age_group.Contains("70") ||
               _persona.age_group.Contains("80");
    }

    /// <summary>
    /// 若年層か判定（子供/10代）
    /// </summary>
    private bool IsYoung()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.age_group)) return false;
        return _persona.age_group.Contains("10") ||
               _persona.age_group.ToLower().Contains("child");
    }

    #endregion

    #region 確率・パラメータ計算

    /// <summary>
    /// 避難確率を取得（mental_stateに応じた基本確率）
    /// 警報回数と時間経過に応じて確率が上昇する
    /// </summary>
    private float GetEvacuationProbability()
    {
        // mental_stateに応じた基本確率
        float baseProbability = GetBaseEvacuationProbability();

        // 警報受信回数ごとに+10%（最大+30%）
        baseProbability += Mathf.Min(_alertReceivedCount * 0.1f, 0.3f);

        // 時間経過による確率増加（経過時間/1800秒 を基準、最大+20%）
        float currentTime = _envManager != null ? _envManager.CurrentTimeSec : Time.time;
        float timeElapsed = _stayStartTime >= 0 ? (currentTime - _stayStartTime) : 0f;
        float timeRatio = Mathf.Clamp01(timeElapsed / 1800f);
        baseProbability += timeRatio * 0.2f;

        return Mathf.Clamp01(baseProbability);
    }

    /// <summary>
    /// mental_stateに応じた基本避難確率を取得
    /// </summary>
    private float GetBaseEvacuationProbability()
    {
        if (_persona == null || string.IsNullOrEmpty(_persona.mental_state))
        {
            return 0.9f; // デフォルトは高確率
        }

        // 楽観的・自己信頼型: 正常性バイアスで最も低い
        if (HasNormalcyBias())
        {
            return 0.15f;
        }

        // 不安傾向・サポート希求型: 不安で動けないことも
        if (HasStrongConformityBias())
        {
            return 0.40f;
        }

        // 社交的・周囲配慮型: 周囲を見て判断
        if (HasConformityBias())
        {
            return 0.50f;
        }

        // 慎重・人混み恐怖: 比較的早く避難
        if (HasCrowdAvoidance())
        {
            return 0.70f;
        }

        // 焦燥・目的外行動: 焦って行動
        if (HasFamilyPriority())
        {
            return 0.80f;
        }

        // 冷静・分析的
        if (_persona.mental_state.Contains("分析"))
        {
            return 0.85f;
        }

        // 冷静・合理的（デフォルト）
        return 0.90f;
    }

    /// <summary>
    /// 確率的判断を行う
    /// </summary>
    private bool ProbabilisticCheck(float probability)
    {
        return _decisionRandom.NextDouble() < probability;
    }

    /// <summary>
    /// 準備時間を計算
    /// </summary>
    private void CalculatePreparationTime()
    {
        // 基本準備時間: 10-30秒
        _preparationTime = _decisionRandom.Next(10, 31);

        // 高齢者は追加時間
        if (IsElderly())
        {
            _preparationTime += _decisionRandom.Next(20, 61);
        }

        // 楽観的・自己信頼型は「まだ大丈夫」と準備が遅い
        if (HasNormalcyBias())
        {
            _preparationTime *= 1.5f;
        }

        // 焦燥系は準備時間が短い
        if (HasFamilyPriority())
        {
            _preparationTime *= 0.5f;
        }
    }

    /// <summary>
    /// 有効な最大待機時間を計算
    /// </summary>
    private float CalculateEffectiveMaxStayDuration()
    {
        float duration = MaxStayDuration;

        // 正常性バイアスの場合は延長（1.5〜2.0倍）
        if (HasNormalcyBias())
        {
            float multiplier = 1.5f + (float)_decisionRandom.NextDouble() * 0.5f;
            duration *= multiplier;
        }

        // 焦燥系は短縮（0.5倍）
        if (HasFamilyPriority())
        {
            duration *= 0.5f;
        }

        return duration;
    }

    #endregion

    #region FOLLOW行動関連

    /// <summary>
    /// 同調によるFOLLOW行動を試みる
    /// </summary>
    private (ActionType action, GameObject target, string reasoning, FamilyMember familyTarget) TryConformityFollow()
    {
        var nearbyEvacuees = GetNearbyEvacuatingAgents();

        int threshold = HasStrongConformityBias() ? StrongConformityThreshold : ConformityThreshold;
        if (nearbyEvacuees.Count < threshold)
        {
            // FOLLOWできない場合
            if (HasStrongConformityBias())
            {
                // 不安傾向は不安で動けない
                return (ActionType.STAY, null, "周囲に避難中の人がいないので不安で動けない", null);
            }
            return (ActionType.STAY, null, null, null); // 次の優先度へ
        }

        // 避難中のエージェントから追従対象を選択
        var followTarget = nearbyEvacuees
            .Where(e => !WouldCreateFollowCycle(e))  // 循環防止
            .OrderBy(e => Vector3.Distance(_evacuee.transform.position, e.transform.position))
            .FirstOrDefault();

        if (followTarget == null)
        {
            return (ActionType.STAY, null, null, null);
        }

        _followTargetEvacuee = followTarget;
        string targetName = followTarget.PersonaName ?? followTarget.EvacueeId;
        return (ActionType.FOLLOW, followTarget.Target,
            $"周囲の人（{targetName}）について行く", null);
    }

    /// <summary>
    /// 周囲の避難中エージェントを取得
    /// </summary>
    private List<Evacuee> GetNearbyEvacuatingAgents()
    {
        var result = new List<Evacuee>();
        if (_envManager == null) return result;

        Vector3 pos = _evacuee.transform.position;

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null || !evacueeObj.activeSelf) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null || evacuee == _evacuee) continue;

            float distance = Vector3.Distance(pos, evacuee.transform.position);
            if (distance > ConformityCheckRadius) continue;

            if (evacuee.CurrentAction == ActionType.EVACUATE ||
                evacuee.CurrentAction == ActionType.FOLLOW)
            {
                result.Add(evacuee);
            }
        }

        return result;
    }

    /// <summary>
    /// 周囲の避難中エージェント数をカウント
    /// </summary>
    private int CountNearbyEvacuatingAgents()
    {
        return GetNearbyEvacuatingAgents().Count;
    }

    /// <summary>
    /// 周囲の待機中エージェント数をカウント
    /// </summary>
    private int CountNearbyStayingAgents()
    {
        int count = 0;
        if (_envManager == null) return count;

        Vector3 pos = _evacuee.transform.position;

        foreach (var evacueeObj in _envManager.Evacuees)
        {
            if (evacueeObj == null || !evacueeObj.activeSelf) continue;

            var evacuee = evacueeObj.GetComponent<Evacuee>();
            if (evacuee == null || evacuee == _evacuee) continue;

            float distance = Vector3.Distance(pos, evacuee.transform.position);
            if (distance > ConformityCheckRadius) continue;

            if (evacuee.CurrentAction == ActionType.STAY)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// FOLLOW循環が発生するか判定
    /// </summary>
    private bool WouldCreateFollowCycle(Evacuee target)
    {
        // 相手が自分をFOLLOWしている場合は循環
        if (target.CurrentAction == ActionType.FOLLOW)
        {
            // 直接自分を追従しているか確認
            var targetFollowTarget = target.FollowTarget;
            if (targetFollowTarget == _evacuee)
            {
                return true;
            }

            // 間接的に自分を追従しているか確認（2段階まで）
            if (targetFollowTarget != null && targetFollowTarget.CurrentAction == ActionType.FOLLOW)
            {
                var indirectTarget = targetFollowTarget.FollowTarget;
                if (indirectTarget == _evacuee)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// FOLLOWターゲットを取得
    /// </summary>
    public Evacuee GetFollowTargetEvacuee()
    {
        return _followTargetEvacuee;
    }

    #endregion

    #region 避難先選択

    /// <summary>
    /// mental_stateに応じた避難先を選択
    /// 一度選択した避難先は有効な限り維持する（避難先振動の防止）
    /// </summary>
    private (GameObject destination, float distance) SelectDestination()
    {
        Vector3 pos = _evacuee.transform.position;

        // ロック中の避難先があり、まだ有効であれば継続
        if (_lockedDestination != null && IsDestinationStillValid(_lockedDestination))
        {
            float dist = Vector3.Distance(pos, _lockedDestination.transform.position);
            return (_lockedDestination, dist);
        }

        // ロック解除（無効になった場合）
        _lockedDestination = null;

        // 新規選択（既存のmental_stateベースのロジック）
        (GameObject destination, float distance) result;

        // 慎重・人混み恐怖: 混雑回避
        if (HasCrowdAvoidance())
        {
            result = FindLeastCrowdedDestination();
        }
        // 焦燥・目的外行動: 高台優先
        else if (HasFamilyPriority())
        {
            result = FindHighElevationDestination();
        }
        // その他（冷静系、楽観系等）: 最寄り
        else
        {
            result = FindNearestDestination();
        }

        // ロック設定
        _lockedDestination = result.destination;

        return result;
    }

    /// <summary>
    /// 避難先がまだ有効か判定（収容可能か）
    /// </summary>
    private bool IsDestinationStillValid(GameObject destination)
    {
        if (destination == null) return false;

        var shelter = destination.GetComponentInParent<Shelter>();
        if (shelter != null)
        {
            return shelter.currentCapacity > 0;
        }

        // 津波避難場所は常に有効（収容制限なし）
        return true;
    }

    /// <summary>
    /// 避難所と津波避難場所を統合した避難先リストを取得
    /// </summary>
    private List<(GameObject obj, float elevation, int capacity, bool isShelter)> GetAllEvacuationDestinations()
    {
        var destinations = new List<(GameObject, float, int, bool)>();

        // 避難所（Shelter）
        if (_envManager?.Shelters != null)
        {
            foreach (var shelterObj in _envManager.Shelters)
            {
                if (shelterObj == null) continue;

                var shelter = shelterObj.GetComponent<Shelter>();
                if (shelter == null || shelter.currentCapacity <= 0) continue;

                var point = shelterObj.transform.childCount > 0
                    ? shelterObj.transform.GetChild(0).gameObject
                    : shelterObj;
                destinations.Add((point, shelter.GetElevation(), shelter.currentCapacity, true));
            }
        }

        // 津波避難場所（TsunamiEvacuationArea）
        // 津波避難場所は収容制限がない（高台なので無制限に受け入れ可能）
        if (_envManager?.TsunamiEvacuationAreas != null)
        {
            foreach (var areaObj in _envManager.TsunamiEvacuationAreas)
            {
                if (areaObj == null) continue;

                var area = areaObj.GetComponent<TsunamiEvacuationArea>();
                if (area == null) continue;

                // 津波避難場所は収容制限なしのため、capacityは十分大きな値を設定
                destinations.Add((areaObj, area.elevationMeters, int.MaxValue, false));
            }
        }

        return destinations;
    }

    /// <summary>
    /// 最寄りの避難先を検索（避難所 + 津波避難場所）
    /// </summary>
    private (GameObject destination, float distance) FindNearestDestination()
    {
        var destinations = GetAllEvacuationDestinations();
        if (destinations.Count == 0) return (null, float.MaxValue);

        Vector3 pos = _evacuee.transform.position;

        var nearest = destinations
            .OrderBy(d => Vector3.Distance(pos, d.obj.transform.position))
            .FirstOrDefault();

        return (nearest.obj, Vector3.Distance(pos, nearest.obj.transform.position));
    }

    /// <summary>
    /// 混雑回避で避難先を選択（最寄りを避けて2番目以降を選択）
    /// 人混み恐怖の人は「近くの避難所は混む」と予測し、あえて少し遠い場所を選ぶ
    /// </summary>
    private (GameObject destination, float distance) FindLeastCrowdedDestination()
    {
        var destinations = GetAllEvacuationDestinations();
        if (destinations.Count == 0) return (null, float.MaxValue);

        Vector3 pos = _evacuee.transform.position;

        // 距離順にソート
        var sortedByDistance = destinations
            .OrderBy(d => Vector3.Distance(pos, d.obj.transform.position))
            .ToList();

        // 最寄りを避けて2番目以降を選択（2箇所以上ある場合）
        // 1箇所しかない場合は仕方なく最寄りを選択
        var selected = sortedByDistance.Count > 1 ? sortedByDistance[1] : sortedByDistance[0];

        return (selected.obj, Vector3.Distance(pos, selected.obj.transform.position));
    }

    /// <summary>
    /// 高台優先で避難先を選択（海抜が高い場所優先）
    /// </summary>
    private (GameObject destination, float distance) FindHighElevationDestination()
    {
        var destinations = GetAllEvacuationDestinations();
        if (destinations.Count == 0) return (null, float.MaxValue);

        Vector3 pos = _evacuee.transform.position;

        // 海抜が高い場所を優先
        var highest = destinations
            .OrderByDescending(d => d.elevation)
            .ThenBy(d => Vector3.Distance(pos, d.obj.transform.position))
            .FirstOrDefault();

        return (highest.obj, Vector3.Distance(pos, highest.obj.transform.position));
    }

    /// <summary>
    /// 最寄りの避難所を検索（後方互換用）
    /// </summary>
    private (GameObject shelter, float distance) FindNearestShelter()
    {
        return FindNearestDestination();
    }

    #endregion

    #region 家族対応

    /// <summary>
    /// SEARCH_FAMILYを実行すべきか判定
    /// 条件: シーン内に未合流の家族がいて、距離閾値以内
    /// </summary>
    private bool ShouldSearchFamily(out FamilyMember target)
    {
        target = null;
        if (_familyData == null || _familyData.members == null) return false;

        var unreunitedFamily = _familyData.members
            .Where(m => m.exists_in_scene && m.agent_id > 0 && !m.is_reunited)
            .ToList();

        if (unreunitedFamily.Count == 0) return false;

        Vector3 pos = _evacuee.transform.position;

        // 距離閾値以内、子供を優先
        target = unreunitedFamily
            .Where(m => Vector3.Distance(pos, m.search_position) < SearchFamilyDistanceThreshold)
            .OrderBy(m => IsChild(m.relation) ? 0 : 1)
            .FirstOrDefault();

        return target != null;
    }

    /// <summary>
    /// 子供かどうかを判定
    /// </summary>
    private bool IsChild(string relation)
    {
        if (string.IsNullOrEmpty(relation)) return false;
        return relation.Contains("息子") || relation.Contains("娘") ||
               relation.Contains("子供") || relation.Contains("子ども");
    }

    #endregion

    #region ユーティリティ

    /// <summary>
    /// 震度を取得（シナリオから推定）
    /// </summary>
    private int GetSeismicIntensity()
    {
        if (_envManager == null) return 2;

        switch (_envManager.Scenario)
        {
            case EnvManager.DisasterScenario.Shindo2:
                return 2;
            case EnvManager.DisasterScenario.Shindo6:
                return 6;
            case EnvManager.DisasterScenario.Shindo7Tsunami:
                return 7;
            default:
                return 2;
        }
    }

    /// <summary>
    /// 避難理由の文字列を生成（津波警報のみで判定）
    /// </summary>
    private string BuildEvacuateReason()
    {
        var reasons = new List<string>();

        if (_hasReceivedJAlert)
        {
            reasons.Add("Jアラートを受信");
        }

        if (_hasHeardBroadcast)
        {
            reasons.Add("行政無線の避難指示を聞いた");
        }

        if (_hasHeardFireTruck)
        {
            reasons.Add("消防団の呼びかけを聞いた");
        }

        if (reasons.Count == 0)
        {
            return "避難を開始";
        }

        return string.Join("、", reasons) + "ため避難を開始";
    }

    /// <summary>
    /// 状態をリセット（エピソード開始時）
    /// </summary>
    public void Reset()
    {
        _stayStartTime = -1f;
        _lastEvaluationTime = 0f;
        _hasHeardBroadcast = false;
        _hasReceivedJAlert = false;
        _hasHeardFireTruck = false;
        _alertReceivedCount = 0;
        _preparationComplete = false;
        _preparationStartTime = -1f;
        _followTargetEvacuee = null;

        // 避難先ロックのリセット
        _lockedDestination = null;

        // 情報伝播フラグのリセット
        _hasHeardFromNearbyAgent = false;
        _informationSourceName = null;

        // 再計算
        if (_decisionRandom != null)
        {
            CalculatePreparationTime();
            _effectiveMaxStayDuration = CalculateEffectiveMaxStayDuration();
        }
    }

    #region Information Diffusion (Pseudo-TALK)

    /// <summary>
    /// 近くの避難中エージェントから情報を受け取った時に呼ばれる
    /// SIRモデルの「感染」に相当
    /// </summary>
    public void OnHeardFromNearbyAgent(string sourceName)
    {
        if (_hasHeardFromNearbyAgent) return; // 既に受信済み

        _hasHeardFromNearbyAgent = true;
        _informationSourceName = sourceName;
        _alertReceivedCount++;

        Debug.Log($"[RuleBased] {_evacuee.gameObject.name}: {sourceName}から避難情報を受け取った（情報伝播）");
    }

    /// <summary>
    /// このエージェントが情報源として機能できるか判定
    /// （警報を受信済みかつ避難中/追従中）
    /// </summary>
    public bool CanSpreadInformation()
    {
        bool hasAlert = _hasHeardBroadcast || _hasReceivedJAlert || _hasHeardFireTruck || _hasHeardFromNearbyAgent;
        if (!hasAlert) return false;

        var currentAction = _evacuee.CurrentAction;
        return currentAction == ActionType.EVACUATE || currentAction == ActionType.FOLLOW;
    }

    /// <summary>
    /// 近接エージェントから情報を受信済みか
    /// </summary>
    public bool HasHeardFromNearbyAgent => _hasHeardFromNearbyAgent;

    /// <summary>
    /// 情報源の名前を取得
    /// </summary>
    public string InformationSourceName => _informationSourceName;

    #endregion

    #endregion
}
}
