using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 배구공 서브·리셋·랠리 카운트.
    /// 공을 플레이어의 타격 존으로 정확히 보내기 위해 탄도를 역산한다.
    /// </summary>
    public class BallLauncher : MonoBehaviour
    {
        [Header("참조")]
        [SerializeField] private Rigidbody _ball;
        [SerializeField] private HandStriker _striker;

        [Header("서브")]
        [Tooltip("공이 출발하는 지점 (코트 반대편)")]
        [SerializeField] private Vector3 _serveOrigin = new Vector3(0f, 1.6f, 6.5f);
        [SerializeField] private float _serveOriginJitter = 1.2f;

        [Tooltip("플레이어가 받아야 할 지점 — 손이 닿는 높이에 두는 게 핵심")]
        [SerializeField] private Vector3 _targetPoint = new Vector3(0f, 1.35f, 0.9f);
        [SerializeField] private float _targetJitter = 0.35f;

        [Tooltip("서브가 목표점에 도달하기까지의 비행 시간. 짧을수록 어렵다.")]
        [SerializeField] private float _flightTime = 1.4f;

        [SerializeField] private float _serveDelay = 2.0f;

        [Header("판정 영역")]
        [Tooltip("공 중심이 이 높이 아래면 접지 상태로 본다.")]
        [SerializeField] private float _floorHeight = 0.35f;

        [Tooltip("완전 정지로 볼 속도 (m/s). 이보다 느려야 점수가 확정된다.")]
        [SerializeField] private float _restSpeed = 0.50f;

        [Tooltip("정지 상태가 이만큼 지속되면 확정 (초). 바운스 사이 순간 정지를 걸러낸다.")]
        [SerializeField] private float _restGrace = 0.7f;

        [SerializeField] private float _outOfBounds = 260f;
        [SerializeField] private float _behindPlayerZ = -4f;

        [Tooltip("공이 코트 가장자리를 벗어나 이 높이 아래로 떨어지면 즉시 종료한다. " +
                 "허공에서는 정지 판정이 영영 성립하지 않아 시간 상한까지 기다리게 되기 때문.")]
        [SerializeField] private float _voidY = -2.5f;

        [Tooltip("공이 움직이지 않는데 정지 판정도 안 될 때의 최종 안전망 (초). " +
                 "22 m/s 타구는 바운스와 구름까지 약 10초가 걸리므로 넉넉해야 한다.")]
        [SerializeField] private float _idleTimeout = 8f;

        [Tooltip("한 시도의 절대 상한 (초).")]
        [SerializeField] private float _maxFlightTime = 40f;

        [Header("점수")]
        [Tooltip("비거리 1m 당 점수")]
        [SerializeField] private int _pointsPerMeter = 10;

        [Tooltip("이 거리를 넘길 때마다 보너스 배율이 한 단계 올라간다 (m). " +
                 "코트가 120m 로 늘어났으므로 간격도 함께 넓혔다.")]
        [SerializeField] private float _bonusStep = 40f;

        [Tooltip("착지 지점에 세울 표식")]
        [SerializeField] private Transform _landingMarker;

        [Header("턴 진행 (시작 화면 → 플레이 → 결과)")]
        [Tooltip("한 턴(한 사람 차례)당 서브 횟수. GameFlowController.BeginTurn 으로 시작한다.")]
        [SerializeField] private int _throwsPerTurn = 5;

        [Header("UI")]
        [SerializeField] private bool _showHud = true;
        [Tooltip("우상단에 공의 높이·위치·속도를 표시. 판정이 이상할 때 원인 파악용.")]
        [SerializeField] private bool _showBallDebug = true;

        private float _nextServeTime;
        private bool _inPlay;
        private int _rally;   // 이번 시도에서 공을 몇 번 쳤는지 (실시간 비거리 표시 조건)

        /// <summary>공이 타격된 뒤 날아가는 중인지. 추적 카메라가 이 값을 본다.</summary>
        public bool BallInFlight => _inPlay && _rally > 0;
        private string _status = "준비";
        private float _lastActivityTime;
        private float _restTimer;
        private float _serveTime;
        private float _maxReached;
        private int _bounces;
        private float _prevY;
        private Vector3 _lastGroundedPoint;
        private float _lastDistance;
        private float _bestDistance;
        private int _totalScore;
        private int _lastPoints;
        private int _throws;
        private bool _newRecord;

        // --- 턴 진행 ---
        private bool _sessionActive;
        private int _turnThrows;
        private int _turnScore;
        private float _turnBestDistance;

        /// <summary>한 턴이 _throwsPerTurn 회 서브를 마치면 (이번 턴 총점, 최고 비거리) 로 발생.</summary>
        public event System.Action<int, float> OnTurnComplete;

        private void Start()
        {
            if (_ball == null)
            {
                Debug.LogError("[BallLauncher] 공 Rigidbody 가 연결되지 않았습니다.");
                enabled = false;
                return;
            }
            if (_striker != null) _striker.OnBallStruck += OnStruck;
            // 자동 서브는 GameFlowController 가 시작 화면에서 BeginTurn() 을 호출한 뒤에만 시작한다.
        }

        /// <summary>시작 화면의 "시작" 버튼에서 호출. 새 턴을 시작한다.</summary>
        public void BeginTurn()
        {
            _turnThrows = 0;
            _turnScore = 0;
            _turnBestDistance = 0f;
            _sessionActive = true;
            ScheduleServe(0.3f);
        }

        public void SetHudVisible(bool visible) => _showHud = visible;

        /// <summary>메인 메뉴에서 공을 코트 위에 가볍게 띄워 둔다. 게임 시작 시 BeginTurn 이 다시 서브 위치로 옮긴다.</summary>
        public void PrepareMenuScene()
        {
            if (_ball == null) return;
            _sessionActive = false;
            _inPlay = false;
            _rally = 0;
            BallPhysics.SetVelocity(_ball, Vector3.zero);
            _ball.angularVelocity = Vector3.zero;
            _ball.isKinematic = true;
            _ball.position = _targetPoint + new Vector3(0f, 0.75f, 1.65f);
            _status = "준비";
            if (_landingMarker != null) _landingMarker.gameObject.SetActive(false);
        }

        /// <summary>개발용 공 물리 진단 텍스트만 별도로 켜고 끈다.</summary>
        public void SetBallDebugVisible(bool visible) => _showBallDebug = visible;

        private void OnDestroy()
        {
            if (_striker != null) _striker.OnBallStruck -= OnStruck;
        }

        private void OnStruck(Rigidbody ball, Vector3 velocity)
        {
            if (!_inPlay) return;
            _rally++;
            _lastActivityTime = Time.time;
            _status = $"타격 {velocity.magnitude:F1} m/s";
        }

        private void Update()
        {
            if (!_inPlay)
            {
                if (_sessionActive && Time.time >= _nextServeTime) Serve();
                return;
            }

            Vector3 p = _ball.position;
            Vector3 v = BallPhysics.GetVelocity(_ball);
            float dt = Time.deltaTime;
            float speed = v.magnitude;

            _maxReached = Mathf.Max(_maxReached, p.z);

            // 바운스 횟수 (내려가다 올라가는 전환점을 센다)
            if (p.y < _floorHeight && v.y > 0.4f && _prevY <= 0f) _bounces++;
            _prevY = v.y;

            // 코트 가장자리를 벗어나 허공으로 떨어진 경우.
            // 바닥이 없으면 계속 낙하해 속도가 줄지 않으므로 정지 판정이 성립하지 않는다.
            // 마지막으로 지면 위에 있던 좌표를 기준으로 즉시 확정한다.
            if (p.y < _voidY)
            {
                Vector3 edge = _lastGroundedPoint.sqrMagnitude > 0f ? _lastGroundedPoint : p;
                Land(new Vector3(edge.x, 0f, edge.z), "장외 낙하");
                return;
            }
            if (p.y >= 0f) _lastGroundedPoint = p;

            // 뒤로 넘긴 경우만 즉시 0점
            if (p.z < _behindPlayerZ) { EndRally("뒤로 넘어감 — 0점"); return; }

            // 코트를 완전히 벗어나면 그 시점 위치로 확정
            if (Mathf.Abs(p.x) > _outOfBounds || p.z > _outOfBounds || p.y > _outOfBounds)
            {
                Land(p, "코트 밖");
                return;
            }

            // ---------------------------------------------------------------
            // 완전 정지 판정.
            // 튀고 구르는 것까지 비거리에 포함되므로, 첫 착지가 아니라
            // '더 이상 움직이지 않을 때' 를 기준으로 삼는다.
            // 22 m/s 타구는 바운스 30회 + 구름까지 약 10초가 걸린다.
            // ---------------------------------------------------------------
            bool atRest = speed < _restSpeed && p.y < _floorHeight;
            _restTimer = atRest ? _restTimer + dt : 0f;
            if (_restTimer > _restGrace) { Land(p, null); return; }

            // 움직이는 동안에는 안전망 타이머를 계속 뒤로 민다
            if (speed > _restSpeed) _lastActivityTime = Time.time;

            if (Time.time - _lastActivityTime > _idleTimeout) { Land(p, "정지"); return; }
            if (Time.time - _serveTime > _maxFlightTime)      { Land(p, "시간 상한"); return; }

            if (InputCompat.ResetPressed) EndRally("리셋");
        }

        /// <summary>착지 처리 — 비거리를 재고 점수를 매긴다.</summary>
        private void Land(Vector3 point, string note)
        {
            // 타격 기준선(z=0)에서 앞으로 나간 거리. 옆으로 벗어난 만큼은 점수에 넣지 않는다.
            float distance = Mathf.Max(0f, point.z);
            _lastDistance = distance;
            _throws++;

            // 10m 를 넘길 때마다 배율 한 단계 (10m=x1, 20m=x2, 30m=x3 ...)
            int tier = Mathf.Max(1, Mathf.FloorToInt(distance / _bonusStep));
            _lastPoints = Mathf.RoundToInt(distance * _pointsPerMeter * tier);
            _totalScore += _lastPoints;

            _newRecord = distance > _bestDistance;
            if (_newRecord) _bestDistance = distance;

            _turnScore += _lastPoints;
            _turnBestDistance = Mathf.Max(_turnBestDistance, distance);

            if (_landingMarker != null)
            {
                _landingMarker.position = new Vector3(point.x, 0f, point.z);
                _landingMarker.gameObject.SetActive(true);
            }

            string tierText = tier > 1 ? $"  x{tier} 보너스" : "";
            string record = _newRecord ? "   신기록!" : "";
            string noteText = string.IsNullOrEmpty(note) ? "" : $"  [{note}]";
            EndRally($"{distance:F1} m   +{_lastPoints}점{tierText}{record}{noteText}");
        }

        private void EndRally(string reason)
        {
            _inPlay = false;
            // Land() 는 이미 완성된 문구를 넘긴다. 그 외 사유는 문구 그대로 표시한다.
            _status = reason;

            // 랠리가 끝나는 모든 경로(착지/뒤로 넘어감/코트 밖/수동 리셋)가 여길 지나므로
            // 턴의 서브 소진 판정은 여기 한 곳에서만 한다.
            _turnThrows++;
            if (_sessionActive && _turnThrows >= _throwsPerTurn)
            {
                _sessionActive = false;
                OnTurnComplete?.Invoke(_turnScore, _turnBestDistance);
                return;
            }

            ScheduleServe(_serveDelay);
        }

        private void ScheduleServe(float delay)
        {
            _nextServeTime = Time.time + delay;
            BallPhysics.SetVelocity(_ball, Vector3.zero);
            _ball.angularVelocity = Vector3.zero;
            _ball.isKinematic = true;
            _ball.position = _serveOrigin;
        }

        private void Serve()
        {
            _rally = 0;
            _inPlay = true;
            _status = "서브!";
            _restTimer = 0f;
            _maxReached = 0f;
            _bounces = 0;
            _prevY = 0f;
            _lastGroundedPoint = Vector3.zero;
            _serveTime = Time.time;
            _lastActivityTime = Time.time;
            if (_landingMarker != null) _landingMarker.gameObject.SetActive(false);

            Vector3 from = _serveOrigin + new Vector3(
                Random.Range(-_serveOriginJitter, _serveOriginJitter), 0f, 0f);
            Vector3 to = _targetPoint + new Vector3(
                Random.Range(-_targetJitter, _targetJitter),
                Random.Range(-_targetJitter, _targetJitter) * 0.5f, 0f);

            _ball.isKinematic = false;
            _ball.position = from;
            _ball.angularVelocity = Vector3.zero;
            BallPhysics.SetVelocity(_ball, BallisticVelocity(from, to, _flightTime));
        }

        /// <summary>
        /// from 에서 출발해 정확히 t초 뒤 to 에 도달하는 초기 속도.
        ///   to = from + v·t + ½·g·t²   →   v = (to − from − ½·g·t²) / t
        /// </summary>
        private static Vector3 BallisticVelocity(Vector3 from, Vector3 to, float t)
        {
            t = Mathf.Max(t, 0.05f);
            Vector3 g = Physics.gravity;
            return (to - from - 0.5f * g * t * t) / t;
        }

        private void OnGUI()
        {
            if (!_showHud) return;

            Matrix4x4 old = MinimalGui.BeginScaled();
            try
            {
                // 플레이 중에는 꼭 필요한 값 3개만 상단 카드로 보여준다.
                DrawHudStat(new Rect(36, 34, 190, 96), "점수", _turnScore.ToString("N0"));
                DrawHudStat(new Rect(705, 34, 190, 96), "남은 서브",
                            Mathf.Max(0, _throwsPerTurn - _turnThrows).ToString());
                DrawHudStat(new Rect(1374, 34, 190, 96), "최고 비거리", $"{_turnBestDistance:F1} m");

                // 현재 상태는 작은 반투명 칩 하나로만 표시한다.
                GUI.Box(new Rect(610, 150, 380, 48), GUIContent.none, MinimalGui.DarkChip);
                GUI.Label(new Rect(628, 150, 344, 48), _status,
                    MinimalGui.CenterLabel(18, Color.white, FontStyle.Bold));

                // 공을 친 뒤에는 실시간 비거리만 한 줄 추가한다.
                if (_inPlay && _ball != null && _rally > 0)
                {
                    string extra = _bounces > 0 ? $"  ·  바운드 {_bounces}" : "";
                    GUI.Label(new Rect(610, 202, 380, 34),
                              $"{Mathf.Max(0f, _ball.position.z):F1} m{extra}",
                              MinimalGui.CenterLabel(20, Color.white, FontStyle.Bold));
                }

                // 디버그를 켠 경우에만 오른쪽 아래에 물리 정보가 나타난다.
                if (_showBallDebug && _ball != null)
                    DrawDebugPanel();
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        private void DrawHudStat(Rect rect, string label, string value)
        {
            MinimalGui.DrawCard(rect);
            GUI.Label(new Rect(rect.x + 16, rect.y + 12, rect.width - 32, 28), label,
                MinimalGui.CenterLabel(15, MinimalGui.Muted));
            GUI.Label(new Rect(rect.x + 12, rect.y + 40, rect.width - 24, 42), value,
                MinimalGui.CenterLabel(28, MinimalGui.Accent, FontStyle.Bold));
        }

        private void DrawDebugPanel()
        {
            Rect panel = new Rect(1240, 690, 324, 170);
            GUI.Box(panel, GUIContent.none, MinimalGui.DarkChip);

            Vector3 bp = _ball.position;
            Vector3 bv = BallPhysics.GetVelocity(_ball);
            string text =
                $"비거리 {Mathf.Max(0f, bp.z):F2} m   최고 {_maxReached:F2} m\n" +
                $"높이 {bp.y:F2} m   속도 {bv.magnitude:F1} m/s\n" +
                $"튕김 {_bounces}회   경과 {Time.time - _serveTime:F1}s\n" +
                $"직전 {_lastDistance:F1} m / +{_lastPoints}점";

            GUI.Label(new Rect(panel.x + 18, panel.y + 14, panel.width - 36, panel.height - 28),
                      text, MinimalGui.Label(14, new Color(1f, 1f, 1f, 0.82f), TextAnchor.UpperLeft));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(_targetPoint, _targetJitter);
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
            Gizmos.DrawLine(new Vector3(-6f, _floorHeight, 0f), new Vector3(6f, _floorHeight, 0f));
            Gizmos.color = Color.cyan;
            for (int i = 1; i <= 6; i++)
                Gizmos.DrawLine(new Vector3(-6f, 0f, i * 10f), new Vector3(6f, 0f, i * 10f));
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(_serveOrigin, 0.15f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(_serveOrigin, _targetPoint);
        }
    }
}
