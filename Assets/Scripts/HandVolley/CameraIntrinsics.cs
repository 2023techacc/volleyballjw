using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace HandVolley
{
    /// <summary>calibrate_camera.py 가 내보내는 JSON 스키마와 1:1 대응.</summary>
    [Serializable]
    public class CameraIntrinsicsData
    {
        public string schema;
        public int image_width = 640;
        public int image_height = 480;
        public float fx, fy, cx, cy;
        public float k1, k2, p1, p2, k3;
        public float fov_horizontal_deg, fov_vertical_deg, fov_diagonal_deg;
        public float rms_reprojection_error;
        public int sample_count;
        public string calibrated_at;
    }

    /// <summary>
    /// 핀홀 + Brown-Conrady 왜곡 모델. 좌표계는 OpenCV 관례를 그대로 따른다.
    ///   픽셀: 좌상단 원점, x 오른쪽 / y 아래쪽
    ///   카메라 공간: x 오른쪽 / y 아래쪽 / z 전방 (미터)
    /// 유니티 좌표계 변환은 HandTracker 가 마지막에 한 번만 수행한다.
    /// </summary>
    public class CameraIntrinsics
    {
        public float fx, fy, cx, cy;
        public float k1, k2, p1, p2, k3;
        public int width, height;
        public float rmsError;
        public bool isCalibrated;

        public float FocalMean => Mathf.Sqrt(fx * fy);

        public float VerticalFovDeg =>
            2f * Mathf.Atan(height * 0.5f / fy) * Mathf.Rad2Deg;

        public float HorizontalFovDeg =>
            2f * Mathf.Atan(width * 0.5f / fx) * Mathf.Rad2Deg;

        // ------------------------------------------------------------------ //
        // 생성
        // ------------------------------------------------------------------ //

        public static CameraIntrinsics FromData(CameraIntrinsicsData d)
        {
            return new CameraIntrinsics
            {
                fx = d.fx, fy = d.fy, cx = d.cx, cy = d.cy,
                k1 = d.k1, k2 = d.k2, p1 = d.p1, p2 = d.p2, k3 = d.k3,
                width = d.image_width, height = d.image_height,
                rmsError = d.rms_reprojection_error,
                isCalibrated = true,
            };
        }

        /// <summary>
        /// 캘리브레이션 파일이 없을 때의 임시 대체값.
        /// 제조사 표기 시야각은 대개 '대각선' 기준이다 (앱코 APC930 = 80°).
        /// </summary>
        public static CameraIntrinsics FromDiagonalFov(int w, int h, float diagonalFovDeg)
        {
            float halfDiagPx = 0.5f * Mathf.Sqrt(w * (float)w + h * (float)h);
            float f = halfDiagPx / Mathf.Tan(diagonalFovDeg * 0.5f * Mathf.Deg2Rad);
            return new CameraIntrinsics
            {
                fx = f, fy = f, cx = w * 0.5f, cy = h * 0.5f,
                width = w, height = h, isCalibrated = false,
            };
        }

        /// <summary>
        /// 캘리브레이션 해상도와 런타임 해상도가 다를 때 내부 파라미터를 선형 스케일.
        /// (종횡비가 같아야 유효하다 — 이 카메라는 4:3 고정이므로 안전)
        /// </summary>
        public CameraIntrinsics ScaledTo(int w, int h)
        {
            if (w == width && h == height) return this;
            float sx = w / (float)width;
            float sy = h / (float)height;
            if (Mathf.Abs(sx / sy - 1f) > 0.02f)
            {
                Debug.LogWarning($"[CameraIntrinsics] 종횡비 불일치: 캘리브레이션 " +
                                 $"{width}x{height} → 런타임 {w}x{h}. 깊이 추정 오차가 생깁니다.");
            }
            return new CameraIntrinsics
            {
                fx = fx * sx, fy = fy * sy, cx = cx * sx, cy = cy * sy,
                k1 = k1, k2 = k2, p1 = p1, p2 = p2, k3 = k3,   // 왜곡계수는 정규화 좌표 기준이라 불변
                width = w, height = h,
                rmsError = rmsError, isCalibrated = isCalibrated,
            };
        }

        // ------------------------------------------------------------------ //
        // 투영 / 역투영
        // ------------------------------------------------------------------ //

        /// <summary>왜곡된 관측 픽셀 → 왜곡 보정된 픽셀 (반복 역산).</summary>
        public Vector2 Undistort(Vector2 pixel, int iterations = 6)
        {
            float x0 = (pixel.x - cx) / fx;
            float y0 = (pixel.y - cy) / fy;
            float x = x0, y = y0;

            for (int i = 0; i < iterations; i++)
            {
                float r2 = x * x + y * y;
                float radial = 1f / (1f + r2 * (k1 + r2 * (k2 + r2 * k3)));
                float dx = 2f * p1 * x * y + p2 * (r2 + 2f * x * x);
                float dy = p1 * (r2 + 2f * y * y) + 2f * p2 * x * y;
                x = (x0 - dx) * radial;
                y = (y0 - dy) * radial;
            }
            return new Vector2(x * fx + cx, y * fy + cy);
        }

        /// <summary>왜곡 보정된 픽셀 + 깊이(m) → 카메라 공간 3D (OpenCV 관례).</summary>
        public Vector3 Unproject(Vector2 undistortedPixel, float depth)
        {
            return new Vector3(
                (undistortedPixel.x - cx) * depth / fx,
                (undistortedPixel.y - cy) * depth / fy,
                depth);
        }

        /// <summary>카메라 공간 3D → 왜곡 보정 좌표계의 픽셀.</summary>
        public Vector2 Project(Vector3 camPoint)
        {
            float invZ = 1f / Mathf.Max(camPoint.z, 1e-4f);
            return new Vector2(fx * camPoint.x * invZ + cx,
                               fy * camPoint.y * invZ + cy);
        }

        public override string ToString() =>
            $"{width}x{height}  f=({fx:F1},{fy:F1})  c=({cx:F1},{cy:F1})  " +
            $"FOV H{HorizontalFovDeg:F1}°/V{VerticalFovDeg:F1}°  " +
            (isCalibrated ? $"RMS {rmsError:F3}px" : "미보정(추정값)");
    }

    /// <summary>StreamingAssets 에서 camera_intrinsics.json 을 읽어온다.</summary>
    public static class CameraIntrinsicsLoader
    {
        public const string DefaultFileName = "camera_intrinsics.json";

        /// <summary>
        /// 플랫폼 무관 로드. Android 등에서는 StreamingAssets 가 압축되어 있어
        /// File.ReadAllText 가 통하지 않으므로 UnityWebRequest 를 쓴다.
        /// </summary>
        public static IEnumerator Load(string fileName, Action<CameraIntrinsics> onDone)
        {
            string path = Path.Combine(Application.streamingAssetsPath, fileName);
            string json = null;

#if UNITY_ANDROID && !UNITY_EDITOR
            using (var req = UnityWebRequest.Get(path))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) json = req.downloadHandler.text;
            }
#else
            if (File.Exists(path)) json = File.ReadAllText(path);
            yield return null;
#endif

            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning($"[CameraIntrinsics] {fileName} 을(를) 찾지 못했습니다.\n" +
                                 $"경로: {path}\n" +
                                 $"tools/calibrate_camera.py 를 실행해 생성하세요. " +
                                 $"일단 대각 80° 추정값으로 진행합니다.");
                onDone?.Invoke(CameraIntrinsics.FromDiagonalFov(640, 480, 80f));
                yield break;
            }

            CameraIntrinsicsData data = null;
            try { data = JsonUtility.FromJson<CameraIntrinsicsData>(json); }
            catch (Exception e) { Debug.LogError($"[CameraIntrinsics] 파싱 실패: {e.Message}"); }

            if (data == null || data.fx <= 0f)
            {
                onDone?.Invoke(CameraIntrinsics.FromDiagonalFov(640, 480, 80f));
                yield break;
            }

            var intr = CameraIntrinsics.FromData(data);
            Debug.Log($"[CameraIntrinsics] 로드 완료 — {intr}");
            if (intr.rmsError > 1.0f)
            {
                Debug.LogWarning($"[CameraIntrinsics] 재투영 오차가 큽니다 " +
                                 $"({intr.rmsError:F2}px). 재캘리브레이션을 권장합니다.");
            }
            onDone?.Invoke(intr);
        }
    }
}
