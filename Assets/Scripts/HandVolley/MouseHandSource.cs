using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 마우스로 조종하는 가짜 손. MediaPipe 없이 물리·게임 로직을 먼저 완성하기 위한 테스트 하네스다.
    ///
    /// 실제 손처럼 21개 랜드마크를 미터 공간에 만들고 핀홀 모델로 투영하므로,
    /// HandPoseSolver 를 포함한 파이프라인 전체가 그대로 돌아간다.
    ///   마우스 이동    → 손 좌우/상하
    ///   마우스 휠      → 깊이
    ///   좌클릭 드래그  → 손 회전
    ///   R             → 깊이·회전 초기화
    /// </summary>
    public class MouseHandSource : MonoBehaviour, IHandLandmarkSource
    {
        [Header("가상 카메라")]
        [SerializeField] private int _imageWidth = 640;
        [SerializeField] private int _imageHeight = 480;
        [SerializeField] private float _diagonalFovDeg = 80f;
        [Tooltip("셀피 뷰 여부. 실제 웹캠과 같은 규약을 따르도록 테스트 소스도 좌우를 뒤집어 내보낸다. " +
                 "좌우가 반대로 움직이면 이 값과 HandTracker 쪽 해석이 어긋난 것이다.")]
        [SerializeField] private bool _mirrored = true;

        [Header("조작")]
        [SerializeField] private float _depth = 0.8f;
        [SerializeField] private Vector2 _depthRange = new Vector2(0.4f, 1.6f);
        [Tooltip("휠 한 노치당 깊이 변화량 (m)")]
        [SerializeField] private float _scrollSpeed = 0.06f;
        [Tooltip("좌클릭 드래그 1픽셀당 회전 각도")]
        [SerializeField] private float _rotateSpeed = 0.35f;

        [Header("현실감")]
        [Tooltip("랜드마크에 섞을 노이즈 (px). 실제 MediaPipe 는 1~3px 수준.")]
        [SerializeField] private float _landmarkNoisePx = 1.5f;
        [Tooltip("프레임 레이트 제한 — 웹캠 30fps 를 흉내낸다. 0 이면 매 프레임 갱신.")]
        [SerializeField] private float _sourceFps = 30f;

        [Header("디버그")]
        [SerializeField] private bool _showOnScreenHelp = true;

        public int ImageWidth => _imageWidth;
        public int ImageHeight => _imageHeight;
        public bool Mirrored => _mirrored;

        // 첫 샘플이 만들어지기 전에 HandTracker 가 읽어가면 쓰레기 값이 나간다
        public bool IsReady => _hasSample;

        private readonly Vector3[] _model = new Vector3[HandLandmark.Count];
        private readonly Vector3[] _world = new Vector3[HandLandmark.Count];
        private readonly Vector3[] _normalized = new Vector3[HandLandmark.Count];

        private float _fx, _fy, _cx, _cy;
        private Quaternion _handRot = Quaternion.identity;
        private double _timestamp;
        private float _nextSampleTime;
        private bool _hasSample;

        private Vector2 _prevMousePos;
        private bool _hasPrevMouse;
        private bool _warnedNoMouse;

        // 입력은 매 렌더 프레임 누적하고, 샘플링 시점에 소비한다.
        // 휠/키 입력은 '그 프레임에만' 존재하는 값이라, 30Hz 샘플링 안에서만 읽으면
        // 렌더 120fps 기준 4번 중 3번을 놓친다. (휠이 안 먹던 원인)
        private float _pendingScroll;
        private Vector2 _pendingDragDelta;
        private bool _pendingReset;

        private void Awake()
        {
            float halfDiag = 0.5f * Mathf.Sqrt(_imageWidth * (float)_imageWidth +
                                               _imageHeight * (float)_imageHeight);
            _fx = _fy = halfDiag / Mathf.Tan(_diagonalFovDeg * 0.5f * Mathf.Deg2Rad);
            _cx = _imageWidth * 0.5f;
            _cy = _imageHeight * 0.5f;
            BuildHandModel();
            _prevMousePos = InputCompat.MouseAvailable ? InputCompat.MousePosition : Vector2.zero;
            Sample();   // 첫 프레임부터 유효한 관측을 내보낸다
        }

        /// <summary>펼친 손 모양의 대략적인 21점 배치 (미터, 손 중심 원점, y 아래쪽).</summary>
        private void BuildHandModel()
        {
            // (x: 새끼→엄지 방향, y: 손끝→손목 방향이 +  ※ OpenCV 관례라 y 가 아래쪽)
            Vector2[] flat =
            {
                new Vector2(0.000f,  0.045f),
                new Vector2(0.028f,  0.028f), new Vector2(0.045f,  0.008f),
                new Vector2(0.055f, -0.008f), new Vector2(0.062f, -0.024f),
                new Vector2(0.022f, -0.008f), new Vector2(0.028f, -0.035f),
                new Vector2(0.030f, -0.052f), new Vector2(0.031f, -0.066f),
                new Vector2(0.002f, -0.012f), new Vector2(0.003f, -0.042f),
                new Vector2(0.004f, -0.061f), new Vector2(0.004f, -0.076f),
                new Vector2(-0.018f, -0.010f), new Vector2(-0.021f, -0.038f),
                new Vector2(-0.023f, -0.055f), new Vector2(-0.024f, -0.069f),
                new Vector2(-0.036f, -0.002f), new Vector2(-0.043f, -0.024f),
                new Vector2(-0.047f, -0.038f), new Vector2(-0.050f, -0.050f),
            };

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < HandLandmark.Count; i++)
            {
                float z = -0.004f - 0.02f * Mathf.Abs(flat[i].x);   // 살짝 오목한 손바닥
                _model[i] = new Vector3(flat[i].x, flat[i].y, z);
                sum += _model[i];
            }
            Vector3 centroid = sum / HandLandmark.Count;
            for (int i = 0; i < HandLandmark.Count; i++) _model[i] -= centroid;
        }

        private void Update()
        {
            if (!InputCompat.MouseAvailable)
            {
                if (!_warnedNoMouse)
                {
                    _warnedNoMouse = true;
                    Debug.LogError("[MouseHandSource] 마우스 장치를 찾을 수 없습니다. " +
                                   "Project Settings > Player > Active Input Handling 을 " +
                                   "'Both' 로 바꾸면 확실합니다.");
                }
                return;
            }

            AccumulateInput();

            if (_sourceFps > 0f && Time.time < _nextSampleTime) return;
            _nextSampleTime = Time.time + 1f / Mathf.Max(_sourceFps, 1f);
            Sample();
        }

        /// <summary>매 렌더 프레임 호출 — 프레임 단위 입력을 하나도 놓치지 않기 위해.</summary>
        private void AccumulateInput()
        {
            _pendingScroll += InputCompat.ScrollDelta;
            if (InputCompat.ResetPressed) _pendingReset = true;

            Vector2 mouse = InputCompat.MousePosition;
            if (_hasPrevMouse && InputCompat.LeftButtonHeld)
                _pendingDragDelta += mouse - _prevMousePos;
            _prevMousePos = mouse;
            _hasPrevMouse = true;
        }

        private void Sample()
        {
            Vector2 mouse = InputCompat.MousePosition;

            if (_pendingReset)
            {
                _handRot = Quaternion.identity;
                _depth = 0.8f;
                _pendingReset = false;
            }

            // 누적해 둔 입력을 여기서 한 번에 소비한다
            _depth = Mathf.Clamp(_depth + _pendingScroll * _scrollSpeed,
                                 _depthRange.x, _depthRange.y);
            _pendingScroll = 0f;

            Vector2 delta = _pendingDragDelta;
            _pendingDragDelta = Vector2.zero;

            if (delta.sqrMagnitude > 0f)
            {
                // 미러링된 화면에서는 드래그 방향과 요(yaw) 방향도 함께 뒤집혀야 자연스럽다
                float yaw = delta.x * (_mirrored ? -1f : 1f);
                _handRot = Quaternion.Euler(-delta.y * _rotateSpeed,
                                             yaw * _rotateSpeed, 0f) * _handRot;
            }

            // 화면 좌표 → 가상 카메라 픽셀.
            //
            // 실제 웹캠은 사용자를 마주 보므로, 사용자가 손을 오른쪽으로 옮기면
            // 영상에서는 왼쪽(작은 x)으로 찍힌다. HandTracker 가 Mirrored 플래그를 보고
            // 이 반전을 되돌린다.
            // 따라서 테스트 소스도 같은 규약을 따라야 한다 —
            // 화면 좌표를 그대로 넣으면 HandTracker 가 한 번 더 뒤집어 좌우가 반전된다.
            float sw = Mathf.Max(Screen.width, 1);
            float sh = Mathf.Max(Screen.height, 1);

            float nx = Mathf.Clamp01(mouse.x / sw);
            if (_mirrored) nx = 1f - nx;          // 카메라가 보는 대로 = 좌우 반전된 상태

            float px = nx * _imageWidth;
            float py = (1f - Mathf.Clamp01(mouse.y / sh)) * _imageHeight;

            Vector3 center = new Vector3((px - _cx) * _depth / _fx,
                                         (py - _cy) * _depth / _fy,
                                         _depth);

            for (int i = 0; i < HandLandmark.Count; i++)
            {
                Vector3 rotated = _handRot * _model[i];
                _world[i] = rotated;                       // handWorldLandmarks 규약: 손 중심 원점

                Vector3 cam = rotated + center;
                float u = _fx * cam.x / cam.z + _cx + Random.Range(-_landmarkNoisePx, _landmarkNoisePx);
                float v = _fy * cam.y / cam.z + _cy + Random.Range(-_landmarkNoisePx, _landmarkNoisePx);

                _normalized[i] = new Vector3(u / _imageWidth, v / _imageHeight,
                                             rotated.z - _world[HandLandmark.Wrist].z);
            }

            _timestamp = Time.timeAsDouble;
            _hasSample = true;
        }

        public HandObservation GetLatest(HandSide preferredSide) => new HandObservation
        {
            valid = _hasSample,
            side = HandSide.Right,
            confidence = 1f,
            normalized = _normalized,
            world = _world,
            timestampSeconds = _timestamp,
        };

        private void OnGUI()
        {
            if (!_showOnScreenHelp) return;
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.LowerLeft,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };
            GUI.Label(new Rect(14, Screen.height - 84, 560, 70),
                      $"[마우스 테스트 모드]  이동=손 위치   휠=깊이 ({_depth:F2}m)   " +
                      $"좌클릭 드래그=회전   R=초기화\n" +
                      $"입력 시스템: {(InputCompat.UsingNewInputSystem ? "New Input System" : "Legacy")}" +
                      $"   미러링: {(_mirrored ? "ON (셀피 뷰)" : "OFF")}",
                      style);
        }
    }
}
