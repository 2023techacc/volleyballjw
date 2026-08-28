// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using Mediapipe.Tasks.Vision.HandLandmarker;

namespace Mediapipe.Unity.Sample.HandLandmarkDetection
{
  public class HandLandmarkDetectionConfig
  {
    public Tasks.Core.BaseOptions.Delegate Delegate { get; set; } =
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
      Tasks.Core.BaseOptions.Delegate.CPU;
#else
    Tasks.Core.BaseOptions.Delegate.GPU;
#endif

    public ImageReadMode ImageReadMode { get; set; } = ImageReadMode.CPUAsync;

    public Tasks.Vision.Core.RunningMode RunningMode { get; set; } = Tasks.Vision.Core.RunningMode.LIVE_STREAM;

    public int NumHands { get; set; } = 1;
    // 최초 검출 문턱은 오검출 방지를 위해 너무 낮추지 않고, presence/tracking 문턱은
    // 낮춰서 빠른 스윙 중 모션 블러로 ROI 추적이 끊기는 것을 줄인다.
    public float MinHandDetectionConfidence { get; set; } = 0.42f;
    public float MinHandPresenceConfidence { get; set; } = 0.32f;
    public float MinTrackingConfidence { get; set; } = 0.30f;
    public string ModelPath => "hand_landmarker.bytes";

    public HandLandmarkerOptions GetHandLandmarkerOptions(HandLandmarkerOptions.ResultCallback resultCallback = null)
    {
      return new HandLandmarkerOptions(
        new Tasks.Core.BaseOptions(Delegate, modelAssetPath: ModelPath),
        runningMode: RunningMode,
        numHands: NumHands,
        minHandDetectionConfidence: MinHandDetectionConfidence,
        minHandPresenceConfidence: MinHandPresenceConfidence,
        minTrackingConfidence: MinTrackingConfidence,
        resultCallback: resultCallback
      );
    }
  }
}
