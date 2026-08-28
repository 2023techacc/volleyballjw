using UnityEngine;

namespace HandVolley
{
    public enum HandSide { Unknown = 0, Left = 1, Right = 2 }

    /// <summary>MediaPipe HandLandmarker 의 21개 랜드마크 인덱스.</summary>
    public static class HandLandmark
    {
        public const int Wrist = 0;
        public const int ThumbCmc = 1, ThumbMcp = 2, ThumbIp = 3, ThumbTip = 4;
        public const int IndexMcp = 5, IndexPip = 6, IndexDip = 7, IndexTip = 8;
        public const int MiddleMcp = 9, MiddlePip = 10, MiddleDip = 11, MiddleTip = 12;
        public const int RingMcp = 13, RingPip = 14, RingDip = 15, RingTip = 16;
        public const int PinkyMcp = 17, PinkyPip = 18, PinkyDip = 19, PinkyTip = 20;
        public const int Count = 21;
    }

    /// <summary>
    /// 한 프레임에서 관측된 손 하나.
    ///
    /// normalized : handLandmarks. x,y ∈ [0,1] 이미지 좌표(y 아래쪽), z 는 손목 기준 상대 깊이.
    /// world      : handWorldLandmarks. 미터 단위, 원점은 손의 기하학적 중심, y 아래쪽.
    ///
    /// 두 배열 모두 길이 21. world 는 손의 '모양'만 담고 있고 절대 위치는 없다.
    /// 절대 위치는 HandPoseSolver 가 둘을 합쳐 복원한다.
    /// </summary>
    public struct HandObservation
    {
        public bool valid;
        public HandSide side;
        public float confidence;
        public Vector3[] normalized;
        public Vector3[] world;
        public double timestampSeconds;

        public static HandObservation Invalid => new HandObservation { valid = false };

        public bool HasWorldLandmarks =>
            world != null && world.Length >= HandLandmark.Count;

        public bool HasNormalized =>
            normalized != null && normalized.Length >= HandLandmark.Count;
    }

    /// <summary>
    /// 랜드마크 공급원 추상화.
    /// 이 인터페이스 덕분에 MediaPipe 없이도(MouseHandSource) 물리·게임 로직을 먼저 완성할 수 있다.
    /// 플러그인 버전이 바뀌어도 갈아끼울 곳은 구현체 한 곳뿐이다.
    /// </summary>
    public interface IHandLandmarkSource
    {
        /// <summary>추론에 사용 중인 실제 영상 해상도. 내부 파라미터 스케일링에 쓰인다.</summary>
        int ImageWidth { get; }
        int ImageHeight { get; }

        /// <summary>영상이 좌우 반전(셀피 뷰)되어 표시되는지.</summary>
        bool Mirrored { get; }

        bool IsReady { get; }

        /// <summary>가장 최근 관측. 손이 없으면 valid=false.</summary>
        HandObservation GetLatest(HandSide preferredSide);
    }
}
