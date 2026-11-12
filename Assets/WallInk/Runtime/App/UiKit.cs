using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WallInk
{
    /// <summary>A flat button on a <see cref="UiPanel"/>. Rect is in panel-local metres, origin at the panel centre.</summary>
    public sealed class UiButton
    {
        public string id;
        public Rect rect;
        public int page = -1;                 // -1 = shown on every page
        public Action onClick;
        public Func<string> labelFn;          // optional dynamic label
        public Func<bool> toggledFn;          // optional "selected" state
        public Func<bool> visibleFn;          // optional visibility
        public float holdSeconds;             // > 0: must be held this long (protects destructive actions)
        public Color baseColor = new Color(1f, 1f, 1f, 0.13f);
        public Color? swatch;                 // colour chip buttons

        public MeshRenderer background;
        public MeshRenderer holdBar;
        public TextMesh label;
        public bool visible = true;
        string _text;

        public void SetText(string text)
        {
            if (text == _text) return;
            _text = text;
            if (label != null) label.text = text;
        }

        public void SetVisible(bool v)
        {
            if (visible == v) return;
            visible = v;
            background.gameObject.SetActive(v);
        }
    }

    /// <summary>
    /// Minimal world-space panel: coloured quads plus TextMesh labels, hit-tested analytically
    /// against a ray or a fingertip. It has no dependency on a UI framework or event system,
    /// so it behaves identically with controllers, hands and the editor mouse.
    /// Panel-local frame matches the board: +X right, +Y up, -Z toward the viewer.
    /// </summary>
    public sealed class UiPanel
    {
        public readonly Transform Root;
        public readonly Vector2 Size;
        public readonly List<UiButton> Buttons = new List<UiButton>();
        const int FontPixels = 64;

        public UiPanel(string name, Transform parent, Vector2 size, Color background)
        {
            Root = new GameObject(name).transform;
            Root.SetParent(parent, false);
            Size = size;
            MeshRenderer bg = InkAssets.NewRenderer("Background", Root, InkAssets.Quad, InkAssets.GlassMaterial, InkAssets.SortUiPanel);
            bg.transform.localScale = new Vector3(size.x, size.y, 1f);
            InkAssets.SetGlass(bg, background, size, Mathf.Min(0.028f, Mathf.Min(size.x, size.y) * 0.5f), 0.0016f);
        }

        public UiButton AddButton(string id, Rect rect, string text, Action onClick, float textHeight = 0.0085f)
        {
            var b = new UiButton { id = id, rect = rect, onClick = onClick };
            b.background = InkAssets.NewRenderer("Btn " + id, Root, InkAssets.Quad, InkAssets.GlassMaterial, InkAssets.SortUiButton);
            Transform t = b.background.transform;
            t.localPosition = new Vector3(rect.center.x, rect.center.y, -0.0005f);
            t.localScale = new Vector3(rect.width, rect.height, 1f);
            // Capsule-shaped: the corner radius is half the shorter side.
            InkAssets.SetGlass(b.background, b.baseColor, rect.size, Mathf.Min(rect.width, rect.height) * 0.5f, 0.0009f);

            b.label = MakeText(t, text, textHeight, Color.white, TextAnchor.MiddleCenter);
            // The label is a child of a non-uniformly scaled quad: undo that scale.
            b.label.transform.localScale = new Vector3(1f / rect.width, 1f / rect.height, 1f);
            b.label.transform.localPosition = new Vector3(0f, 0f, -0.0005f);
            b.SetText(text);

            b.holdBar = InkAssets.NewRenderer("Hold", t, InkAssets.SolidQuad, InkAssets.InkMaterial, InkAssets.SortUiButton + 1);
            b.holdBar.enabled = false;
            InkAssets.SetTint(b.holdBar, new Color(1f, 0.45f, 0.3f, 0.9f));
            Buttons.Add(b);
            return b;
        }

        public TextMesh AddLabel(Vector2 topLeft, string text, float textHeight, Color color)
        {
            TextMesh tm = MakeText(Root, text, textHeight, color, TextAnchor.UpperLeft);
            tm.transform.localPosition = new Vector3(topLeft.x, topLeft.y, -0.0008f);
            return tm;
        }

        static TextMesh MakeText(Transform parent, string text, float height, Color color, TextAnchor anchor)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = InkAssets.Font;
            tm.fontSize = FontPixels;
            // TextMesh renders 10 font pixels per world unit at characterSize 1.
            tm.characterSize = height * 10f / FontPixels;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
            tm.color = color;
            tm.text = text;
            tm.richText = false;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = InkAssets.TextMaterial;
            mr.sortingOrder = InkAssets.SortUiText;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return tm;
        }

        /// <summary>World point -> (x, y, height in front of the panel).</summary>
        public Vector3 ToLocal(Vector3 world)
        {
            Vector3 l = QuatUtil.Inverse(Root.rotation) * (world - Root.position);
            return new Vector3(l.x, l.y, -l.z);
        }

        public bool Contains(Vector2 local, float margin = 0f)
        {
            return Mathf.Abs(local.x) <= Size.x * 0.5f + margin && Mathf.Abs(local.y) <= Size.y * 0.5f + margin;
        }

        /// <summary>Intersects a ray with the panel from its front side.</summary>
        public bool Raycast(Vector3 origin, Vector3 direction, out Vector2 local, out float distance)
        {
            local = default;
            distance = 0f;
            Vector3 n = Root.rotation * Vector3.back; // toward the viewer
            float denom = Vector3.Dot(direction, n);
            if (denom > -1e-5f) return false;         // parallel, or coming from behind
            distance = Vector3.Dot(Root.position - origin, n) / denom;
            if (distance <= 0f) return false;
            Vector3 l = ToLocal(origin + direction * distance);
            local = new Vector2(l.x, l.y);
            return Contains(local);
        }

        public UiButton ButtonAt(Vector2 local)
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                UiButton b = Buttons[i];
                if (b.visible && b.onClick != null && b.rect.Contains(local)) return b;
            }
            return null;
        }

        static readonly StringBuilder s_wrap = new StringBuilder(256);

        /// <summary>Greedy word wrap by character count (TextMesh has no wrapping of its own).</summary>
        public static string Wrap(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            s_wrap.Length = 0;
            int lineLen = 0;
            string[] paragraphs = text.Split('\n');
            for (int p = 0; p < paragraphs.Length; p++)
            {
                if (p > 0) { s_wrap.Append('\n'); lineLen = 0; }
                string[] words = paragraphs[p].Split(' ');
                for (int w = 0; w < words.Length; w++)
                {
                    string word = words[w];
                    if (lineLen > 0 && lineLen + 1 + word.Length > maxChars) { s_wrap.Append('\n'); lineLen = 0; }
                    if (lineLen > 0) { s_wrap.Append(' '); lineLen++; }
                    s_wrap.Append(word);
                    lineLen += word.Length;
                }
            }
            return s_wrap.ToString();
        }
    }

    /// <summary>Crosshair targets and guide boxes drawn on the board for calibration and accuracy checks.</summary>
    public sealed class BoardMarkers
    {
        readonly Mesh _mesh;
        readonly MeshRenderer _renderer;
        readonly MeshBuffers _buffers = new MeshBuffers();

        public BoardMarkers(Transform boardTransform)
        {
            _mesh = new Mesh { name = "Board Markers" };
            _renderer = InkAssets.NewRenderer("Markers", boardTransform, _mesh, InkAssets.InkMaterial, InkAssets.SortBoardOverlay + 1);
            _renderer.enabled = false;
        }

        public void Hide() => _renderer.enabled = false;

        public void Begin() => _buffers.Clear();

        public void Cross(Vector2 p, float arm, Color32 color, float width = 0.0008f)
        {
            Line(new Vector2(p.x - arm, p.y), new Vector2(p.x + arm, p.y), color, width);
            Line(new Vector2(p.x, p.y - arm), new Vector2(p.x, p.y + arm), color, width);
        }

        public void Box(Vector2 min, Vector2 max, Color32 color, float width = 0.0006f)
        {
            var s = new StrokeData { space = StrokeSpace.Board, width = width, color = color };
            s.points.Add(new Vector3(min.x, min.y)); s.points.Add(new Vector3(max.x, min.y));
            s.points.Add(new Vector3(max.x, max.y)); s.points.Add(new Vector3(min.x, max.y));
            s.points.Add(new Vector3(min.x, min.y));
            StrokeMeshBuilder.Append(_buffers, s, 0.0003f);
        }

        public void Line(Vector2 a, Vector2 b, Color32 color, float width)
        {
            var s = new StrokeData { space = StrokeSpace.Board, width = width, color = color };
            s.points.Add(new Vector3(a.x, a.y)); s.points.Add(new Vector3(b.x, b.y));
            StrokeMeshBuilder.Append(_buffers, s, 0.0003f);
        }

        public void Dot(Vector2 p, float diameter, Color32 color)
        {
            var s = new StrokeData { space = StrokeSpace.Board, width = diameter, color = color };
            s.points.Add(new Vector3(p.x, p.y));
            StrokeMeshBuilder.Append(_buffers, s, 0.0003f);
        }

        public void End()
        {
            InkAssets.Upload(_mesh, _buffers);
            _renderer.enabled = _buffers.VertexCount > 0;
        }
    }
}
