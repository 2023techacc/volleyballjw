using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 빈 씬에 이 스크립트 하나만 붙이고 Play 를 누르면 전체 시스템이 구성된다.
    /// 코트, 공, 손 콜라이더, 카메라, 물리 설정, 스크립트 배선까지 전부 자동.
    ///
    /// 인스펙터 수작업 배선을 없애기 위한 것으로, 구조가 굳으면
    /// 프리팹으로 저장해 두고 이 스크립트는 지워도 된다.
    /// </summary>
    public class HandVolleyBootstrap : MonoBehaviour
    {
        [Header("입력 방식")]
        [Tooltip("체크 해제 시 마우스 테스트 모드. MediaPipe 설치 전에는 이쪽으로 먼저 개발할 것.")]
        [SerializeField] private bool _useMediaPipe = false;

        [Header("표시")]
        [Tooltip("점수 HUD, 공 디버그 패널, HandTracker 진단 텍스트를 한번에 켜고 끈다. " +
                 "화면 녹화할 때 꺼두면 깨끗하게 찍힌다. 시작 화면/결과 화면 자체는 꺼지지 않는다.")]
        [SerializeField] private bool _showDebugText = false;

        [Header("배치")]
        [Tooltip("웹캠의 물리적 위치와 방향. 카메라가 바라보는 쪽이 +Z 가 된다.")]
        [SerializeField] private Vector3 _webcamPosition = new Vector3(0f, 1.25f, 0f);
        [SerializeField] private Vector3 _webcamEuler = new Vector3(0f, 0f, 0f);

        [Header("거리 보정")]
        [Tooltip("HandTracker 는 Play 시점에 코드로 생기는 오브젝트라 씬에 미리 없다 — " +
                 "여기서 미리 값을 맞춰 두면 Play 때마다 Hierarchy 에서 HandTracker 를 " +
                 "따로 찾아 토글할 필요가 없다. 추정 깊이를 게임 공간 깊이로 리매핑: " +
                 "z_game = (z_cam - pivot) * scale + pivot")]
        [SerializeField] private float _depthPivot = 0.8f;
        [Tooltip("깊이(앞뒤) 이동 반영 비율. 실제로 앞뒤로 움직인 만큼 게임 속 손도 더 크게 " +
                 "움직이게 하려면 이 값을 올린다.")]
        [SerializeField, Range(0f, 3f)] private float _depthScale = 1.6f;
        [SerializeField] private Vector2 _depthClamp = new Vector2(0.35f, 1.8f);
        [Tooltip("깊이 이동 방향을 반전. 실제로 손을 앞(카메라 쪽)으로 뻗었는데 게임 속 손이 " +
                 "뒤로 물러나면 켠다.")]
        [SerializeField] private bool _invertDepth = true;

        [Header("손 회전 보정")]
        [Tooltip("앞뒤 기울임(피치)이 실기기에서 과장되어 나오는 문제 보정. " +
                 "HandTracker 는 Play 시점에 코드로 생기는 오브젝트라 씬에 미리 없다 — " +
                 "여기서 미리 값을 맞춰 두면 Play 때마다 Hierarchy 에서 HandTracker 를 " +
                 "따로 찾아 토글할 필요가 없다.")]
        [SerializeField] private bool _invertPalmNormal = false;
        [SerializeField] private bool _invertPalmUp = true;
        [SerializeField] private bool _invertPitch = true;
        [Tooltip("피치(앞뒤 기울임) 배율. 1보다 작게 잡으면 과장된 기울임을 눌러준다.")]
        [SerializeField, Range(0.2f, 1.5f)] private float _pitchGain = 0.4f;

        [Header("물리")]
        [Tooltip("기본 0.02 로는 빠른 스윙에서 반드시 관통이 발생한다. " +
                 "다만 관통 방지의 실제 주력은 HandStriker 의 이동 경로 SweepTest 이고, " +
                 "손의 목표 위치 자체는 렌더 프레임에서만 갱신되므로 이 값을 더 낮춰도 " +
                 "손 이동 해상도는 올라가지 않는다 (공의 자유낙하/바운스 정밀도만 개선됨).")]
        [SerializeField] private float _fixedTimestep = 0.01f;
        [SerializeField] private int _solverIterations = 12;
        [SerializeField] private float _contactOffset = 0.005f;

        [Header("치수 (실제 배구 규격)")]
        [Tooltip("실제 배구공 반지름은 0.105m 지만, 처음 하는 사람도 맞히기 쉽도록 " +
                 "조금 키워 뒀다.")]
        [SerializeField] private float _ballRadius = 0.13f;
        [SerializeField] private float _ballMass = 0.27f;
        [Tooltip("손바닥 크기 (m). x=폭, y=길이, z=두께. 실제 성인 손바닥은 대략 0.10 x 0.17 x 0.03 이지만, " +
                 "화면에서는 더 커야 잘 보이고 치기도 편해서 실측보다 키워 뒀다. " +
                 "손가락 두께도 이 값(z)에 비례해 자동으로 커진다.")]
        [SerializeField] private Vector3 _palmSize = new Vector3(0.24f, 0.32f, 0.07f);

        [Tooltip("손가락 길이 배율. 1.0 이면 팜 길이에 비례한 기본 길이, 높일수록 손가락만 더 길어진다.")]
        [SerializeField] private float _fingerLengthMultiplier = 1.5f;

        [Tooltip("손가락 시각 표현 추가 (콜라이더 없음 — 물리는 히트박스 하나로 처리)")]
        [SerializeField] private bool _showFingers = true;

        [Tooltip("실제 충돌 판정 크기 배율 (좌우/상하). 보이는 손보다 크게 잡으면 훨씬 치기 쉬워진다. " +
                 "난이도를 낮추려고 기본보다 키워 뒀다.")]
        [SerializeField] private float _hitboxScale = 2.6f;

        [Tooltip("앞뒤(깊이) 방향 배율. 단일 카메라에서 가장 오차가 큰 축이라 따로 크게 잡는다. " +
                 "초심자는 깊이 감각이 특히 부정확하므로 더 넉넉히 잡았다.")]
        [SerializeField] private float _hitboxDepthScale = 6.0f;

        [Tooltip("히트박스를 와이어프레임으로 표시. 기본은 꺼짐 — 켜면 손이 네모나게 보인다.")]
        [SerializeField] private bool _showHitbox = false;
        [Tooltip("비거리 측정 코트의 길이 (m). 22 m/s 최대 타구는 바운스와 구름까지 " +
                 "포함하면 약 90m 까지 나간다.")]
        [SerializeField] private float _fieldLength = 220f;
        [SerializeField] private float _fieldWidth = 28f;

        [Tooltip("거리 표시선 간격 (m)")]
        [SerializeField] private float _markerSpacing = 10f;

        [Tooltip("기둥을 세울 간격 (m). 원근감으로 거리를 가늠하게 해준다.")]
        [SerializeField] private float _postSpacing = 25f;

        [Tooltip("최대 타구 속도 (m/s). 38 m/s 면 바운스 포함 약 200m 까지 나간다.")]
        [SerializeField] private float _maxBallSpeed = 38f;

        [Header("네트 (그물 부분에 콜라이더 있음 — 맞으면 튕겨나감)")]
        [SerializeField] private bool _buildNet = true;
        [Tooltip("네트를 세울 위치 (타격 기준선 앞쪽, m). 서브 시작점(z≈5.5~6.5)과 " +
                 "받는 지점(z≈0.9~1.35) 사이 — 너무 가까우면 타격 직전 손 바로 앞에 " +
                 "네트가 겹쳐 보인다.")]
        [SerializeField] private float _netDistance = 4.5f;
        [Tooltip("네트 높이 (공식 배구 규격은 2.43m 이지만, 처음 하는 사람도 쉽게 넘기도록 " +
                 "화면 구도상 필요한 것보다도 더 낮춰 뒀다).")]
        [SerializeField] private float _netHeight = 1.2f;
        [Tooltip("그물이 시작되는 높이 (m). 이 아래는 뚫려 있어서(실제 네트처럼) 바닥에 " +
                 "떨어져 굴러가는 공이 네트에 막히지 않고 지나간다.")]
        [SerializeField] private float _netGapHeight = 0.4f;
        [Tooltip("네트에 맞았을 때 반발 정도. 너무 높으면 살짝 스친 공도 엉뚱한 방향으로 " +
                 "튕겨서 초심자에게 억울하게 느껴지므로 낮춰 뒀다.")]
        [SerializeField] private float _netBounciness = 0.6f;

        [Header("게임 진행 (시작 화면 / 순위)")]
        [Tooltip("한 턴(한 사람 차례)당 서브 횟수. 다 소진하면 결과 화면으로 넘어간다.")]
        [SerializeField] private int _throwsPerTurn = 5;
        [Tooltip("결과 화면을 띄워 두는 시간 (초). 지나면 자동으로 시작 화면으로 돌아간다.")]
        [SerializeField] private float _resultHoldSeconds = 0f;

        [Header("손 크기 조절 범위")]
        [SerializeField] private float _handSizeMin = 0.7f;
        [SerializeField] private float _handSizeMax = 1.5f;

        private const int BallLayer = 9;

        private void Awake()
        {
            ApplyPhysicsSettings();

            Transform origin = BuildTrackingOrigin();
            BallChaseCamera chaseCam = BuildCameraInactive(origin);
            BuildCourt();
            if (_buildNet) BuildNet();

            Rigidbody ball = BuildBall();
            HandStriker striker = BuildHand(origin);
            MonoBehaviour source = BuildSource();
            Transform marker = BuildLandingMarker();

            WireTracker(origin, source, striker);
            BallLauncher launcher = WireLauncher(ball, striker, marker);

            // 카메라에 공/런처 연결.
            // "비활성 → 주입 → 활성화" 규칙을 이 오브젝트에도 지킨다 — 활성화 이전에
            // 주입해야 BallChaseCamera.Awake 의 _lastBallPos 초기화가 실제 공 위치를 본다.
            SetPrivate(chaseCam, "_ball", ball.transform);
            SetPrivate(chaseCam, "_launcher", launcher);
            chaseCam.gameObject.SetActive(true);

            WireGameFlow(striker, launcher);

            int rend = striker.GetComponentsInChildren<Renderer>(true).Length;
            int coll = striker.GetComponentsInChildren<Collider>(true).Length;
            var hb = striker.transform.Find("Hitbox");
            Debug.Log($"[HandVolley] 손 구성: 렌더러 {rend}개, 콜라이더 {coll}개 " +
                      $"(콜라이더는 히트박스 1개가 정상)" +
                      (hb != null ? $"  히트박스 {hb.localScale.x*100:F0}x" +
                                    $"{hb.localScale.y*100:F0}x{hb.localScale.z*100:F0}cm" : ""));

            Debug.Log("[HandVolley] 부트스트랩 완료. " +
                      (_useMediaPipe
                          ? "웹캠 앞에서 손을 움직여 보세요."
                          : "마우스=위치, 휠=깊이, 좌클릭 드래그=회전, R=초기화"));

            if (!_useMediaPipe && InputCompat.UsingNewInputSystem)
            {
                Debug.Log("[HandVolley] 신규 Input System 으로 동작 중입니다. " +
                          "마우스가 안 먹으면 Project Settings > Player > " +
                          "Active Input Handling 을 'Both' 로 바꿔 보세요.");
            }
        }

        // ------------------------------------------------------------------ //

        private void ApplyPhysicsSettings()
        {
            Time.fixedDeltaTime = _fixedTimestep;
            Time.maximumDeltaTime = 0.1f;   // 물리 스텝 폭주 방지
            Physics.defaultSolverIterations = _solverIterations;
            Physics.defaultSolverVelocityIterations = 4;
            Physics.defaultContactOffset = _contactOffset;
        }

        private Transform BuildTrackingOrigin()
        {
            var go = new GameObject("TrackingOrigin (webcam)");
            go.transform.SetPositionAndRotation(_webcamPosition, Quaternion.Euler(_webcamEuler));
            return go.transform;
        }

        /// <summary>
        /// 비활성 상태로 반환한다. 다른 Build 메서드들과 같은 "비활성 → 주입 → 활성화"
        /// 규칙을 따르기 위함이다 — Awake() 가 _ball/_launcher 를 주입한 뒤 호출자가
        /// 직접 활성화한다.
        /// </summary>
        private BallChaseCamera BuildCameraInactive(Transform origin)
        {
            // 카메라는 TrackingOrigin 의 자식이 아니다.
            // 공을 따라 자유롭게 움직여야 하는데, HandTracker 는 Origin 기준으로
            // 손 좌표를 계산하므로 카메라를 분리해도 추적에는 전혀 영향이 없다.
            var camGo = new GameObject("GameCamera");
            camGo.SetActive(false);

            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(300f, _fieldLength * 2f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.82f, 0.89f, 0.97f);
            camGo.AddComponent<AudioListener>();

            var chase = camGo.AddComponent<BallChaseCamera>();
            SetPrivate(chase, "_trackingOrigin", origin);

            var lightGo = new GameObject("Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.05f;
            light.transform.rotation = Quaternion.Euler(48f, -25f, 0f);
            return chase;
        }

        private void BuildCourt()
        {
            // Plane 프리미티브는 기본 10x10m
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, 0f, _fieldLength * 0.5f - 3f);
            ground.transform.localScale = new Vector3(_fieldWidth / 10f, 1f, _fieldLength / 10f);
            Tint(ground, new Color(0.12f, 0.25f, 0.65f));

            // --- 거리 표시선 ---
            // 깊이를 읽을 수 있는 기준선을 깐다 (네트는 별도로 BuildNet 이 세운다).
            int lines = Mathf.FloorToInt(_fieldLength / _markerSpacing);
            // 부동소수점 나머지(z % _postSpacing)로 판정하면 두 간격의 최소공배수에서만
            // major 가 참이 된다 (10m 표시선, 25m 기둥 간격이면 실제로는 50m 마다만 섬).
            // 정수 인덱스 배수로 판정해야 의도한 _postSpacing 간격이 그대로 나온다.
            int postEvery = Mathf.Max(1, Mathf.RoundToInt(_postSpacing / _markerSpacing));
            for (int i = 1; i <= lines; i++)
            {
                float z = i * _markerSpacing;
                bool major = i % postEvery == 0;

                var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
                line.name = $"Mark_{z:F0}m";
                DestroyImmediate(line.GetComponent<Collider>());
                line.transform.position = new Vector3(0f, 0.012f, z);
                line.transform.localScale = new Vector3(_fieldWidth * 0.85f,
                                                        0.02f, major ? 0.16f : 0.06f);
                Color lineColor = major
                    ? new Color(0.25f, 0.45f, 0.85f, 1f)
                    : new Color(0.15f, 0.30f, 0.70f, 1f);
                Tint(line, lineColor);

                if (!major) continue;

                // 양쪽 거리 기둥도 한 톤의 다른 파랑색으로 통일해 화면을 복잡하게 만들지 않는다.
                float t = Mathf.Clamp01(z / _fieldLength);
                Color postColor = Color.Lerp(new Color(0.10f, 0.20f, 0.60f),
                                             new Color(0.08f, 0.15f, 0.50f), t);
                for (int side = -1; side <= 1; side += 2)
                {
                    var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    post.name = $"Post_{z:F0}m";
                    DestroyImmediate(post.GetComponent<Collider>());
                    float height = 0.6f + 0.5f * t;
                    post.transform.position = new Vector3(
                        side * _fieldWidth * 0.45f, height * 0.5f, z);
                    post.transform.localScale = new Vector3(0.12f, height, 0.12f);
                    Tint(post, postColor);

                    // 기둥 위 눈금 — _postSpacing(기둥 간격)마다 한 칸씩 늘어난다
                    int ticks = Mathf.RoundToInt(z / _postSpacing);
                    for (int k = 0; k < ticks; k++)
                    {
                        var tick = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        tick.name = $"Tick_{z:F0}_{k}";
                        DestroyImmediate(tick.GetComponent<Collider>());
                        tick.transform.position = new Vector3(
                            side * _fieldWidth * 0.45f,
                            height + 0.10f + k * 0.16f, z);
                        tick.transform.localScale = new Vector3(0.30f, 0.10f, 0.12f);
                        Tint(tick, postColor);
                    }
                }
            }

            // 타격 기준선 (0m)
            var origin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            origin.name = "Mark_0m";
            DestroyImmediate(origin.GetComponent<Collider>());
            origin.transform.position = new Vector3(0f, 0.014f, 0f);
            origin.transform.localScale = new Vector3(_fieldWidth * 0.9f, 0.02f, 0.24f);
            Tint(origin, new Color(0.25f, 0.45f, 0.85f));
        }

        /// <summary>
        /// 그물 부분(지면에서 _netGapHeight 위부터 _netHeight 까지)에만 콜라이더가 있어
        /// 맞으면 튕겨나간다. 그 아래는 뚫려 있다 — 바닥에 떨어져 굴러가는 공까지
        /// 막아버리면 비거리 판정이 네트 앞에서 영영 끝나버리기 때문이다.
        /// </summary>
        private void BuildNet()
        {
            var net = new GameObject("Net");
            net.transform.position = new Vector3(0f, 0f, _netDistance);

            float halfWidth = _fieldWidth * 0.42f;
            float meshHeight = Mathf.Max(0.1f, _netHeight - _netGapHeight);
            // 반투명 알파는 렌더 파이프라인에 따라 불투명하게 나오기 쉽다
            // (히트박스를 안 그리는 이유와 동일). 대신 얇은 불투명 선을 성기게 배치해
            // 그물처럼 보이게 한다.
            var netColor = new Color(0.90f, 0.93f, 0.97f);

            for (int side = -1; side <= 1; side += 2)
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = "NetPost";
                DestroyImmediate(post.GetComponent<Collider>());
                post.transform.SetParent(net.transform, false);
                post.transform.localPosition = new Vector3(side * halfWidth, _netHeight * 0.5f, 0f);
                post.transform.localScale = new Vector3(0.05f, _netHeight * 0.5f, 0.05f);
                Tint(post, new Color(0.85f, 0.85f, 0.88f));
            }

            // 상단 테이프
            var tape = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tape.name = "NetTape";
            DestroyImmediate(tape.GetComponent<Collider>());
            tape.transform.SetParent(net.transform, false);
            tape.transform.localPosition = new Vector3(0f, _netHeight, 0f);
            tape.transform.localScale = new Vector3(halfWidth * 2f, 0.05f, 0.05f);
            Tint(tape, Color.white);

            // 그물 시각 — _netGapHeight 아래로는 그리지 않는다.
            const int meshLines = 14;
            for (int i = 0; i <= meshLines; i++)
            {
                float t = i / (float)meshLines;

                var vLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
                vLine.name = $"NetMesh_V{i}";
                DestroyImmediate(vLine.GetComponent<Collider>());
                vLine.transform.SetParent(net.transform, false);
                vLine.transform.localPosition = new Vector3(
                    Mathf.Lerp(-halfWidth, halfWidth, t), _netGapHeight + meshHeight * 0.5f, 0f);
                vLine.transform.localScale = new Vector3(0.012f, meshHeight, 0.012f);
                Tint(vLine, netColor);

                var hLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
                hLine.name = $"NetMesh_H{i}";
                DestroyImmediate(hLine.GetComponent<Collider>());
                hLine.transform.SetParent(net.transform, false);
                hLine.transform.localPosition = new Vector3(0f, Mathf.Lerp(_netGapHeight, _netHeight, t), 0f);
                hLine.transform.localScale = new Vector3(halfWidth * 2f, 0.012f, 0.012f);
                Tint(hLine, netColor);
            }

            // --- 물리 충돌 ---
            // 시각용 그물은 얇은 선 여러 개로 쪼개 놓았지만, 콜라이더까지 선마다 붙이면
            // (손가락/히트박스와 같은 이유로) 물리가 불안정해진다. 그물 영역 전체를
            // 덮는 콜라이더 하나로 처리하고, 렌더러는 이미 위의 얇은 선들이 담당하므로
            // 이 콜라이더는 보이지 않게 지운다.
            var netCollider = GameObject.CreatePrimitive(PrimitiveType.Cube);
            netCollider.name = "NetCollider";
            netCollider.transform.SetParent(net.transform, false);
            netCollider.transform.localPosition = new Vector3(0f, _netGapHeight + meshHeight * 0.5f, 0f);
            netCollider.transform.localScale = new Vector3(halfWidth * 2f, meshHeight, 0.08f);
            DestroyImmediate(netCollider.GetComponent<Renderer>());

            var netPhysics = netCollider.GetComponent<BoxCollider>();
#if UNITY_6000_0_OR_NEWER
            netPhysics.material = new PhysicsMaterial("NetBounce")
            {
                bounciness = _netBounciness,
                bounceCombine = PhysicsMaterialCombine.Maximum,
            };
#else
            netPhysics.material = new PhysicMaterial("NetBounce")
            {
                bounciness = _netBounciness,
                bounceCombine = PhysicMaterialCombine.Maximum,
            };
#endif
        }

        /// <summary>착지 지점에 남는 표식. 다음 서브까지 유지된다.</summary>
        private Transform BuildLandingMarker()
        {
            var go = new GameObject("LandingMarker");

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            DestroyImmediate(disc.GetComponent<Collider>());
            disc.transform.SetParent(go.transform, false);
            disc.transform.localScale = new Vector3(0.9f, 0.01f, 0.9f);
            disc.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            Tint(disc, new Color(0.16f, 0.39f, 0.82f, 1f));

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pole.name = "Pole";
            DestroyImmediate(pole.GetComponent<Collider>());
            pole.transform.SetParent(go.transform, false);
            pole.transform.localScale = new Vector3(0.07f, 1.6f, 0.07f);
            pole.transform.localPosition = new Vector3(0f, 0.8f, 0f);
            Tint(pole, new Color(0.16f, 0.39f, 0.82f, 1f));

            go.SetActive(false);
            return go.transform;
        }

        private Rigidbody BuildBall()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Volleyball";
            go.layer = BallLayer;
            go.transform.localScale = Vector3.one * (_ballRadius * 2f);
            TintTextured(go, BuildVolleyballTexture());

            var col = go.GetComponent<SphereCollider>();
