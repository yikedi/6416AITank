using UnityEngine;

namespace CE6127.Tanks.AI
{
    /// <summary>
    /// Class <c>OrbitTracker</c> detects that the target is kiting — circling a fixed point —
    /// and estimates the centre and radius of that loop.
    /// <para>
    /// The centre is recovered by least-squares circle fitting over the target's recent path
    /// rather than by probing for obstacles, so it works the same whether the player is
    /// circling a building, a rock cluster, or open ground. A rounded-rectangle path around a
    /// square building still fits well, whereas the instantaneous turn centre (v / omega) would
    /// jump between corners.
    /// </para>
    /// <para>
    /// The state is static and advanced once per frame so that every tank in the platoon reads
    /// exactly the same pivot, which is what makes the role assignment in
    /// <see cref="TankSM.DecideRingMove"/> consistent across the platoon.
    /// </para>
    /// </summary>
    internal static class OrbitTracker
    {
        private const int Samples = 12;                  // Path samples kept.
        private const float Window = 3f;                 // Seconds of history.
        private const float SampleInterval = Window / Samples;
        private const float EnterSweptDeg = 100f;        // Swept heading needed to declare kiting.
        private const float ExitSweptDeg = 55f;          // Schmitt trigger: lower bar to stay in.
        private const float MinRadius = 5f;
        private const float MaxRadius = 45f;
        private const float StaleSeconds = 0.5f;

        // Shared state. s_Frame gates the update to once per frame; the rest is the path history
        // and the estimate fitted from it.
        private static Transform s_Target;
        private static int s_Frame = -1;
        private static float s_LastTickTime = -99f;
        private static readonly Vector3[] s_Pos = new Vector3[Samples];
        private static readonly float[] s_Heading = new float[Samples];
        private static int s_Index;
        private static int s_Filled;
        private static float s_NextSample;
        private static float s_CumHeading;               // Unwrapped, monotonic while turning.
        private static float s_PrevHeading;
        private static bool s_Orbiting;

        /// <summary>Whether the target is currently circling a fixed point.</summary>
        public static bool IsOrbiting => s_Orbiting;

        /// <summary>Centre of the loop the target is describing.</summary>
        public static Vector3 Pivot { get; private set; }

        /// <summary>Radius of the fitted loop.</summary>
        public static float Radius { get; private set; }

        /// <summary>Signed heading swept over the history window (degrees).</summary>
        public static float SweptDeg { get; private set; }

        /// <summary>+1 or -1: the direction the target travels around <see cref="Pivot"/>.</summary>
        public static float Sign => SweptDeg >= 0f ? 1f : -1f;

        public static void Reset()
        {
            s_Target = null;
            s_Index = 0;
            s_Filled = 0;
            s_CumHeading = 0f;
            SweptDeg = 0f;
            s_Orbiting = false;
            Pivot = Vector3.zero;
            Radius = 0f;
        }

        /// <summary>
        /// Advances the estimate. Safe to call from every tank: only the first call in a frame
        /// does any work.
        /// </summary>
        public static void Tick(Transform target)
        {
            if (s_Frame == Time.frameCount)
                return;
            s_Frame = Time.frameCount;

            if (target == null)
            {
                Reset();
                return;
            }

            // A new target, or a gap in ticking (round change), invalidates the history.
            if (target != s_Target || Time.time - s_LastTickTime > StaleSeconds)
            {
                Reset();
                s_Target = target;
                s_PrevHeading = target.eulerAngles.y;
                s_NextSample = Time.time;
            }
            s_LastTickTime = Time.time;

            // Unwrapped heading: a circling target accumulates monotonically, a weaving one
            // cancels out. This is the primary "is this kiting" signal.
            float heading = target.eulerAngles.y;
            s_CumHeading += Mathf.DeltaAngle(s_PrevHeading, heading);
            s_PrevHeading = heading;

            if (Time.time >= s_NextSample)
            {
                s_NextSample = Time.time + SampleInterval;
                s_Pos[s_Index] = target.position;
                s_Heading[s_Index] = s_CumHeading;
                s_Index = (s_Index + 1) % Samples;
                if (s_Filled < Samples)
                    ++s_Filled;
            }

            if (s_Filled < Samples)
            {
                s_Orbiting = false;
                return;
            }

            SweptDeg = s_CumHeading - s_Heading[s_Index]; // s_Index now points at the oldest entry.

            bool fitted = FitCircle(out Vector3 centre, out float radius);
            if (fitted)
            {
                Pivot = centre;
                Radius = radius;
            }

            float swept = Mathf.Abs(SweptDeg);
            s_Orbiting = fitted && swept >= (s_Orbiting ? ExitSweptDeg : EnterSweptDeg);
        }

        /// <summary>
        /// Algebraic (Kasa) least-squares circle fit over the path history. Solving
        /// x^2 + z^2 = A*x + B*z + C is linear, so a single 3x3 solve is enough.
        /// Rejects fits that are collinear, out of range, or simply not circular.
        /// </summary>
        private static bool FitCircle(out Vector3 centre, out float radius)
        {
            centre = Vector3.zero;
            radius = 0f;

            // Centre the samples first so the normal equations stay well conditioned.
            float mx = 0f;
            float mz = 0f;
            for (int i = 0; i < Samples; ++i)
            {
                mx += s_Pos[i].x;
                mz += s_Pos[i].z;
            }
            mx /= Samples;
            mz /= Samples;

            double suu = 0;
            double svv = 0;
            double suv = 0;
            double su = 0;
            double sv = 0;
            double sw = 0;
            double suw = 0;
            double svw = 0;
            for (int i = 0; i < Samples; ++i)
            {
                double u = s_Pos[i].x - mx;
                double v = s_Pos[i].z - mz;
                double w = u * u + v * v;
                suu += u * u;
                svv += v * v;
                suv += u * v;
                su += u;
                sv += v;
                sw += w;
                suw += u * w;
                svw += v * w;
            }

            double n = Samples;
            double det = suu * (svv * n - sv * sv) - suv * (suv * n - sv * su) + su * (suv * sv - svv * su);
            if (System.Math.Abs(det) < 1e-6)
                return false; // Collinear: the target is going straight.

            double d1 = suw * (svv * n - sv * sv) - suv * (svw * n - sv * sw) + su * (svw * sv - svv * sw);
            double d2 = suu * (svw * n - sv * sw) - suw * (suv * n - sv * su) + su * (suv * sw - svw * su);
            double d3 = suu * (svv * sw - svw * sv) - suv * (suv * sw - svw * su) + suw * (suv * sv - svv * su);

            double a = 0.5 * (d1 / det);
            double b = 0.5 * (d2 / det);
            double rr = (d3 / det) + a * a + b * b;
            if (rr <= 0.0)
                return false;

            double r = System.Math.Sqrt(rr);
            if (r < MinRadius || r > MaxRadius)
                return false;

            // Reject anything that is not actually close to a circle (zig-zag, dog-leg).
            double rms = 0.0;
            for (int i = 0; i < Samples; ++i)
            {
                double u = s_Pos[i].x - mx - a;
                double v = s_Pos[i].z - mz - b;
                double e = System.Math.Sqrt(u * u + v * v) - r;
                rms += e * e;
            }
            rms = System.Math.Sqrt(rms / Samples);
            if (rms > System.Math.Max(3.0, 0.30 * r))
                return false;

            centre = new Vector3((float)(mx + a), s_Pos[0].y, (float)(mz + b));
            radius = (float)r;
            return true;
        }
    }
}
