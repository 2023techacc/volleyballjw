using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 추적된 손을 물리 세계에 얹는다.
    ///
    /// Kinematic Rigidbody 는 PhysX 에 속도를 전달하지 않으므로,
    /// 타격 임펄스는 여기서 직접 계산한다.
    /// 빠른 스윙에서의 관통은 (1) 연속 충돌 검사 (2) 이동 경로 SweepTestAll
    /// (3) 목표 위치 주변 보조 판정(OverlapSphere) 세 겹으로 막는다.
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

        [Tooltip("타격 결과의 y 성분이 이보다 작을 때 끌어올리는 목표 상승 속도 (m/s). " +
                 "예전처럼 매번 무조건 더하지 않고, 손 법선이 심하게 아래를 향하지 않을 " +
                 "때만 최소한의 아크를 보장한다.")]
        [SerializeField] private float _upwardBias = 3.2f;

        [Tooltip("공이 최소한 이 속도 이상으로는 튀어나가게 한다 (m/s).")]
        [SerializeField] private float _minBallSpeed = 5f;

        [SerializeField] private float _maxBallSpeed = 22f;
        [SerializeField] private float _hitCooldown = 0.12f;

        [Header("관통 방지")]
        [Tooltip("공이 속한 레이어. SweepTestAll/OverlapSphere 안전망이 이 레이어만 검사한다.")]
        [SerializeField] private LayerMask _ballLayer = ~0;

        [Tooltip("목표 위치 중심 보조 판정 반경 (m). SweepTestAll 이 바닥/네트 같은 다른 " +
                 "콜라이더에 가려 공을 놓치는 경우를 이걸로 보완한다.")]
        [SerializeField, Range(0f, 0.5f)] private float _hitAssistRadius = 0.30f;

        [Tooltip("타격 속도 계산에 쓰는 손 속도 상한 (m/s). 추적 튐이나 물리 이동 속도가 " +
                 "순간적으로 튀어도 이 이상으로는 타격에 반영하지 않는다.")]
        [SerializeField] private float _maxHandSpeed = 14f;

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

        // FixedUpdate 의 실제 이동량 기반 속도. HandTracker 가 주는 _trackedVelocity(추적
        // 필터 속도)와 섞어서 쓴다 — 어느 한쪽만 쓰면 추적 노이즈나 목표 도달 지연 중
        // 하나에 치우친 타격 속도가 나온다.
        private Vector3 _physicalVelocity;
        private Vector3 _lastHandVelocity;

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

            // 실제 이동량 기반 속도와 추적 속도를 섞는다. kinematic 손은 PhysX 에 속도를
            // 전달하지 않으므로, 여기서 직접 두 소스를 합쳐 타격 속도로 쓴다.
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            Vector3 tickVelocity = delta / dt;
            _physicalVelocity = Vector3.Lerp(_physicalVelocity,
                Vector3.ClampMagnitude(tickVelocity, _maxHandSpeed), 0.65f);
            Vector3 handVelocity = Vector3.ClampMagnitude(
                Vector3.Lerp(_trackedVelocity, _physicalVelocity, 0.6f), _maxHandSpeed);
            _lastHandVelocity = handVelocity;

            // --- 안전망 1: 이동 경로 전체 훑기 ---
            // SweepTest(단수)는 가장 가까운 콜라이더 '하나'만 돌려준다. 바닥이나 네트가
            // 공보다 먼저 걸리면 그 뒤에 있는 공을 그냥 지나친다. SweepTestAll 로 경로
            // 위의 모든 히트를 받아, 그중 공 레이어에 속한 것만 골라 처리한다.
            if (dist > 1e-4f)
            {
                RaycastHit[] hits = _rb.SweepTestAll(delta / dist, dist, QueryTriggerInteraction.Ignore);
                Rigidbody closestBall = null;
                Vector3 closestNormal = Vector3.zero;
                float closestDist = float.MaxValue;
                foreach (var hit in hits)
                {
                    if (hit.collider == null) continue;
                    if ((_ballLayer.value & (1 << hit.collider.gameObject.layer)) == 0) continue;
                    if (hit.distance < closestDist)
                    {
                        closestDist = hit.distance;
                        closestBall = hit.rigidbody;
                        closestNormal = hit.normal;
                    }
                }
                // hit.normal 은 '공 → 손' 방향이므로 뒤집어 '손 → 공' 으로 만든다
                if (closestBall != null) ApplyStrike(closestBall, -closestNormal, handVelocity);
            }

            // --- 안전망 2: 목표 위치 주변 보조 판정 ---
            // SweepTestAll 은 콜라이더 '모양'을 그대로 훑으므로, 손이 이미 공에 딱 붙어
            // 시작하거나 아주 짧은 거리만 움직이는 프레임에서는 히트가 안 잡힐 수 있다.
            // 목표 위치 중심 구 하나로 그런 경우를 보완한다.
            if (_hitAssistRadius > 0f)
            {
                Collider[] overlaps = Physics.OverlapSphere(
                    _targetPos, _hitAssistRadius, _ballLayer.value, QueryTriggerInteraction.Ignore);
                foreach (var col in overlaps)
                {
                    var ballRb = col.attachedRigidbody;
                    if (ballRb == null) continue;
                    Vector3 n = ballRb.position - _targetPos;
                    if (n.sqrMagnitude < 1e-8f) n = Vector3.up;
                    ApplyStrike(ballRb, n.normalized, handVelocity);
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
            ApplyStrike(collision.rigidbody, -collision.GetContact(0).normal, _lastHandVelocity);
        }

        /// <summary>
        /// 상대속도 기반 타격 계산. 기존의 "입사 전체 반사 + 매번 상향 속도 더하기"
        /// 대신, 손과 공의 상대 접근 속도로 반발을 구하고 위쪽 아크는 손 법선이 심하게
        /// 아래를 향하지 않을 때만 최소한으로 보장한다.
        /// </summary>
        private void ApplyStrike(Rigidbody ball, Vector3 hitDirection, Vector3 handVelocity)
        {
            if (ball == null || ball.isKinematic) return;
            if (Time.time - _lastHitTime < _hitCooldown) return;
            if (hitDirection.sqrMagnitude < 1e-6f) return;

            _lastHitTime = Time.time;

            Vector3 n = hitDirection.normalized;
            Vector3 incoming = BallPhysics.GetVelocity(ball);

            Vector3 relative = incoming - handVelocity;
            float closingSpeed = Mathf.Max(0f, -Vector3.Dot(relative, n));
            float incomingNormalMag = Vector3.Dot(incoming, n);
            Vector3 incomingTangent = incoming - n * incomingNormalMag;
            float handApproach = Mathf.Max(0f, Vector3.Dot(handVelocity, n));
            float outgoingNormal = closingSpeed * _bounceRetain
                                  + handApproach * _handPower
                                  + _normalBoost;
            Vector3 handTangent = handVelocity - n * Vector3.Dot(handVelocity, n);

            Vector3 result = incomingTangent * 0.72f + handTangent * _spinTransfer + n * outgoingNormal;

            // 배구는 띄워야 넘어간다 — 다만 손 법선이 심하게 아래를 향할 때(강 스매시 의도)는
            // 억지로 끌어올리지 않는다.
            if (n.y > -0.65f && result.y < _upwardBias)
                result.y = Mathf.Lerp(result.y, _upwardBias, 0.8f);

            if (result.magnitude < _minBallSpeed)
                result = result.normalized * _minBallSpeed;
            result = Vector3.ClampMagnitude(result, _maxBallSpeed);

            BallPhysics.SetVelocity(ball, result);

            Vector3 spinAxis = Vector3.Cross(n, handTangent - incomingTangent);
            ball.angularVelocity = Vector3.ClampMagnitude(spinAxis * 3f, 80f);

            // 겹침 해소 — 다음 틱에 같은 충돌이 다시 잡히는 것을 막는다
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
