using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 기존 비거리 모드와 별개로 도는 간단한 AI 1대1 랠리 모드.
    /// AI가 서브하면 플레이어가 받아 넘기고, 공이 AI 코트에 도달하면 AI가 반응 시간 뒤
    /// 받아친다. 바닥에 먼저 닿은 쪽의 반대편이 득점하며, 정해진 점수를 먼저 내면
    /// 매치가 끝난다.
    ///
    /// AI 손은 콜라이더 없는 시각 피드백용 Transform이다 — 실제 타격 판정은 필요 없고
    /// (AI는 코드로 직접 공 속도를 바꾼다), 공을 눈으로 따라가는 느낌만 주면 된다.
    /// </summary>
    public class AiVolleyController : MonoBehaviour
    {
        private enum State { Idle, BallTowardPlayer, BallTowardAi, AiReacting }

        [Header("참조")]
        [SerializeField] private Rigidbody _ball;
        [SerializeField] private HandStriker _playerHand;
        [SerializeField] private Transform _aiHand;

        [Header("코트")]
        [Tooltip("네트 위치 (z). 이 값을 기준으로 어느 쪽 바닥에 떨어졌는지 판정한다.")]
        [SerializeField] private float _netZ = 4.5f;
        [Tooltip("공이 이 z 를 넘으면 AI 코트에 도달한 것으로 보고 반응 시간을 예약한다.")]
        [SerializeField] private float _aiHitZ = 6.1f;
        [SerializeField] private float _courtHalfWidth = 5.5f;
        [SerializeField] private float _ballRadius = 0.105f;
        [SerializeField] private int _pointsToWin = 5;

        [Header("AI 반응")]
        [Tooltip("공이 AiHitZ 에 도달한 뒤 AI 가 받아치기까지 걸리는 시간 범위 (초).")]
        [SerializeField] private Vector2 _aiReactionRange = new Vector2(0.10f, 0.26f);
        [Tooltip("AI 리턴/서브가 목표 지점까지 도달하는 데 걸리는 시간 범위 (초). " +
                 "짧을수록 빠르고 어려운 공이 된다.")]
        [SerializeField] private Vector2 _returnFlightTime = new Vector2(0.95f, 1.18f);

        [Header("목표 지점 (플레이어 타격 존)")]
        [SerializeField] private Vector3 _targetMin = new Vector3(-0.75f, 1.15f, 0.65f);
        [SerializeField] private Vector3 _targetMax = new Vector3(0.75f, 1.65f, 1.15f);
        [SerializeField] private float _serveDelay = 1.2f;

        [Header("표시")]
        [SerializeField] private bool _showHud = true;

        private State _state = State.Idle;
        private float _aiReactionDeadline;
        private bool _pointResolved;
        private bool _waitingToServe;
        private float _nextServeTime;

        private int _playerScore;
        private int _aiScore;
        private string _statusText = "";
        private Vector3 _aiHandVelocity;

        public bool IsPlaying { get; private set; }
        public event System.Action<int, int> OnMatchComplete;

        private void Awake()
        {
            if (_playerHand != null) _playerHand.OnBallStruck += OnPlayerStruck;
        }

        private void OnDestroy()
        {
            if (_playerHand != null) _playerHand.OnBallStruck -= OnPlayerStruck;
        }

        /// <summary>GameFlowController 가 AI 모드 버튼에서 호출한다.</summary>
        public void BeginMatch()
        {
            _playerScore = 0;
            _aiScore = 0;
            IsPlaying = true;
            if (_aiHand != null) _aiHand.gameObject.SetActive(true);
            ScheduleServe(0.5f);
        }

        /// <summary>매치 도중 메인 메뉴로 돌아가거나 다른 모드로 전환할 때 호출한다.</summary>
        public void StopMatch()
        {
            IsPlaying = false;
            _waitingToServe = false;
            _state = State.Idle;
            if (_aiHand != null) _aiHand.gameObject.SetActive(false);
            if (_ball != null)
            {
                _ball.isKinematic = true;
                BallPhysics.SetVelocity(_ball, Vector3.zero);
                _ball.angularVelocity = Vector3.zero;
            }
        }

        private void ScheduleServe(float delay)
        {
            _waitingToServe = true;
            _nextServeTime = Time.time + delay;
            _statusText = "서브 대기...";
            if (_ball != null)
            {
                _ball.isKinematic = true;
                BallPhysics.SetVelocity(_ball, Vector3.zero);
                _ball.angularVelocity = Vector3.zero;
                _ball.position = new Vector3(0f, 1.7f, _aiHitZ);
            }
        }

        private void Update()
        {
            if (!IsPlaying || _ball == null) return;

            UpdateAiHandVisual();

            if (_waitingToServe)
            {
                if (Time.time >= _nextServeTime) Serve();
                return;
            }

            CheckBallState();

            if (_state == State.AiReacting && Time.time >= _aiReactionDeadline)
            {
                TryAiReturn();
            }
        }

        private void UpdateAiHandVisual()
        {
            if (_aiHand == null || _ball == null) return;
            Vector3 target = new Vector3(
                Mathf.Clamp(_ball.position.x, -_courtHalfWidth, _courtHalfWidth),
                Mathf.Clamp(_ball.position.y, 1.0f, 2.2f),
                _aiHand.position.z);
            _aiHand.position = Vector3.SmoothDamp(_aiHand.position, target, ref _aiHandVelocity, 0.12f);
        }

        private void Serve()
        {
            _waitingToServe = false;
            _pointResolved = false;
            _state = State.BallTowardPlayer;
            _statusText = "AI 서브";

            Vector3 from = new Vector3(0f, 1.7f, _aiHitZ);
            Vector3 to = RandomTarget();
            float t = Random.Range(_returnFlightTime.x, _returnFlightTime.y);

            _ball.isKinematic = false;
            _ball.position = from;
            _ball.angularVelocity = Vector3.zero;
            BallPhysics.SetVelocity(_ball, BallisticVelocity(from, to, t));
        }

        private Vector3 RandomTarget() => new Vector3(
            Random.Range(_targetMin.x, _targetMax.x),
            Random.Range(_targetMin.y, _targetMax.y),
            Random.Range(_targetMin.z, _targetMax.z));

        private void OnPlayerStruck(Rigidbody ball, Vector3 velocity)
        {
            if (!IsPlaying || _pointResolved) return;
            _state = State.BallTowardAi;
            _statusText = "플레이어 타격";
        }

        private void CheckBallState()
        {
            if (_pointResolved) return;

            Vector3 p = _ball.position;

            // 코트 바깥/뒤쪽 경계 — 위치 기준으로 어느 편의 실책인지 정해 반대편에 점수를 준다.
            if (Mathf.Abs(p.x) > _courtHalfWidth || p.z < -1.0f || p.z > _aiHitZ + 2.5f || p.y < -3f)
            {
                ResolvePoint(landedOnPlayerSide: p.z < _netZ);
                return;
            }

            // 바닥 접촉 — 처음 닿은 쪽의 반대편이 득점한다.
            if (p.y <= _ballRadius + 0.02f)
            {
                ResolvePoint(landedOnPlayerSide: p.z < _netZ);
                return;
            }

            if (_state == State.BallTowardAi && p.z >= _aiHitZ)
            {
                _state = State.AiReacting;
                _aiReactionDeadline = Time.time + Random.Range(_aiReactionRange.x, _aiReactionRange.y);
                _statusText = "AI 반응 중...";
            }
        }

        /// <summary>landedOnPlayerSide 가 true 면 플레이어 쪽 바닥에 떨어진 것 — AI 득점.</summary>
        private void ResolvePoint(bool landedOnPlayerSide)
        {
            _pointResolved = true;
            if (landedOnPlayerSide) _aiScore++; else _playerScore++;
            _statusText = landedOnPlayerSide ? "AI 득점" : "PLAYER 득점";

            if (_playerScore >= _pointsToWin || _aiScore >= _pointsToWin)
            {
                IsPlaying = false;
                _state = State.Idle;
                OnMatchComplete?.Invoke(_playerScore, _aiScore);
                return;
            }

            ScheduleServe(_serveDelay);
        }

        private void TryAiReturn()
        {
            // 반응 시간이 지나는 동안 공이 이미 바닥에 닿았거나 AI 코트를 벗어났으면
            // CheckBallState 가 먼저 처리했을 것이다. 그래도 못 칠 상황이면 다음
            // 프레임에 다시 판단한다 (상태를 바꾸지 않고 그냥 기다린다).
            if (_ball.position.z <= _netZ || _ball.position.y < _ballRadius + 0.05f) return;

            Vector3 from = _ball.position;
            Vector3 to = RandomTarget();
            float t = Random.Range(_returnFlightTime.x, _returnFlightTime.y);

            BallPhysics.SetVelocity(_ball, BallisticVelocity(from, to, t));
            _ball.angularVelocity = Vector3.zero;
            _state = State.BallTowardPlayer;
            _statusText = "AI 리턴";
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
            if (!_showHud || !IsPlaying) return;

            var big = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                alignment = TextAnchor.UpperCenter,
                normal = { textColor = Color.white },
            };
            GUI.Label(new Rect(0, 14, Screen.width, 40), $"PLAYER {_playerScore} : {_aiScore} AI", big);

            var small = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                alignment = TextAnchor.UpperCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };
            GUI.Label(new Rect(0, 52, Screen.width, 28), _statusText, small);
        }
    }
}
