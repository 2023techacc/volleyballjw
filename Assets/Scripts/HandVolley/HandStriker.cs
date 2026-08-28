using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 추적된 손을 물리 세계에 얹는다.
    ///
    /// Kinematic Rigidbody 는 PhysX 에 속도를 전달하지 않으므로,
    /// 타격 임펄스는 여기서 직접 계산한다.
    /// 빠른 스윙에서의 관통은 (1) 연속 충돌 검사 (2) 이동 경로 SphereCast 두 겹으로 막는다.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class HandStriker : MonoBehaviour
    {
        [Header("타격 튜닝")]
        [Tooltip("손 속도를 공에 얼마나 전달할지")]
        [SerializeField] private float _handPower = 1.6f;

        [Tooltip("공의 입사 속도 중 반사되어 남는 비율. 손에서 튕기는 탄성 그 자체 — " +
                 "높을수록 스치기만 해도 세게 튕겨나간다.")]
        [SerializeField, Range(0f, 1f)] private float _bounceRetain = 0.7f;

        [Tooltip("손이 멈춰 있어도 최소한 이만큼은 밀어낸다 (m/s)")]
        [SerializeField] private float _normalBoost = 1.2f;

        [Tooltip("접선 방향 속도 → 스핀/커브")]
        [SerializeField, Range(0f, 1f)] private float _spinTransfer = 0.25f;

        [Tooltip("타격에 더해지는 상향 속도 (m/s). 배구는 포물선으로 띄워야 넘어간다. " +
                 "0 이면 손 높이에서 수평으로 날아가 약 3m 앞에 떨어진다.")]
        [SerializeField] private float _upwardBias = 3.2f;

        [Tooltip("공이 최소한 이 속도 이상으로는 튀어나가게 한다 (m/s).")]
        [SerializeField] private float _minBallSpeed = 5f;

        [SerializeField] private float _maxBallSpeed = 22f;
        [SerializeField] private float _hitCooldown = 0.12f;

        [Header("관통 방지")]
        [Tooltip("공이 속한 레이어. SphereCast 안전망이 이 레이어만 검사한다.")]
        [SerializeField] private LayerMask _ballLayer = ~0;

        [Tooltip("겹침 즉시 해소 거리")]
        [SerializeField] private float _depenetration = 0.02f;

        [Header("표시")]
        [Tooltip("추적이 끊겼을 때 손을 아예 숨길지. 꺼두면 반투명으로만 표시해 " +
                 "'손이 사라졌다'는 혼란을 막는다.")]
        [SerializeField] private bool _hideWhenLost = false;

        [Header("이벤트")]
        [SerializeField] private AudioSource _hitSound;

        // ------------------------------------------------------------------ //

        private Rigidbody _rb;
        private Collider[] _colliders;
        private Renderer[] _renderers;

        private Vector3 _targetPos;
        private Quaternion _targetRot = Quaternion.identity;
        private Vector3 _trackedVelocity;
        private bool _hasTarget;
        private bool _active;
        private float _lastHitTime = -99f;

        public Vector3 HandVelocity => _trackedVelocity;
        public System.Action<Rigidbody, Vector3> OnBallStruck;

        // ------------------------------------------------------------------ //

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            // includeInactive: true 가 핵심.
            // Awake 는 SetActive(true) 도중에 실행되므로, 이 시점에 자식들이 아직
            // 활성으로 잡히지 않을 수 있다. 기본값(false)이면 배열이 비어 돌아오고
            // 그 뒤로는 렌더러를 영영 켜지 못한다. (손이 안 보이던 원인)
            RefreshChildren();

            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            // Kinematic 바디에서 쓸 수 있는 유일한 연속 검사 모드
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            _targetPos = _rb.position;
            _active = true;      // SetActive 의 '변화 없음' 조기 반환을 피하려고 반대값에서 시작
            SetActive(false);
        }

        /// <summary>HandTracker 가 매 프레임 호출.</summary>
        public void SetTarget(Vector3 worldPos, Quaternion worldRot, Vector3 velocity)
        {
            _targetPos = worldPos;
            _targetRot = worldRot;
            _trackedVelocity = velocity;
            _hasTarget = true;
        }

        private void RefreshChildren()
        {
            _colliders = GetComponentsInChildren<Collider>(true);
            _renderers = GetComponentsInChildren<Renderer>(true);
        }

        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;

            // 안전망: Awake 시점에 자식을 못 잡았다면 여기서 다시 훑는다
            if (_renderers == null || _renderers.Length == 0 ||
                _colliders == null || _colliders.Length == 0)
            {
                RefreshChildren();
            }

            // 콜라이더는 추적 상태에 따라 켜고 끈다 — 유령 손에 공이 맞으면 안 되니까
            foreach (var c in _colliders) if (c != null) c.enabled = active;

            // 렌더러는 기본적으로 항상 켜 둔다.
            // 추적이 잠깐 끊겼다고 손이 사라지면 사용자는 버그로 인식한다.
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.enabled = active || !_hideWhenLost;
            }

            if (!active) _trackedVelocity = Vector3.zero;
        }

        private void FixedUpdate()
        {
            if (!_hasTarget || !_active) return;

            Vector3 from = _rb.position;
            Vector3 delta = _targetPos - from;
            float dist = delta.magnitude;

            // --- 안전망 ---
            // 200Hz 라도 스파이크 스윙(15 m/s)이면 한 틱에 7.5cm 를 이동한다.
            // 이동 경로를 미리 훑어 놓친 공을 잡아낸다.
            if (dist > 1e-4f)
            {
                // SweepTest 는 이 리지드바디에 붙은 콜라이더 '모양 그대로' 훑는다.
                // 손을 납작한 손바닥 형태로 바꿔도 코드를 고칠 필요가 없다.
                if (_rb.SweepTest(delta / dist, out RaycastHit hit, dist,
                                  QueryTriggerInteraction.Ignore)
                    && (_ballLayer.value & (1 << hit.collider.gameObject.layer)) != 0)
                {
                    ApplyStrike(hit.rigidbody, -hit.normal);
                }
            }

            _rb.MovePosition(_targetPos);
            _rb.MoveRotation(_targetRot);
        }

        private void OnCollisionEnter(Collision collision) => HandleContact(collision);
        private void OnCollisionStay(Collision collision) => HandleContact(collision);

        private void HandleContact(Collision collision)
        {
            if (collision.contactCount == 0) return;
            // contact.normal 은 '공 → 손' 방향이므로 뒤집어 '손 → 공' 으로 만든다
            ApplyStrike(collision.rigidbody, -collision.GetContact(0).normal);
        }

        private void ApplyStrike(Rigidbody ball, Vector3 hitDirection)
        {
            if (ball == null || ball.isKinematic) return;
            if (Time.time - _lastHitTime < _hitCooldown) return;
            if (hitDirection.sqrMagnitude < 1e-6f) return;

            _lastHitTime = Time.time;

            Vector3 n = hitDirection.normalized;
            Vector3 incoming = BallPhysics.GetVelocity(ball);

            // 1) 입사 속도의 반사 성분
            Vector3 reflected = Vector3.Reflect(incoming, n) * _bounceRetain;

            // 2) 손이 밀어내는 성분. 법선 방향 성분만 전달해 '스치는 손'은 약하게 만든다.
            float approach = Mathf.Max(0f, Vector3.Dot(_trackedVelocity, n));
            Vector3 push = n * approach * _handPower;

            // 3) 접선 성분 → 커브
            Vector3 tangential = _trackedVelocity - n * Vector3.Dot(_trackedVelocity, n);
            Vector3 drift = tangential * _spinTransfer;

            Vector3 result = reflected + push + drift + n * _normalBoost;

            // 배구는 띄워야 넘어간다. 법선이 어느 쪽을 향하든 위로 뜨는 성분을 더한다.
            result += Vector3.up * _upwardBias;

            if (result.magnitude < _minBallSpeed)
                result = result.normalized * _minBallSpeed;
            result = Vector3.ClampMagnitude(result, _maxBallSpeed);

            BallPhysics.SetVelocity(ball, result);
            ball.angularVelocity = Vector3.Cross(n, tangential) * 3f;

            // 4) 겹침 해소 — 다음 틱에 같은 충돌이 다시 잡히는 것을 막는다
            ball.position += n * _depenetration;

            if (_hitSound != null) _hitSound.Play();
            OnBallStruck?.Invoke(ball, result);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
            foreach (var c in GetComponentsInChildren<Collider>())
            {
                var b = c.bounds;
                Gizmos.DrawWireCube(b.center, b.size);
            }
        }
    }

    /// <summary>Unity 6 에서 Rigidbody.velocity 가 linearVelocity 로 바뀐 것에 대한 호환 계층.</summary>
    public static class BallPhysics
    {
        public static Vector3 GetVelocity(Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetVelocity(Rigidbody rb, Vector3 v)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = v;
#else
            rb.velocity = v;
#endif
        }
    }
}
