// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using System.Collections;
using Mediapipe.Tasks.Vision.HandLandmarker;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity.Sample.HandLandmarkDetection
{
  public class HandLandmarkerRunner : VisionTaskApiRunner<HandLandmarker>
  {
    [SerializeField] private HandLandmarkerResultAnnotationController _handLandmarkerResultAnnotationController;

    [Tooltip("여러 웹캠이 연결돼 있을 때 사용할 장치 번호. WebCamSource 는 원래 항상 0번을 " +
             "쓰도록 돼 있어서, 다른 카메라를 쓰려면 이 값을 바꿔야 한다. Play 직후 Console 에 " +
             "찍히는 '사용 가능한 웹캠' 목록을 보고 원하는 번호를 넣으면 된다.")]
    [SerializeField] private int _webcamDeviceIndex = 0;

    [Tooltip("HandVolley 쪽 MediaPipeHandSource.Configure 에 넘길 미러 여부. " +
             "ImageSource.isFrontFacing 은 폰의 전면/후면 카메라 구분용이라 외장 USB " +
             "웹캠에서는 대부분 false 로 나온다 — 이 값을 그대로 쓰면 실제로는 사용자를 " +
             "마주 보는 배치인데도 안 뒤집힌 것으로 처리돼 손 회전/좌우가 통째로 어긋난다. " +
             "카메라가 사용자를 마주 보고 있으면 true 로 두는 것이 정상이다 (README 참고).")]
    [SerializeField] private bool _mirrored = true;

    private Experimental.TextureFramePool _textureFramePool;

    public readonly HandLandmarkDetectionConfig config = new HandLandmarkDetectionConfig();

    public override void Stop()
    {
      base.Stop();
      _textureFramePool?.Dispose();
      _textureFramePool = null;
    }

    protected override IEnumerator Run()
    {
      Debug.Log($"Delegate = {config.Delegate}");
      Debug.Log($"Image Read Mode = {config.ImageReadMode}");
      Debug.Log($"Running Mode = {config.RunningMode}");
      Debug.Log($"NumHands = {config.NumHands}");
      Debug.Log($"MinHandDetectionConfidence = {config.MinHandDetectionConfidence}");
      Debug.Log($"MinHandPresenceConfidence = {config.MinHandPresenceConfidence}");
      Debug.Log($"MinTrackingConfidence = {config.MinTrackingConfidence}");

      yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

      var options = config.GetHandLandmarkerOptions(config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnHandLandmarkDetectionOutput : null);
      taskApi = HandLandmarker.CreateFromOptions(options, GpuManager.GpuResources);
      var imageSource = ImageSourceProvider.ImageSource;

      // HandVolley 연결: WebCamSource 는 Initialize() 에서 항상 availableSources[0] 을
      // 고르도록 돼 있어서, 다른 카메라를 쓰려면 Play() 전에 직접 SelectSource 를 호출해야
      // 한다. sourceCandidateNames 를 먼저 찍어서 어떤 번호가 어떤 장치인지 확인할 수 있게 한다.
      if (imageSource is WebCamSource webCamSource)
      {
        var names = webCamSource.sourceCandidateNames;
        if (names != null)
        {
          for (int i = 0; i < names.Length; i++) Debug.Log($"[HandVolley] 웹캠 {i}: {names[i]}");
        }
        if (names != null && _webcamDeviceIndex >= 0 && _webcamDeviceIndex < names.Length)
        {
          webCamSource.SelectSource(_webcamDeviceIndex);
        }
        else if (_webcamDeviceIndex != 0)
        {
          Debug.LogWarning($"[HandVolley] 웹캠 번호 {_webcamDeviceIndex} 가 유효하지 않습니다. 0번을 사용합니다.");
        }
      }

      yield return imageSource.Play();

      if (!imageSource.isPrepared)
      {
        Debug.LogError("Failed to start ImageSource, exiting...");
        yield break;
      }

      // HandVolley 연결: 실제 웹캠 해상도/미러 여부를 HandVolleyBootstrap 이 만든
      // MediaPipeHandSource 에 한 번 전달한다. Instance 가 아직 null 이면(예: 씬 로드
      // 순서 문제로 HandVolleyBootstrap 이 아직 Awake 되지 않음) 조용히 건너뛴다 —
      // 그러면 MediaPipeHandSource 가 Fallback Width/Height 값으로 동작하며 콘솔에
      // 경고를 띄운다.
      HandVolley.MediaPipeHandSource.Instance?.Configure(
          imageSource.textureWidth, imageSource.textureHeight, _mirrored);

      // Use RGBA32 as the input format.
      // TODO: When using GpuBuffer, MediaPipe assumes that the input format is BGRA, so maybe the following code needs to be fixed.
      _textureFramePool = new Experimental.TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, 10);

      // NOTE: The screen will be resized later, keeping the aspect ratio.
      screen.Initialize(imageSource);

      SetupAnnotationController(_handLandmarkerResultAnnotationController, imageSource);

      var transformationOptions = imageSource.GetTransformationOptions();
      var flipHorizontally = transformationOptions.flipHorizontally;
      var flipVertically = transformationOptions.flipVertically;
      var imageProcessingOptions = new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transformationOptions.rotationAngle);

      AsyncGPUReadbackRequest req = default;
      var waitUntilReqDone = new WaitUntil(() => req.done);
      var waitForEndOfFrame = new WaitForEndOfFrame();
      var result = HandLandmarkerResult.Alloc(options.numHands);

      // NOTE: we can share the GL context of the render thread with MediaPipe (for now, only on Android)
      var canUseGpuImage = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && GpuManager.GpuResources != null;
      using var glContext = canUseGpuImage ? GpuManager.GetGlContext() : null;

      while (true)
      {
        if (isPaused)
        {
          yield return new WaitWhile(() => isPaused);
        }

        if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
        {
          yield return new WaitForEndOfFrame();
          continue;
        }

        // Build the input Image
        Image image;
        switch (config.ImageReadMode)
        {
          case ImageReadMode.GPU:
            if (!canUseGpuImage)
            {
              throw new System.Exception("ImageReadMode.GPU is not supported");
            }
            textureFrame.ReadTextureOnGPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            image = textureFrame.BuildGPUImage(glContext);
            // TODO: Currently we wait here for one frame to make sure the texture is fully copied to the TextureFrame before sending it to MediaPipe.
            // This usually works but is not guaranteed. Find a proper way to do this. See: https://github.com/homuler/MediaPipeUnityPlugin/pull/1311
            yield return waitForEndOfFrame;
            break;
          case ImageReadMode.CPU:
            yield return waitForEndOfFrame;
            textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          case ImageReadMode.CPUAsync:
          default:
            req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            yield return waitUntilReqDone;

            if (req.hasError)
            {
              Debug.LogWarning($"Failed to read texture from the image source");
              continue;
            }
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
        }

        switch (taskApi.runningMode)
        {
          case Tasks.Vision.Core.RunningMode.IMAGE:
            if (taskApi.TryDetect(image, imageProcessingOptions, ref result))
            {
              _handLandmarkerResultAnnotationController.DrawNow(result);
            }
            else
            {
              _handLandmarkerResultAnnotationController.DrawNow(default);
            }
            break;
          case Tasks.Vision.Core.RunningMode.VIDEO:
            if (taskApi.TryDetectForVideo(image, GetCurrentTimestampMillisec(), imageProcessingOptions, ref result))
            {
              _handLandmarkerResultAnnotationController.DrawNow(result);
            }
            else
            {
              _handLandmarkerResultAnnotationController.DrawNow(default);
            }
            break;
          case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
            taskApi.DetectAsync(image, GetCurrentTimestampMillisec(), imageProcessingOptions);
            break;
        }
      }
    }

    private void OnHandLandmarkDetectionOutput(HandLandmarkerResult result, Image image, long timestamp)
    {
      _handLandmarkerResultAnnotationController.DrawLater(result);
      // HandVolley 연결: 이 결과를 HandVolleyBootstrap 이 만든 MediaPipeHandSource 로 넘긴다.
      // 이 콜백은 MediaPipe 워커 스레드에서 호출될 수 있으므로, OnLandmarkerResult 내부는
      // Unity API(Debug.Log 제외)를 직접 호출하지 않도록 되어 있다 (MediaPipeHandSource.cs 참고).
      HandVolley.MediaPipeHandSource.Instance?.OnLandmarkerResult(result, timestamp);
    }
  }
}
