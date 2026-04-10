using System.Collections;
using System.Collections.Generic;
using System;
using System.Linq;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using EvacSim.Core;

namespace EvacSim.Shelter
{
/// <summary>
/// 実装する全てのエージェントは、
/// ML-AgentsパッケージにあるAgentクラスを継承して実装します。
/// https://docs.unity3d.com/Packages/com.unity.ml-agents@3.0/api/Unity.MLAgents.Agent.html
/// </summary>

public class ShelterManagementAgent : Agent {
    
    public GameObject[] ShelterCandidates; //エージェントが操作する避難所の候補リスト
    public Material SelectedMaterial; // 避難所としてその建物を選択している時のマテリアル
    public Material NonSelectMaterial; // 避難所としてその建物を選択していない時のマテリアル
    public Action OnDidActioned; // 行動選択後に呼ばれるイベント関数(避難者の初期化にて使用します)

    public bool AlwaysActivateAllShelters = true;

    // episode, step, 各避難所候補の選択状況のリスト(true or false)
    public List<Tuple<int, int, List<bool>>> ActionLogs = new List<Tuple<int, int, List<bool>>>(); 
    public bool Disabled = false;
    public List<GameObject> ConstBldgs;
    private EnvManager _env; // 環境管理クラスの参照
    private readonly List<int> _workingActionBuffer = new List<int>();
    
    void Start() {
        _env = GetComponentInParent<EnvManager>();
    }

    public override void Initialize() {
        if(ShelterCandidates.Length == 0) {
            //Debug.LogError("No shelter candidates");
            // NOTE: 予め候補地は事前に設定させておくこと
            ShelterCandidates = GameObject.FindGameObjectsWithTag("Shelter");
        }
    }

    /// <summary>
    /// Agent.EndEpisode()後に呼ばれる
    /// 環境の初期化処理実行後に、エージェントの行動をリクエストします。 
    /// </summary>
    public override void OnEpisodeBegin() {
        _env.OnEpisodeBegin();
        if (AlwaysActivateAllShelters)
        {
            ActivateAllShelters();
            OnDidActioned?.Invoke();
        }
        else
        {
            RequestDecision(); // 行動選択をリクエスト
        }
    }

    public void OnEndEpisode() {
        // AlwaysActivateAllSheltersが有効な場合はログ生成をスキップ
        if (AlwaysActivateAllShelters)
        {
            ActionLogs.Clear();
            return;
        }

        // データの保存とActionLogsの初期化
        // 避難所の建物IDを取得
        string[] shelterIds = new string[ShelterCandidates.Length];
        for(int i = 0; i < ShelterCandidates.Length; i++) {
            shelterIds[i] = ShelterCandidates[i].name;
        }
        // CSVデータの作成
        string[] headers = new string[ShelterCandidates.Length + 2];
        headers[0] = "Episode";
        headers[1] = "Step";
        Array.Copy(shelterIds, 0, headers, 2, shelterIds.Length);
        Utils.SaveResultCSV(
            headers,
            ActionLogs,
            (data) => new string[] { data.Item1.ToString(), data.Item2.ToString() }.Concat(data.Item3.ConvertAll(x => x ? "1" : "0")).ToArray(),
            $"{_env.recordID}/ActionLog_Episode_{_env.currentEpisodeId}.csv"
        );

        ActionLogs.Clear(); // 行動ログの初期化
    }

    /// <summary>
    /// ここでは環境内の状態をエージェントに観測させるための処理を記述します。
    /// モデルへの入力として使用されます。
    /// 今回は観測情報として以下の情報をモデルに入力します。
    /// 1. 避難所候補地の位置情報（x, y, z）
    /// 2. 避難所候補地の収容可能人数
    /// 3. 環境内の避難者数
    /// 4. 避難者の位置情報（x, y, z）
    /// CollectObservations内で観測できるデータの種類については下記docsを参照してください。
    /// https://docs.unity3d.com/Packages/com.unity.ml-agents@3.0/api/Unity.MLAgents.Agent.html#Unity_MLAgents_Agent_CollectObservations_Unity_MLAgents_Sensors_VectorSensor_
    /// </summary>
    /// <param name="sensor">この仮引数にあるAddObservationメソッドを通じて観測を行います</param>
    public override void CollectObservations(VectorSensor sensor) {

        // 避難所候補地のリストを巡回し、各避難所候補地の位置情報と収容可能人数を入力
        foreach(GameObject shelter in ShelterCandidates) {
            // 子オブジェクトがある場合はその位置、ない場合は親オブジェクトの位置を観測
            Transform targetTransform = shelter.transform.childCount > 0
                ? shelter.transform.GetChild(0)
                : shelter.transform;
            sensor.AddObservation(targetTransform.position);
            sensor.AddObservation(shelter.GetComponent<Shelter>().currentCapacity);
        }

        // NOTE : 観測のタイミングで避難者が避難してGameObjectが消えることがあるので、ここでコピーを作成
        List<GameObject> evacuees = new List<GameObject>(_env.Evacuees);
        sensor.AddObservation(evacuees.Count); // 環境内の避難者数

        // 避難者のリストを巡回し、各避難者の位置情報を追加
        foreach(GameObject evacuee in evacuees) {
            if(evacuee != null) {
                sensor.AddObservation(evacuee.transform.position);
            } else {
                // NOTE : 観測サイズは固定でなければならないため、万一取得に失敗した場合は0ベクトルを追加
                sensor.AddObservation(Vector3.zero);
            }
        }
        

    }

