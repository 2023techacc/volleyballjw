// ---------------------------------------------------------------------------
// MediaPipe Unity Plugin (homuler) 브리지 어댑터 — 수신 전용
//
// 이 스크립트는 웹캠도, HandLandmarker 도 직접 만들지 않는다.
// 이미 정상 동작하는 MediaPipe 샘플(Hand Landmark Detection)이 있다는 전제로,
// 그 샘플이 만들어낸 HandLandmarkerResult 를 "받아서" HandObservation 으로
// 변환하는 역할만 한다.
//
// 이렇게 나눈 이유:
//   - 웹캠을 두 시스템이 동시에 열려는 충돌을 원천 차단
//   - HandLandmarker.CreateFromOptions 시그니처, 모델 로드 경로 같은
//     플러그인 버전별 API 차이를 이 어댑터가 신경 쓸 필요가 없어짐
//     (그 부분은 이미 검증된 샘플 코드가 담당)
//
// 연결 방법 (샘플의 실행 스크립트, 보통 HandLandmarkerRunner.cs 를 아주 조금 확장):
//
//   1) 결과를 처리하는 콜백을 찾는다. 보통 이런 모양이다.
//
//        private void OnHandLandmarkDetectionOutput(
//            HandLandmarkerResult result, Image image, long timestamp)
//        {
//            _handLandmarkerResultAnnotationController.DrawLater(result);
//            // ↓ 이 한 줄만 추가
//            HandVolley.MediaPipeHandSource.Instance?.OnLandmarkerResult(result, timestamp);
//        }
//
//   2) 웹캠 해상도/미러 여부도 샘플이 갖고 있으므로, 샘플이 재생을 시작한 직후
//      (보통 ImageSource.Play() 가 끝난 콜백에서) 한 번만 아래를 호출한다.
//
//        HandVolley.MediaPipeHandSource.Instance?.Configure(
//            imageSource.textureWidth, imageSource.textureHeight, imageSource.isFrontFacing);
//
//      정확한 프로퍼티 이름은 설치된 플러그인 버전에 따라 다를 수 있다.
//
// 이 파일 자체는 그 실행 스크립트가 무엇이든 손댈 필요가 없다 — 위 1~2번만
// 샘플 쪽에 추가하면 된다. (Instance 정적 참조를 쓰므로 인스펙터 드래그 연결도 필요 없다 —
// HandVolleyBootstrap 이 Play 시작 시점에 이 오브젝트를 코드로 만들기 때문에 에디터에
// 미리 배치된 오브젝트가 아니라서 드래그해 둘 대상이 없다.)
// ---------------------------------------------------------------------------

using System;
using UnityEngine;
using HandLandmarkerRunner = Mediapipe.Unity.Sample.HandLandmarkDetection.HandLandmarkerRunner;

#if HANDVOLLEY_MEDIAPIPE
using Mediapipe.Tasks.Vision.HandLandmarker;
#endif

namespace HandVolley
{
    public class MediaPipeHandSource : MonoBehaviour, IHandLandmarkSource
    {
        [Header("설정이 도착하기 전 기본값")]
        [Tooltip("Configure() 가 아직 호출되지 않았을 때 쓰는 대체 해상도.")]
        [SerializeField] private int _fallbackWidth = 640;
        [SerializeField] private int _fallbackHeight = 480;
        [SerializeField] private bool _fallbackMirrored = true;

        [Tooltip("배구는 손이 빠르다. 소스 쪽 minTrackingConfidence 를 " +
                 "0.3~0.4 로 낮추면 유실이 줄어든다 — 이 값은 참고용 표시일 뿐, " +
                 "실제 설정은 샘플의 HandLandmarkerOptions 에서 한다.")]
        [SerializeField, Range(0.1f, 0.9f)] private float _recommendedTrackingConfidence = 0.35f;

