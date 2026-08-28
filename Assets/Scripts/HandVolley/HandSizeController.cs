using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 손 크기 조절. 플레이어마다 실제 손 크기가 다르고, 이 파이프라인은 손 위치만
    /// 복원할 뿐 개인별 손 크기를 자동으로 보정하지 않으므로(README 참고), 시작 화면에서
    /// 수동으로 맞출 수 있게 한다.
    ///
    /// Hand 루트(팜/손가락/엄지/히트박스가 전부 이 루트의 상대 자식)의 localScale 만
    /// 바꾼다. HandStriker 의 SweepTest/충돌 판정은 실제 콜라이더 형상을 그대로 읽으므로
    /// (캐시된 월드 크기 없음) 이 방식이 시각+판정 크기를 함께, 안전하게 바꾼다.
    /// 손 위치 추적(HandTracker/HandPoseSolver 의 깊이 계산)에는 전혀 관여하지 않는다.
    /// </summary>
    public class HandSizeController : MonoBehaviour
    {
        private const string PrefsKey = "HandVolley_HandSize";

        [SerializeField] private float _min = 0.7f;
        [SerializeField] private float _max = 1.5f;

        public float Min => _min;
        public float Max => _max;

        public float Scale
        {
            get => transform.localScale.x;
            set
            {
                float clamped = Mathf.Clamp(value, _min, _max);
                transform.localScale = Vector3.one * clamped;
                PlayerPrefs.SetFloat(PrefsKey, clamped);
            }
        }

        private void Awake()
        {
            float saved = PlayerPrefs.GetFloat(PrefsKey, 1f);
            Scale = saved;
        }

        /// <summary>
        /// HandTracker 가 실측 손 크기로 매 관측마다 호출한다. Scale 프로퍼티와 달리
        /// PlayerPrefs 에 저장하지 않는다 — 이건 "플레이어가 고른 값"이 아니라
        /// 매 프레임 갱신되는 실측값이라, 매번 디스크에 쓸 이유가 없다.
        /// </summary>
        public void ApplyDetectedScale(float rawValue)
        {
            float clamped = Mathf.Clamp(rawValue, _min, _max);
            transform.localScale = Vector3.one * clamped;
        }
    }
}
