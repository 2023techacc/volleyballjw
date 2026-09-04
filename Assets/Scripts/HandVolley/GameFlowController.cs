using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 시작 화면 → 전환 → 카운트다운 → 플레이 → 결과 화면을 관리한다.
    /// UI는 별도 Canvas/이미지 에셋 없이 MinimalGui 를 사용해 심플한 카드형 IMGUI 로 그린다.
    ///
    /// 시작 화면과 게임 화면이 "다른 화면"으로 읽히도록 세 가지를 함께 바꾼다.
    ///   1. 카메라 — 메뉴는 높고 넓게, 플레이는 낮고 코트에 붙게 (BallChaseCamera.SetMenuView)
    ///   2. 밝기   — 메뉴는 밝은 스크림, 플레이는 어두운 HUD 칩
    ///   3. UI 밀도 — 메뉴는 상단바/히어로/특징 바, 플레이는 HUD 만
    /// 그 사이를 대각 와이프(Transition)와 카운트다운(Ready)이 이어 준다.
    /// </summary>
    public class GameFlowController : MonoBehaviour
    {
        private enum State { MainMenu, Transition, Ready, Playing, Result }

        /// <summary>대각 와이프 + 카메라 돌리인에 쓰는 시간 (초).</summary>
        private const float TransitionDuration = 0.60f;
        /// <summary>3 · 2 · 1 카운트다운 길이 (초).</summary>
        private const float CountdownSeconds = 3f;
        /// <summary>결과 카드가 페이드 + 슬라이드업으로 등장하는 시간 (초).</summary>
        private const float ResultIntro = 0.25f;
        /// <summary>시작 화면으로 되돌아올 때의 페이드인 시간 (초).</summary>
        private const float MenuFadeIn = 0.35f;

        [SerializeField] private BallLauncher _launcher;
        [SerializeField] private RankingStore _ranking;
        [SerializeField] private HandSizeController _handSize;
        [Tooltip("시작 화면 ↔ 플레이 시점을 오가는 카메라. 비워 두면 MainCamera 에서 찾는다.")]
        [SerializeField] private BallChaseCamera _camera;
        [Tooltip("0 이면 결과 화면을 버튼을 누를 때까지 유지한다. 0보다 크면 해당 초 뒤 메인으로 돌아간다.")]
        [SerializeField] private float _resultHoldSeconds = 0f;
        [Tooltip("개발용 공/추적 진단 텍스트. 실제 게임 UI는 이 값과 무관하게 항상 보인다.")]
        [SerializeField] private bool _showDebugText = false;

        private State _state = State.MainMenu;
        private float _stateTime;
        private float _resultTimer;
        private int _lastTurnScore;
        private float _lastTurnBestDistance;
        private int _lastTurnRallies;
        private int _lastTurnRank;
        private string _lastTurnBucket;
        private bool _isPractice;
        private bool _pendingPractice;
        private bool _showRankingPanel;
        private bool _showSettingsPanel;
        private bool _showHelpPanel;
        private string _calibrateMessage;
        private float _calibrateMessageUntil;

        private bool AnyPanelOpen => _showHelpPanel || _showRankingPanel || _showSettingsPanel;

        private void Awake()
        {
            if (_launcher != null)
            {
                _launcher.OnTurnComplete += OnTurnComplete;
                _launcher.SetBallDebugVisible(_showDebugText);
            }
            if (_camera == null && Camera.main != null)
                _camera = Camera.main.GetComponent<BallChaseCamera>();

            EnterMainMenu();
            // 첫 프레임은 보간 없이 메뉴 시점에서 시작한다.
            if (_camera != null) _camera.SetMenuView(true, snap: true);
        }

        private void OnDestroy()
        {
            if (_launcher != null) _launcher.OnTurnComplete -= OnTurnComplete;
        }

        private void Update()
        {
            _stateTime += Time.deltaTime;

            switch (_state)
            {
                case State.MainMenu:
                    if (InputCompat.StartPressed && !AnyPanelOpen) BeginStartSequence(false);
                    break;

                case State.Transition:
                    if (_stateTime >= TransitionDuration) EnterReady();
                    break;

                case State.Ready:
                    if (InputCompat.MenuPressed) { EnterMainMenu(); break; }
                    if (_stateTime >= CountdownSeconds) StartTurn();
                    break;

                case State.Playing:
                    if (InputCompat.MenuPressed)
                    {
                        _launcher?.AbortTurn();
                        EnterMainMenu();
                    }
                    break;

                case State.Result:
                    if (InputCompat.MenuPressed) { EnterMainMenu(); break; }
                    if (_resultHoldSeconds <= 0f) break;
                    _resultTimer -= Time.deltaTime;
                    if (_resultTimer <= 0f) EnterMainMenu();
                    break;
            }
        }

        private void SetState(State next)
        {
            _state = next;
            _stateTime = 0f;
        }

        // ------------------------------------------------------------------ //
        // 상태 전이
        // ------------------------------------------------------------------ //

        private void EnterMainMenu()
        {
            SetState(State.MainMenu);
            _showRankingPanel = false;
            _showSettingsPanel = false;
            _showHelpPanel = false;
            if (_launcher != null)
            {
                _launcher.SetHudVisible(false);
                _launcher.SetHudAlpha(1f);
                _launcher.PrepareMenuScene();
            }
            if (_camera != null) _camera.SetMenuView(true);
        }

        /// <summary>
        /// 시작 화면 → 게임 화면 전환의 진입점. 대각 와이프가 도는 동안 카메라를
        /// 플레이 시점으로 돌리인시키고, 끝나면 카운트다운으로 넘어간다.
        /// 연습(practice) 이면 결과가 랭킹에 기록되지 않는다.
        /// </summary>
        private void BeginStartSequence(bool practice)
        {
            _pendingPractice = practice;
            SetState(State.Transition);
            _showRankingPanel = false;
            _showSettingsPanel = false;
            _showHelpPanel = false;
            if (_camera != null) _camera.SetMenuView(false);
            if (_launcher != null) _launcher.SetHudVisible(false);
        }

        private void EnterReady()
        {
            SetState(State.Ready);
            if (_launcher == null) return;
            // HUD 를 흐리게 미리 깔아 두면 카운트다운이 끝나는 순간이 '켜짐'으로 읽힌다.
            _launcher.SetHudVisible(true);
            _launcher.SetHudAlpha(0.32f);
        }

        private void StartTurn()
        {
            _isPractice = _pendingPractice;
            SetState(State.Playing);
            if (_launcher == null) return;
            _launcher.SetHudVisible(true);
            _launcher.SetHudAlpha(1f);
            _launcher.SetBallDebugVisible(_showDebugText);
            _launcher.BeginTurn();
        }

        private void OnTurnComplete(int score, float bestDistance)
        {
            _lastTurnScore = score;
            _lastTurnBestDistance = bestDistance;
            _lastTurnRallies = _launcher != null ? _launcher.TurnRallies : 0;

            if (!_isPractice && _ranking != null)
            {
                _lastTurnBucket = _ranking.GetCurrentBucketName();
                _ranking.AddEntry(score, bestDistance);
                _lastTurnRank = _ranking.GetRank(_lastTurnBucket, score);
            }
            else
            {
                _lastTurnBucket = null;
                _lastTurnRank = 0;
            }

            if (_launcher != null) _launcher.SetHudVisible(false);
            _resultTimer = _resultHoldSeconds;
            SetState(State.Result);
        }

        // ------------------------------------------------------------------ //
        // 그리기
        // ------------------------------------------------------------------ //

        private void OnGUI()
        {
            Matrix4x4 old = MinimalGui.BeginScaled();
            Color oldColor = GUI.color;
            try
            {
                switch (_state)
                {
                    case State.MainMenu: DrawMainMenu(); break;
                    case State.Transition: DrawTransition(); break;
                    case State.Ready: DrawReady(); break;
                    case State.Result: DrawResult(); break;
                    case State.Playing: break; // BallLauncher 의 게임 HUD가 담당
                }
            }
            finally
            {
                GUI.color = oldColor;
                GUI.matrix = old;
            }
        }

        // ---------------------------- 시작 화면 ---------------------------- //

        private void DrawMainMenu()
        {
            // 메인으로 돌아올 때 상단바/히어로가 페이드인한다.
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(_stateTime / MenuFadeIn));

            // 3D 코트 위에 얹는 좌측 스크림. 히어로 텍스트의 가독성을 확보하면서도
            // 오른쪽 절반은 코트가 그대로 보이게 남긴다.
            MinimalGui.ScrimFill(new Rect(0f, 86f, MinimalGui.ReferenceWidth, 644f),
                                 new Color(0.949f, 0.973f, 1f, 1f));

            DrawTopBar();

            GUI.Label(new Rect(110f, 172f, 620f, 100f), "HAND",
                MinimalGui.Label(76, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(110f, 264f, 620f, 112f), "VOLLEY",
                MinimalGui.Label(88, MinimalGui.Accent, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(114f, 392f, 620f, 40f), "손으로 공을 쳐보세요!",
                MinimalGui.Label(26, MinimalGui.Muted, TextAnchor.MiddleLeft));

            if (MinimalGui.PillButton(new Rect(112f, 458f, 320f, 88f), "시작하기  ›", 27,
                                      MinimalGui.Accent, MinimalGui.AccentHover, Color.white))
                BeginStartSequence(false);

            GUI.Label(new Rect(130f, 560f, 460f, 30f), "Space 키로도 시작할 수 있어요",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));

            string handText = _handSize == null
                ? "손 크기 자동 보정"
                : $"손 크기 자동 보정  {_handSize.Scale:F2}x";
            GUI.Label(new Rect(130f, 596f, 460f, 30f), handText,
                MinimalGui.Label(16, new Color(0.15f, 0.23f, 0.34f, 0.68f), TextAnchor.MiddleLeft));

            // 우측은 패널이 열려 있으면 패널이, 아니면 손 인식 프리뷰 카드가 차지한다.
            if (_showHelpPanel) DrawHelpPanel();
            else if (_showRankingPanel) DrawRankingPanel();
            else if (_showSettingsPanel) DrawSettingsPanel();
            else DrawTrackingPreview();

            DrawFeatureBand();
        }

        private void DrawTopBar()
        {
            MinimalGui.Fill(new Rect(0f, 0f, MinimalGui.ReferenceWidth, 86f), MinimalGui.NavyBar);

            var mark = new Rect(44f, 22f, 42f, 42f);
            MinimalGui.RingFill(mark, MinimalGui.AccentSoft);
            GUI.Label(mark, "HV", MinimalGui.CenterLabel(18, MinimalGui.OnNavy, FontStyle.Bold));

            GUI.Label(new Rect(100f, 22f, 320f, 42f), "HAND VOLLEY",
                MinimalGui.Label(22, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold));

            if (MinimalGui.NavButton(new Rect(330f, 20f, 62f, 50f), "홈", !AnyPanelOpen))
            {
                _showHelpPanel = false;
                _showRankingPanel = false;
                _showSettingsPanel = false;
            }

            if (MinimalGui.NavButton(new Rect(408f, 20f, 124f, 50f), "게임 방법", _showHelpPanel))
            {
                _showHelpPanel = !_showHelpPanel;
                _showRankingPanel = false;
                _showSettingsPanel = false;
            }

            if (MinimalGui.NavButton(new Rect(548f, 20f, 78f, 50f), "랭킹", _showRankingPanel))
            {
                _showRankingPanel = !_showRankingPanel;
                _showHelpPanel = false;
                _showSettingsPanel = false;
            }

            if (MinimalGui.NavButton(new Rect(642f, 20f, 78f, 50f), "설정", _showSettingsPanel))
            {
                _showSettingsPanel = !_showSettingsPanel;
                _showHelpPanel = false;
                _showRankingPanel = false;
            }

            bool tracking = HandTracker.Instance != null && HandTracker.Instance.IsTracking;
            var chip = new Rect(1256f, 22f, 304f, 42f);
            MinimalGui.PillFill(chip, new Color(1f, 1f, 1f, 0.12f));
            MinimalGui.CircleFill(new Rect(chip.x + 20f, chip.y + 17f, 9f, 9f),
                tracking ? MinimalGui.Mint : new Color(0.95f, 0.66f, 0.23f, 1f));
            GUI.Label(new Rect(chip.x + 40f, chip.y, chip.width - 56f, chip.height),
                tracking ? "손 인식 중 · 준비 완료" : "손을 카메라 앞에 보여주세요",
                MinimalGui.Label(17, MinimalGui.OnNavy, TextAnchor.MiddleLeft, FontStyle.Bold));
        }

        /// <summary>
        /// 시안의 웹캠 프리뷰 자리. 아직 영상 텍스처를 끌어오지 않으므로 지금은
        /// HandTracker 의 인식 상태를 같은 크기/위치의 카드로 보여준다.
        /// </summary>
        private void DrawTrackingPreview()
        {
            var card = new Rect(1180f, 168f, 300f, 214f);
            MinimalGui.RoundFill(card, new Color(0.047f, 0.145f, 0.298f, 0.92f));

            HandTracker tracker = HandTracker.Instance;
            bool tracking = tracker != null && tracker.IsTracking;

            var badge = new Rect(card.x + 16f, card.y + 16f, 150f, 32f);
            MinimalGui.PillFill(badge, new Color(0.016f, 0.071f, 0.157f, 0.62f));
            MinimalGui.CircleFill(new Rect(badge.x + 14f, badge.y + 12f, 8f, 8f),
                tracking ? MinimalGui.Mint : new Color(0.95f, 0.66f, 0.23f, 1f));
            GUI.Label(new Rect(badge.x + 32f, badge.y, badge.width - 40f, badge.height),
                "HAND TRACKING", MinimalGui.Label(13, MinimalGui.OnNavy, TextAnchor.MiddleLeft,
                                                  FontStyle.Bold));

            GUI.Label(new Rect(card.x, card.y + 80f, card.width, 46f),
                tracking ? "손 인식됨" : "손이 안 보여요",
                MinimalGui.CenterLabel(30, Color.white, FontStyle.Bold));

            string detail = tracker == null
                ? "추적기가 아직 준비되지 않았습니다"
                : tracking
                    ? $"기준 거리 {tracker.DepthPivot:F2} m"
                    : "카메라 앞으로 손을 옮겨 주세요";
            GUI.Label(new Rect(card.x + 16f, card.y + 132f, card.width - 32f, 30f), detail,
                MinimalGui.CenterLabel(16, MinimalGui.HudLabel));

            GUI.Label(new Rect(card.x, card.y + 246f, card.width, 30f), "손을 카메라 안에 두세요",
                MinimalGui.CenterLabel(17, new Color(0.071f, 0.235f, 0.494f, 1f), FontStyle.Bold));
        }

        /// <summary>하단 네이비 특징 바 — 시작 화면에만 있고 게임 화면에는 없다.</summary>
        private void DrawFeatureBand()
        {
            MinimalGui.Fill(new Rect(0f, 730f, MinimalGui.ReferenceWidth, 170f), MinimalGui.Navy);

            DrawFeature(120f, 768f, "정확도", "손의 위치를 정확하게 인식", false);
            DrawFeature(620f, 768f, "실시간", "빠른 반응 속도", true);

            int best = 0;
            if (_ranking != null)
            {
                var top = _ranking.GetTop(_ranking.GetCurrentBucketName(), 1);
                if (top.Count > 0) best = top[0].score;
            }

            GUI.Label(new Rect(1180f, 762f, 280f, 24f), "오늘의 1위",
                MinimalGui.Label(16, MinimalGui.OnNavyDim, TextAnchor.MiddleRight));
            GUI.Label(new Rect(1180f, 788f, 280f, 42f),
                best > 0 ? $"{best:N0} 점" : "기록 없음",
                MinimalGui.Label(30, Color.white, TextAnchor.MiddleRight, FontStyle.Bold));
        }

        private void DrawFeature(float x, float y, string title, string desc, bool filled)
        {
            var icon = new Rect(x, y, 62f, 62f);
            MinimalGui.RingFill(icon, new Color(1f, 1f, 1f, 0.35f));
            if (filled)
                MinimalGui.CircleFill(new Rect(icon.x + 22f, icon.y + 22f, 18f, 18f),
                                      new Color(0.812f, 0.894f, 1f, 0.9f));
            else
                MinimalGui.RingFill(new Rect(icon.x + 16f, icon.y + 16f, 30f, 30f),
                                    new Color(0.812f, 0.894f, 1f, 0.9f));

            GUI.Label(new Rect(x + 82f, y + 2f, 400f, 32f), title,
                MinimalGui.Label(24, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(x + 82f, y + 34f, 400f, 28f), desc,
                MinimalGui.Label(17, MinimalGui.OnNavyDim, TextAnchor.MiddleLeft));
        }

        // ---------------------------- 전환 화면 ---------------------------- //

        /// <summary>
        /// 대각 와이프. 오른쪽 밖에서 들어온 네이비 면이 화면을 덮는 동안
        /// 카메라는 이미 플레이 시점으로 이동하고 있다.
        /// </summary>
        private void DrawTransition()
        {
            float t = Mathf.Clamp01(_stateTime / TransitionDuration);
            float e = Mathf.SmoothStep(0f, 1f, t);

            // 뒤로 밀려나는 시작 화면의 잔상 (버튼 없이 배경과 히어로만).
            GUI.color = new Color(1f, 1f, 1f, (1f - e) * 0.35f);
            float slide = -150f * e;
            MinimalGui.ScrimFill(new Rect(slide, 86f, MinimalGui.ReferenceWidth, 644f),
                                 new Color(0.949f, 0.973f, 1f, 1f));
            GUI.Label(new Rect(110f + slide, 172f, 620f, 100f), "HAND",
                MinimalGui.Label(76, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(110f + slide, 264f, 620f, 112f), "VOLLEY",
                MinimalGui.Label(88, MinimalGui.Accent, TextAnchor.MiddleLeft, FontStyle.Bold));

            GUI.color = Color.white;
            float edgeX = Mathf.Lerp(1780f, 190f, e);
            DrawWipeBand(edgeX, 2600f, MinimalGui.NavyDeep);
            DrawWipeBand(edgeX - 54f, 30f, MinimalGui.Accent);
            DrawWipeBand(edgeX - 106f, 16f, MinimalGui.AccentSoft);

            // 문구는 와이프가 지나간 뒤에 떠오른다.
            GUI.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.85f, t)));
            GUI.Label(new Rect(640f, 318f, 520f, 34f), "GET READY",
                MinimalGui.Label(22, MinimalGui.AccentSoft, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(640f, 356f, 720f, 112f), "코트로 이동",
                MinimalGui.Label(80, Color.white, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(642f, 476f, 620f, 36f), "카메라가 코트 뒤로 이동합니다",
                MinimalGui.Label(24, new Color(0.663f, 0.784f, 0.949f, 1f), TextAnchor.MiddleLeft));

            MinimalGui.ProgressBar(new Rect(642f, 532f, 520f, 8f), t,
                                   new Color(1f, 1f, 1f, 0.16f), MinimalGui.AccentSoft);
            GUI.Label(new Rect(642f, 552f, 620f, 26f),
                $"{_stateTime:F2}s / {TransitionDuration:F2}s  ·  대각 와이프 + 카메라 돌리인",
                MinimalGui.Label(16, new Color(0.498f, 0.651f, 0.859f, 1f), TextAnchor.MiddleLeft));
        }

        /// <summary>
        /// 세로로 세운 띠를 살짝 기울여 그린다. IMGUI 에는 다각형 채우기가 없어서
        /// GUI 행렬을 회전시킨 뒤 사각형을 채우는 방식으로 대각선을 만든다.
        /// </summary>
        private static void DrawWipeBand(float x, float width, Color color)
        {
            Matrix4x4 baseMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(-14f, new Vector2(x, MinimalGui.ReferenceHeight * 0.5f));
            MinimalGui.Fill(new Rect(x, -400f, width, 1700f), color);
            GUI.matrix = baseMatrix;
        }

        // --------------------------- 카운트다운 --------------------------- //

        private void DrawReady()
        {
            MinimalGui.Fill(new Rect(0f, 0f, MinimalGui.ReferenceWidth, MinimalGui.ReferenceHeight),
                            new Color(0.024f, 0.078f, 0.173f, 0.42f));

            int remaining = Mathf.Clamp(Mathf.CeilToInt(CountdownSeconds - _stateTime), 1, 3);
            float withinSecond = 1f - Mathf.Repeat(_stateTime, 1f);

            var ring = new Rect(640f, 240f, 320f, 320f);
            MinimalGui.CircleFill(ring, new Color(0.027f, 0.102f, 0.220f, 0.55f));
            MinimalGui.RingFill(ring, new Color(1f, 1f, 1f, 0.18f));
            DrawCountdownDots(ring, withinSecond);

            // 숫자가 바뀌는 순간 살짝 커졌다 제자리로 돌아온다.
            int size = Mathf.RoundToInt(Mathf.Lerp(174f, 150f,
                Mathf.Clamp01((1f - withinSecond) / 0.18f)));
            GUI.Label(ring, remaining.ToString(),
                MinimalGui.CenterLabel(size, Color.white, FontStyle.Bold));

            GUI.Label(new Rect(500f, 596f, 600f, 40f), "손을 화면 안에 들어오게 하세요",
                MinimalGui.CenterLabel(30, MinimalGui.OnNavy, FontStyle.Bold));

            bool tracking = HandTracker.Instance != null && HandTracker.Instance.IsTracking;
            GUI.Label(new Rect(500f, 646f, 600f, 30f),
                tracking ? "손 인식됨 · 준비 완료" : "손이 안 보여요 — 카메라 앞으로",
                MinimalGui.CenterLabel(19, tracking ? MinimalGui.Mint
                                                    : new Color(0.95f, 0.66f, 0.23f, 1f),
                                       FontStyle.Bold));
        }

        /// <summary>
        /// 링 위를 도는 진행 표시. 호(arc)를 그릴 수 없으므로 12개의 점으로 나눠
        /// 남은 비율만큼만 밝게 칠한다.
        /// </summary>
        private static void DrawCountdownDots(Rect ring, float progress)
        {
            const int count = 12;
            float radius = ring.width * 0.5f - 10f;
            Vector2 center = ring.center;
            int lit = Mathf.CeilToInt(progress * count);

            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * Mathf.PI * 2f - Mathf.PI * 0.5f;
                var dot = new Rect(center.x + Mathf.Cos(angle) * radius - 7f,
                                   center.y + Mathf.Sin(angle) * radius - 7f, 14f, 14f);
                MinimalGui.CircleFill(dot, i < lit
                    ? MinimalGui.AccentSoft
                    : new Color(1f, 1f, 1f, 0.16f));
            }
        }

        // ---------------------------- 결과 화면 ---------------------------- //

        private void DrawResult()
        {
            float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_stateTime / ResultIntro));
            GUI.color = new Color(1f, 1f, 1f, e);

            // 게임 화면을 밝게 덮어 "플레이가 멈췄다"는 신호를 준다.
            GUI.DrawTexture(new Rect(0f, 0f, MinimalGui.ReferenceWidth, MinimalGui.ReferenceHeight),
                            MinimalGui.OverlayTexture, ScaleMode.StretchToFill, true);

            var card = new Rect(510f, 118f + Mathf.Lerp(24f, 0f, e), 580f, 664f);
            MinimalGui.RoundFill(card, new Color(1f, 1f, 1f, 0.97f));

            GUI.Label(new Rect(card.x, card.y + 36f, card.width, 30f),
                _isPractice ? "PRACTICE CLEAR" : "TURN CLEAR",
                MinimalGui.CenterLabel(19, MinimalGui.Accent, FontStyle.Bold));
            GUI.Label(new Rect(card.x, card.y + 72f, card.width, 50f),
                _isPractice ? "연습 완료" : "이번 턴 결과",
                MinimalGui.CenterLabel(36, MinimalGui.Ink, FontStyle.Bold));

            GUI.Label(new Rect(card.x, card.y + 148f, card.width, 28f), "SCORE",
                MinimalGui.CenterLabel(17, MinimalGui.Muted));
            GUI.Label(new Rect(card.x, card.y + 176f, card.width, 120f), _lastTurnScore.ToString("N0"),
                MinimalGui.CenterLabel(90, MinimalGui.Accent, FontStyle.Bold));

            MinimalGui.Fill(new Rect(card.x + 44f, card.y + 300f, card.width - 88f, 1f),
                            new Color(0.890f, 0.925f, 0.976f, 1f));

            DrawResultRow(card, 322f, "최고 비거리", $"{_lastTurnBestDistance:F1} m", true);
            DrawResultRow(card, 374f, "성공 랠리", $"{_lastTurnRallies} 회", false);

            string rankValue = _isPractice
                ? "기록 안 함"
                : _lastTurnRank > 0 ? $"{_lastTurnRank} 위" : "—";
            DrawResultRow(card, 426f, _isPractice ? "연습 기록" : "오늘 순위", rankValue, false);

            var primary = new Rect(card.x + 44f, card.y + 500f, card.width - 88f, 78f);
            if (MinimalGui.PillButton(primary, _isPractice ? "실전 시작" : "다시 하기", 26,
                                      MinimalGui.Accent, MinimalGui.AccentHover, Color.white))
                BeginStartSequence(false);

            var secondary = new Rect(card.x + 44f, card.y + 592f, card.width - 88f, 68f);
            if (MinimalGui.PillButton(secondary, "메인으로", 23,
                                      new Color(1f, 1f, 1f, 0.96f),
                                      MinimalGui.SoftBlue, MinimalGui.Ink))
                EnterMainMenu();

            string footer;
            if (_resultHoldSeconds > 0f)
                footer = $"{Mathf.CeilToInt(_resultTimer)}초 뒤 메인으로 이동";
            else if (_isPractice)
                footer = "연습 기록은 랭킹에 남지 않아요";
            else if (!string.IsNullOrEmpty(_lastTurnBucket))
                footer = $"{_lastTurnBucket} 시간대 기준  ·  Esc 로 메인";
            else
                footer = "Esc 로 메인";
            GUI.Label(new Rect(card.x, card.y + 676f, card.width, 30f), footer,
                MinimalGui.CenterLabel(17, MinimalGui.Muted));
        }

        private static void DrawResultRow(Rect card, float dy, string key, string value, bool accent)
        {
            GUI.Label(new Rect(card.x + 44f, card.y + dy, 300f, 34f), key,
                MinimalGui.Label(20, MinimalGui.Muted, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(card.x + card.width - 344f, card.y + dy, 300f, 34f), value,
                MinimalGui.Label(24, accent ? MinimalGui.Accent : MinimalGui.Ink,
                                 TextAnchor.MiddleRight, FontStyle.Bold));
        }

        // ------------------------------ 패널 ------------------------------ //

        /// <summary>시작 화면 우측 패널의 공통 자리. 하단 특징 바(y=730) 위에서 끝난다.</summary>
        private static Rect PanelRect => new Rect(1112f, 140f, 400f, 500f);

        private void DrawHelpPanel()
        {
            Rect panel = PanelRect;
            MinimalGui.DrawCard(panel);

            GUI.Label(new Rect(panel.x + 28f, panel.y + 24f, panel.width - 56f, 42f), "게임 방법",
                MinimalGui.Label(26, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));

            string[] rules =
            {
                "공이 날아오면 손으로 쳐서 넘기세요",
                "최대한 멀리 보낼수록 점수가 높아요",
                "한 턴에 5번의 기회가 주어져요",
                "공을 뒤로 넘기면 그 시도는 0점이에요",
                "손이 잘 안 맞으면 설정에서 거리를 보정해보세요",
            };

            float y = panel.y + 88f;
            foreach (string rule in rules)
            {
                GUI.Label(new Rect(panel.x + 28f, y, 24f, 44f), "•",
                    MinimalGui.Label(20, MinimalGui.Accent, TextAnchor.UpperLeft, FontStyle.Bold));
                GUI.Label(new Rect(panel.x + 52f, y, panel.width - 80f, 44f), rule,
                    MinimalGui.Label(16, MinimalGui.Ink, TextAnchor.UpperLeft));
                y += 54f;
            }

            if (MinimalGui.PillButton(new Rect(panel.x + 28f, panel.y + 404f, panel.width - 56f, 64f),
                                      "연습해보기", 22,
                                      MinimalGui.Accent, MinimalGui.AccentHover, Color.white))
                BeginStartSequence(true);
        }

        private void DrawRankingPanel()
        {
            Rect panel = PanelRect;
            MinimalGui.DrawCard(panel);

            GUI.Label(new Rect(panel.x + 28f, panel.y + 24f, panel.width - 56f, 42f), "랭킹",
                MinimalGui.Label(26, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));

            if (_ranking == null)
            {
                GUI.Label(new Rect(panel.x + 28f, panel.y + 92f, panel.width - 56f, 40f),
                    "랭킹 정보를 불러올 수 없습니다.",
                    MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));
                return;
            }

            string bucket = _ranking.GetCurrentBucketName();
            var entries = _ranking.GetTop(bucket, 5);
            GUI.Label(new Rect(panel.x + 28f, panel.y + 70f, panel.width - 56f, 28f), $"{bucket} 시간대",
                MinimalGui.Label(15, MinimalGui.Muted, TextAnchor.MiddleLeft));

            if (entries.Count == 0)
            {
                GUI.Label(new Rect(panel.x + 28f, panel.y + 150f, panel.width - 56f, 36f),
                    "아직 기록이 없습니다.", MinimalGui.CenterLabel(18, MinimalGui.Muted));
                return;
            }

            float y = panel.y + 118f;
            for (int i = 0; i < entries.Count; i++)
            {
                Color rowColor = i == 0 ? MinimalGui.Accent : MinimalGui.Ink;
                GUI.Label(new Rect(panel.x + 28f, y, 56f, 46f), $"{i + 1}",
                    MinimalGui.Label(20, rowColor, TextAnchor.MiddleLeft, FontStyle.Bold));
                GUI.Label(new Rect(panel.x + 84f, y, 165f, 46f), $"{entries[i].score:N0}점",
                    MinimalGui.Label(19, MinimalGui.Ink, TextAnchor.MiddleLeft));
                GUI.Label(new Rect(panel.x + 245f, y, 120f, 46f), $"{entries[i].bestDistance:F1} m",
                    MinimalGui.Label(18, MinimalGui.Accent, TextAnchor.MiddleRight, FontStyle.Bold));
                y += 58f;
            }
        }

        private void DrawSettingsPanel()
        {
            Rect panel = PanelRect;
            MinimalGui.DrawCard(panel);

            GUI.Label(new Rect(panel.x + 28f, panel.y + 24f, panel.width - 56f, 42f), "설정",
                MinimalGui.Label(26, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));

            GUI.Label(new Rect(panel.x + 28f, panel.y + 88f, 210f, 34f), "손 크기",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(panel.x + 250f, panel.y + 88f, 110f, 34f),
                _handSize == null ? "자동" : $"{_handSize.Scale:F2}x",
                MinimalGui.Label(18, MinimalGui.Ink, TextAnchor.MiddleRight, FontStyle.Bold));

            GUI.Label(new Rect(panel.x + 28f, panel.y + 136f, 210f, 34f), "손 회전 보정",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(panel.x + 250f, panel.y + 136f, 110f, 34f), "자동",
                MinimalGui.Label(18, MinimalGui.Accent, TextAnchor.MiddleRight, FontStyle.Bold));

            DrawDepthCalibrationSection(panel);

            string debugText = _showDebugText ? "디버그 정보  ON" : "디버그 정보  OFF";
            if (GUI.Button(new Rect(panel.x + 28f, panel.y + 420f, panel.width - 56f, 44f),
                           debugText, MinimalGui.SecondaryButton))
            {
                _showDebugText = !_showDebugText;
                if (_launcher != null) _launcher.SetBallDebugVisible(_showDebugText);
            }
        }

        /// <summary>
        /// 거리 보정(기준 거리) UI. HandTracker 는 Play 시점에 코드로 생기는 오브젝트라
        /// 인스펙터로 미리 연결해 둘 수 없으므로 HandTracker.Instance 정적 참조로 찾는다.
        /// </summary>
        private void DrawDepthCalibrationSection(Rect panel)
        {
            GUI.Label(new Rect(panel.x + 28f, panel.y + 184f, 260f, 30f), "거리 보정 (기준 거리)",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));

            HandTracker tracker = HandTracker.Instance;
            float pivot = tracker != null ? tracker.DepthPivot : 0.8f;
            GUI.Label(new Rect(panel.x + 28f, panel.y + 214f, panel.width - 56f, 30f), $"{pivot:F2} m",
                MinimalGui.CenterLabel(20, MinimalGui.Ink, FontStyle.Bold));

            if (GUI.Button(new Rect(panel.x + 28f, panel.y + 254f, 84f, 52f), "-",
                           MinimalGui.SecondaryButton))
                tracker?.AdjustDepthPivot(-0.05f);

            if (GUI.Button(new Rect(panel.x + 124f, panel.y + 254f, 152f, 52f),
                           "지금 위치로", MinimalGui.PrimaryButton))
            {
                bool ok = tracker != null && tracker.CalibrateDepthToCurrent();
                _calibrateMessage = ok ? "보정 완료!" : "손이 안 보여요 — 카메라 앞에서 다시 시도하세요";
                _calibrateMessageUntil = Time.time + 2f;
            }

            if (GUI.Button(new Rect(panel.x + 288f, panel.y + 254f, 84f, 52f), "+",
                           MinimalGui.SecondaryButton))
                tracker?.AdjustDepthPivot(0.05f);

            GUI.Label(new Rect(panel.x + 28f, panel.y + 314f, panel.width - 56f, 26f),
                "손을 치기 편한 자리에 두고 버튼을 눌러 맞추세요",
                MinimalGui.CenterLabel(13, MinimalGui.Muted));

            if (!string.IsNullOrEmpty(_calibrateMessage) && Time.time < _calibrateMessageUntil)
            {
                GUI.Label(new Rect(panel.x + 28f, panel.y + 342f, panel.width - 56f, 26f),
                    _calibrateMessage, MinimalGui.CenterLabel(13, MinimalGui.Accent, FontStyle.Bold));
            }
        }
    }
}
