using UnityEngine;

namespace WallInk
{
    public enum AlignmentState
    {
        None,            // no board yet
        Preview,         // being placed or adjusted
        NeedsValidation, // restored from storage; the user must confirm it still matches the wall
        Confirmed,       // the user confirmed alignment; writing allowed
        Uncertain,       // tracking was disrupted or the anchor is not located; writing paused
    }

    public enum GridMode { Off = 0, Grid = 1, Ruled = 2 }

    /// <summary>
    /// The virtual board. Its transform IS the board frame (see <see cref="BoardFrame"/>):
    /// centre at the transform position, local X right, Y up, -Z toward the writer.
    /// Strokes are children in board-local metres, so correcting the board's pose moves all
    /// existing ink with it and never distorts it.
    /// </summary>
    public sealed class WhiteboardSurface : MonoBehaviour
    {
        public Vector2 size = new Vector2(1.2f, 0.8f);
        [Range(0f, 1f)] public float backingOpacity = 0.35f;
        public GridMode grid = GridMode.Off;
        public float gridSpacing = 0.025f;
        public AlignmentState alignment = AlignmentState.None;

        public const float MinSize = 0.2f;
        public const float MaxSize = 4f;

        Material _boardMaterial;
        MeshRenderer _backing;
        Mesh _outlineMesh;
        MeshRenderer _outline;
        Mesh _normalMesh;
        MeshRenderer _normal;
        readonly MeshBuffers _buffers = new MeshBuffers();
        Transform _strokeRoot;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BoardSizeId = Shader.PropertyToID("_BoardSize");
        static readonly int GridSpacingId = Shader.PropertyToID("_GridSpacing");
        static readonly int GridModeId = Shader.PropertyToID("_GridMode");

        public Transform StrokeRoot => _strokeRoot;
        public bool Exists => alignment != AlignmentState.None;
        public bool WritingAllowed => alignment == AlignmentState.Confirmed;
        public BoardFrame Frame => new BoardFrame(transform.position, transform.rotation, size);

        public void Init()
        {
            _boardMaterial = new Material(InkAssets.BoardShader) { name = "WallInk Board" };
            _backing = InkAssets.NewRenderer("Backing", transform, InkAssets.Quad, _boardMaterial, InkAssets.SortBoard);

            _outlineMesh = new Mesh { name = "Board Outline" };
            _outline = InkAssets.NewRenderer("Outline", transform, _outlineMesh, InkAssets.InkMaterial, InkAssets.SortBoardOverlay);
            _normalMesh = new Mesh { name = "Board Normal" };
            _normal = InkAssets.NewRenderer("Normal", transform, _normalMesh, InkAssets.AirInkMaterial, InkAssets.SortBoardOverlay);

            var root = new GameObject("Strokes");
            root.transform.SetParent(transform, false);
            _strokeRoot = root.transform;

            Refresh();
        }

        public void SetFrame(BoardFrame f)
        {
            transform.SetPositionAndRotation(f.origin, f.rotation);
            size = new Vector2(Mathf.Clamp(f.size.x, MinSize, MaxSize), Mathf.Clamp(f.size.y, MinSize, MaxSize));
            Refresh();
        }

        /// <summary>Moves the board in its own axes: x right, y up, z = toward the writer (out of the surface), metres.</summary>
        public void Nudge(Vector3 rightUpOut)
        {
            transform.position += transform.rotation * new Vector3(rightUpOut.x, rightUpOut.y, -rightUpOut.z);
        }

        /// <summary>Rotates about the board centre: pitch about local X, yaw about local Y, roll about the normal, degrees.</summary>
        public void RotateLocal(float pitch, float yaw, float roll)
        {
            Quaternion q = transform.rotation;
            q = q * QuatUtil.AngleAxis(pitch, Vector3.right) * QuatUtil.AngleAxis(yaw, Vector3.up) * QuatUtil.AngleAxis(roll, Vector3.forward);
            transform.rotation = QuatUtil.Normalize(q);
        }

        public void Resize(float dWidth, float dHeight)
        {
            size = new Vector2(Mathf.Clamp(size.x + dWidth, MinSize, MaxSize), Mathf.Clamp(size.y + dHeight, MinSize, MaxSize));
            Refresh();
        }

