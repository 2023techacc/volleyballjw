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
    [SerializeField] private int _webcamDeviceIndex = 1;

    [Tooltip("HandVolley 쪽 MediaPipeHandSource.Configure 에 넘길 미러 여부. " +
             "ImageSource.isFrontFacing 은 폰의 전면/후면 카메라 구분용이라 외장 USB " +
             "웹캠에서는 대부분 false 로 나온다 — 이 값을 그대로 쓰면 실제로는 사용자를 " +
             "마주 보는 배치인데도 안 뒤집힌 것으로 처리돼 손 회전/좌우가 통째로 어긋난다. " +
             "카메라가 사용자를 마주 보고 있으면 true 로 두는 것이 정상이다 (README 참고).")]
    [SerializeField] private bool _mirrored = true;

    private Experimental.TextureFramePool _textureFramePool;
    private Canvas _debugPreviewCanvas;
    private Camera _debugPreviewCamera;

    public readonly HandLandmarkDetectionConfig config = new HandLandmarkDetectionConfig();

    /// <summary>
    /// HandVolley 연결: 게임 설정 화면에서 카메라를 바꿀 수 있도록 두는 정적 참조.
    /// HandTracker/MediaPipeHandSource 와 같은 패턴 — 이 오브젝트는 씬에 미리 배치된 게
    /// 아니라 Play 시점에 생기므로 인스펙터 드래그 연결이 불가능하다.
    /// </summary>
    public static HandLandmarkerRunner Instance { get; private set; }

    private void Awake() => Instance = this;
    private void OnDestroy()
    {
      if (Instance == this) Instance = null;
    }

    /// <summary>HandVolley 연결: 지금 선택된 웹캠 장치 번호.</summary>
    public int WebcamIndex => _webcamDeviceIndex;

    /// <summary>
    /// HandVolley 연결: Run() 시작 시 콘솔에 찍는 것과 같은 웹캠 이름 목록. Run() 이 아직
    /// 한 번도 실행되지 않았으면(Bootstrap 대기 중) null.
    /// </summary>
    public string[] AvailableCameraNames => (ImageSourceProvider.ImageSource as WebCamSource)?.sourceCandidateNames;

    /// <summary>
    /// HandVolley 연결: 게임 설정 화면의 카메라 번호 버튼이 호출한다. 장치 번호를 바꾸고
    /// 전체 파이프라인(웹캠 캡처 + MediaPipe 추론)을 재시작한다. VisionTaskApiRunner.Play()
    /// 가 이미 "돌고 있으면 먼저 Stop() 한다"는 로직을 갖고 있으므로, 여기서는 필드 값만
    /// 바꾸고 Play() 를 다시 부르면 된다 — Run() 이 처음부터 다시 실행되며 새 번호로
    /// SelectSource 를 호출한다.
    /// </summary>
    public void SwitchWebcam(int index)
    {
      _webcamDeviceIndex = index;
      Play();
    }

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

      // HandVolley 연결: HandVolleyBootstrap 의 Show Debug Text 설정에 맞춰 웹캠 화면과
      // 랜드마크 오버레이(손 골격 표시)를 함께 켜고 끈다. Instance 가 없으면(마우스 모드 등)
      // 건드리지 않고 씬에 있던 상태 그대로 둔다. 게임 설정 화면에서 이 값을 실행 중에도
      // 바꿀 수 있으므로(아래 while 루프 참고), 시작할 때 값은 초깃값일 뿐이다.
      bool showDebugPreview = HandVolley.MediaPipeHandSource.Instance != null &&
                              HandVolley.MediaPipeHandSource.Instance.ShowDebugLandmarks;

      // 웹캠 화면(Annotatable Screen) 을 켠다. Annotation Layer(랜드마크 오버레이)는
      // 프리팹 구조상 이미 그 자식이라(AnnotationController 는 "부모 화면 크기에
      // 맞춰 스스로를 채운다" 는 전제로 동작한다), 따로 떼어내지 않는다 — 떼어내면
      // 좌표 기준이 어긋나 랜드마크가 안 보이거나 엉뚱한 곳에 찍힌다.
      if (screen != null) screen.gameObject.SetActive(showDebugPreview);
      if (_handLandmarkerResultAnnotationController != null)
      {
        _handLandmarkerResultAnnotationController.gameObject.SetActive(showDebugPreview);
      }

      // Use RGBA32 as the input format.
      // TODO: When using GpuBuffer, MediaPipe assumes that the input format is BGRA, so maybe the following code needs to be fixed.
      _textureFramePool = new Experimental.TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, 10);

      // NOTE: The screen will be resized later, keeping the aspect ratio.
      screen.Initialize(imageSource);

      SetupAnnotationController(_handLandmarkerResultAnnotationController, imageSource);

      // HandVolley 연결: Annotatable Screen 에는 AutoFit 컴포넌트가 붙어 있는데, 이건
      // 매 프레임 "자신의 부모 RectTransform 크기"에 맞춰 스스로(offsetMin/Max, 즉 실제
      // 표시 크기)를 다시 계산해 덮어쓴다. 그래서 이 오브젝트의 sizeDelta/anchoredPosition
      // 을 직접 지정해 봤자 다음 프레임에 AutoFit 이 원래 부모(전체 화면 Canvas) 기준으로
      // 다시 확대해 버린다 — 미리보기가 "너무 크게" 보이던 원인 중 하나였다. 대신 정확히
      // 원하는 크기를 가진 전용 Canvas 를 만들고 그 밑에 Annotatable Screen 을 넣어주면
      // AutoFit 이 그 크기에 맞춰 알아서 화면비를 유지하며 축소해 준다.
      //
      // 또, 실제 손 랜드마크 점(Point Annotation)은 UI Image 가 아니라 Annotation Layer
      // 밑에 놓인 평범한 3D Sphere(MeshRenderer) 를 transform.localPosition 으로 배치하는
      // 방식이다. Screen Space - Overlay Canvas 는 CanvasRenderer 를 가진 UI 그래픽만
      // 그리고 일반 3D Renderer 는 그리지 않기 때문에, 웹캠 화면(RawImage, UI 그래픽)은
      // 보여도 점(3D 오브젝트)은 절대 안 보인다 — 이게 랜드마크가 안 보이던 진짜 원인이다.
      // 3D Renderer가 같이 그려지려면 실제 Camera 가 필요하므로, 미리보기 전용 Camera 를
      // 하나 만들고 Canvas 를 그 Camera 에 맞춰 Screen Space - Camera 모드로 붙인다.
      // 이 Camera 는 게임 화면(코트/공/손)과 절대 안 겹치도록 원점에서 아주 먼 허공에
      // 놓고, Camera.rect 로 화면 오른쪽 위 작은 영역에만 렌더링해서 나머지 3D 게임 화면과
      // 기존 "Main Canvas" 의 다른 UI(회색 패널 등)에는 전혀 영향을 주지 않는다.
      // HandVolley 연결: 게임 설정 화면에서 카메라 디버그 미리보기를 실행 중에도 켜고 끌
      // 수 있게 하려면, 처음에 꺼진 상태로 시작하더라도 이 Canvas/Camera 배선은 미리
      // 해 둬야 한다 — 그래서 showDebugPreview 값과 무관하게 항상 한 번 실행한다.
      // 실제로 보이고 안 보이고는 밑에서 SetDebugPreviewVisible 로 따로 제어한다.
      var previewCanvasRt = GetOrCreateDebugPreviewCanvas();
      var screenRt = screen != null ? screen.GetComponent<RectTransform>() : null;

      if (screenRt != null)
      {
        screenRt.SetParent(previewCanvasRt, worldPositionStays: false);
        // 프리팹 원래 앵커(중앙 고정)로 되돌린다. AutoFit 은 "부모 rect 안에 화면비를
        // 유지하며 맞춘다" 는 전제로 동작하므로, 앵커가 다른 값으로 남아 있으면
        // (예전 코드가 오른쪽 아래로 바꿔놓은 채였다면) 그 전제가 깨진다.
        screenRt.anchorMin = new Vector2(0.5f, 0.5f);
        screenRt.anchorMax = new Vector2(0.5f, 0.5f);
        screenRt.pivot = new Vector2(0.5f, 0.5f);
        screenRt.anchoredPosition = Vector2.zero;
      }

      // AutoFit/AnnotationController.Start() 는 이 프레임의 나머지 Update 이후에나
      // 실행되므로, 한 프레임 기다렸다가 실제로 계산된 크기를 찍어봐야 의미가 있다.
      yield return null;

      var annotationRt = _handLandmarkerResultAnnotationController != null
          ? _handLandmarkerResultAnnotationController.GetComponent<RectTransform>()
          : null;
      var rootAnnotation = _handLandmarkerResultAnnotationController != null
          ? _handLandmarkerResultAnnotationController.GetComponentInChildren<HierarchicalAnnotation>(true)
          : null;

      // 점/선 기본 크기(반지름 15, 선 두께 1.0)는 원래 샘플처럼 화면 전체를 채우는
      // 큰 미리보기를 기준으로 잡혀 있어서, 이 작은 구석 미리보기에서는 손이 안 보일
      // 정도로 큰 점으로 덮여 보인다. 작은 미리보기 크기에 맞게 축소한다.
      if (rootAnnotation is MultiHandLandmarkListAnnotation multiHandAnnotation)
      {
        multiHandAnnotation.SetLandmarkRadius(4f);
        multiHandAnnotation.SetConnectionWidth(0.35f);
      }

      if (showDebugPreview)
      {
        Debug.Log("[HandVolley] 미리보기 진단 — " +
                  $"screen active={(screenRt == null ? "null" : screenRt.gameObject.activeInHierarchy.ToString())} rect={(screenRt == null ? "null" : screenRt.rect.ToString())} | " +
                  $"annotationLayer active={(annotationRt == null ? "null" : annotationRt.gameObject.activeInHierarchy.ToString())} rect={(annotationRt == null ? "null" : annotationRt.rect.ToString())} | " +
                  $"rootAnnotation={(rootAnnotation == null ? "없음(!)" : $"{rootAnnotation.name} active={rootAnnotation.isActiveInHierarchy}")}");
      }

      // 초기 상태를 반영한다 — 카메라/캔버스는 방금 항상 만들었으므로, 꺼진 채로
      // 시작하는 경우 여기서 명시적으로 숨겨야 한다 (새로 만든 GameObject 는 기본 활성).
      SetDebugPreviewVisible(showDebugPreview);

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

      float nextDebugStatusLogTime = 0f;

      while (true)
      {
        if (isPaused)
        {
          yield return new WaitWhile(() => isPaused);
        }

        // HandVolley 연결: 게임 설정 화면에서 카메라 디버그 토글을 눌렀는지 매 프레임
        // 확인한다. 파이프라인을 재시작하지 않고 미리보기만 즉시 켜고 끈다.
        bool wantDebugPreview = HandVolley.MediaPipeHandSource.Instance != null &&
                                HandVolley.MediaPipeHandSource.Instance.ShowDebugLandmarks;
        if (wantDebugPreview != showDebugPreview)
        {
          showDebugPreview = wantDebugPreview;
          SetDebugPreviewVisible(showDebugPreview);
        }

        // HandVolley 연결: 검출 결과는 계속 들어오는데 실제로 점이 안 보인다는 제보가 있어
        // 메인 스레드에서 주기적으로 (약 2초마다) 실제 렌더링 오브젝트의 활성 상태를 찍어본다.
        // (OnHandLandmarkDetectionOutput 쪽은 워커 스레드에서 호출될 수 있어 Unity API를
        // 직접 부르면 안 되므로, 반드시 여기 메인 스레드 루프에서 확인해야 한다. 프레임
        // 카운트 나머지 비교는 이 루프가 프레임마다 정확히 한 번씩 돌지 않아서(GPU 리드백
        // 대기 등으로 프레임을 건너뛸 수 있음) 조건을 영영 못 만족할 수 있어 시간 기준으로 바꿨다.)
        if (showDebugPreview && _handLandmarkerResultAnnotationController != null && Time.time >= nextDebugStatusLogTime)
        {
          nextDebugStatusLogTime = Time.time + 2f;
          var loopRootAnnotation = _handLandmarkerResultAnnotationController.GetComponentInChildren<HierarchicalAnnotation>(true);
          Debug.Log("[HandVolley] 랜드마크 상태 — " +
                    $"root={(loopRootAnnotation == null ? "없음(!)" : $"{loopRootAnnotation.name} active={loopRootAnnotation.isActiveInHierarchy}")}");
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

    private int _debugLandmarkLogCounter;

    private void OnHandLandmarkDetectionOutput(HandLandmarkerResult result, Image image, long timestamp)
    {
      if (HandVolley.MediaPipeHandSource.Instance != null && HandVolley.MediaPipeHandSource.Instance.ShowDebugLandmarks &&
          (_debugLandmarkLogCounter++ % 60 == 0))
      {
        Debug.Log($"[HandVolley] 검출 결과 — handLandmarks 개수={(result.handLandmarks == null ? "null" : result.handLandmarks.Count.ToString())}");
      }
      _handLandmarkerResultAnnotationController.DrawLater(result);
      // HandVolley 연결: 이 결과를 HandVolleyBootstrap 이 만든 MediaPipeHandSource 로 넘긴다.
      // 이 콜백은 MediaPipe 워커 스레드에서 호출될 수 있으므로, OnLandmarkerResult 내부는
      // Unity API(Debug.Log 제외)를 직접 호출하지 않도록 되어 있다 (MediaPipeHandSource.cs 참고).
      HandVolley.MediaPipeHandSource.Instance?.OnLandmarkerResult(result, timestamp);
    }

    /// <summary>
    /// HandVolley 연결: 미리보기 전용 Camera 와, 그 Camera 에 물린 Screen Space - Camera
    /// Canvas 를 화면 오른쪽 위 한 구석에만 그리도록 새로 만들어(이미 있으면 재사용) Canvas
    /// 의 RectTransform 을 반환한다. 손 랜드마크 점은 UI 가 아니라 3D Sphere(MeshRenderer)
    /// 라서 실제 Camera 가 있어야 그려진다 — 자세한 이유는 호출부 주석 참고.
    /// </summary>
    private RectTransform GetOrCreateDebugPreviewCanvas()
    {
      if (_debugPreviewCanvas != null) return _debugPreviewCanvas.GetComponent<RectTransform>();

      // 게임 코트/공/손은 원점 근처에 있으므로, 이 Camera 를 원점에서 아주 먼 허공에
      // 두고 아래를 보게 하면 cullingMask 를 따로 제한하지 않아도 실제 게임 3D 오브젝트가
      // 섞여 들어올 일이 없다.
      var cameraGo = new GameObject("HandVolleyDebugPreviewCamera");
      cameraGo.transform.position = new Vector3(0f, 5000f, 0f);
      cameraGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
      _debugPreviewCamera = cameraGo.AddComponent<Camera>();
      _debugPreviewCamera.clearFlags = CameraClearFlags.SolidColor;
      _debugPreviewCamera.backgroundColor = UnityEngine.Color.black; // 이 네임스페이스 트리 안에서는 Mediapipe.Color(protobuf)가 Color 를 가린다.
      _debugPreviewCamera.nearClipPlane = 0.3f;
      _debugPreviewCamera.farClipPlane = 50f;
      _debugPreviewCamera.depth = 100f; // GameCamera/Main Camera보다 확실히 나중에 그려지도록.
      // 화면 오른쪽 위 구석(가로 17%, 세로 23%)에만 그린다. Camera.rect 는 (0,0)이 화면
      // 왼쪽 아래인 정규화 좌표계라 y 는 1-height 부터 시작해야 위쪽 구석이 된다.
      _debugPreviewCamera.rect = new UnityEngine.Rect(1f - 0.17f, 1f - 0.23f, 0.17f, 0.23f);

      var canvasGo = new GameObject("HandVolleyDebugPreviewCanvas");
      _debugPreviewCanvas = canvasGo.AddComponent<Canvas>();
      _debugPreviewCanvas.renderMode = RenderMode.ScreenSpaceCamera;
      _debugPreviewCanvas.worldCamera = _debugPreviewCamera;
      _debugPreviewCanvas.planeDistance = 10f;

      return _debugPreviewCanvas.GetComponent<RectTransform>();
    }

    /// <summary>
    /// HandVolley 연결: 카메라 디버그 미리보기(웹캠 화면 + 손 랜드마크 오버레이 + 그
    /// 전용 Camera/Canvas)를 한 번에 켜고 끈다. 파이프라인 자체(웹캠 캡처, MediaPipe
    /// 추론)는 계속 돌아간다 — 화면에 그리는지 여부만 바뀐다. 카메라를 꺼 두면
    /// _debugPreviewCamera 도 비활성화되어, 화면 구석에 항상 그려지던 검은 사각형(카메라의
    /// SolidColor 클리어)도 함께 사라진다.
    /// </summary>
    private void SetDebugPreviewVisible(bool visible)
    {
      if (screen != null) screen.gameObject.SetActive(visible);
      if (_handLandmarkerResultAnnotationController != null)
      {
        _handLandmarkerResultAnnotationController.gameObject.SetActive(visible);
      }
      if (_debugPreviewCamera != null) _debugPreviewCamera.gameObject.SetActive(visible);
      if (_debugPreviewCanvas != null) _debugPreviewCanvas.gameObject.SetActive(visible);
    }
  }
}