        [Header("안정화")]
        [Tooltip("빈 검출 결과가 와도 이 프레임 수까지는 마지막으로 유효했던 손을 그대로 " +
                 "유지한다. 빠른 스윙 중 모션 블러나, 손을 카메라에 거의 수직으로 세워 " +
                 "얇게 보이는 순간처럼 MediaPipe 자체가 검출을 놓치는 구간에서 손이 " +
                 "바로 사라지지 않게 한다. HandTracker 의 Grace Seconds 와 별개로 더해진다.")]
        [SerializeField, Range(0, 8)] private int _missFramesToHold = 5;

        [Tooltip("일반 USB 웹캠 원본 입력은 MediaPipe handedness 판정이 셀피(전면 카메라) " +
                 "기준과 반대로 나오는 경우가 많다 (실측으로 확인됨). 손등/손가락이 반대로 " +
                 "붙어 보이면 이 값을 토글한다.")]
        [SerializeField] private bool _invertReportedHandedness = true;

        [Header("진단")]
        [Tooltip("Configure() 나 결과 수신이 일정 시간 없으면 경고를 낸다.")]
        [SerializeField] private float _staleWarningSeconds = 3f;

        private volatile bool _configured;
        private int _width, _height;
        private volatile bool _mirrored;

        // Time.time 은 메인 스레드에서만 접근 가능한 API 다.
        // OnLandmarkerResult 는 MediaPipe 워커 스레드에서 호출될 수 있으므로,
        // 그 안에서 Time.time 을 직접 읽으면 예외가 나거나 값이 깨질 수 있다.
        // 대신 워커 스레드는 정수 카운터만 원자적으로 올리고,
        // '몇 초 전에 마지막 결과가 왔는지' 판단은 메인 스레드(Update)에서만 한다.
        private long _resultCounter;
        private long _lastSeenCounter;
        private float _lastResultTime = -1f;   // 메인 스레드에서만 쓰고 읽는다
        private bool _warnedStale;
        private bool _warnedNeverConfigured;
        private bool _warnedNoSymbol;

        public int ImageWidth => _configured ? _width : _fallbackWidth;
        public int ImageHeight => _configured ? _height : _fallbackHeight;
        public bool Mirrored => _configured ? _mirrored : _fallbackMirrored;

        public static MediaPipeHandSource Instance { get; private set; }

        /// <summary>
        /// HandVolleyBootstrap 의 Show Debug Text 설정을 그대로 반영한다. 샘플의
        /// HandLandmarkerRunner 가 이 값을 읽어 랜드마크 오버레이(웹캠 화면 위 손 골격
        /// 표시)를 켜고 끈다 — 화면 녹화할 때는 꺼서 깨끗하게, 디버그할 때는 켜서
        /// 실제로 뭐가 인식되는지 눈으로 확인할 수 있게 한다.
        /// </summary>
        public bool ShowDebugLandmarks { get; set; }

        /// <summary>
        /// HandVolley 연결: 게임 설정 화면의 카메라 번호 조절 UI 가 쓰는 얇은 창구.
        /// 실제 웹캠 열기/닫기는 전부 HandLandmarkerRunner 가 담당하고, 여긴 그쪽으로
        /// 그대로 넘겨주기만 한다. HandLandmarkerRunner.Instance 가 없으면(마우스 모드,
        /// 또는 아직 Bootstrap 대기 중) 안전하게 아무 것도 하지 않는다.
        /// </summary>
        public string[] AvailableCameraNames => HandLandmarkerRunner.Instance?.AvailableCameraNames;
        public int CameraIndex => HandLandmarkerRunner.Instance?.WebcamIndex ?? -1;
        public void SetCameraIndex(int index) => HandLandmarkerRunner.Instance?.SwitchWebcam(index);

        private void Awake() => Instance = this;
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // 첫 결과가 도착했는지는 워커 스레드에서 안전하게 증가시킨 카운터로 판단한다.
        public bool IsReady => System.Threading.Interlocked.Read(ref _resultCounter) > 0;

        // 콜백 스레드와 메인 스레드가 동시에 만지므로 락으로 보호한다.
        private readonly object _lock = new object();
        private readonly HandBuffer[] _buffers = { new HandBuffer(), new HandBuffer() };

