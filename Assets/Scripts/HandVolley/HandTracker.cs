using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 손 추적 파이프라인 전체.
    ///
    ///   랜드마크 → 픽셀 변환 → 렌즈 왜곡 보정 → 3D 위치 복원(LM)
    ///   → 유니티 좌표계 변환 → 게인/깊이 리매핑 → 1€ 필터 → HandStriker
    ///
    /// 카메라 내부 파라미터(fx, fy, cx, cy, 왜곡계수)를 직접 쓰기 때문에
    /// 유니티 카메라의 FOV 설정과 무관하게 동작한다. 게임 카메라를 어떻게 잡든
    /// 손 위치는 물리적으로 올바르게 나온다.
    /// </summary>
    public class HandTracker : MonoBehaviour
    {
        [Header("입력")]
        [Tooltip("IHandLandmarkSource 를 구현한 컴포넌트 (MediaPipeHandSource / MouseHandSource)")]
        [SerializeField] private MonoBehaviour _sourceBehaviour;

        [Tooltip("StreamingAssets 안의 캘리브레이션 파일 이름")]
        [SerializeField] private string _intrinsicsFileName = CameraIntrinsicsLoader.DefaultFileName;

        [Tooltip("파일이 없을 때 사용할 대각 시야각. 앱코 APC930 = 80°")]
        [SerializeField] private float _fallbackDiagonalFovDeg = 80f;

        [Header("출력")]
        [SerializeField] private HandSide _trackedSide = HandSide.Unknown;
        [SerializeField] private HandStriker _striker;
        [Tooltip("웹캠 시점의 원점. 비워두면 이 오브젝트의 Transform 을 쓴다.")]
        [SerializeField] private Transform _trackingOrigin;

        [Header("손 크기 (실측 반영)")]
        [Tooltip("켜면 _striker 오브젝트의 HandSizeController 크기를 매 관측마다 실제 " +
                 "손목→중지 끝 길이로 자동 갱신한다. 플레이어가 슬라이더로 맞추는 대신 " +
                 "실제 손 크기를 그대로 반영한다.")]
        [SerializeField] private bool _useDetectedHandSize = true;
        [Tooltip("Scale = 1.0 에 대응하는 기준 손목→중지 끝 길이 (m). 성인 손은 대략 " +
                 "0.17~0.20m.")]
        [SerializeField] private float _referenceHandLength = 0.18f;

        [Header("공간 매핑")]
        [Tooltip("화면 중앙 기준 확대 배율. 좁은 화각을 보완해 손이 프레임 밖으로 나가는 것을 막는다. " +
                 "실제 손 움직임 대비 게임 속 손 이동량을 키우고 싶으면 이 값을 올린다.")]
        [SerializeField, Range(1f, 3f)] private float _lateralGain = 2.2f;

        [Tooltip("추정 깊이를 게임 공간 깊이로 리매핑. z_game = (z_cam - offset) * scale + offset")]
        [SerializeField] private float _depthPivot = 0.8f;
        [Tooltip("깊이(앞뒤) 이동 반영 비율. 실제로 앞뒤로 움직인 만큼 게임 속 손도 더 크게 " +
                 "움직이게 하려면 이 값을 올린다.")]
        [SerializeField, Range(0f, 3f)] private float _depthScale = 1.6f;
        [SerializeField] private Vector2 _depthClamp = new Vector2(0.35f, 1.8f);
        [Tooltip("깊이 이동 방향을 반전. 실제로 손을 앞(카메라 쪽)으로 뻗었는데 게임 속 손이 " +
                 "뒤로 물러나면 켠다.")]
        [SerializeField] private bool _invertDepth = true;

        [Header("필터 (1€)")]
        [SerializeField] private float _minCutoff = 2.0f;
        [SerializeField] private float _beta = 0.06f;
        [Tooltip("Z 는 노이즈가 훨씬 크므로 따로, 더 강하게 건다.")]
        [SerializeField] private float _zMinCutoff = 0.3f;
        [SerializeField] private float _zBeta = 0.008f;
        [SerializeField, Range(0f, 1f)] private float _rotationSmoothing = 0.35f;

        [Header("지연 보상 / 프레임 보간")]
        [Tooltip("웹캠 30fps + 추론 지연을 속도 외삽으로 일부 상쇄. 과하면 오버슈트가 보인다.")]
        [SerializeField, Range(0f, 0.08f)] private float _latencyCompensation = 0.012f;

        [Tooltip("외삽 총량 상한(초). 관측이 끊겼을 때 손이 날아가는 것을 막는다.")]
        [SerializeField, Range(0.01f, 0.15f)] private float _maxLeadTime = 0.05f;

        [Tooltip("예측에 쓰는 속도 상한 (m/s). 노이즈성 속도 급등을 잘라낸다.")]
        [SerializeField] private float _maxPredictionSpeed = 12f;

        [Tooltip("이 거리 미만의 변화는 무시한다 (m). 정지 시 미세 떨림 제거.")]
        [SerializeField] private float _lateralDeadband = 0.0008f;
        [SerializeField] private float _depthDeadband = 0.002f;

        [Tooltip("데드밴드를 적용할 최대 속도 (m/s). 이보다 빠르면 데드밴드를 끈다 — " +
                 "이동 중에는 데드밴드가 계단식 떨림을 만들기 때문.")]
        [SerializeField] private float _deadbandMaxSpeed = 0.05f;

        [Tooltip("렌더 프레임 스무딩 시간 (초). 30Hz 관측이 갱신될 때 생기는 " +
                 "앵커 점프를 없앤다. 0 이면 끄기.")]
        [SerializeField, Range(0f, 0.12f)] private float _renderSmoothTime = 0.03f;

        [Header("손 회전 보정")]
        [Tooltip("손등 법선(앞/뒤) 방향을 반전. 실기기에서 손을 앞뒤로 기울일 때 " +
                 "게임 손이 반대로 기울면 켠다.")]
        [SerializeField] private bool _invertPalmNormal = false;
        [Tooltip("손 위(손목→중지) 방향을 반전. 손을 위/아래로 기울일 때 " +
                 "게임 손이 반대로 기울면 켠다.")]
        [SerializeField] private bool _invertPalmUp = true;
        [Tooltip("Invert Palm Normal/Up 조합으로도 안 잡히는 앞뒤 기울임(피치)만 " +
                 "따로 반전한다. 최종 회전의 오일러 X 성분만 뒤집으므로 다른 축과 " +
                 "얽히지 않는다.")]
        [SerializeField] private bool _invertPitch = true;

        [Header("추적 유실 처리")]
        [Tooltip("짧은 유실을 관성으로 메우는 유예 시간 (초). 프레임 수 기준이면 " +
                 "렌더 fps 에 따라 실제 유예 시간이 최대 5배까지 달라진다.")]
        [SerializeField] private float _graceSeconds = 0.15f;
        [SerializeField, Range(0f, 1f)] private float _lostVelocityDecay = 0.85f;

        [Header("디버그")]
        [SerializeField] private bool _drawGizmos = true;
        [SerializeField] private bool _logDiagnostics = false;

        // ------------------------------------------------------------------ //

        private IHandLandmarkSource _source;
        private CameraIntrinsics _intrinsics;
        private CameraIntrinsics _scaled;

        private OneEuroFilterVector3 _posFilter;
        private readonly Vector2[] _pixels = new Vector2[HandLandmark.Count];

        private Vector3? _warmStart;
        private Vector3 _filteredPos;
        private Quaternion _filteredRot = Quaternion.identity;
        private Vector3 _velocity;
        private float _lostTime;
        private double _lastTimestamp = -1;
        private float _lastReprojError;
        private HandSide _lastObservedSide = HandSide.Unknown;

        // 관측 시각 기준 앵커. 렌더 프레임과 관측 프레임이 다른 속도로 도는 것을 분리한다.
        private Vector3 _anchorPos;
        private float _anchorTime;
        private bool _hasAnchor;
        private int _observationCount;
        private Vector3 _rawTarget;
        private Vector3 _smoothVelocity;
        private bool _warnedNoSource;
        private HandSizeController _handSizeController;
        private bool _handSizeControllerResolved;
        private float _smoothedHandLength = -1f;

        public bool IsTracking { get; private set; }
        public Vector3 WorldPosition => _filteredPos;
        public Quaternion WorldRotation => _filteredRot;
        public Vector3 WorldVelocity => _velocity;
        public float ReprojectionError => _lastReprojError;
        public CameraIntrinsics Intrinsics => _scaled;

        private Transform Origin => _trackingOrigin != null ? _trackingOrigin : transform;

        // ------------------------------------------------------------------ //

        private void Awake()
        {
            _posFilter = new OneEuroFilterVector3(_minCutoff, _beta, _zMinCutoff, _zBeta);
        }

        /// <summary>
        /// 소스는 Awake 가 아니라 매 프레임 지연 해석한다.
        /// AddComponent 는 그 자리에서 Awake 를 실행하므로, 코드로 컴포넌트를 붙인 뒤
        /// 필드를 주입하는 순서라면 Awake 시점에는 아직 참조가 비어 있다.
        /// (HandVolleyBootstrap 이 정확히 그 패턴이었고, 손이 멈춰 있던 원인이다.)
        /// </summary>
        private bool EnsureSource()
        {
            if (_source != null) return true;

            _source = _sourceBehaviour as IHandLandmarkSource
                      ?? GetComponent<IHandLandmarkSource>() as IHandLandmarkSource;

            if (_source == null)
            {
                // 씬 어디에 있든 찾아낸다 — 배선 실수에 대한 최종 안전망
                foreach (var mb in FindObjectsOfType<MonoBehaviour>(true))
                {
                    if (mb is IHandLandmarkSource found)
                    {
                        _source = found;
                        _sourceBehaviour = mb;
                        Debug.Log($"[HandTracker] 소스를 자동으로 연결했습니다: {mb.GetType().Name}");
                        break;
                    }
                }
            }

            if (_source == null && !_warnedNoSource)
            {
                _warnedNoSource = true;
                Debug.LogError("[HandTracker] IHandLandmarkSource 를 찾을 수 없습니다. " +
                               "MouseHandSource 또는 MediaPipeHandSource 를 씬에 두고 연결하세요.");
            }
            return _source != null;
        }

        private void Start()
        {
            StartCoroutine(CameraIntrinsicsLoader.Load(_intrinsicsFileName, intr =>
            {
                _intrinsics = intr ?? CameraIntrinsics.FromDiagonalFov(640, 480, _fallbackDiagonalFovDeg);
                if (!_intrinsics.isCalibrated)
                {
                    Debug.LogWarning("[HandTracker] 미보정 추정값으로 동작 중입니다. " +
                                     "깊이가 최대 25%까지 틀릴 수 있습니다. " +
                                     "tools/calibrate_camera.py 를 실행하세요.");
                }
            }));
        }

        private void Update()
        {
            if (!EnsureSource()) return;
            if (_intrinsics == null || !_source.IsReady) return;

            // 런타임 해상도가 캘리브레이션 해상도와 다르면 내부 파라미터를 스케일
            if (_scaled == null ||
                _scaled.width != _source.ImageWidth || _scaled.height != _source.ImageHeight)
            {
                _scaled = _intrinsics.ScaledTo(_source.ImageWidth, _source.ImageHeight);
                if (_logDiagnostics) Debug.Log($"[HandTracker] 내부 파라미터: {_scaled}");
            }

            HandObservation obs = _source.GetLatest(_trackedSide);
            float dtRender = Mathf.Max(Time.deltaTime, 1e-4f);
            if (obs.valid) _lastObservedSide = obs.side;

            if (!obs.valid || !obs.HasNormalized || !obs.HasWorldLandmarks)
            {
                HandleLost(dtRender);
                return;
            }

            // ---------------------------------------------------------------
            // 관측 프레임과 렌더 프레임의 분리.
            //
            // 웹캠은 30Hz, 렌더는 60~144Hz 다. 같은 관측을 매 렌더 프레임 다시
            // 필터에 넣으면 필터가 "렌더 dt 동안 이만큼 움직였다"고 오해해
            // 속도를 몇 배로 부풀린다. 그 속도에 지연 보상을 곱하면 손이 앞으로
            // 튀었다가 되돌아오는 진동이 생긴다 (깊이에서 특히 심하다).
            //
            // 따라서 무거운 연산과 필터링은 '새 관측'에서만 하고,
            // 나머지 렌더 프레임은 속도 외삽으로 부드럽게 메운다.
            // ---------------------------------------------------------------
            bool isNewObservation = obs.timestampSeconds > _lastTimestamp;

            if (isNewObservation)
            {
                float dtObs = _lastTimestamp < 0
                    ? dtRender
                    : Mathf.Clamp((float)(obs.timestampSeconds - _lastTimestamp), 1f / 240f, 0.1f);
                _lastTimestamp = obs.timestampSeconds;

                if (!TrySolve(obs, out Vector3 camSpace, out Quaternion camRot))
                {
                    HandleLost(dtRender);
                    return;
                }

                if (_useDetectedHandSize) ApplyDetectedHandSize(obs.world);

                Vector3 filtered = _posFilter.Filter(ToWorld(camSpace), dtObs);

                // 필터 내부 속도 추정값. 이제 관측 간격 기준이라 물리적으로 옳다.
                _velocity = Vector3.ClampMagnitude(_posFilter.Velocity, _maxPredictionSpeed);

                _anchorPos = filtered;
                _anchorTime = Time.time;
                _hasAnchor = true;
                _observationCount++;

                Quaternion targetRot = ToWorldRotation(camRot);
                if (_invertPitch)
                {
                    Vector3 e = targetRot.eulerAngles;
                    e.x = -e.x;
                    targetRot = Quaternion.Euler(e);
                }
                _filteredRot = Quaternion.Slerp(_filteredRot, targetRot,
                                                1f - Mathf.Pow(_rotationSmoothing, dtObs * 60f));

                _lostTime = 0f;
                IsTracking = true;
            }

            if (!_hasAnchor) return;

            // 앵커 이후 경과 시간만큼 외삽해 렌더 프레임을 부드럽게 채운다
            float lead = Mathf.Min(Time.time - _anchorTime + _latencyCompensation, _maxLeadTime);
            Vector3 predicted = _anchorPos + _velocity * lead;

            // 데드밴드는 '정지에 가까울 때만' 적용한다.
            // 이동 중에 걸면 위치가 계단식으로 튀어 오히려 진행 방향으로 덜덜 떨린다.
            float speed = _velocity.magnitude;
            if (speed < _deadbandMaxSpeed)
            {
                Vector3 diff = predicted - _rawTarget;
                if (Mathf.Abs(diff.x) <= _lateralDeadband) predicted.x = _rawTarget.x;
                if (Mathf.Abs(diff.y) <= _lateralDeadband) predicted.y = _rawTarget.y;
                if (Mathf.Abs(diff.z) <= _depthDeadband) predicted.z = _rawTarget.z;
            }
            _rawTarget = predicted;

            // 렌더 스무딩: 30Hz 관측이 갱신될 때마다 앵커가 툭 점프하는데,
            // 그대로 내보내면 이동 방향으로 33ms 주기의 떨림이 보인다.
            // 임계 감쇠 스프링으로 그 계단을 없앤다.
            // (시뮬레이션: 저크 RMS 15.5 → 1.1, 추가 지연 약 4.6mm)
            _filteredPos = _renderSmoothTime > 0.0001f
                ? Vector3.SmoothDamp(_filteredPos, _rawTarget, ref _smoothVelocity,
                                     _renderSmoothTime, Mathf.Infinity, dtRender)
                : _rawTarget;

            if (_striker != null)
            {
                _striker.SetTarget(_filteredPos, _filteredRot, _velocity);
                _striker.SetActive(true);
            }
        }

        // ------------------------------------------------------------------ //

        private bool TrySolve(in HandObservation obs, out Vector3 camSpace, out Quaternion camRot)
        {
            camSpace = Vector3.zero;
            camRot = Quaternion.identity;

            int w = _scaled.width, h = _scaled.height;

            // 정규화 좌표 → 픽셀 → 왜곡 보정
            // (정규화 x 는 폭, y 는 높이 기준이므로 각각 곱해야 한다 — 종횡비 함정)
            for (int i = 0; i < HandLandmark.Count; i++)
            {
                Vector2 px = new Vector2(obs.normalized[i].x * w, obs.normalized[i].y * h);
                _pixels[i] = _scaled.Undistort(px);
            }

            var result = HandPoseSolver.Solve(obs.world, _pixels, _scaled, _warmStart);
            if (!result.success) { _warmStart = null; return false; }

            _lastReprojError = result.meanReprojError;

            // 재투영 오차가 비정상적으로 크면 잘못 수렴한 것으로 보고 버린다
            if (result.meanReprojError > 25f)
            {
                _warmStart = null;
                if (_logDiagnostics)
                    Debug.LogWarning($"[HandTracker] 수렴 실패 (재투영 {result.meanReprojError:F1}px)");
                return false;
            }

            _warmStart = result.translation;
            camSpace = result.translation;

            // 법선 반전 방지: 인스펙터에서 추적 대상 손을 지정했으면 그 값을,
            // 아니면 이번 관측의 handedness 를, 그것도 모르면 Right 를 기본으로 쓴다.
            HandSide sideForPose = _trackedSide != HandSide.Unknown ? _trackedSide
                                  : obs.side != HandSide.Unknown ? obs.side
                                  : HandSide.Right;
            HandPoseSolver.TryGetPalmRotation(obs.world, sideForPose, _invertPalmNormal, _invertPalmUp, out camRot);
            return true;
        }

        /// <summary>
        /// 손 크기 조절 슬라이더 대신, 관측된 손목→중지 끝 길이로 HandSizeController 를
        /// 갱신한다. obs.world 는 MediaPipe 가 이미 미터 단위로 복원한 실제 손 모양이라
        /// (HandPoseSolver 의 깊이 추정과는 별개 파이프라인), 설계상 카메라 거리와
        /// 무관해야 한다. 다만 단안 카메라 ML 추정이라 거리에 따른 잔여 흔들림이 있을
        /// 수 있어, 매 프레임 값을 그대로 쓰지 않고 천천히(수 초에 걸쳐) 평균 내서
        /// 순간적인 잡음이나 약한 거리 의존성이 크기에 바로 반영되지 않게 한다.
        /// </summary>
        private void ApplyDetectedHandSize(Vector3[] world)
        {
            if (!_handSizeControllerResolved)
            {
                _handSizeController = _striker != null ? _striker.GetComponent<HandSizeController>() : null;
                _handSizeControllerResolved = true;
            }
            if (_handSizeController == null) return;
            if (world == null || world.Length < HandLandmark.Count) return;

            float length = Vector3.Distance(world[HandLandmark.Wrist], world[HandLandmark.MiddleTip]);
            if (length < 0.01f) return;

            // 관측 주기(30Hz 안팎) 기준 천천히 수렴하는 평균. 값이 클수록 더 빨리 따라간다.
            const float smoothing = 0.03f;
            _smoothedHandLength = _smoothedHandLength < 0f
                ? length
                : Mathf.Lerp(_smoothedHandLength, length, smoothing);

            _handSizeController.ApplyDetectedScale(_smoothedHandLength / _referenceHandLength);
        }

        /// <summary>OpenCV 카메라 공간 → 유니티 월드 공간.</summary>
        private Vector3 ToWorld(Vector3 camSpace)
        {
            float z = Mathf.Clamp(camSpace.z, _depthClamp.x, _depthClamp.y);
            float depthDelta = (z - _depthPivot) * _depthScale;
            if (_invertDepth) depthDelta = -depthDelta;
            float zGame = depthDelta + _depthPivot;

            // 화면 중앙 기준 확대 (광축이 원점이므로 단순 배율이면 된다)
            float x = camSpace.x * _lateralGain;
            float y = camSpace.y * _lateralGain;

            // OpenCV 는 y 아래쪽, 유니티는 y 위쪽
            if (_source.Mirrored) x = -x;
            Vector3 local = new Vector3(x, -y, zGame);

            return Origin.TransformPoint(local);
        }

        private Quaternion ToWorldRotation(Quaternion camRot)
        {
            // y 축 반전에 대응하는 사원수 변환
            Quaternion flipped = new Quaternion(-camRot.x, camRot.y, -camRot.z, camRot.w);
            if (_source.Mirrored)
                flipped = new Quaternion(flipped.x, -flipped.y, -flipped.z, flipped.w);
            return Origin.rotation * flipped;
        }

        private void HandleLost(float dt)
        {
            _lostTime += dt;

            if (_lostTime <= _graceSeconds)
            {
                // 짧은 유실은 관성으로 메운다 — 손이 사라졌다 나타나는 깜빡임 방지
                _velocity *= _lostVelocityDecay;
                _filteredPos += _velocity * dt;
                if (_striker != null) _striker.SetTarget(_filteredPos, _filteredRot, _velocity);
                return;
            }

            if (IsTracking)
            {
                IsTracking = false;
                _posFilter.Reset();
                _warmStart = null;
                _velocity = Vector3.zero;
                _smoothVelocity = Vector3.zero;
                _hasAnchor = false;
                _lastTimestamp = -1;
                if (_striker != null) _striker.SetActive(false);
            }
        }

        // ------------------------------------------------------------------ //

        private void OnDrawGizmos()
        {
            if (!_drawGizmos || !Application.isPlaying || !IsTracking) return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(_filteredPos, 0.09f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(_filteredPos, _velocity * 0.1f);
            Gizmos.color = Color.magenta;
            Gizmos.DrawRay(_filteredPos, _filteredRot * Vector3.forward * 0.15f);
        }

        private void OnGUI()
        {
            if (!_logDiagnostics) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            string s =
                _source == null ? "소스 없음 — MouseHandSource / MediaPipeHandSource 확인" :
                !_source.IsReady ? "소스 준비 중..." :
                _scaled == null ? "내부 파라미터 로딩 중..." :
                $"{_scaled}\n" +
                $"추적: {(IsTracking ? "ON" : "OFF")}   재투영 오차: {_lastReprojError:F2} px\n" +
                $"위치: {_filteredPos}\n" +
                $"속도: {_velocity.magnitude:F2} m/s   관측 {_observationCount}회\n" +
                $"감지된 손: {_lastObservedSide}   (Tracked Side 설정: {_trackedSide})";
            GUI.Label(new Rect(12, 12, 700, 140), s, style);
        }
    }
}
