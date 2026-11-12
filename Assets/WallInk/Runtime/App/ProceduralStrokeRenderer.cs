using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// Renders strokes as procedural meshes.
    ///
    ///  - Finished strokes are packed into bounded "chunk" meshes in creation order. Only the
    ///    chunk that changed is rebuilt (on commit, erase, undo or redo), never the whole board.
    ///  - The stroke being drawn has its own small mesh that is rebuilt each frame from reused
    ///    buffers, so drawing produces no per-frame managed allocations once the buffers have
    ///    grown to size.
    ///  - Chunks render in creation order (sorting order), so later ink covers earlier ink
    ///    without depth fighting between coplanar strokes.
    /// </summary>
    public sealed class ProceduralStrokeRenderer : MonoBehaviour
    {
        [Tooltip("Soft limit of vertices per chunk mesh; a new chunk starts when exceeded.")]
        public int maxVerticesPerChunk = 30000;
        [Tooltip("Total vertex budget. New strokes are refused beyond it.")]
        public int maxTotalVertices = 1500000;
        [Tooltip("Stroke budget. New strokes are refused beyond it.")]
        public int maxStrokes = 6000;
        [Tooltip("Board ink is lifted this far off the backing toward the writer (metres).")]
        public float planeOffset = 0.0005f;

        sealed class Chunk
        {
            public GameObject go;
            public Mesh mesh;
            public MeshRenderer renderer;
            public readonly List<StrokeData> strokes = new List<StrokeData>();
            public int estimatedVertices;
            public int vertexCount;
            public bool dirty;
        }

        readonly List<Chunk> _chunks = new List<Chunk>();
        readonly MeshBuffers _scratch = new MeshBuffers();
        readonly MeshBuffers _liveBuffers = new MeshBuffers();
        Material _material;
        int _sortingBase;
        Mesh _liveMesh;
        MeshRenderer _liveRenderer;
        int _strokeCount;

        public int StrokeCount => _strokeCount;
        public int ChunkCount => _chunks.Count;

        public int TotalVertexCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _chunks.Count; i++) n += _chunks[i].vertexCount;
                return n;
            }
        }

        public void Init(Material material, int sortingBase)
        {
            _material = material;
            _sortingBase = sortingBase;
            _liveMesh = new Mesh { name = "Live Stroke" };
            _liveMesh.MarkDynamic();
            _liveRenderer = InkAssets.NewRenderer("Live Stroke", transform, _liveMesh, material, sortingBase + 1500);
            _liveRenderer.enabled = false;
        }

        /// <summary>False when adding another stroke would exceed the stroke or vertex budget.</summary>
        public bool HasBudget => _strokeCount < maxStrokes && TotalVertexCount < maxTotalVertices;

        public void AddStroke(StrokeData s)
        {
            int estimate = StrokeMeshBuilder.MaxVertexCount(s.points.Count);
            Chunk c = _chunks.Count > 0 ? _chunks[_chunks.Count - 1] : null;
            if (c == null || (c.strokes.Count > 0 && c.estimatedVertices + estimate > maxVerticesPerChunk))
                c = NewChunk();
            c.strokes.Add(s);
            c.estimatedVertices += estimate;
            c.dirty = true;
            s.chunkIndex = _chunks.Count - 1;
            _strokeCount++;
        }

        /// <summary>Call when a stroke's visibility changed (erase, undo, redo).</summary>
        public void RefreshStroke(StrokeData s)
        {
            if (s.chunkIndex >= 0 && s.chunkIndex < _chunks.Count) _chunks[s.chunkIndex].dirty = true;
        }

        /// <summary>Call when a stroke can never be shown again.</summary>
        public void RemoveStroke(StrokeData s)
        {
            if (s.chunkIndex < 0 || s.chunkIndex >= _chunks.Count) return;
            Chunk c = _chunks[s.chunkIndex];
            if (c.strokes.Remove(s))
            {
                c.estimatedVertices -= StrokeMeshBuilder.MaxVertexCount(s.points.Count);
                c.dirty = true;
                _strokeCount--;
            }
            s.chunkIndex = -1;
        }

        /// <summary>Shows the in-progress stroke. Rebuilds its mesh from reused buffers.</summary>
        public void UpdateLive(StrokeData s)
        {
            _liveBuffers.Clear();
            StrokeMeshBuilder.Append(_liveBuffers, s, planeOffset);
            InkAssets.Upload(_liveMesh, _liveBuffers);
            _liveRenderer.enabled = _liveBuffers.VertexCount > 0;
        }

        public void ClearLive()
        {
            if (_liveRenderer != null) _liveRenderer.enabled = false;
        }

        void LateUpdate()
        {
            for (int i = 0; i < _chunks.Count; i++)
                if (_chunks[i].dirty) Rebuild(_chunks[i]);
        }

        void Rebuild(Chunk c)
        {
            c.dirty = false;
            _scratch.Clear();
            for (int i = 0; i < c.strokes.Count; i++)
                if (c.strokes[i].visible) StrokeMeshBuilder.Append(_scratch, c.strokes[i], planeOffset);
            InkAssets.Upload(c.mesh, _scratch);
            c.vertexCount = _scratch.VertexCount;
            c.renderer.enabled = c.vertexCount > 0;
        }

        Chunk NewChunk()
        {
            var c = new Chunk { mesh = new Mesh { name = "Stroke Chunk " + _chunks.Count } };
            c.renderer = InkAssets.NewRenderer(c.mesh.name, transform, c.mesh, _material, _sortingBase + _chunks.Count);
            c.go = c.renderer.gameObject;
            _chunks.Add(c);
            return c;
        }

        void OnDestroy()
        {
            for (int i = 0; i < _chunks.Count; i++)
                if (_chunks[i].mesh != null) Destroy(_chunks[i].mesh);
            if (_liveMesh != null) Destroy(_liveMesh);
        }
    }
}