        // GetLatest 가 반환하는 배열. HandBuffer 를 참조로 그대로 넘기면 lock 이
        // 풀린 뒤 메인 스레드가 그 배열을 읽는 동안 워커 스레드가 같은 배열을
        // 덮어써 랜드마크 집합이 절반은 이전 프레임, 절반은 다음 프레임으로 찢어질 수
        // 있다 (실기기에서만 나타나는 깊이 튐의 원인). lock 안에서 이 버퍼로 복사해
        // 호출자가 항상 한 시점의 스냅샷만 보게 한다.
        private readonly Vector3[] _outNormalized = new Vector3[HandLandmark.Count];
        private readonly Vector3[] _outWorld = new Vector3[HandLandmark.Count];

        // GetLatest 자체의 프레임 간 연속성 판단용 (아래 참고).
        private Vector2 _lastChosenWrist;
        private bool _hasLastChosenWrist;

        private class HandBuffer
        {
            public bool valid;
            public HandSide side;
            public float confidence;
            public double timestamp;
            public readonly Vector3[] normalized = new Vector3[HandLandmark.Count];
            public readonly Vector3[] world = new Vector3[HandLandmark.Count];

            // 안정화용 상태 — 전부 워커 스레드(OnLandmarkerResult)에서만 lock 안에서 쓴다.
            public int missFrames;
            public Vector2 lastWristNormalized;
            public bool hasLastWrist;
        }

        /// <summary>
        /// 샘플이 실제 재생을 시작한 직후 한 번 호출.
        /// 해상도가 바뀌면(예: 장치 회전) 다시 호출해도 안전하다.
        /// </summary>
        public void Configure(int width, int height, bool mirrored)
        {
            if (width <= 0 || height <= 0)
            {
                Debug.LogWarning($"[MediaPipeHandSource] 잘못된 해상도로 Configure 호출됨: " +
                                 $"{width}x{height}. 무시합니다.");
                return;
            }
            _width = width;
            _height = height;
            _mirrored = mirrored;
            _configured = true;
            Debug.Log($"[MediaPipeHandSource] Configure 완료: {width}x{height}, " +
                      $"mirrored={mirrored}");
        }