#if UNITY_6000_0_OR_NEWER
            col.material = new PhysicsMaterial("BallBounce")
            {
                bounciness = 0.75f,
                dynamicFriction = 0.3f,
                staticFriction = 0.3f,
                bounceCombine = PhysicsMaterialCombine.Maximum,
                frictionCombine = PhysicsMaterialCombine.Average,
            };
#else
            col.material = new PhysicMaterial("BallBounce")
            {
                bounciness = 0.75f,
                dynamicFriction = 0.3f,
                staticFriction = 0.3f,
                bounceCombine = PhysicMaterialCombine.Maximum,
                frictionCombine = PhysicMaterialCombine.Average,
            };
#endif

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = _ballMass;
            // 회전 감쇠(angularDamping)를 크게 잡아 "구르는" 공만 빨리 멈추게 한다.
            // PhysicMaterial 의 마찰(dynamicFriction/staticFriction)은 튕길 때(순간 접촉)와
            // 구를 때(지속 접촉) 모두에 똑같이 적용돼 둘을 따로 조절할 수 없다. 반면
            // 회전 감쇠는 매 프레임 스핀을 지속적으로 깎기 때문에, 구르는 동안 누적된
            // 회전이 필요한 "굴러가는 움직임"에는 크게 영향을 주지만, 순간적인 튕김
            // 자체(반발/속도 반사)에는 거의 영향이 없다.
