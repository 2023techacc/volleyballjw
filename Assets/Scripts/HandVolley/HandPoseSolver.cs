using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// MediaPipe 가 주는 두 좌표계를 합쳐 손의 '절대 3D 위치'를 복원한다.
    ///
    ///   handWorldLandmarks : 미터 단위지만 원점이 손 중심 → 모양만 있고 위치가 없음
    ///   handLandmarks      : 위치는 있지만 2D 이미지 좌표 → 깊이가 없음
    ///
    /// 두 집합을 재투영 오차 최소화로 정합하면 평행이동 t(3자유도)가 나온다.
    /// 손이 회전해도 21점 전체를 쓰므로, 두 랜드마크 사이 거리로 크기를 재는
    /// 흔한 휴리스틱보다 훨씬 안정적이다.
    /// (합성 데이터 검증: 2점 방식 깊이오차 중앙값 141mm → 본 방식 3.9mm)
    ///
    /// 모든 좌표는 OpenCV 관례(y 아래쪽, z 전방)를 유지한다.
    /// </summary>
    public static class HandPoseSolver
    {
        public const float MinDepth = 0.15f;
        public const float MaxDepth = 3.0f;

        // 다중 시작점: 스케일 추정이 실패해도 여기서 건진다.
        private static readonly float[] FallbackDepths = { 0.4f, 0.7f, 1.1f, 1.7f };

        /// <summary>평균 제곱 재투영 오차가 이 값 아래면 충분히 수렴한 것으로 본다 (px²).</summary>
        private const float GoodEnoughCost = 4.0f;

        public struct Result
        {
            public bool success;
            public Vector3 translation;   // 카메라 공간(OpenCV) 손 중심 위치 (m)
            public float meanReprojError; // 평균 재투영 오차 (px)
        }

        /// <param name="world">handWorldLandmarks (21, m, 손 중심 원점)</param>
        /// <param name="pixels">왜곡 보정된 관측 픽셀 (21)</param>
        /// <param name="warmStart">이전 프레임 결과. 있으면 수렴이 빠르고 더 안정적이다.</param>
        public static Result Solve(
            Vector3[] world, Vector2[] pixels, CameraIntrinsics K,
            Vector3? warmStart = null, int iterations = 10)
        {
            if (world == null || pixels == null ||
                world.Length < HandLandmark.Count || pixels.Length < HandLandmark.Count)
                return new Result { success = false };

            int n = HandLandmark.Count;

            Vector3 bestT = Vector3.zero;
            float bestCost = float.MaxValue;
            bool any = false;

            // 1) 이전 프레임 결과 (가장 좋은 초기값)
            if (warmStart.HasValue && warmStart.Value.z > MinDepth)
            {
                if (TryRefine(world, pixels, K, warmStart.Value, iterations,
                              out Vector3 t, out float c))
                { bestT = t; bestCost = c; any = true; }
                if (bestCost < GoodEnoughCost)
                    return Done(bestT, bestCost, n);
            }

            // 2) 분산 비율로 스케일 추정 (회전에 강함)
            float zGuess = EstimateDepthByScale(world, pixels, K);
            if (zGuess > MinDepth && zGuess < MaxDepth)
            {
                Vector3 seed = SeedFromDepth(world, pixels, K, zGuess);
                if (TryRefine(world, pixels, K, seed, iterations, out Vector3 t, out float c)
                    && c < bestCost)
                { bestT = t; bestCost = c; any = true; }
                if (bestCost < GoodEnoughCost)
                    return Done(bestT, bestCost, n);
            }

            // 3) 고정 깊이 다중 시작
            foreach (float z in FallbackDepths)
            {
                Vector3 seed = SeedFromDepth(world, pixels, K, z);
                if (TryRefine(world, pixels, K, seed, iterations, out Vector3 t, out float c)
                    && c < bestCost)
                { bestT = t; bestCost = c; any = true; }
                if (bestCost < GoodEnoughCost) break;
            }

            return any ? Done(bestT, bestCost, n) : new Result { success = false };
        }

        private static Result Done(Vector3 t, float cost, int n) => new Result
        {
            success = true,
            translation = t,
            meanReprojError = Mathf.Sqrt(Mathf.Max(cost, 0f)),
        };

        // ------------------------------------------------------------------ //

        /// <summary>
        /// 월드 랜드마크의 측면 분산 대 픽셀 분산 비율로 깊이를 추정.
        /// 두 점 사이 거리를 쓰는 방식과 달리 손이 기울어져도 잘 버틴다.
        /// </summary>
        private static float EstimateDepthByScale(Vector3[] world, Vector2[] pixels, CameraIntrinsics K)
        {
            int n = HandLandmark.Count;

            Vector2 wMean = Vector2.zero, pMean = Vector2.zero;
            for (int i = 0; i < n; i++)
            {
                wMean += new Vector2(world[i].x, world[i].y);
                pMean += pixels[i];
            }
            wMean /= n; pMean /= n;

            float wVar = 0f, pVar = 0f;
            for (int i = 0; i < n; i++)
            {
                wVar += (new Vector2(world[i].x, world[i].y) - wMean).sqrMagnitude;
                pVar += (pixels[i] - pMean).sqrMagnitude;
            }
            float wSigma = Mathf.Sqrt(wVar / n);
            float pSigma = Mathf.Sqrt(pVar / n);
            if (pSigma < 1e-4f || wSigma < 1e-5f) return -1f;

            return K.FocalMean * wSigma / pSigma;
        }

        /// <summary>주어진 깊이에서 손 중심을 픽셀 중심 시선 위에 올려놓는 초기 t.</summary>
        private static Vector3 SeedFromDepth(Vector3[] world, Vector2[] pixels,
                                             CameraIntrinsics K, float z)
        {
            int n = HandLandmark.Count;
            Vector2 pMean = Vector2.zero;
            Vector3 wMean = Vector3.zero;
            for (int i = 0; i < n; i++) { pMean += pixels[i]; wMean += world[i]; }
            pMean /= n; wMean /= n;

            return K.Unproject(pMean, z) - wMean;
        }

        /// <summary>Levenberg-Marquardt 로 t(3자유도)를 정련.</summary>
        private static bool TryRefine(
            Vector3[] world, Vector2[] pixels, CameraIntrinsics K,
            Vector3 t, int iterations, out Vector3 result, out float cost)
        {
            result = t;
            cost = Cost(world, pixels, K, t);
            if (float.IsInfinity(cost)) return false;

            float lambda = 1e-3f;
            int n = HandLandmark.Count;

            for (int iter = 0; iter < iterations; iter++)
            {
                // 정규방정식 누적 (3x3 대칭)
                float m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
                float g0 = 0, g1 = 0, g2 = 0;

                for (int i = 0; i < n; i++)
                {
                    Vector3 p = world[i] + result;
                    if (p.z < MinDepth * 0.5f) return false;

                    float invZ = 1f / p.z;
                    float u = K.fx * p.x * invZ + K.cx;
                    float v = K.fy * p.y * invZ + K.cy;
                    float ru = u - pixels[i].x;
                    float rv = v - pixels[i].y;

                    // ∂u/∂t = (fx/z, 0, -fx·x/z²),  ∂v/∂t = (0, fy/z, -fy·y/z²)
                    float a = K.fx * invZ;
                    float b = -K.fx * p.x * invZ * invZ;
                    float c = K.fy * invZ;
                    float d = -K.fy * p.y * invZ * invZ;

                    m00 += a * a;          m02 += a * b;
                    m11 += c * c;          m12 += c * d;
                    m22 += b * b + d * d;
                    // m01 은 두 행 모두 0 성분이라 0으로 남는다
                    g0 += a * ru;
                    g1 += c * rv;
                    g2 += b * ru + d * rv;
                }

                bool stepTaken = false;
                Vector3 delta = Vector3.zero;

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    float d00 = m00 * (1f + lambda) + 1e-9f;
                    float d11 = m11 * (1f + lambda) + 1e-9f;
                    float d22 = m22 * (1f + lambda) + 1e-9f;

                    if (!Solve3x3(d00, m01, m02, d11, m12, d22, -g0, -g1, -g2, out delta))
                    { lambda *= 10f; continue; }

                    Vector3 candidate = result + delta;
                    candidate.z = Mathf.Clamp(candidate.z, MinDepth - AvgZ(world), MaxDepth);

                    float candCost = Cost(world, pixels, K, candidate);
                    if (candCost < cost)
                    {
                        result = candidate;
                        cost = candCost;
                        lambda = Mathf.Max(lambda * 0.4f, 1e-6f);
                        stepTaken = true;
                        break;
                    }
                    lambda *= 8f;
                }

                if (!stepTaken || delta.sqrMagnitude < 1e-14f) break;
            }
            return true;
        }

        private static float AvgZ(Vector3[] world)
        {
            float s = 0f;
            for (int i = 0; i < HandLandmark.Count; i++) s += world[i].z;
            return s / HandLandmark.Count;
        }

        private static float Cost(Vector3[] world, Vector2[] pixels, CameraIntrinsics K, Vector3 t)
        {
            float sum = 0f;
            int n = HandLandmark.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = world[i] + t;
                if (p.z < MinDepth * 0.5f) return float.PositiveInfinity;
                float invZ = 1f / p.z;
                float du = K.fx * p.x * invZ + K.cx - pixels[i].x;
                float dv = K.fy * p.y * invZ + K.cy - pixels[i].y;
                sum += du * du + dv * dv;
            }
            return sum / n;   // 평균 제곱 재투영 오차 (px²)
        }

        /// <summary>대칭 3x3 선형계 해법 (여인수 전개).</summary>
        private static bool Solve3x3(
            float a00, float a01, float a02,
            float a11, float a12, float a22,
            float b0, float b1, float b2, out Vector3 x)
        {
            float c00 = a11 * a22 - a12 * a12;
            float c01 = a02 * a12 - a01 * a22;
            float c02 = a01 * a12 - a02 * a11;

            float det = a00 * c00 + a01 * c01 + a02 * c02;
            if (Mathf.Abs(det) < 1e-12f) { x = Vector3.zero; return false; }

            float c11 = a00 * a22 - a02 * a02;
            float c12 = a02 * a01 - a00 * a12;
            float c22 = a00 * a11 - a01 * a01;

            float inv = 1f / det;
            x = new Vector3(
                (c00 * b0 + c01 * b1 + c02 * b2) * inv,
                (c01 * b0 + c11 * b1 + c12 * b2) * inv,
                (c02 * b0 + c12 * b1 + c22 * b2) * inv);
            return true;
        }

        /// <summary>
        /// 월드 랜드마크로부터 손바닥 자세를 구성한다 (OpenCV 좌표계).
        /// across(검지→새끼) 와 up(손목→중지) 의 관계는 좌/우손에서 거울상이라,
        /// side 를 모르면 cross(across, up) 이 한쪽 손에서는 손등, 반대쪽에서는
        /// 손바닥 법선을 가리키게 된다. Right 일 때 across 를 뒤집어 항상 손등
        /// 법선이 나오게 한다 (실측으로 방향 확인함 — Left 기준이었던 이전 버전은 반대였다).
        ///
        /// invertNormal/invertUp: 실기기·플러그인 버전마다 카메라 좌표계 관례가
        /// 미묘하게 달라서 부호를 코드로 단정하기 어렵다. HandTracker 인스펙터에서
        /// 즉시 토글해 가며 맞는 조합을 찾을 수 있게 노출한다.
        /// </summary>
        public static bool TryGetPalmRotation(Vector3[] world, HandSide side,
            bool invertNormal, bool invertUp, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (world == null || world.Length < HandLandmark.Count) return false;

            Vector3 wrist = world[HandLandmark.Wrist];
            Vector3 up = world[HandLandmark.MiddleMcp] - wrist;          // 손목 → 중지
            Vector3 across = world[HandLandmark.PinkyMcp] - world[HandLandmark.IndexMcp];
            if (side == HandSide.Right) across = -across;

            if (up.sqrMagnitude < 1e-8f || across.sqrMagnitude < 1e-8f) return false;

            up.Normalize();
            Vector3 forward = Vector3.Cross(up, across.normalized);      // 손등 법선
            if (forward.sqrMagnitude < 1e-6f) return false;

            forward.Normalize();
            if (invertNormal) forward = -forward;
            up = Vector3.Cross(forward, Vector3.Cross(up, forward)).normalized;  // 재직교화
            if (invertUp) up = -up;
            rotation = Quaternion.LookRotation(forward, up);
            return true;
        }
    }
}