        private void Update()
        {
            if (!_configured && !_warnedNeverConfigured && Time.time > 2f)
            {
                _warnedNeverConfigured = true;
                Debug.LogWarning($"[MediaPipeHandSource] Configure() 가 아직 호출되지 않았습니다. " +
                                 $"{_fallbackWidth}x{_fallbackHeight} 대체값으로 동작 중입니다. " +
                                 $"실제 웹캠 해상도와 다르면 깊이 추정이 틀어집니다. " +
                                 $"샘플 쪽에서 Configure() 를 호출하도록 연결하세요.");
            }

            // 워커 스레드가 올린 카운터가 바뀌었으면, 그 '도착 시각'을 메인 스레드에서
            // 지금 이 순간으로 기록한다. Time.time 은 여기(메인 스레드)에서만 읽는다.
            long current = System.Threading.Interlocked.Read(ref _resultCounter);
            if (current != _lastSeenCounter)
            {
                _lastSeenCounter = current;
                _lastResultTime = Time.time;
            }

            if (_lastResultTime > 0f && Time.time - _lastResultTime > _staleWarningSeconds
                && !_warnedStale)
            {
                _warnedStale = true;
                Debug.LogWarning($"[MediaPipeHandSource] {_staleWarningSeconds}초 이상 결과가 " +
                                 $"들어오지 않았습니다. 샘플 쪽 콜백에서 OnLandmarkerResult 를 " +
                                 $"호출하는 부분이 빠졌거나, 웹캠/추론이 멈췄을 수 있습니다.");
            }
            else if (_lastResultTime > 0f && Time.time - _lastResultTime <= _staleWarningSeconds)
            {
                _warnedStale = false;
            }
        }

#if HANDVOLLEY_MEDIAPIPE
        /// <summary>
        /// 샘플의 결과 콜백에서 호출한다. MediaPipe 워커 스레드에서 호출될 수 있으므로
        /// 유니티 API(Debug.Log 제외)를 이 안에서 직접 부르지 않는다.
        /// </summary>
        public void OnLandmarkerResult(HandLandmarkerResult result, long timestampMs)
        {
            lock (_lock)
            {
                var updatedThisFrame = new bool[_buffers.Length];

                if (result.handLandmarks != null)
                {
                    int count = Mathf.Min(result.handLandmarks.Count, _buffers.Length);
                    for (int h = 0; h < count; h++)
                    {
                        var lm = result.handLandmarks[h].landmarks;
                        var wl = result.handWorldLandmarks != null && h < result.handWorldLandmarks.Count
                                 ? result.handWorldLandmarks[h].landmarks : null;
                        if (lm == null || lm.Count < HandLandmark.Count ||
                            wl == null || wl.Count < HandLandmark.Count) continue;

                        if (!IsSane(lm, wl)) continue;

                        float wx = lm[HandLandmark.Wrist].x;
                        float wy = lm[HandLandmark.Wrist].y;
                        if (wx < -0.25f || wx > 1.25f || wy < -0.25f || wy > 1.25f) continue;

                        var wristW = wl[HandLandmark.Wrist];
                        var midTipW = wl[HandLandmark.MiddleTip];
                        float dx = midTipW.x - wristW.x, dy = midTipW.y - wristW.y, dz = midTipW.z - wristW.z;
                        float handLen = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (handLen < 0.06f || handLen > 0.30f) continue;

                        HandSide side = HandSide.Unknown;
                        float confidence = 1f;
                        if (result.handedness != null && h < result.handedness.Count)
                        {
                            var cats = result.handedness[h].categories;
                            if (cats != null && cats.Count > 0)
                            {
                                bool isLeftLabel = cats[0].categoryName == "Left";
                                // 알려진 이슈: 일반 USB 웹캠에서는 셀피 기준과 반대로 나오는
                                // 경우가 많다 (실측 확인됨) — _invertReportedHandedness 로 뒤집는다.
                                if (_invertReportedHandedness) isLeftLabel = !isLeftLabel;
                                side = isLeftLabel ? HandSide.Left : HandSide.Right;
                                confidence = cats[0].score;
                            }
                        }

                        int slot = ChooseSlot(wx, wy, side);
                        var buf = _buffers[slot];
                        for (int i = 0; i < HandLandmark.Count; i++)
                        {
                            buf.normalized[i] = new Vector3(lm[i].x, lm[i].y, lm[i].z);
                            buf.world[i] = new Vector3(wl[i].x, wl[i].y, wl[i].z);
                        }
                        buf.side = side;
                        buf.confidence = confidence;
                        buf.timestamp = timestampMs / 1000.0;
                        buf.valid = true;
                        buf.missFrames = 0;
                        buf.lastWristNormalized = new Vector2(wx, wy);
                        buf.hasLastWrist = true;
                        updatedThisFrame[slot] = true;
                    }
                }

                // 이번 프레임에 안 갱신된 슬롯은, 유예 프레임 안에서는 마지막 값을 그대로
                // 유지한 채 missFrames 만 늘린다. 유예를 넘겨야 실제로 무효화한다.
                for (int i = 0; i < _buffers.Length; i++)
                {
                    if (updatedThisFrame[i]) continue;
                    var buf = _buffers[i];
                    if (!buf.valid) continue;
                    buf.missFrames++;
                    if (buf.missFrames > _missFramesToHold)
                    {
                        buf.valid = false;
                        buf.hasLastWrist = false;
                    }
                }
            }
            // 워커 스레드에서 안전한 것은 이 원자적 증가뿐이다.
            // '도착 시각'은 메인 스레드(Update)가 이 카운터 변화를 보고 스스로 기록한다.
            System.Threading.Interlocked.Increment(ref _resultCounter);
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool IsSane(
            System.Collections.Generic.List<Mediapipe.Tasks.Components.Containers.NormalizedLandmark> lm,
            System.Collections.Generic.List<Mediapipe.Tasks.Components.Containers.Landmark> wl)
        {
            for (int i = 0; i < HandLandmark.Count; i++)
            {
                if (!IsFinite(lm[i].x) || !IsFinite(lm[i].y) || !IsFinite(lm[i].z)) return false;
                if (!IsFinite(wl[i].x) || !IsFinite(wl[i].y) || !IsFinite(wl[i].z)) return false;
            }
            return true;
        }

        /// <summary>
        /// 이번 프레임 검출을 어느 버퍼 슬롯에 넣을지 고른다. MediaPipe 결과 배열의
        /// 순서는 프레임마다 바뀔 수 있으므로, 이전 프레임 손목 위치와 가장 가까운
        /// 슬롯을 우선 재사용해 같은 손이 같은 슬롯을 유지하게 한다.
        /// </summary>
        private int ChooseSlot(float wx, float wy, HandSide side)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _buffers.Length; i++)
            {
                var b = _buffers[i];
                if (!b.hasLastWrist) continue;
                float ddx = wx - b.lastWristNormalized.x;
                float ddy = wy - b.lastWristNormalized.y;
                float d = ddx * ddx + ddy * ddy;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (best >= 0 && bestDist < 0.35f * 0.35f) return best;

            for (int i = 0; i < _buffers.Length; i++)
                if (_buffers[i].valid && _buffers[i].side == side) return i;

            for (int i = 0; i < _buffers.Length; i++)
                if (!_buffers[i].valid) return i;

            return 0;
        }
#else
        /// <summary>
        /// HANDVOLLEY_MEDIAPIPE 심볼이 없을 때의 자리표시자.
        /// Project Settings > Player > Scripting Define Symbols 에 추가하면
        /// 위쪽의 실제 구현으로 바뀐다.
        /// </summary>
        public void OnLandmarkerResult(object result, long timestampMs)
        {
            if (!_warnedNoSymbol)
            {
                _warnedNoSymbol = true;
                Debug.LogError("[MediaPipeHandSource] HANDVOLLEY_MEDIAPIPE 심볼이 정의되지 않아 " +
                               "결과를 처리할 수 없습니다. Project Settings > Player > " +
                               "Scripting Define Symbols 에 추가하세요.");
            }
        }
#endif

