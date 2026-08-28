using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 공기 저항(속도 제곱에 비례하는 항력)과 마그누스 효과(스핀이 궤적을 휘게 하는 힘)를
    /// 더한다. 기존 타격 물리는 이 두 힘이 없어서 "매 타격마다 과도하게 솟거나
    /// 레이저처럼 일직선으로 날아가는" 느낌이 났다 — 실제 배구공은 날아가는 동안
    /// 저항으로 감속하고, 회전이 걸리면 궤적이 휜다.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class VolleyballAerodynamics : MonoBehaviour
    {
        [Tooltip("속도 제곱에 비례하는 항력 계수. 클수록 날아가면서 더 빨리 감속한다.")]
        [SerializeField] private float _quadraticDrag = 0.012f;

        [Tooltip("마그누스 효과 계수. 스핀(angularVelocity)과 속도의 외적에 곱해 " +
                 "궤적을 휘게 한다.")]
        [SerializeField] private float _magnusCoefficient = 0.004f;

        [Tooltip("마그누스 가속도 상한 (m/s²). 스핀이 과할 때 궤적이 튕기듯 급격히 " +
                 "꺾이는 것을 막는다.")]
        [SerializeField] private float _maxMagnusAcceleration = 4f;

        private Rigidbody _rb;

        private void Awake() => _rb = GetComponent<Rigidbody>();

        private void FixedUpdate()
        {
            if (_rb == null || _rb.isKinematic) return;

            Vector3 velocity = BallPhysics.GetVelocity(_rb);
            float speed = velocity.magnitude;
            if (speed < 0.05f) return;

            Vector3 dragAcceleration = -velocity * speed * _quadraticDrag;

            Vector3 magnusAcceleration = Vector3.ClampMagnitude(
                Vector3.Cross(_rb.angularVelocity, velocity) * _magnusCoefficient,
                _maxMagnusAcceleration);

            _rb.AddForce(dragAcceleration + magnusAcceleration, ForceMode.Acceleration);
        }
    }
}
