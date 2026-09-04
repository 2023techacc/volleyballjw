using System.Collections.Generic;
using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// HandVolley 전용 미니멀 IMGUI 테마.
    /// 별도 이미지 에셋 없이 런타임에서 둥근 사각형 텍스처를 만들어 사용한다.
    /// 기존 OnGUI 구조를 유지하면서도 카드/버튼을 한 톤으로 맞출 수 있다.
    ///
    /// 시작 화면(밝은 톤)과 게임 화면(어두운 톤)을 시각적으로 분리하기 위해
    /// 네이비 계열 팔레트와 알약/원형/그라디언트 드로잉 헬퍼를 함께 제공한다.
    /// </summary>
    public static class MinimalGui
    {
        public const float ReferenceHeight = 900f;

        /// <summary>
        /// 화면 실제 가로세로 비율에 맞춘 가로 기준값. 세로(900)는 고정하고 가로만
        /// 늘어나므로, 화면이 16:9보다 넓어도 레터박스(빈 여백) 없이 꽉 채운다.
        /// 오른쪽/가운데 정렬 요소는 이 값을 기준으로 위치를 계산해야 화면 폭이
        /// 바뀌어도 계속 오른쪽 끝/가운데에 붙는다.
        /// </summary>
        public static float ReferenceWidth =>
            ReferenceHeight * Mathf.Max(Screen.width, 1) / Mathf.Max(Screen.height, 1);

        public static readonly Color Accent = new Color(0.16f, 0.39f, 0.82f, 1f);
        public static readonly Color AccentHover = new Color(0.20f, 0.46f, 0.92f, 1f);
        public static readonly Color AccentPressed = new Color(0.12f, 0.31f, 0.70f, 1f);
        public static readonly Color Ink = new Color(0.10f, 0.15f, 0.23f, 1f);
        public static readonly Color Muted = new Color(0.36f, 0.42f, 0.52f, 1f);
        public static readonly Color SoftBlue = new Color(0.91f, 0.95f, 1f, 0.96f);

        // --- 시작/게임 화면 분리용 팔레트 ---
        /// <summary>하단 특징 바 / 전환 배경의 기본 네이비.</summary>
        public static readonly Color Navy = new Color(0.055f, 0.180f, 0.388f, 1f);
        /// <summary>시작 화면 상단바 (반투명 네이비).</summary>
        public static readonly Color NavyBar = new Color(0.039f, 0.133f, 0.290f, 0.92f);
        /// <summary>전환 화면 좌측의 가장 어두운 네이비.</summary>
        public static readonly Color NavyDeep = new Color(0.027f, 0.102f, 0.220f, 1f);
        /// <summary>손 인식 표시등 / 게이지에 쓰는 민트.</summary>
        public static readonly Color Mint = new Color(0.247f, 0.839f, 0.604f, 1f);
        /// <summary>네이비 위에 올리는 밝은 본문색.</summary>
        public static readonly Color OnNavy = new Color(0.863f, 0.922f, 1f, 1f);
        /// <summary>네이비 위에 올리는 보조 본문색.</summary>
        public static readonly Color OnNavyDim = new Color(0.620f, 0.741f, 0.902f, 1f);
        /// <summary>게임 HUD 칩의 라벨색.</summary>
        public static readonly Color HudLabel = new Color(0.616f, 0.745f, 0.917f, 1f);
        /// <summary>게임 HUD 칩 배경.</summary>
        public static readonly Color HudChip = new Color(0.027f, 0.094f, 0.204f, 0.72f);
        /// <summary>전환 화면의 밝은 강조 띠.</summary>
        public static readonly Color AccentSoft = new Color(0.498f, 0.690f, 1f, 1f);

        private static bool _ready;
        private static Texture2D _whiteCard;
        private static Texture2D _softCard;
        private static Texture2D _blue;
        private static Texture2D _blueHover;
        private static Texture2D _bluePressed;
        private static Texture2D _darkGlass;
        private static Texture2D _overlay;
        private static Texture2D _white;
        private static Texture2D _circle;
        private static Texture2D _ring;
        private static Texture2D _roundWhite;
        private static Texture2D _scrim;

        private static GUIStyle _card;
        private static GUIStyle _softCardStyle;
        private static GUIStyle _primaryButton;
        private static GUIStyle _secondaryButton;
        private static GUIStyle _darkChip;
        private static GUIStyle _roundWhiteStyle;

        private static readonly Dictionary<(int, TextAnchor, FontStyle, Color), GUIStyle> _labelCache =
            new Dictionary<(int, TextAnchor, FontStyle, Color), GUIStyle>();

        public static GUIStyle Card { get { Ensure(); return _card; } }
        public static GUIStyle SoftCard { get { Ensure(); return _softCardStyle; } }
        public static GUIStyle PrimaryButton { get { Ensure(); return _primaryButton; } }
        public static GUIStyle SecondaryButton { get { Ensure(); return _secondaryButton; } }
        public static GUIStyle DarkChip { get { Ensure(); return _darkChip; } }
        public static Texture2D OverlayTexture { get { Ensure(); return _overlay; } }

        public static Matrix4x4 BeginScaled()
        {
            Matrix4x4 old = GUI.matrix;
            // ReferenceWidth 가 항상 현재 화면 비율에 맞춰 계산되므로 세로 기준
            // 스케일 하나만 적용하면 가로/세로 모두 화면을 정확히 채운다 (레터박스 없음).
            float scale = Screen.height / ReferenceHeight;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                      new Vector3(scale, scale, 1f));
            return old;
        }

        public static GUIStyle Label(int fontSize, Color color, TextAnchor anchor,
                                     FontStyle fontStyle = FontStyle.Normal)
        {
            // OnGUI 는 매 프레임 두 번(Layout/Repaint) 이상 호출된다. 라벨마다 GUIStyle 을
            // 새로 만들면 프레임당 수십 개가 GC 로 흘러가므로 조합별로 캐시한다.
            var key = (fontSize, anchor, fontStyle, color);
            if (_labelCache.TryGetValue(key, out GUIStyle cached)) return cached;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = anchor,
                fontStyle = fontStyle,
                clipping = TextClipping.Clip,
                wordWrap = false,
                normal = { textColor = color },
            };
            _labelCache[key] = style;
            return style;
        }

        public static GUIStyle CenterLabel(int fontSize, Color color,
                                           FontStyle fontStyle = FontStyle.Normal)
            => Label(fontSize, color, TextAnchor.MiddleCenter, fontStyle);

        public static void DrawCard(Rect rect, bool soft = false)
        {
            GUI.Box(rect, GUIContent.none, soft ? SoftCard : Card);
        }

        // ------------------------------------------------------------------ //
        // 채우기 헬퍼. 모두 현재 GUI.color 의 알파를 곱하므로 화면 전체 페이드와
        // 자연스럽게 합성된다.
        // ------------------------------------------------------------------ //

        /// <summary>사각형을 단색으로 채운다.</summary>
        public static void Fill(Rect rect, Color color)
        {
            Ensure();
            DrawTinted(rect, _white, color);
        }

        /// <summary>모서리가 둥근 사각형(카드/칩)을 채운다.</summary>
        public static void RoundFill(Rect rect, Color color)
        {
            Ensure();
            Color old = GUI.color;
            GUI.color = Multiply(color, old);
            GUI.Box(rect, GUIContent.none, _roundWhiteStyle);
            GUI.color = old;
        }

        /// <summary>
        /// 양 끝이 완전한 반원인 알약 모양을 채운다. 높이를 지름으로 쓴다.
        ///
        /// 끝 캡은 원 텍스처의 왼쪽/오른쪽 절반만 잘라 쓴다. 원 전체를 두 번 겹쳐
        /// 그리면 가운데 사각형과 겹치는 구간에서 알파가 두 번 곱해져 반투명 칩의
        /// 양 끝이 눈에 띄게 진해진다.
        /// </summary>
        public static void PillFill(Rect rect, Color color)
        {
            Ensure();
            if (rect.width <= rect.height)
            {
                DrawTinted(rect, _circle, color);
                return;
            }

            float r = rect.height * 0.5f;
            Color old = GUI.color;
            GUI.color = Multiply(color, old);

            GUI.DrawTextureWithTexCoords(new Rect(rect.x, rect.y, r, rect.height),
                                         _circle, new Rect(0f, 0f, 0.5f, 1f));
            GUI.DrawTexture(new Rect(rect.x + r, rect.y, rect.width - rect.height, rect.height), _white);
            GUI.DrawTextureWithTexCoords(new Rect(rect.xMax - r, rect.y, r, rect.height),
                                         _circle, new Rect(0.5f, 0f, 0.5f, 1f));

            GUI.color = old;
        }

        /// <summary>속이 빈 원. 카운트다운 링의 바탕으로 쓴다.</summary>
        public static void RingFill(Rect rect, Color color)
        {
            Ensure();
            DrawTinted(rect, _ring, color);
        }

        public static void CircleFill(Rect rect, Color color)
        {
            Ensure();
            DrawTinted(rect, _circle, color);
        }

        /// <summary>
        /// 왼쪽이 불투명하고 오른쪽으로 갈수록 투명해지는 가로 그라디언트.
        /// 시작 화면에서 3D 코트 위에 올려 히어로 텍스트의 가독성을 확보한다.
        /// </summary>
        public static void ScrimFill(Rect rect, Color color)
        {
            Ensure();
            DrawTinted(rect, _scrim, color);
        }

        /// <summary>수평 진행 바 (배경 + 채움).</summary>
        public static void ProgressBar(Rect rect, float t, Color track, Color fill)
        {
            PillFill(rect, track);
            t = Mathf.Clamp01(t);
            if (t <= 0f) return;
            float w = Mathf.Max(rect.height, rect.width * t);
            PillFill(new Rect(rect.x, rect.y, w, rect.height), fill);
        }

        private static void DrawTinted(Rect rect, Texture2D tex, Color color)
        {
            Color old = GUI.color;
            GUI.color = Multiply(color, old);
            GUI.DrawTexture(rect, tex);
            GUI.color = old;
        }

        private static Color Multiply(Color c, Color current)
            => new Color(c.r, c.g, c.b, c.a * current.a);

        // ------------------------------------------------------------------ //
        // 버튼. 알약/둥근 모양을 직접 그리고 히트 판정만 GUI.Button 에 맡긴다.
        // GUIStyle 의 9-slice 로는 완전한 알약을 만들 수 없어서 이 방식을 쓴다.
        // ------------------------------------------------------------------ //

        /// <summary>알약 모양 버튼. 눌리면 true.</summary>
        public static bool PillButton(Rect rect, string text, int fontSize,
                                      Color background, Color hover, Color textColor)
        {
            bool over = rect.Contains(Event.current.mousePosition);
            PillFill(rect, over ? hover : background);
            GUI.Label(rect, text, CenterLabel(fontSize, textColor, FontStyle.Bold));
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        /// <summary>배경 없이 텍스트만 있는 상단바용 탭. 선택되면 밑줄이 붙는다.</summary>
        public static bool NavButton(Rect rect, string text, bool selected)
        {
            bool over = rect.Contains(Event.current.mousePosition);
            Color c = selected ? Color.white : (over ? Color.white : OnNavyDim);
            GUI.Label(rect, text, CenterLabel(21, c, selected ? FontStyle.Bold : FontStyle.Normal));
            if (selected)
            {
                float w = Mathf.Min(rect.width - 16f, text.Length * 22f);
                Fill(new Rect(rect.center.x - w * 0.5f, rect.yMax - 10f, w, 3f), AccentSoft);
            }
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        // ------------------------------------------------------------------ //

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
            _white = MakeSolid(Color.white);
            _circle = MakeCircle(false);
            _ring = MakeCircle(true);
            _roundWhite = MakeRounded(Color.white, 18);
            _scrim = MakeHorizontalScrim();

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

            _roundWhiteStyle = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(20, 20, 20, 20),
                padding = new RectOffset(0, 0, 0, 0),
                normal = { background = _roundWhite },
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

        private static Texture2D NewTexture(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        private static Texture2D MakeSolid(Color color)
        {
            var tex = NewTexture(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 흰색 원. ring 이면 가장자리만 남긴 링을 만든다.
        /// 알약 버튼의 양 끝 캡과 카운트다운 링에 쓰인다.
        /// </summary>
        private static Texture2D MakeCircle(bool ring)
        {
            const int size = 128;
            const float outer = size * 0.5f;
            float inner = outer - 7f;

            var tex = NewTexture(size, size);
            var pixels = new Color[size * size];
            float c = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = 1f - Mathf.SmoothStep(outer - 1.5f, outer + 0.5f, d);
                    if (ring) a *= Mathf.SmoothStep(inner - 1.5f, inner + 0.5f, d);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 왼쪽 → 오른쪽으로 사라지는 가로 그라디언트. 시안의 좌측 스크림과 같은
        /// 정지점(0% 0.97 → 24% 0.93 → 40% 0.55 → 54% 0)을 쓴다.
        /// </summary>
        private static Texture2D MakeHorizontalScrim()
        {
            const int width = 256;
            var tex = NewTexture(width, 1);
            var pixels = new Color[width];

            for (int x = 0; x < width; x++)
            {
                float t = x / (float)(width - 1);
                float a;
                if (t < 0.24f) a = Mathf.Lerp(0.97f, 0.93f, t / 0.24f);
                else if (t < 0.40f) a = Mathf.Lerp(0.93f, 0.55f, (t - 0.24f) / 0.16f);
                else if (t < 0.54f) a = Mathf.Lerp(0.55f, 0f, (t - 0.40f) / 0.14f);
                else a = 0f;
                pixels[x] = new Color(1f, 1f, 1f, a);
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeRounded(Color color, int radius)
        {
            const int size = 64;
            var tex = NewTexture(size, size);

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
