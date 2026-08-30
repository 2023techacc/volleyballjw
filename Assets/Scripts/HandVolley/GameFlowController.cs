using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 시작 화면 → 플레이 → 결과 화면을 관리한다.
    /// UI는 별도 Canvas/이미지 에셋 없이 MinimalGui 를 사용해 심플한 카드형 IMGUI 로 그린다.
    /// </summary>
    public class GameFlowController : MonoBehaviour
    {
        private enum State { MainMenu, Playing, Result }

        [SerializeField] private BallLauncher _launcher;
        [SerializeField] private RankingStore _ranking;
        [SerializeField] private HandSizeController _handSize;
        [Tooltip("0 이면 결과 화면을 버튼을 누를 때까지 유지한다. 0보다 크면 해당 초 뒤 메인으로 돌아간다.")]
        [SerializeField] private float _resultHoldSeconds = 0f;
        [Tooltip("개발용 공/추적 진단 텍스트. 실제 게임 UI는 이 값과 무관하게 항상 보인다.")]
        [SerializeField] private bool _showDebugText = false;

        private State _state = State.MainMenu;
        private float _resultTimer;
        private int _lastTurnScore;
        private float _lastTurnBestDistance;
        private int _lastTurnRank;
        private string _lastTurnBucket;
        private bool _isPractice;
        private bool _showRankingPanel;
        private bool _showSettingsPanel;
        private bool _showHelpPanel;
        private string _calibrateMessage;
        private float _calibrateMessageUntil;

        private void Awake()
        {
            if (_launcher != null)
            {
                _launcher.OnTurnComplete += OnTurnComplete;
                _launcher.SetBallDebugVisible(_showDebugText);
            }
            EnterMainMenu();
        }

        private void OnDestroy()
        {
            if (_launcher != null) _launcher.OnTurnComplete -= OnTurnComplete;
        }

        private void Update()
        {
            if (_state != State.Result || _resultHoldSeconds <= 0f) return;

            _resultTimer -= Time.deltaTime;
            if (_resultTimer <= 0f) EnterMainMenu();
        }

        private void EnterMainMenu()
        {
            _state = State.MainMenu;
            _showRankingPanel = false;
            _showSettingsPanel = false;
            _showHelpPanel = false;
            if (_launcher != null)
            {
                _launcher.SetHudVisible(false);
                _launcher.PrepareMenuScene();
            }
        }

        /// <summary>연습(practice) 이면 결과가 랭킹에 기록되지 않는다 — 규칙 설명 패널의
        /// "연습해보기" 버튼과 결과 화면의 재시작 버튼이 이 값을 넘긴다.</summary>
        private void StartTurn(bool practice = false)
        {
            _isPractice = practice;
            _state = State.Playing;
            _showRankingPanel = false;
            _showSettingsPanel = false;
            _showHelpPanel = false;
            if (_launcher != null)
            {
                // 미니멀 플레이 HUD 는 항상 보이고, 진단 텍스트만 별도 옵션으로 켠다.
                _launcher.SetHudVisible(true);
                _launcher.SetBallDebugVisible(_showDebugText);
                _launcher.BeginTurn();
            }
        }

        private void OnTurnComplete(int score, float bestDistance)
        {
            _lastTurnScore = score;
            _lastTurnBestDistance = bestDistance;

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
            _state = State.Result;
        }

        private void OnGUI()
        {
            Matrix4x4 old = MinimalGui.BeginScaled();
            try
            {
                switch (_state)
                {
                    case State.MainMenu: DrawMainMenu(); break;
                    case State.Result: DrawResult(); break;
                    case State.Playing: break; // BallLauncher 의 미니멀 HUD가 담당
                }
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        private void DrawMainMenu()
        {
            // 레퍼런스처럼 화면 왼쪽에 제목 + 버튼만 두고 3D 코트/공/손은 그대로 보이게 한다.
            GUI.Label(new Rect(96, 94, 370, 74), "HAND",
                MinimalGui.Label(62, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(96, 158, 420, 82), "VOLLEY",
                MinimalGui.Label(68, MinimalGui.Accent, TextAnchor.MiddleLeft, FontStyle.Bold));
            GUI.Label(new Rect(100, 246, 420, 36), "손으로 공을 쳐보세요!",
                MinimalGui.Label(22, MinimalGui.Muted, TextAnchor.MiddleLeft));

            if (GUI.Button(new Rect(96, 332, 350, 82), "▶   게임 시작", MinimalGui.PrimaryButton))
                StartTurn();

            if (GUI.Button(new Rect(96, 428, 350, 60), "게임 방법", MinimalGui.SecondaryButton))
            {
                _showHelpPanel = !_showHelpPanel;
                _showRankingPanel = false;
                _showSettingsPanel = false;
            }

            if (GUI.Button(new Rect(96, 502, 350, 60), "랭킹", MinimalGui.SecondaryButton))
            {
                _showRankingPanel = !_showRankingPanel;
                _showSettingsPanel = false;
                _showHelpPanel = false;
            }

            if (GUI.Button(new Rect(96, 576, 350, 60), "설정", MinimalGui.SecondaryButton))
            {
                _showSettingsPanel = !_showSettingsPanel;
                _showRankingPanel = false;
                _showHelpPanel = false;
            }

            string handText = _handSize == null
                ? "손 크기 자동 보정"
                : $"손 크기 자동 보정  {_handSize.Scale:F2}x";
            GUI.Label(new Rect(102, 656, 340, 30), handText,
                MinimalGui.Label(16, new Color(0.15f, 0.23f, 0.34f, 0.68f), TextAnchor.MiddleLeft));

            if (_showHelpPanel) DrawHelpPanel();
            if (_showRankingPanel) DrawRankingPanel();
            if (_showSettingsPanel) DrawSettingsPanel();
        }

        private void DrawHelpPanel()
        {
            Rect panel = new Rect(1125, 110, 380, 468);
            MinimalGui.DrawCard(panel);

            GUI.Label(new Rect(panel.x + 28, panel.y + 24, panel.width - 56, 42), "게임 방법",
                MinimalGui.Label(26, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));

            string[] rules =
            {
                "공이 날아오면 손으로 쳐서 넘기세요",
                "최대한 멀리 보낼수록 점수가 높아요",
                "한 턴에 5번의 기회가 주어져요",
                "공을 뒤로 넘기면 그 시도는 0점이에요",
                "손이 잘 안 맞으면 설정에서 거리를 보정해보세요",
            };

            float y = panel.y + 88;
            foreach (string rule in rules)
            {
                GUI.Label(new Rect(panel.x + 28, y, 24, 44), "•",
                    MinimalGui.Label(20, MinimalGui.Accent, TextAnchor.UpperLeft, FontStyle.Bold));
                GUI.Label(new Rect(panel.x + 52, y, panel.width - 80, 44), rule,
                    MinimalGui.Label(16, MinimalGui.Ink, TextAnchor.UpperLeft));
                y += 54;
            }

            if (GUI.Button(new Rect(panel.x + 28, panel.y + 388, panel.width - 56, 64),
                           "▶  연습해보기", MinimalGui.PrimaryButton))
                StartTurn(practice: true);
        }

        private void DrawRankingPanel()
        {
            Rect panel = new Rect(1125, 110, 380, 468);
            MinimalGui.DrawCard(panel);

            GUI.Label(new Rect(panel.x + 28, panel.y + 24, panel.width - 56, 42), "랭킹",
                MinimalGui.Label(26, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));

            if (_ranking == null)
            {
                GUI.Label(new Rect(panel.x + 28, panel.y + 92, panel.width - 56, 40), "랭킹 정보를 불러올 수 없습니다.",
                    MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));
                return;
            }

            string bucket = _ranking.GetCurrentBucketName();
            var entries = _ranking.GetTop(bucket, 5);
            GUI.Label(new Rect(panel.x + 28, panel.y + 70, panel.width - 56, 28), $"{bucket} 시간대",
                MinimalGui.Label(15, MinimalGui.Muted, TextAnchor.MiddleLeft));

            if (entries.Count == 0)
            {
                GUI.Label(new Rect(panel.x + 28, panel.y + 150, panel.width - 56, 36), "아직 기록이 없습니다.",
                    MinimalGui.CenterLabel(18, MinimalGui.Muted));
                return;
            }

            float y = panel.y + 118;
            for (int i = 0; i < entries.Count; i++)
            {
                Color rowColor = i == 0 ? MinimalGui.Accent : MinimalGui.Ink;
                GUI.Label(new Rect(panel.x + 28, y, 56, 46), $"{i + 1}",
                    MinimalGui.Label(20, rowColor, TextAnchor.MiddleLeft, FontStyle.Bold));
                GUI.Label(new Rect(panel.x + 84, y, 145, 46), $"{entries[i].score:N0}점",
                    MinimalGui.Label(19, MinimalGui.Ink, TextAnchor.MiddleLeft));
                GUI.Label(new Rect(panel.x + 225, y, 120, 46), $"{entries[i].bestDistance:F1} m",
                    MinimalGui.Label(18, MinimalGui.Accent, TextAnchor.MiddleRight, FontStyle.Bold));
                y += 58;
            }
        }

        private void DrawSettingsPanel()
        {
            Rect panel = new Rect(1125, 110, 380, 452);
            MinimalGui.DrawCard(panel);

            GUI.Label(new Rect(panel.x + 28, panel.y + 24, panel.width - 56, 42), "설정",
                MinimalGui.Label(26, MinimalGui.Ink, TextAnchor.MiddleLeft, FontStyle.Bold));

            GUI.Label(new Rect(panel.x + 28, panel.y + 88, 210, 34), "손 크기",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(panel.x + 230, panel.y + 88, 110, 34),
                _handSize == null ? "자동" : $"{_handSize.Scale:F2}x",
                MinimalGui.Label(18, MinimalGui.Ink, TextAnchor.MiddleRight, FontStyle.Bold));

            GUI.Label(new Rect(panel.x + 28, panel.y + 136, 210, 34), "손 회전 보정",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(panel.x + 230, panel.y + 136, 110, 34), "자동",
                MinimalGui.Label(18, MinimalGui.Accent, TextAnchor.MiddleRight, FontStyle.Bold));

            DrawDepthCalibrationSection(panel);

            string debugText = _showDebugText ? "디버그 정보  ON" : "디버그 정보  OFF";
            if (GUI.Button(new Rect(panel.x + 28, panel.y + 392, panel.width - 56, 40),
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
            GUI.Label(new Rect(panel.x + 28, panel.y + 184, 210, 30), "거리 보정 (기준 거리)",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.MiddleLeft));

            HandTracker tracker = HandTracker.Instance;
            float pivot = tracker != null ? tracker.DepthPivot : 0.8f;
            GUI.Label(new Rect(panel.x + 28, panel.y + 214, panel.width - 56, 30), $"{pivot:F2} m",
                MinimalGui.CenterLabel(20, MinimalGui.Ink, FontStyle.Bold));

            if (GUI.Button(new Rect(panel.x + 28, panel.y + 254, 80, 52), "-", MinimalGui.SecondaryButton))
                tracker?.AdjustDepthPivot(-0.05f);

            if (GUI.Button(new Rect(panel.x + 116, panel.y + 254, 148, 52),
                           "지금 위치로", MinimalGui.PrimaryButton))
            {
                bool ok = tracker != null && tracker.CalibrateDepthToCurrent();
                _calibrateMessage = ok ? "보정 완료!" : "손이 안 보여요 — 카메라 앞에서 다시 시도하세요";
                _calibrateMessageUntil = Time.time + 2f;
            }

            if (GUI.Button(new Rect(panel.x + 272, panel.y + 254, 80, 52), "+", MinimalGui.SecondaryButton))
                tracker?.AdjustDepthPivot(0.05f);

            GUI.Label(new Rect(panel.x + 28, panel.y + 312, panel.width - 56, 26),
                "손을 치기 편한 자리에 두고 버튼을 눌러 맞추세요",
                MinimalGui.CenterLabel(13, MinimalGui.Muted));

            if (!string.IsNullOrEmpty(_calibrateMessage) && Time.time < _calibrateMessageUntil)
            {
                GUI.Label(new Rect(panel.x + 28, panel.y + 340, panel.width - 56, 26), _calibrateMessage,
                    MinimalGui.CenterLabel(13, MinimalGui.Accent, FontStyle.Bold));
            }
        }

        private void DrawResult()
        {
            // 결과 화면은 밝은 오버레이 + 두 개 카드만 남겨 레퍼런스처럼 간단하게 구성한다.
            GUI.DrawTexture(new Rect(0, 0, MinimalGui.ReferenceWidth, MinimalGui.ReferenceHeight),
                            MinimalGui.OverlayTexture, ScaleMode.StretchToFill, true);

            Rect result = new Rect(350, 170, 430, 540);
            Rect ranking = new Rect(820, 170, 430, 540);
            MinimalGui.DrawCard(result);
            MinimalGui.DrawCard(ranking);

            GUI.Label(new Rect(result.x + 40, result.y + 44, result.width - 80, 46),
                _isPractice ? "연습 완료!" : "GAME OVER",
                MinimalGui.CenterLabel(28, MinimalGui.Ink, FontStyle.Bold));
            GUI.Label(new Rect(result.x + 40, result.y + 106, result.width - 80, 34), "SCORE",
                MinimalGui.CenterLabel(16, MinimalGui.Muted));
            GUI.Label(new Rect(result.x + 30, result.y + 138, result.width - 60, 108), _lastTurnScore.ToString("N0"),
                MinimalGui.CenterLabel(72, MinimalGui.Accent, FontStyle.Bold));

            GUI.Label(new Rect(result.x + 64, result.y + 250, 150, 32), "최고 비거리",
                MinimalGui.Label(16, MinimalGui.Muted, TextAnchor.MiddleLeft));
            GUI.Label(new Rect(result.x + 214, result.y + 250, 150, 32), $"{_lastTurnBestDistance:F1} m",
                MinimalGui.Label(19, MinimalGui.Ink, TextAnchor.MiddleRight, FontStyle.Bold));

            string rankText = _isPractice
                ? "연습 기록은 랭킹에 남지 않아요"
                : string.IsNullOrEmpty(_lastTurnBucket)
                    ? ""
                    : $"{_lastTurnBucket} 시간대  {_lastTurnRank}위";
            GUI.Label(new Rect(result.x + 50, result.y + 292, result.width - 100, 34), rankText,
                MinimalGui.CenterLabel(17, MinimalGui.Muted));

            if (_isPractice)
            {
                if (GUI.Button(new Rect(result.x + 72, result.y + 362, result.width - 144, 66),
                               "▶  실전 시작", MinimalGui.PrimaryButton))
                    StartTurn();

                if (GUI.Button(new Rect(result.x + 72, result.y + 444, result.width - 144, 58),
                               "↻  한 번 더 연습", MinimalGui.SecondaryButton))
                    StartTurn(practice: true);
            }
            else
            {
                if (GUI.Button(new Rect(result.x + 72, result.y + 362, result.width - 144, 66),
                               "↻  다시 하기", MinimalGui.PrimaryButton))
                    StartTurn();

                if (GUI.Button(new Rect(result.x + 72, result.y + 444, result.width - 144, 58),
                               "메인 메뉴", MinimalGui.SecondaryButton))
                    EnterMainMenu();
            }

            if (_resultHoldSeconds > 0f)
            {
                GUI.Label(new Rect(result.x + 50, result.y + 505, result.width - 100, 24),
                          $"{Mathf.CeilToInt(_resultTimer)}초 뒤 메인으로 이동",
                          MinimalGui.CenterLabel(13, MinimalGui.Muted));
            }

            if (_isPractice) DrawPracticeTip(ranking); else DrawResultRanking(ranking);
        }

        private void DrawPracticeTip(Rect panel)
        {
            GUI.Label(new Rect(panel.x + 34, panel.y + 34, panel.width - 68, 44), "TIP",
                MinimalGui.CenterLabel(25, MinimalGui.Ink, FontStyle.Bold));

            GUI.Label(new Rect(panel.x + 40, panel.y + 120, panel.width - 80, 160),
                "지금은 연습이라 점수가 기록되지 않아요.\n\n" +
                "감이 잡혔으면 메인 메뉴의 [게임 시작]을 눌러 실전으로 넘어가세요.",
                MinimalGui.Label(17, MinimalGui.Muted, TextAnchor.UpperLeft));
        }

        private void DrawResultRanking(Rect panel)
        {
            GUI.Label(new Rect(panel.x + 34, panel.y + 34, panel.width - 68, 44), "RANKING",
                MinimalGui.CenterLabel(25, MinimalGui.Ink, FontStyle.Bold));

            if (_ranking == null)
            {
                GUI.Label(new Rect(panel.x + 40, panel.y + 120, panel.width - 80, 40), "기록 없음",
                    MinimalGui.CenterLabel(18, MinimalGui.Muted));
                return;
            }

            string bucket = string.IsNullOrEmpty(_lastTurnBucket)
                ? _ranking.GetCurrentBucketName()
                : _lastTurnBucket;
            var entries = _ranking.GetTop(bucket, 5);
            float y = panel.y + 102;

            for (int i = 0; i < entries.Count; i++)
            {
                bool mine = entries[i].score == _lastTurnScore &&
                            Mathf.Abs(entries[i].bestDistance - _lastTurnBestDistance) < 0.01f;
                Color c = mine ? MinimalGui.Accent : MinimalGui.Ink;

                GUI.Label(new Rect(panel.x + 42, y, 50, 48), $"{i + 1}",
                    MinimalGui.Label(19, c, TextAnchor.MiddleLeft, FontStyle.Bold));
                GUI.Label(new Rect(panel.x + 96, y, 170, 48), mine ? "이번 기록" : "기록",
                    MinimalGui.Label(18, c, TextAnchor.MiddleLeft, mine ? FontStyle.Bold : FontStyle.Normal));
                GUI.Label(new Rect(panel.x + 265, y, 120, 48), entries[i].score.ToString("N0"),
                    MinimalGui.Label(19, c, TextAnchor.MiddleRight, FontStyle.Bold));
                y += 66;
            }

            if (_lastTurnRank > 5)
            {
                GUI.Label(new Rect(panel.x + 42, panel.y + 452, panel.width - 84, 38),
                          $"내 기록   {_lastTurnRank}위   {_lastTurnScore:N0}점",
                          MinimalGui.CenterLabel(17, MinimalGui.Accent, FontStyle.Bold));
            }
        }
    }
}