#if UNITY_6000_0_OR_NEWER
            rb.linearDamping = 0.1f;
            rb.angularDamping = 3.0f;
#else
            rb.drag = 0.1f;
            rb.angularDrag = 3.0f;
#endif
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            // 빠른 공이 손이나 바닥을 뚫는 것을 막는 1차 방어선
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            return rb;
        }

        private HandStriker BuildHand(Transform origin)
        {
            // 루트는 빈 오브젝트. 자식으로 손바닥 + 손가락을 붙인다.
            // HandTracker 가 주는 회전은 local +Z = 손등 법선, local +Y = 손목→중지 방향이다.
            // 손 크기 조절(HandSizeController)은 이 루트의 localScale 을 바꾸는 방식이라,
            // 아래 자식들은 전부 이 루트 기준 상대 위치/크기로만 정의되어야 안전하게 함께 커진다.
            var go = new GameObject("Hand");
            go.transform.position = origin.position + origin.forward * 0.8f;
            go.SetActive(false);

            var color = new Color(0.94f, 0.96f, 0.99f); // 미니멀 UI에 맞춘 밝은 글러브 톤

            // --- 시각 전용 루트 ---
            // 팜/손가락은 전부 이 아래에 둔다. 판정 히트박스는 여기 넣지 않고 Hand 루트에
            // 직접 붙여서, HandStriker 가 타격 순간 이 루트만 살짝 밀어냈다 되돌리는 연출을
            // 넣어도 판정 크기/위치에는 전혀 영향이 없게 분리해 둔다.
            var visualRoot = new GameObject("VisualRoot");
            visualRoot.transform.SetParent(go.transform, false);

            // --- 손바닥 (시각용) — Capsule 로 위/아래 끝을 둥글려 손바닥 윤곽에 가깝게 ---
            // Capsule 프리미티브 메시는 기본 높이가 지름의 2배(스케일 1일 때 높이 2, 지름 1)라,
            // _palmSize 를 그대로 넣으면 의도한 높이의 2배로 렌더링돼 길쭉한 소시지 모양이 된다.
            // y 스케일만 절반으로 보정해야 실제 높이가 _palmSize.y 와 일치한다
            // (BuildFingerChain 의 캡슐 세그먼트에도 이미 같은 보정이 들어가 있다).
            var palm = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            palm.name = "Palm";
            DestroyImmediate(palm.GetComponent<Collider>());
            palm.transform.SetParent(visualRoot.transform, false);
            palm.transform.localScale = new Vector3(_palmSize.x, _palmSize.y * 0.5f, _palmSize.z);
            Tint(palm, color);

            // --- 히트박스 (물리 전담) ---
            // 보이는 손과 판정 크기를 분리한다.
            // 단일 카메라 깊이 추정 오차가 3~8cm 라, 실제 손 크기로 판정하면
            // "쳤는데 안 맞음" 이 반복돼 게임이 즉시 재미없어진다.
            // 판정을 넉넉히 잡아도 플레이어는 차이를 거의 느끼지 못한다.
            var hitbox = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hitbox.name = "Hitbox";
            hitbox.transform.SetParent(go.transform, false);
            hitbox.transform.localScale = new Vector3(
                _palmSize.x * _hitboxScale,
                _palmSize.y * _hitboxScale,
                _palmSize.z * _hitboxDepthScale);

            // 반투명 머티리얼은 파이프라인마다 설정이 달라 불투명 상자로 보이기 쉽다.
            // 손이 네모나게 보이는 원인이므로 렌더러를 아예 제거하는 것이 기본이다.
            DestroyImmediate(hitbox.GetComponent<Renderer>());
            if (_showHitbox) hitbox.AddComponent<WireBoxGizmo>();

            if (_showFingers)
            {
                // 손가락은 시각용, 콜라이더 없음. 2관절 캡슐 체인 + 약한 곡률로
                // 모델링 없이도 "손가락이 살짝 굽어 있다"는 인상을 준다.
                // 전부 _palmSize 에 대한 비율로 정의해, Palm Size 를 조절하면
                // 손가락도 함께 커지고 작아진다 (예전에는 손가락 길이가 고정값이라
                // 팜 크기를 키워도 손가락만 짧아 보이는 문제가 있었다).
                float[] offsetFrac = { -0.33f, -0.113f, 0.113f, 0.33f };
                float[] lengthFrac = { 0.297f, 0.354f, 0.331f, 0.251f };
                float thickness = _palmSize.z * 0.62f;
                for (int i = 0; i < offsetFrac.Length; i++)
                {
                    float length = _palmSize.y * lengthFrac[i] * _fingerLengthMultiplier;
                    BuildFingerChain(visualRoot.transform, $"Finger_{i}",
                        new Vector3(_palmSize.x * offsetFrac[i], _palmSize.y * 0.5f, 0f),
                        length * 0.58f, length * 0.42f,
                        thickness, thickness * 0.82f, 14f, color);
                }

                // 엄지 — 밑동을 42° 틀어 놓은 뒤 그 방향으로 체인을 뻗는다.
                var thumbRoot = new GameObject("ThumbRoot");
                thumbRoot.transform.SetParent(visualRoot.transform, false);
                thumbRoot.transform.localPosition = new Vector3(_palmSize.x * 0.55f, _palmSize.y * 0.07f, 0f);
                thumbRoot.transform.localRotation = Quaternion.Euler(0f, 0f, 42f);
                float thumbLength = _palmSize.y * 0.33f * _fingerLengthMultiplier;
                BuildFingerChain(thumbRoot.transform, "Thumb",
                    Vector3.zero, thumbLength * 0.6f, thumbLength * 0.4f,
                    _palmSize.z * 0.68f, _palmSize.z * 0.55f, 10f, color);
            }

            var striker = go.AddComponent<HandStriker>();
            SetPrivate(striker, "_ballLayer", (LayerMask)(1 << BallLayer));
            SetPrivate(striker, "_maxBallSpeed", _maxBallSpeed);
            SetPrivate(striker, "_visualRoot", visualRoot.transform);

            var sizeController = go.AddComponent<HandSizeController>();
            SetPrivate(sizeController, "_min", _handSizeMin);
            SetPrivate(sizeController, "_max", _handSizeMax);

            go.SetActive(true);
            return striker;
        }

        /// <summary>
        /// 2관절 캡슐 체인 하나를 parent 아래에 만든다. baseLocalPos 는 첫 관절(뿌리)의
        /// parent 기준 로컬 위치, 체인은 그 자리에서 로컬 +Y 방향으로 뻗어나간다.
        /// 두 번째 관절에 bendDeg 만큼 X 축 회전을 줘 살짝 굽은 손가락처럼 보이게 한다.
        /// </summary>
        private static void BuildFingerChain(Transform parent, string name, Vector3 baseLocalPos,
            float length1, float length2, float thickness1, float thickness2,
            float bendDeg, Color color)
        {
            var knuckle = new GameObject(name + "_Knuckle");
            knuckle.transform.SetParent(parent, false);
            knuckle.transform.localPosition = baseLocalPos;

            var seg1 = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            seg1.name = name + "_Seg1";
            // Destroy 는 프레임 끝까지 지연된다. 그러면 HandStriker.Awake 가
            // 아직 살아 있는 손가락 콜라이더까지 물리 대상으로 잡아
            // SweepTest 가 얇은 조각들을 훑게 된다. 즉시 제거해야 한다.
            DestroyImmediate(seg1.GetComponent<Collider>());
            seg1.transform.SetParent(knuckle.transform, false);
            seg1.transform.localPosition = new Vector3(0f, length1 * 0.5f, 0f);
            seg1.transform.localScale = new Vector3(thickness1, length1 * 0.5f, thickness1);
            Tint(seg1, color);

            var joint2 = new GameObject(name + "_Joint2");
            joint2.transform.SetParent(knuckle.transform, false);
            joint2.transform.localPosition = new Vector3(0f, length1, 0f);
            joint2.transform.localRotation = Quaternion.Euler(bendDeg, 0f, 0f);

            var seg2 = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            seg2.name = name + "_Seg2";
            DestroyImmediate(seg2.GetComponent<Collider>());
            seg2.transform.SetParent(joint2.transform, false);
            seg2.transform.localPosition = new Vector3(0f, length2 * 0.5f, 0f);
            seg2.transform.localScale = new Vector3(thickness2, length2 * 0.5f, thickness2);
            Tint(seg2, color);
        }

        private MonoBehaviour BuildSource()
        {
            var go = new GameObject(_useMediaPipe ? "MediaPipeHandSource" : "MouseHandSource");
            if (_useMediaPipe)
            {
                var mp = go.AddComponent<MediaPipeHandSource>();
                mp.ShowDebugLandmarks = _showDebugText;
                return mp;
            }
            return go.AddComponent<MouseHandSource>();
        }

        private void WireTracker(Transform origin, MonoBehaviour source, HandStriker striker)
        {
            // 비활성 상태로 만든 뒤 주입하고 켠다.
            // AddComponent 는 활성 오브젝트에서 Awake 를 즉시 실행하므로,
            // 이 순서를 지키지 않으면 컴포넌트가 주입 전의 빈 필드를 보게 된다.
            var go = new GameObject("HandTracker");
            go.SetActive(false);
            var tracker = go.AddComponent<HandTracker>();
            SetPrivate(tracker, "_sourceBehaviour", source);
            SetPrivate(tracker, "_striker", striker);
            SetPrivate(tracker, "_trackingOrigin", origin);
            SetPrivate(tracker, "_logDiagnostics", _showDebugText);
            SetPrivate(tracker, "_depthPivot", _depthPivot);
            SetPrivate(tracker, "_depthScale", _depthScale);
            SetPrivate(tracker, "_depthClamp", _depthClamp);
            SetPrivate(tracker, "_invertDepth", _invertDepth);
            SetPrivate(tracker, "_invertPalmNormal", _invertPalmNormal);
            SetPrivate(tracker, "_invertPalmUp", _invertPalmUp);
            SetPrivate(tracker, "_invertPitch", _invertPitch);
            SetPrivate(tracker, "_pitchGain", _pitchGain);
            go.SetActive(true);
        }

        private BallLauncher WireLauncher(Rigidbody ball, HandStriker striker, Transform marker)
        {
            var go = new GameObject("BallLauncher");
            go.SetActive(false);
            var launcher = go.AddComponent<BallLauncher>();
            SetPrivate(launcher, "_ball", ball);
            SetPrivate(launcher, "_striker", striker);
            SetPrivate(launcher, "_serveOrigin", new Vector3(0f, 1.75f, 5.5f));
            SetPrivate(launcher, "_landingMarker", marker);
            SetPrivate(launcher, "_outOfBounds", _fieldLength + 30f);
            SetPrivate(launcher, "_targetPoint", _webcamPosition + Vector3.forward * 0.9f + Vector3.up * 0.1f);
            SetPrivate(launcher, "_floorHeight", _ballRadius + 0.22f);
            SetPrivate(launcher, "_voidY", -2.5f);
            SetPrivate(launcher, "_throwsPerTurn", _throwsPerTurn);
            SetPrivate(launcher, "_showBallDebug", _showDebugText);
            go.SetActive(true);
            return launcher;
        }

        /// <summary>순위 저장소 + 시작 화면/결과 화면 상태 머신을 배선한다.</summary>
        private void WireGameFlow(HandStriker striker, BallLauncher launcher)
        {
            var rankingGo = new GameObject("RankingStore");
            var ranking = rankingGo.AddComponent<RankingStore>();

            var handSize = striker.GetComponent<HandSizeController>();

            var flowGo = new GameObject("GameFlowController");
            flowGo.SetActive(false);
            var flow = flowGo.AddComponent<GameFlowController>();
            SetPrivate(flow, "_launcher", launcher);
            SetPrivate(flow, "_ranking", ranking);
            SetPrivate(flow, "_handSize", handSize);
            SetPrivate(flow, "_resultHoldSeconds", _resultHoldSeconds);
            SetPrivate(flow, "_showDebugText", _showDebugText);
            flowGo.SetActive(true);
        }

        // ------------------------------------------------------------------ //

        /// <summary>
        /// 외부 배구공 텍스처 에셋이 없으므로, 절차적으로 배구공 특유의 곡선 패널
        /// 무늬를 그려 넣는다. Unity 구체 프리미티브는 표준 equirectangular UV 를
        /// 쓰므로 가로(u) 방향으로 6등분 패널 + 적도 이음매 하나면 그럴듯하게 보인다.
        /// </summary>
        private static Texture2D BuildVolleyballTexture()
        {
            const int width = 256;
            const int height = 128;
            const int numPanels = 6;

            var panels = new[]
            {
                new Color(0.96f, 0.93f, 0.83f), // 크림
                new Color(0.16f, 0.42f, 0.78f), // 파랑
                new Color(0.98f, 0.78f, 0.16f), // 노랑
            };
            var seam = new Color(0.08f, 0.08f, 0.08f);

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = y / (float)(height - 1);
                for (int x = 0; x < width; x++)
                {
                    float u = x / (float)width;

                    // 세로 패널 경계를 v 에 따라 살짝 휘어서 곡선 패널처럼 보이게 한다.
                    float wavedU = u + 0.02f * Mathf.Sin(v * Mathf.PI * 2f);
                    wavedU -= Mathf.Floor(wavedU);   // [0,1) 로 감아준다 (Repeat 타일링)

                    float panelPos = wavedU * numPanels;
                    int panelIndex = Mathf.FloorToInt(panelPos);
                    float frac = panelPos - panelIndex;
                    bool onSeam = frac < 0.02f || frac > 0.98f;

                    // 적도 이음매 — 완만한 곡선
                    float equator = 0.5f + 0.05f * Mathf.Sin(u * Mathf.PI * numPanels);
                    bool onEquator = Mathf.Abs(v - equator) < 0.015f;

                    Color c = panels[((panelIndex % panels.Length) + panels.Length) % panels.Length];
                    pixels[y * width + x] = (onSeam || onEquator) ? seam : c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        private static Shader _cachedShader;

        /// <summary>Tint 와 같지만 텍스처도 함께 입힌다 (배구공 전용).</summary>
        private static void TintTextured(GameObject go, Texture2D texture)
        {
            Tint(go, Color.white);
            var r = go.GetComponent<Renderer>();
            if (r != null && r.material != null) r.material.mainTexture = texture;
        }

        private static void Tint(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;

            if (_cachedShader == null)
            {
                // 렌더 파이프라인에 맞는 셰이더를 찾는다.
                // 전부 실패하면 머티리얼이 null 이 되어 오브젝트가 통째로 안 보이므로
                // 기존 머티리얼을 그대로 두는 편이 낫다.
                _cachedShader = Shader.Find("Universal Render Pipeline/Lit")
                                ?? Shader.Find("Standard")
                                ?? Shader.Find("Sprites/Default");
                if (_cachedShader == null)
                {
                    Debug.LogWarning("[HandVolley] 사용 가능한 셰이더를 찾지 못했습니다. " +
                                     "기본 머티리얼로 진행합니다.");
                }
            }
            if (_cachedShader == null) return;

            r.material = new Material(_cachedShader) { color = color };
        }

        /// <summary>인스펙터 수작업 배선을 대신하기 위한 리플렉션 주입.</summary>
        private static void SetPrivate(object target, string fieldName, object value)
        {
            var f = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (f == null)
            {
                Debug.LogWarning($"[Bootstrap] 필드를 찾지 못함: {target.GetType().Name}.{fieldName}");
                return;
            }
            f.SetValue(target, value);
        }
    }
}
