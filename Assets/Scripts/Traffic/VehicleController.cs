using UnityEngine;

namespace EvacSim.Traffic
{
    /// <summary>
    /// 個別車両のGameObject制御。
    /// SUMOから受信した位置・速度・方向を適用し、ビジュアルを更新する。
    /// </summary>
    public class VehicleController : MonoBehaviour
    {
        /// <summary>この車両のSUMO ID</summary>
        public string VehicleId { get; set; }

        /// <summary>現在のSUMOエッジID</summary>
        public string CurrentEdgeId { get; private set; }

        /// <summary>現在の速度（m/s）</summary>
        public float CurrentSpeed { get; private set; }

        /// <summary>停車中かどうか</summary>
        public bool IsStopped { get; private set; }

        /// <summary>対応するEvacueeのID（車両避難者の場合）</summary>
        public string OwnerEvacueeId { get; set; }

        [Header("補間設定")]
        [SerializeField] private float positionLerpSpeed = 10f;
        [SerializeField] private float rotationLerpSpeed = 8f;

        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private bool _hasTarget;

        /// <summary>
        /// SUMOからの更新データを適用する
        /// </summary>
        public void ApplyUpdate(VehicleUpdate update)
        {
            _targetPosition = new Vector3(
                update.position.x,
                update.position.y,
                update.position.z
            );

            // SUMOの角度（0=北、時計回り）をUnityの回転に変換
            float unityAngle = 360f - update.angle;
            _targetRotation = Quaternion.Euler(0, unityAngle, 0);

            CurrentSpeed = update.speed;
            CurrentEdgeId = update.edge_id;
            IsStopped = update.is_stopped;
            _hasTarget = true;
        }

        private void Update()
        {
            if (!_hasTarget) return;

            // 位置と回転を滑らかに補間
            transform.position = Vector3.Lerp(
                transform.position,
                _targetPosition,
                Time.deltaTime * positionLerpSpeed
            );

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                _targetRotation,
                Time.deltaTime * rotationLerpSpeed
            );
        }

        /// <summary>車両を非アクティブにする（プールに返却前）</summary>
        public void Deactivate()
        {
            _hasTarget = false;
            VehicleId = null;
            OwnerEvacueeId = null;
            CurrentEdgeId = null;
            CurrentSpeed = 0;
            IsStopped = false;
            gameObject.SetActive(false);
        }
    }
}
