// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the wind the decorations move in, and short blasts (gun fire). Put one in the scene to set
// the wind; without one there is a light breeze. Blasts are kept in a small list that the motion component reads.
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD.Details.Decor
{
    [AddComponentMenu("SMC-AD/SMC-DETAILS 装備の風")]
    public class SMDecorWind : MonoBehaviour
    {
        [Tooltip("風の向き（この物体の向きに対してではなく、世界の向き）")] public Vector3 direction = new Vector3(1f, 0f, 0.4f);
        [Tooltip("風速 (m/s)")] [Range(0f, 25f)] public float speed = 4f;
        [Tooltip("突風の強さ（風速の揺らぎ）")] [Range(0f, 1f)] public float gust = 0.5f;
        [Tooltip("発砲の爆風の強さ（倍率）")] [Range(0f, 3f)] public float blastScale = 1f;
        [Tooltip("エディターでも動かす（再生していない時）")] public bool inEditor;

        public static SMDecorWind Active;
        public struct Blast { public Vector3 position, direction; public float strength, born; }
        static readonly List<Blast> blasts = new List<Blast>();
        public const float BlastLife = 0.8f;

        void OnEnable() { Active = this; }
        void OnDisable() { if (Active == this) Active = null; }

        /// <summary>Wind velocity (m/s, world) at time t, with gusts.</summary>
        public static Vector3 Velocity(float t)
        {
            var w = Active;
            Vector3 dir = w != null && w.direction.sqrMagnitude > 1e-6f ? w.direction.normalized : new Vector3(0.9f, 0f, 0.4f).normalized;
            float spd = w != null ? w.speed : 2f, g = w != null ? w.gust : 0.4f;
            float gustF = 1f + g * (Mathf.PerlinNoise(t * 0.35f, 0.7f) * 1.6f - 0.8f);
            return dir * spd * Mathf.Max(0f, gustF);
        }

        public static bool RunInEditor => Active != null && Active.inEditor;

        /// <summary>A short push outwards from a point (a gun firing). strength ~ 1 for a main gun.</summary>
        public static void AddBlast(Vector3 position, Vector3 direction, float strength)
        {
            float scale = Active != null ? Active.blastScale : 1f;
            blasts.Add(new Blast { position = position, direction = direction.normalized, strength = strength * scale, born = Now });
            if (blasts.Count > 8) blasts.RemoveAt(0);
        }

        /// <summary>The live blasts (newest last) and how much each still pushes (1 at once, 0 when gone).</summary>
        public static IEnumerable<(Blast b, float left)> Blasts()
        {
            float now = Now;
            for (int i = blasts.Count - 1; i >= 0; i--)
            {
                float age = now - blasts[i].born;
                if (age > BlastLife || age < 0f) { blasts.RemoveAt(i); continue; }
            }
            foreach (var b in blasts) yield return (b, Mathf.Exp(-(now - b.born) * 6f));
        }

        /// <summary>Set (>= 0) to drive the clock by hand (tests, recording); -1 = game time.</summary>
        public static float Clock = -1f;
        public static float Now => Clock >= 0f ? Clock : Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartupAsDouble;
    }
}
