using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// One Euro filter (Casiez, Roussel and Vogel, 2012) on a 3D point. A low-pass filter whose
    /// cutoff rises with speed: more smoothing while the tip is nearly still (where jitter is
    /// visible), less during fast strokes (where lag and rounded corners are visible).
    ///
    /// Smoothing only reduces random noise. It cannot fix a constant offset, so alignment
    /// errors must be corrected by calibration, not by raising the smoothing.
    /// minCutoffHz &lt;= 0 disables filtering.
    /// </summary>
    public struct OneEuroFilter
    {
        public float minCutoffHz;
        public float beta;          // cutoff increase in Hz per (m/s) of tip speed
        public float derivCutoffHz;

        bool _has;
        Vector3 _x;
        Vector3 _dx;

        public OneEuroFilter(float minCutoffHz, float beta, float derivCutoffHz = 4f)
        {
            this.minCutoffHz = minCutoffHz;
            this.beta = beta;
            this.derivCutoffHz = derivCutoffHz;
            _has = false;
            _x = default;
            _dx = default;
        }

        public void Reset()
        {
            _has = false;
            _dx = default;
        }

        static float Alpha(float cutoffHz, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoffHz);
            return 1f / (1f + tau / dt);
        }

        public Vector3 Filter(Vector3 x, float dt)
        {
            if (minCutoffHz <= 0f || !_has || dt <= 0f)
            {
                _has = true;
                _x = x;
                _dx = default;
                return x;
            }
            Vector3 dx = (x - _x) / dt;
            _dx = Vector3.Lerp(_dx, dx, Alpha(derivCutoffHz, dt));
            float cutoff = minCutoffHz + beta * _dx.magnitude;
            _x = Vector3.Lerp(_x, x, Alpha(cutoff, dt));
            return _x;
        }

        /// <summary>
        /// Approximate lag in milliseconds at a given tip speed, from the time constant of a
        /// first-order low-pass (tau = 1 / (2*pi*fc)). An estimate from the filter settings,
        /// not a measurement of end-to-end latency.
        /// </summary>
        public static float EstimatedDelayMs(float minCutoffHz, float beta, float speedMps)
        {
            if (minCutoffHz <= 0f) return 0f;
            return 1000f / (2f * Mathf.PI * (minCutoffHz + beta * speedMps));
        }
    }
}