        public void SetAlignment(AlignmentState state)
        {
            alignment = state;
            Refresh();
        }

        /// <summary>Rebuilds visuals after any change to size, state or appearance.</summary>
        public void Refresh()
        {
            if (_backing == null) return;
            bool show = Exists;
            _backing.enabled = show;
            _outline.enabled = show;
            _normal.enabled = show && alignment != AlignmentState.Confirmed;
            _strokeRoot.gameObject.SetActive(show);
            if (!show) return;

            _backing.transform.localScale = new Vector3(size.x, size.y, 1f);
            _boardMaterial.SetColor(ColorId, new Color(1f, 1f, 1f, backingOpacity));
            _boardMaterial.SetVector(BoardSizeId, new Vector4(size.x, size.y, 0, 0));
            _boardMaterial.SetFloat(GridSpacingId, gridSpacing);
            _boardMaterial.SetFloat(GridModeId, (float)grid);

            Color32 c = OutlineColor(alignment);
            float hw = size.x * 0.5f, hh = size.y * 0.5f;

            // Outline: a closed rectangle stroke lying in the board plane.
            _buffers.Clear();
            var s = new StrokeData { space = StrokeSpace.Board, width = alignment == AlignmentState.Confirmed ? 0.002f : 0.004f, color = c };
            s.points.Add(new Vector3(-hw, -hh, 0));
            s.points.Add(new Vector3(hw, -hh, 0));
            s.points.Add(new Vector3(hw, hh, 0));
            s.points.Add(new Vector3(-hw, hh, 0));
            s.points.Add(new Vector3(-hw, -hh, 0));
            StrokeMeshBuilder.Append(_buffers, s, 0.0002f);
            // Centre cross helps judge position against a mark on the real surface.
            if (alignment != AlignmentState.Confirmed)
            {
                var h = new StrokeData { space = StrokeSpace.Board, width = 0.0015f, color = c };
                h.points.Add(new Vector3(-0.03f, 0, 0)); h.points.Add(new Vector3(0.03f, 0, 0));
                StrokeMeshBuilder.Append(_buffers, h, 0.0002f);
                var v = new StrokeData { space = StrokeSpace.Board, width = 0.0015f, color = c };
                v.points.Add(new Vector3(0, -0.03f, 0)); v.points.Add(new Vector3(0, 0.03f, 0));
                StrokeMeshBuilder.Append(_buffers, v, 0.0002f);
            }
            InkAssets.Upload(_outlineMesh, _buffers);

            // Normal arrow: 12 cm from the centre toward the writer (local -Z).
            _buffers.Clear();
            var n = new StrokeData { space = StrokeSpace.Air, width = 0.004f, color = c, ribbonNormal = Vector3.up };
            n.points.Add(Vector3.zero);
            n.points.Add(new Vector3(0, 0, -0.12f));
            StrokeMeshBuilder.Append(_buffers, n, 0f);
            var head = new StrokeData { space = StrokeSpace.Air, width = 0.004f, color = c, ribbonNormal = Vector3.up };
            head.points.Add(new Vector3(-0.015f, 0, -0.1f));
            head.points.Add(new Vector3(0, 0, -0.12f));
            head.points.Add(new Vector3(0.015f, 0, -0.1f));
            StrokeMeshBuilder.Append(_buffers, head, 0f);
            InkAssets.Upload(_normalMesh, _buffers);
        }

        static Color32 OutlineColor(AlignmentState s)
        {
            switch (s)
            {
                case AlignmentState.Confirmed: return new Color32(60, 170, 90, 200);
                case AlignmentState.Uncertain: return new Color32(230, 60, 50, 255);
                case AlignmentState.NeedsValidation: return new Color32(240, 150, 30, 255);
                default: return new Color32(40, 140, 255, 255);
            }
        }

        void OnDestroy()
        {
            if (_outlineMesh != null) Destroy(_outlineMesh);
            if (_normalMesh != null) Destroy(_normalMesh);
            if (_boardMaterial != null) Destroy(_boardMaterial);
        }
    }
}
