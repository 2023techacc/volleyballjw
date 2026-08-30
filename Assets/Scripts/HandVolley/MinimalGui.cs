using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// HandVolley 전용 미니멀 IMGUI 테마.
    /// 별도 이미지 에셋 없이 런타임에서 둥근 사각형 텍스처를 만들어 사용한다.
    /// 기존 OnGUI 구조를 유지하면서도 카드/버튼을 한 톤으로 맞출 수 있다.
    /// </summary>
    public static class MinimalGui
    {
        public const float ReferenceWidth = 1600f;
        public const float ReferenceHeight = 900f;

        public static readonly Color Accent = new Color(0.16f, 0.39f, 0.82f, 1f);
        public static readonly Color AccentHover = new Color(0.20f, 0.46f, 0.92f, 1f);
        public static readonly Color AccentPressed = new Color(0.12f, 0.31f, 0.70f, 1f);
        public static readonly Color Ink = new Color(0.10f, 0.15f, 0.23f, 1f);
        public static readonly Color Muted = new Color(0.36f, 0.42f, 0.52f, 1f);
        public static readonly Color SoftBlue = new Color(0.91f, 0.95f, 1f, 0.96f);

        private static bool _ready;
        private static Texture2D _whiteCard;
        private static Texture2D _softCard;
        private static Texture2D _blue;
        private static Texture2D _blueHover;
        private static Texture2D _bluePressed;
        private static Texture2D _darkGlass;
        private static Texture2D _overlay;

        private static GUIStyle _card;
        private static GUIStyle _softCardStyle;
        private static GUIStyle _primaryButton;
        private static GUIStyle _secondaryButton;
        private static GUIStyle _darkChip;

        public static GUIStyle Card { get { Ensure(); return _card; } }
        public static GUIStyle SoftCard { get { Ensure(); return _softCardStyle; } }
        public static GUIStyle PrimaryButton { get { Ensure(); return _primaryButton; } }
        public static GUIStyle SecondaryButton { get { Ensure(); return _secondaryButton; } }
        public static GUIStyle DarkChip { get { Ensure(); return _darkChip; } }
        public static Texture2D OverlayTexture { get { Ensure(); return _overlay; } }

        public static Matrix4x4 BeginScaled()
        {
            Matrix4x4 old = GUI.matrix;
            float scale = Mathf.Min(Screen.width / ReferenceWidth, Screen.height / ReferenceHeight);
            float x = (Screen.width - ReferenceWidth * scale) * 0.5f;
            float y = (Screen.height - ReferenceHeight * scale) * 0.5f;
            GUI.matrix = Matrix4x4.TRS(new Vector3(x, y, 0f), Quaternion.identity,
                                      new Vector3(scale, scale, 1f));
            return old;
        }

        public static GUIStyle Label(int fontSize, Color color, TextAnchor anchor,
                                     FontStyle fontStyle = FontStyle.Normal)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = anchor,
                fontStyle = fontStyle,
                clipping = TextClipping.Clip,
                normal = { textColor = color },
            };
        }

        public static GUIStyle CenterLabel(int fontSize, Color color,
                                           FontStyle fontStyle = FontStyle.Normal)
            => Label(fontSize, color, TextAnchor.MiddleCenter, fontStyle);

        public static void DrawCard(Rect rect, bool soft = false)
        {
            GUI.Box(rect, GUIContent.none, soft ? SoftCard : Card);
        }

        private static void Ensure()
        {
            if (_ready) return;
            _ready = true;

            _whiteCard = MakeRounded(new Color(1f, 1f, 1f, 0.94f), 16);
            _softCard = MakeRounded(new Color(0.96f, 0.98f, 1f, 0.94f), 16);
            _blue = MakeRounded(Accent, 16);
            _blueHover = MakeRounded(AccentHover, 16);
            _bluePressed = MakeRounded(AccentPressed, 16);
            _darkGlass = MakeRounded(new Color(0.06f, 0.11f, 0.20f, 0.76f), 14);
            _overlay = MakeSolid(new Color(0.94f, 0.97f, 1f, 0.82f));

            _card = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(18, 18, 18, 18),
                padding = new RectOffset(18, 18, 18, 18),
                normal = { background = _whiteCard },
            };

            _softCardStyle = new GUIStyle(_card)
            {
                normal = { background = _softCard },
            };

            _primaryButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(18, 18, 18, 18),
                normal = { background = _blue, textColor = Color.white },
                hover = { background = _blueHover, textColor = Color.white },
                active = { background = _bluePressed, textColor = Color.white },
            };

            _secondaryButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(18, 18, 18, 18),
                normal = { background = _whiteCard, textColor = Ink },
                hover = { background = _softCard, textColor = Accent },
                active = { background = _softCard, textColor = AccentPressed },
            };

            _darkChip = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(16, 16, 16, 16),
                normal = { background = _darkGlass },
            };
        }

        private static Texture2D MakeSolid(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeRounded(Color color, int radius)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(r - x, 0f) + Mathf.Max(x - (size - 1 - r), 0f);
                    float dy = Mathf.Max(r - y, 0f) + Mathf.Max(y - (size - 1 - r), 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = 1f - Mathf.SmoothStep(r - 1.5f, r + 0.5f, dist);
                    Color c = color;
                    c.a *= Mathf.Clamp01(alpha);
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
