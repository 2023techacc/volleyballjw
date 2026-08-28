using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 1€ Filter (Casiez et al., 2012).
    /// 손이 멈춰 있으면 강하게, 빠르게 움직이면 약하게 필터링해
    /// 지터와 지연을 동시에 억제한다. 파라미터가 2개뿐이라 칼만보다 튜닝이 쉽다.
    ///
    ///   minCutoff ↓  → 정지 시 안정성 ↑, 지연 ↑
    ///   beta      ↑  → 빠른 동작에서 지연 ↓, 떨림 ↑
    /// </summary>
    public class OneEuroFilter
    {
        private readonly float _minCutoff;
        private readonly float _beta;
        private readonly float _dCutoff;

        private float _xPrev;
        private float _dxPrev;
        private bool _initialized;

        public OneEuroFilter(float minCutoff = 1.0f, float beta = 0.02f, float dCutoff = 1.0f)
        {
            _minCutoff = Mathf.Max(minCutoff, 1e-3f);
            _beta = beta;
            _dCutoff = Mathf.Max(dCutoff, 1e-3f);
        }

        private static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        public float Filter(float x, float dt)
        {
            if (dt <= 0f) return _initialized ? _xPrev : x;

            if (!_initialized)
            {
                _xPrev = x;
                _dxPrev = 0f;
                _initialized = true;
                return x;
            }

            float dx = (x - _xPrev) / dt;
            float dxHat = _dxPrev + Alpha(_dCutoff, dt) * (dx - _dxPrev);
            float cutoff = _minCutoff + _beta * Mathf.Abs(dxHat);
            float xHat = _xPrev + Alpha(cutoff, dt) * (x - _xPrev);

            _xPrev = xHat;
            _dxPrev = dxHat;
            return xHat;
        }

        /// <summary>필터가 추정 중인 속도. 물리 계산에 재사용하면 노이즈가 크게 준다.</summary>
        public float Derivative => _dxPrev;

        public void Reset() => _initialized = false;
    }

    /// <summary>
    /// X/Y 와 Z 를 서로 다른 세기로 거르는 Vector3 래퍼.
    /// 단일 카메라에서 Z 노이즈는 X/Y 의 5~10배이므로 반드시 분리해야 한다.
    /// </summary>
    public class OneEuroFilterVector3
    {
        private readonly OneEuroFilter _x, _y, _z;

        public OneEuroFilterVector3(float minCutoff, float beta, float zMinCutoff, float zBeta)
        {
            _x = new OneEuroFilter(minCutoff, beta);
            _y = new OneEuroFilter(minCutoff, beta);
            _z = new OneEuroFilter(zMinCutoff, zBeta);
        }

        public Vector3 Filter(Vector3 v, float dt) => new Vector3(
            _x.Filter(v.x, dt),
            _y.Filter(v.y, dt),
            _z.Filter(v.z, dt));

        /// <summary>필터 내부 속도 추정값 (m/s). 손 타격 속도로 그대로 쓸 수 있다.</summary>
        public Vector3 Velocity => new Vector3(_x.Derivative, _y.Derivative, _z.Derivative);

        public void Reset() { _x.Reset(); _y.Reset(); _z.Reset(); }
    }
}
