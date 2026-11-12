using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    public enum StrokeSpace
    {
        /// <summary>Points are (x, y, 0) in board coordinates, metres.</summary>
        Board,
        /// <summary>Points are 3D positions in the air-canvas local space, metres.</summary>
        Air,
    }

    /// <summary>An unfiltered captured sample, kept so smoothing can be re-tuned without losing the original data.</summary>
    [Serializable]
    public struct RawSample
    {
        /// <summary>Board strokes: (x, y, height above surface). Air strokes: local position.</summary>
        public Vector3 position;
        /// <summary>Seconds since the stroke began.</summary>
        public float time;
        public float confidence;
    }

    /// <summary>One ink stroke. Geometry is stored in physical units, independent of rendering.</summary>
    public sealed class StrokeData
    {
        public int id;
        public StrokeSpace space;
        public Color32 color;
        /// <summary>Constant stroke width in metres.</summary>
        public float width;
        /// <summary>Points used for rendering (filtered and distance-sampled).</summary>
        public readonly List<Vector3> points = new List<Vector3>();
        /// <summary>Original captured samples.</summary>
        public readonly List<RawSample> raw = new List<RawSample>();
        /// <summary>Air strokes only: ribbon face normal at the first point.</summary>
        public Vector3 ribbonNormal = Vector3.back;
        public StylusSource source;
        public bool isStraightLine;

        /// <summary>False while erased, cleared or undone.</summary>
        public bool visible = true;

        public Vector3 boundsMin;
        public Vector3 boundsMax;

        /// <summary>Renderer bookkeeping: index of the mesh chunk holding this stroke, or -1.</summary>
        public int chunkIndex = -1;

        public void RecomputeBounds()
        {
            if (points.Count == 0)
            {
                boundsMin = boundsMax = Vector3.zero;
                return;
            }
            Vector3 mn = points[0], mx = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                mn = Vector3.Min(mn, points[i]);
                mx = Vector3.Max(mx, points[i]);
            }
            boundsMin = mn;
            boundsMax = mx;
        }

        public float PathLength()
        {
            float len = 0f;
            for (int i = 1; i < points.Count; i++) len += (points[i] - points[i - 1]).magnitude;
            return len;
        }
    }

    [Serializable]
    public class StrokeBuildSettings
    {
        [Tooltip("Minimum spacing between stored points, millimetres. Small enough to keep the corners of 5 mm handwriting.")]
        public float spacingMm = 0.4f;
        [Tooltip("One Euro filter minimum cutoff in Hz. 0 disables smoothing.")]
        public float minCutoffHz = 6f;
        [Tooltip("One Euro filter speed coefficient, Hz per m/s.")]
        public float beta = 150f;
        public int maxPointsPerStroke = 4000;
        public int maxRawPerStroke = 8000;
    }

    /// <summary>
    /// Turns accepted tip samples into a stroke: stores the raw sample, applies light smoothing,
    /// and keeps a rendered point only when it moved at least the spacing distance.
    /// The newest position is always present as a provisional "tail" point, so the ink reaches
    /// the tip immediately instead of waiting for the spacing to be exceeded.
    /// </summary>
    public sealed class StrokeBuilder
    {
        StrokeData _stroke;
        StrokeBuildSettings _settings;
        OneEuroFilter _filter;
        int _committed;
        double _startTime;
        double _lastTime;
        Vector3 _lastRaw;
        bool _line;

        public StrokeData Stroke => _stroke;
        public bool IsActive => _stroke != null;

        public void Begin(StrokeData stroke, StrokeBuildSettings settings, Vector3 point, double time, float confidence, bool straightLine)
        {
            _stroke = stroke;
            _settings = settings;
            _line = straightLine;
            stroke.isStraightLine = straightLine;
            _filter = new OneEuroFilter(straightLine ? 0f : settings.minCutoffHz, settings.beta);
            _startTime = time;
            _lastTime = time;
            _lastRaw = point;
            stroke.points.Clear();
            stroke.raw.Clear();
            stroke.raw.Add(new RawSample { position = point, time = 0f, confidence = confidence });
            Vector3 p = _filter.Filter(Flatten(point), 0f);
            stroke.points.Add(p);
            _committed = 1;
        }

        /// <summary>Returns false when the stroke has reached its point budget and should be ended.</summary>
        public bool Add(Vector3 point, double time, float confidence)
        {
            if (_stroke == null) return false;
            float dt = (float)(time - _lastTime);
            _lastTime = time;
            _lastRaw = point;
            if (_stroke.raw.Count < _settings.maxRawPerStroke)
                _stroke.raw.Add(new RawSample { position = point, time = (float)(time - _startTime), confidence = confidence });

            Vector3 f = _filter.Filter(Flatten(point), dt);
            var pts = _stroke.points;

            if (_line)
            {
                if (pts.Count < 2) pts.Add(f); else pts[1] = f;
                return true;
            }

            float spacing = _settings.spacingMm * 0.001f;
            if ((f - pts[_committed - 1]).sqrMagnitude >= spacing * spacing)
            {
                if (pts.Count > _committed) pts[_committed] = f; else pts.Add(f);
                _committed++;
            }
            else
            {
                if (pts.Count > _committed) pts[_committed] = f; else pts.Add(f);
            }
            return _committed < _settings.maxPointsPerStroke;
        }

        /// <summary>
        /// Finishes the stroke. The last point is snapped to the last raw sample so the stroke
        /// ends where the tip actually was rather than where the filter had caught up to.
        /// A stroke that never moved stays a single point and is rendered as a dot.
        /// </summary>
        public StrokeData End()
        {
            StrokeData s = _stroke;
            if (s == null) return null;
            var pts = s.points;
            Vector3 last = Flatten(_lastRaw);
            if (pts.Count > _committed) pts[_committed] = last;      // replace provisional tail
            else if (pts.Count > 1) pts[pts.Count - 1] = last;        // pull final committed point to the tip
            // Drop a tail that adds nothing (keeps taps as clean single-point dots).
            if (pts.Count >= 2 && (pts[pts.Count - 1] - pts[pts.Count - 2]).sqrMagnitude < 1e-10f)
                pts.RemoveAt(pts.Count - 1);
            s.RecomputeBounds();
            _stroke = null;
            return s;
        }

        Vector3 Flatten(Vector3 p)
        {
            // Board strokes live in the plane; height is kept only in the raw samples.
            return _stroke.space == StrokeSpace.Board ? new Vector3(p.x, p.y, 0f) : p;
        }

        /// <summary>
        /// Recomputes the rendered points of a finished stroke from its raw samples with
        /// different settings. Lets smoothing be tuned without recapturing.
        /// </summary>
        public static void RebuildFromRaw(StrokeData stroke, StrokeBuildSettings settings)
        {
            if (stroke.raw.Count == 0 || stroke.isStraightLine) return;
            var raw = new List<RawSample>(stroke.raw);
            var b = new StrokeBuilder();
            b.Begin(stroke, settings, raw[0].position, raw[0].time, raw[0].confidence, false);
            for (int i = 1; i < raw.Count; i++) b.Add(raw[i].position, raw[i].time, raw[i].confidence);
            b.End();
        }
    }
}
