using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HandVolley
{
    /// <summary>
    /// 레거시 Input Manager 와 신규 Input System 을 모두 지원하는 얇은 래퍼.
    ///
    /// Project Settings > Player > Active Input Handling 이 "Input System Package (New)" 로만
    /// 되어 있으면 UnityEngine.Input.* 호출이 InvalidOperationException 을 던진다.
    /// Unity 6 의 일부 템플릿이 이 설정을 기본값으로 쓰기 때문에,
    /// 테스트용 MouseHandSource 가 조용히 죽는 원인이 되기 쉽다.
    /// </summary>
    public static class InputCompat
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        public const bool UsingNewInputSystem = true;
#else
        public const bool UsingNewInputSystem = false;
#endif

        /// <summary>화면 픽셀 좌표. 좌하단 원점 (양쪽 시스템 공통).</summary>
        public static Vector2 MousePosition
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
                return Input.mousePosition;
#endif
            }
        }

        public static bool LeftButtonHeld
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
                return Input.GetMouseButton(0);
#endif
            }
        }

        /// <summary>휠 스크롤. 두 시스템의 단위 차이를 흡수해 노치당 대략 ±1 로 맞춘다.</summary>
        public static float ScrollDelta
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                if (Mouse.current == null) return 0f;
                float raw = Mouse.current.scroll.ReadValue().y;
                // 플랫폼마다 노치당 값이 다르다 (Windows 120, macOS 1 근처).
                // 크기를 보고 정규화해 어느 쪽이든 노치당 대략 ±1 이 되게 한다.
                return Mathf.Abs(raw) > 10f ? raw / 120f : raw;
#else
                return Input.mouseScrollDelta.y;
#endif
            }
        }

        public static bool ResetPressed
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
                return Input.GetKeyDown(KeyCode.R);
#endif
            }
        }

        public static bool MouseAvailable
        {
            get
            {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                return Mouse.current != null;
#else
                return true;
#endif
            }
        }
    }
}
