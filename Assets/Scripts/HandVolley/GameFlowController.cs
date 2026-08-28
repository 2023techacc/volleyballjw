using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 여러 명이 번갈아 하는 흐름: 시작 화면(손 크기 조절 + 시작 버튼) → 플레이(BallLauncher 가
    /// 기존 서브/랠리 루프를 진행) → 결과 화면(잠시 표시) → 다시 시작 화면.
    ///
    /// 시작 버튼은 2D 화면 버튼이다 — 실제 카메라로 손을 추적하는 플레이 중에는
    /// 마우스 커서가 없을 수 있으나, 버튼을 누르는 시점은 아직 플레이 시작 전이라
    /// 마우스/탭 클릭으로 충분하다는 전제다.
    /// </summary>
    public class GameFlowController : MonoBehaviour
    {
        private enum State { MainMenu, Playing, Result }

        [SerializeField] private BallLauncher _launcher;
        [SerializeField] private RankingStore _ranking;
        [SerializeField] private HandSizeController _handSize;
        [SerializeField] private float _resultHoldSeconds = 4f;
        [Tooltip("꺼두면 플레이 중에도 BallLauncher 의 점수 HUD 가 계속 숨겨진다. " +
                 "시작/결과 화면 자체(시작 버튼, 결과 요약)는 이 설정과 무관하게 항상 보인다.")]
        [SerializeField] private bool _showDebugText = true;

        private State _state = State.MainMenu;
        private float _resultTimer;
        private int _lastTurnScore;
        private float _lastTurnBestDistance;
        private int _lastTurnRank;
        private string _lastTurnBucket;

        private void Awake()
        {
            if (_launcher != null) _launcher.OnTurnComplete += OnTurnComplete;
            EnterMainMenu();
        }

        private void OnDestroy()
        {
            if (_launcher != null) _launcher.OnTurnComplete -= OnTurnComplete;
        }

        private void Update()
        {
            if (_state != State.Result) return;

            _resultTimer -= Time.deltaTime;
            if (_resultTimer <= 0f) EnterMainMenu();
        }

        private void EnterMainMenu()
        {
            _state = State.MainMenu;
            if (_launcher != null) _launcher.SetHudVisible(false);
        }

        private void StartTurn()
        {
            _state = State.Playing;
            if (_launcher != null)
            {
                _launcher.SetHudVisible(_showDebugText);
                _launcher.BeginTurn();
            }
        }

        private void OnTurnComplete(int score, float bestDistance)
        {
            _lastTurnScore = score;
            _lastTurnBestDistance = bestDistance;

            if (_ranking != null)
            {
                _lastTurnBucket = _ranking.GetCurrentBucketName();
                _ranking.AddEntry(score, bestDistance);
                _lastTurnRank = _ranking.GetRank(_lastTurnBucket, score);
            }

            if (_launcher != null) _launcher.SetHudVisible(false);
            _resultTimer = _resultHoldSeconds;
            _state = State.Result;
        }

        private void OnGUI()
        {
            switch (_state)
            {
                case State.MainMenu: DrawMainMenu(); break;
                case State.Result: DrawResult(); break;
                case State.Playing: break;   // BallLauncher 자체 HUD 가 담당
            }
        }

        private void DrawMainMenu()
        {
            float cx = Screen.width * 0.5f;

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 34,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            GUI.Label(new Rect(cx - 220, Screen.height * 0.18f, 440, 60), "HandVolley", title);

            if (_handSize != null)
            {
                // 손 크기는 이제 슬라이더로 고르지 않고 HandTracker 가 실측 손 크기로
                // 매 관측마다 자동으로 맞춘다 (HandTracker.ApplyDetectedHandSize 참고).
                // 여기서는 참고용으로 현재 값만 보여준다.
                var label = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.85f) },
                };
                GUI.Label(new Rect(cx - 150, Screen.height * 0.42f, 300, 24),
                          $"손 크기 (실측) {_handSize.Scale:F2}x", label);
            }

            DrawRankingPreview(cx, Screen.height * 0.56f);

            var buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 24 };
            if (GUI.Button(new Rect(cx - 90, Screen.height * 0.74f, 180, 56), "시작", buttonStyle))
            {
                StartTurn();
            }
        }

        private void DrawRankingPreview(float cx, float top)
        {
            if (_ranking == null) return;

            string bucket = _ranking.GetCurrentBucketName();
            var entries = _ranking.GetTop(bucket, 3);

            var header = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) },
            };
            GUI.Label(new Rect(cx - 150, top, 300, 22), $"[{bucket}] 순위 Top 3", header);

            var row = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.8f) },
            };

            if (entries.Count == 0)
            {
                GUI.Label(new Rect(cx - 150, top + 24, 300, 20), "아직 기록이 없습니다", row);
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                GUI.Label(new Rect(cx - 150, top + 24 + i * 20, 300, 20),
                          $"{i + 1}위   {entries[i].score:N0}점   {entries[i].bestDistance:F1}m", row);
            }
        }

        private void DrawResult()
        {
            float cx = Screen.width * 0.5f;

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.85f, 0.2f) },
            };
            GUI.Label(new Rect(cx - 220, Screen.height * 0.32f, 440, 50), "이번 턴 결과", title);

            var body = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            string bucketText = string.IsNullOrEmpty(_lastTurnBucket) ? "" :
                $"[{_lastTurnBucket}] {_lastTurnRank}위";
            GUI.Label(new Rect(cx - 220, Screen.height * 0.42f, 440, 100),
                      $"총점 {_lastTurnScore:N0}점\n" +
                      $"최고 비거리 {_lastTurnBestDistance:F1} m\n" +
                      bucketText, body);

            var small = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.6f) },
            };
            GUI.Label(new Rect(cx - 220, Screen.height * 0.6f, 440, 24),
                      $"{Mathf.CeilToInt(_resultTimer)}초 뒤 시작 화면으로 돌아갑니다", small);
        }
    }
}
