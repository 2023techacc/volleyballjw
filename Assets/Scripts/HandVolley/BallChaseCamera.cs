using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 평소에는 플레이어 시점, 공을 치면 공을 따라간다.
    ///
    /// 중요: 손 좌표는 HandTracker 가 TrackingOrigin 기준으로 계산하므로
    /// 이 카메라를 아무리 움직여도 추적/타격 판정에는 영향이 없다.
    /// 카메라를 TrackingOrigin 의 자식으로 두지 않는 이유이기도 하다.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class BallChaseCamera : MonoBehaviour
    {
        [Header("참조")]
        [SerializeField] private Transform _trackingOrigin;
        [SerializeField] private Transform _ball;
        [SerializeField] private BallLauncher _launcher;

        [Header("플레이어 시점")]
        [Tooltip("TrackingOrigin 기준 카메라 위치. 너무 낮고 가까우면 서브가 날아오는 " +
                 "궤적이 시야 위쪽으로 잘려 공이 어디 있는지 알기 어렵다.")]
        [SerializeField] private Vector3 _playerViewOffset = new Vector3(0f, 0.35f, -1.0f);
        [SerializeField] private float _playerFov = 78f;
        [Tooltip("정면 대비 아래로 기울이는 각도 (도). 0 이면 완전 수평 — 서브가 " +
                 "위에서 내려올 때 시야 상단으로 벗어나기 쉽다. 살짝 내려다보게 하면 " +
                 "공과 코트 바닥이 함께 보인다.")]
        [SerializeField] private float _playerViewPitchDeg = 10f;

        [Header("추적 시점")]
        [Tooltip("공 기준 뒤/위 거리")]
        [SerializeField] private float _chaseBack = 7f;
        [SerializeField] private float _chaseUp = 3.2f;

        [Tooltip("공보다 조금 앞을 보게 해서 착지점이 미리 보이게 한다")]
        [SerializeField] private float _lookAhead = 6f;

        [SerializeField] private float _chaseFov = 62f;

        [Tooltip("공이 이만큼 멀어져야 추적을 시작한다 (m). 손 근처에서는 플레이어 시점 유지.")]
        [SerializeField] private float _chaseStartDistance = 3.5f;

        [Header("부드러움")]
        [SerializeField] private float _positionSmooth = 0.28f;
        [SerializeField] private float _rotationSmooth = 0.20f;
        [SerializeField] private float _fovSmooth = 0.35f;

        [Tooltip("카메라가 바닥 아래로 내려가지 않게 하는 최소 높이")]
        [SerializeField] private float _minHeight = 1.0f;

        [Header("옵션")]
        [SerializeField] private bool _enableChase = true;

        private Camera _cam;
        private Vector3 _posVelocity;
        private float _fovVelocity;
        private Vector3 _lastBallPos;
        private Vector3 _travelDir = Vector3.forward;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.fieldOfView = _playerFov;
            if (_ball != null) _lastBallPos = _ball.position;
            SnapToPlayerView();
        }

        private Quaternion PlayerViewRotation() =>
            _trackingOrigin.rotation * Quaternion.Euler(_playerViewPitchDeg, 0f, 0f);

        private void SnapToPlayerView()
        {
            if (_trackingOrigin == null) return;
            transform.SetPositionAndRotation(
                _trackingOrigin.TransformPoint(_playerViewOffset),
                PlayerViewRotation());
        }

        private void LateUpdate()
        {
            if (_trackingOrigin == null) return;

            // out 변수는 미리 선언한다.
            // `_enableChase && ShouldChase(out ...)` 형태로 쓰면 단락 평가 때문에
            // ShouldChase 가 호출되지 않을 수 있어 컴파일러가 미대입으로 판단한다 (CS0165).
            Vector3 chasePos = Vector3.zero;
            Quaternion chaseRot = Quaternion.identity;
            bool chasing = _enableChase && ShouldChase(out chasePos, out chaseRot);

            Vector3 targetPos;
            Quaternion targetRot;
            float targetFov;

            if (chasing)
            {
                targetPos = chasePos;
                targetRot = chaseRot;
                targetFov = _chaseFov;
            }
            else
            {
                targetPos = _trackingOrigin.TransformPoint(_playerViewOffset);
                targetRot = PlayerViewRotation();
                targetFov = _playerFov;
            }

            targetPos.y = Mathf.Max(targetPos.y, _minHeight);

            transform.position = Vector3.SmoothDamp(
                transform.position, targetPos, ref _posVelocity, _positionSmooth);

            transform.rotation = Quaternion.Slerp(
                transform.rotation, targetRot,
                1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(_rotationSmooth, 1e-3f)));

            _cam.fieldOfView = Mathf.SmoothDamp(
                _cam.fieldOfView, targetFov, ref _fovVelocity, _fovSmooth);
        }

        private bool ShouldChase(out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;

            if (_ball == null) return false;
            if (_launcher != null && !_launcher.BallInFlight) return false;

            Vector3 bp = _ball.position;

            // 공이 아직 손 근처면 플레이어 시점을 유지한다
            if (Vector3.Distance(bp, _trackingOrigin.position) < _chaseStartDistance)
            {
                _lastBallPos = bp;
                return false;
            }

            // 진행 방향을 부드럽게 추정 (공이 멈춰도 카메라가 홱 돌지 않게)
            Vector3 delta = bp - _lastBallPos;
            _lastBallPos = bp;
            if (delta.sqrMagnitude > 1e-6f)
            {
                Vector3 flat = new Vector3(delta.x, 0f, delta.z);
                if (flat.sqrMagnitude > 1e-6f)
                    _travelDir = Vector3.Slerp(_travelDir, flat.normalized,
                                               1f - Mathf.Exp(-Time.deltaTime * 4f));
            }

            pos = bp - _travelDir * _chaseBack + Vector3.up * _chaseUp;

            Vector3 lookTarget = bp + _travelDir * _lookAhead;
            rot = Quaternion.LookRotation((lookTarget - pos).normalized, Vector3.up);
            return true;
        }
    }
}