    /// <summary>
    /// エージェントの行動を実装するメソッドです。
    /// 今回は避難所候補地のリストの中から、それぞれの建物を避難所とするか否かを選択します。
    /// 選択する場合は1、選択しない場合は0を選択します。
    /// https://docs.unity3d.com/Packages/com.unity.ml-agents@3.0/api/Unity.MLAgents.Agent.html#Unity_MLAgents_Agent_OnActionReceived_Unity_MLAgents_Actuators_ActionBuffers_
    /// </summary>
    /// <param name="actions">モデルの行動出力を受け取るための仮引数で、この値を元に環境に行動を反映させます</param>
    public override void OnActionReceived(ActionBuffers actions) {
        if (AlwaysActivateAllShelters)
        {
            return;
        }
        var Selects = actions.DiscreteActions; //エージェントの選択。環境の候補地配列と同じ順序。[<建物１の避難所選択結果 0 or 1>, <建物２の避難所選択結果 0 or 1>, ...]

        if(Selects.Length != ShelterCandidates.Length) {
            Debug.LogError("Invalid action size : 避難所候補地のサイズとエージェントの選択サイズが不一致です");
            return;
        }
        var selectList = ApplyActionVector(Selects.ToList());

        // 行動ログを記録（episode, step, 各避難所候補の選択状況のリスト(true or false)）
        ActionLogs.Add(new Tuple<int, int, List<bool>>(_env.currentEpisodeId, _env.currentStep, selectList));
        

        OnDidActioned?.Invoke();
    }

    /// <summary>
    /// （比較実験用）
    ///  ニューラルネットワークではなく、ランダムに建物を選択するヒューリスティック関数
    ///  ML-Agentsの訓練モードを起動していない & 学習済みモデルがアタッチされていない場合はこの関数に従い行動します
    ///  https://docs.unity3d.com/Packages/com.unity.ml-agents@3.0/api/Unity.MLAgents.Agent.html#Unity_MLAgents_Agent_Heuristic_Unity_MLAgents_Actuators_ActionBuffers__
    /// </summary>
    public override void Heuristic(in ActionBuffers actionsOut) {
        var Selects = actionsOut.DiscreteActions;

        for(int i = 0; i < Selects.Length; i++) {
            if(Disabled) {
                Selects[i] = ConstBldgs.Contains(ShelterCandidates[i]) ? 1 : 0;
            } else {
                Selects[i] = UnityEngine.Random.Range(0, 2);
            }
        }
    }

    private void ApplyHeuristicActions()
    {
        _workingActionBuffer.Clear();
        foreach (var candidate in ShelterCandidates)
        {
            var shelter = candidate.GetComponent<Shelter>();
            var select = (shelter != null && shelter.currentCapacity > 0) ? 1 : 0;
            _workingActionBuffer.Add(select);
        }
        var selectList = ApplyActionVector(_workingActionBuffer);
        ActionLogs.Add(new Tuple<int, int, List<bool>>(_env.currentEpisodeId, _env.currentStep, selectList));
        OnDidActioned?.Invoke();
    }

    private List<bool> ApplyActionVector(IList<int> selects)
    {
        List<bool> selectList = new List<bool>();
        for(int i = 0; i < ShelterCandidates.Length; i++) {
            int select = i < selects.Count ? selects[i] : 0; // 0:非選択、1:選択
            GameObject Shelter = ShelterCandidates[i];
            if(select == 1) {
                if(!_env.CurrentShelters.Contains(Shelter)) {
                    _env.CurrentShelters.Add(Shelter);
                }
                Shelter.tag = "Shelter";
                var renderer = Shelter.GetComponent<MeshRenderer>();
                if(renderer != null) {
                    renderer.material = SelectedMaterial;
                }
                selectList.Add(true);
            } else if(select == 0) {
                _env.CurrentShelters.Remove(Shelter);
                Shelter.tag = "Untagged";
                var renderer = Shelter.GetComponent<MeshRenderer>();
                if(renderer != null) {
                    renderer.material = NonSelectMaterial;
                }
                selectList.Add(false);
            } else {
                Debug.LogError("Invalid action");
            }
        }

        return selectList;
    }

    private void ActivateAllShelters()
    {
        _env.CurrentShelters.Clear();
        foreach (var shelterObj in ShelterCandidates)
        {
            if (shelterObj == null) continue;
            if (!_env.CurrentShelters.Contains(shelterObj))
            {
                _env.CurrentShelters.Add(shelterObj);
            }
            shelterObj.tag = "Shelter";
            var renderer = shelterObj.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.material = SelectedMaterial;
            }
        }
    }


}
}
