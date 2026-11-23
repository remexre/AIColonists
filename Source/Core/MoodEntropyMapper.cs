using Verse;

namespace AIColonists.Core
{
    /// <summary>
    /// Mood → Sampling Entropy mapper (Spec §3.2). Decouples cognitive depth (Intellect) from entropy (Mood): mood nudges
    /// Layer 1's temperature up or down from the player's base temperature.
    /// 2026-11-22: the offset is now RELATIVE to the base, clamped only to the API's legal range. It used to be hard-clamped
    /// to [0.50, 0.85] (tuned for 1.5B-14B local models), which silently capped any base temperature the player set
    /// above ~0.8; hosted models like Inkling are benchmarked at 1.0. At the old default base 0.65 the results match the
    /// old band (0.50-0.85). top_p is no longer mood-mapped: min_p already trims the tail, and stacking both cuts variety.
    /// </summary>
    public static class MoodEntropyMapper
    {
        public const float MinTemperature = 0f;
        public const float MaxTemperature = 2f; // OpenAI-compatible APIs reject anything above 2

        // Spec §3.2 MOOD_ENTROPY_MAP, keyed by mood tier: temperature offsets from the base.
        private static readonly System.Collections.Generic.Dictionary<string, float> Table =
            new System.Collections.Generic.Dictionary<string, float>
        {
            { "ecstatic",  +0.05f },
            { "happy",      0.00f },
            { "content",   -0.05f }, // predictable, steady routine
            { "stressed",  +0.10f }, // slightly erratic
            { "furious",   +0.20f }, // impulsive, sharp
            { "depressed", -0.15f }, // flat, repetitive, low-energy
        };

        /// <summary>
        /// Phase 2 (Spec §3.2): map a mood percentage (0..1, from pawn.needs.mood.CurLevelPercentage) to a tier key.
        /// Bands mirror the vanilla mood curve: comfortable above ~0.6, strained mid-range, and the two low-mood poles —
        /// flat/depressed just below break threshold, impulsive/furious near the horrible-break zone where vanilla
        /// colonists actually go berserk (so "furious" = erratic sampling there is behaviorally faithful).
        /// Values outside 0..1 (unknown mood) fall back to the neutral "content" baseline.
        /// </summary>
        public static string MoodTier(float moodPct)
        {
            if (moodPct < 0f || moodPct > 1f) return "content";
            if (moodPct >= 0.75f) return "ecstatic";
            if (moodPct >= 0.60f) return "happy";
            if (moodPct >= 0.45f) return "content";
            if (moodPct >= 0.30f) return "stressed";
            if (moodPct >= 0.15f) return "depressed";
            return "furious";
        }

        /// <summary>Main-thread convenience: tier from the pawn's mood need (null-safe — some pawns have no mood need).</summary>
        public static string MoodTier(Pawn pawn)
        {
            if (pawn == null || pawn.needs == null || pawn.needs.mood == null) return "content";
            try { return MoodTier(pawn.needs.mood.CurLevelPercentage); }
            catch { return "content"; } // defensive rule — a throwing mood read must never break the caller
        }

        private static float OffsetFor(string tier)
        {
            float o;
            if (!Table.TryGetValue(tier, out o))
                o = Table["content"]; // defensive — every tier key the mappers emit is in the table
            return o;
        }

        /// <summary>Background-thread path (Phase 2): the dispatch worker only sees the snapshot, so it passes the
        /// mood percentage captured on the main thread.</summary>
        public static float MoodToTemperature(float baseTemp, float moodPct) =>
            UnityEngine.Mathf.Clamp(baseTemp + OffsetFor(MoodTier(moodPct)), MinTemperature, MaxTemperature);

        /// <summary>Legacy main-thread overload (kept for the harness and any future pawn-side callers). A null pawn is an
        /// UNKNOWN mood — the content baseline applies.</summary>
        public static float MoodToTemperature(float baseTemp, Pawn pawn = null)
        {
            string tier = pawn == null ? "content" : MoodTier(pawn);
            return UnityEngine.Mathf.Clamp(baseTemp + OffsetFor(tier), MinTemperature, MaxTemperature);
        }
    }
}