        /// <summary>
        /// 가장 최근 유효한 손 하나를 고른다. 후보가 여럿이면 다음 점수로 고른다:
        /// handedness 가 preferredSide 와 같으면 +2, 직전에 GetLatest 가 골랐던 손목
        /// 위치에 가까울수록 최대 +3, handedness confidence * 0.25.
        /// </summary>
        public HandObservation GetLatest(HandSide preferredSide)
        {
            lock (_lock)
            {
                HandBuffer chosen = null;
                float bestScore = float.NegativeInfinity;

                foreach (var b in _buffers)
                {
                    if (!b.valid) continue;

                    float score = 0f;
                    if (preferredSide != HandSide.Unknown && b.side == preferredSide) score += 2f;

                    if (_hasLastChosenWrist)
                    {
                        Vector2 wrist = new Vector2(b.normalized[HandLandmark.Wrist].x,
                                                     b.normalized[HandLandmark.Wrist].y);
                        float dist = Vector2.Distance(wrist, _lastChosenWrist);
                        score += Mathf.Max(0f, 3f - dist * 10f);
                    }

                    score += b.confidence * 0.25f;

                    if (score > bestScore) { bestScore = score; chosen = b; }
                }

                if (chosen == null)
                {
                    _hasLastChosenWrist = false;
                    return HandObservation.Invalid;
                }

                _lastChosenWrist = new Vector2(chosen.normalized[HandLandmark.Wrist].x,
                                                chosen.normalized[HandLandmark.Wrist].y);
                _hasLastChosenWrist = true;

                System.Array.Copy(chosen.normalized, _outNormalized, HandLandmark.Count);
                System.Array.Copy(chosen.world, _outWorld, HandLandmark.Count);

                return new HandObservation
                {
                    valid = true,
                    side = chosen.side,
                    confidence = chosen.confidence,
                    normalized = _outNormalized,
                    world = _outWorld,
                    timestampSeconds = chosen.timestamp,
                };
            }
        }
    }
}
