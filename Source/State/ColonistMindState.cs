using System.Collections.Generic;
using AIColonists.Inference;
using RimWorld; // TraitSet / Trait — the trait-weight seeding source (pawn.story.traits.allTraits)
using Verse;

namespace AIColonists.State
{
    /// <summary>
    /// Per-pawn persistent mind (Spec §4.1 differentiation stack): identity, continuous trait weights, four-part
    /// memory store, and relational vectors. Saved via AIColonistWorldComponent (Spec §5.2).
    /// </summary>
    public class ColonistMindState : IExposable
    {
        public string pawnName = "";

        // Differentiation layer 2: continuous 0..1 trait weights, initialized from vanilla traits so two pawns with
        // the same vanilla trait can still differ in degree (Spec §4.1).
        public Dictionary<string, float> traitWeights = new Dictionary<string, float>();

        public MemoryStore memory = new MemoryStore();
        public List<RelationshipVector> relationships = new List<RelationshipVector>();

        // Layer 3 bookkeeping (Spec §7.4): staggered consolidation cadence + most recent long-term longing,
        // fed back into the static identity prefix so it stays KV-cached.
        public int lastLayer3Tick;
        public string longTermLonging = "";

        // Phase 4 (UI Inspector, Spec §9): the last inner monologue that actually turned into a job. Written on
        // the MAIN thread by JobGiver_AIColonistDesire when a popped desire starts — mind state is not thread-safe
        // and Layer 1 results arrive on background workers, so the worker itself never touches this object.
        public string lastInnerVoice = "";
        public int lastInnerVoiceTick;

        public ColonistMindState() { }

        public ColonistMindState(Pawn pawn)
        {
            InitializeFrom(pawn);
        }

        private void InitializeFrom(Pawn pawn)
        {
            // Live-verified NRE on 1.6 (2026-11-14, save load): thoughts are granted while the pawn is only half-deserialized,
            // so Pawn.Label falls back to KindLabel → GenLabel.BestKindLabel, which derefs kind/race defs still NULL at that point.
            // Leave the name empty rather than store garbage — AIColonistWorldComponent.GetOrCreateMindState self-heals it on a
            // later tick once the pawn is fully loaded (pawnName is only consumed by the Layer 3 system prompt).
            pawnName = AIColonists.Prompts.PawnText.ShortName(pawn) ?? ""; // never LabelCap (title + color tags)

            // TODO(Phase 2): seed traitWeights from pawn.story.traits (e.g., Stoic → "stoic": 0.8, Jumpy → "anxious": 0.7)
            //   and pull backstory lines from pawn.story.bio for the identity prefix (Spec §4.1 layer 1).
        }

        /// <summary>Finds (or creates) the relationship vector toward a target pawn, keyed by ThingID (Spec §4.2).</summary>
        public RelationshipVector GetOrCreateRelationship(string targetPawnId)
        {
            if (string.IsNullOrEmpty(targetPawnId)) return null;
            foreach (RelationshipVector rv in relationships)
                if (rv != null && rv.targetPawnId == targetPawnId) return rv;

            var fresh = new RelationshipVector { targetPawnId = targetPawnId };
            relationships.Add(fresh);
            return fresh;
        }

        /// <summary>READ-ONLY lookup (Phase 2 relational framing): null when no vector exists yet — unlike
        /// GetOrCreateRelationship, this never plants a default-valued vector as a side effect.</summary>
        public RelationshipVector GetRelationship(string targetPawnId)
        {
            if (string.IsNullOrEmpty(targetPawnId)) return null;
            foreach (RelationshipVector rv in relationships)
                if (rv != null && rv.targetPawnId == targetPawnId) return rv;
            return null;
        }

        /// <summary>Phase 2 differentiation (Spec §4.1 layer 2): seed the continuous trait weights from the pawn's vanilla
        /// traits so two pawns with the same trait still differ by degree, and the identity prefix has real material.
        /// Keyed by lower-cased trait label; weight = 0.5 + 0.15·degree clamped to [0.05, 0.95]. Idempotent — only called
        /// while the map is empty (fresh state or pre-Phase-2 save), so Layer 3 drift never gets overwritten.</summary>
        public static void SeedTraitWeightsFromPawn(ColonistMindState mind, Pawn pawn)
        {
            if (mind == null || pawn == null || mind.traitWeights.Count != 0) return;
            try
            {
                // [CONFIRMED decompiled 2026-11-15] 1.6: TraitSet lives on the story tracker — pawn.story.traits.allTraits.
                var all = pawn.story?.traits?.allTraits;
                if (all == null) return;
                foreach (Trait t in all)
                {
                    if (t == null || t.Suppressed) continue;
                    string key = t.LabelCap.ToLower();
                    if (string.IsNullOrEmpty(key)) continue;
                    float w = 0.5f + 0.15f * t.Degree;
                    mind.traitWeights[key] = UnityEngine.Mathf.Clamp(w, 0.05f, 0.95f);
                }
            }
            catch { /* half-loaded pawn — the lazy re-seed on a later snapshot capture will pick it up */ }
        }

        /// <summary>
        /// Applies a Layer 3 consolidation result (Spec §7.4): semantic memory with dedup, longing feedback into the
        /// identity prefix, relationship drift clamped to ±0.05 per axis, trait drift clamped to ±0.02, and episodic
        /// pruning of entries older than ~30 in-game days. Main thread only.
        /// </summary>
        public void ApplyLayer3Result(Layer3Result result, Dictionary<string, string> nameToThingId, int nowTick)
        {
            if (result == null) return;

            // 1. Distilled self-knowledge — deduped against existing semantic memories (consolidation repeats every ~5 min).
            if (!string.IsNullOrEmpty(result.new_semantic_memory))
            {
                bool dup = false;
                foreach (string s in memory.semantic)
                    if (s == result.new_semantic_memory) { dup = true; break; }
                if (!dup) memory.AddSemantic(result.new_semantic_memory);
            }

            // 2. Long-term longing feeds back into the static identity prefix so it stays KV-cached (Spec §7.4).
            if (!string.IsNullOrEmpty(result.long_term_longing))
                longTermLonging = result.long_term_longing;

            // 3. Relationship drift — server values clamped to ±0.05 per axis (Spec §7.4); names resolved to ThingIDs.
            foreach (RelationshipDelta delta in result.relationship_deltas)
            {
                if (delta == null || string.IsNullOrEmpty(delta.target_name)) continue;
                string thingId;
                if (nameToThingId == null || !nameToThingId.TryGetValue(delta.target_name, out thingId)) continue;

                RelationshipVector rv = GetOrCreateRelationship(thingId);
                rv.ApplyDeltas(
                    UnityEngine.Mathf.Clamp(delta.trust_delta, -0.05f, 0.05f),
                    UnityEngine.Mathf.Clamp(delta.affection_delta, -0.05f, 0.05f),
                    UnityEngine.Mathf.Clamp(delta.respect_delta, -0.05f, 0.05f),
                    UnityEngine.Mathf.Clamp(delta.frustration_delta, -0.05f, 0.05f));
            }

            // 4. Trait drift — clamped to ±0.02 per cycle (Spec §7.4).
            if (result.trait_drift != null && !string.IsNullOrEmpty(result.trait_drift.trait_name))
            {
                float current;
                traitWeights.TryGetValue(result.trait_drift.trait_name, out current);
                traitWeights[result.trait_drift.trait_name] =
                    UnityEngine.Mathf.Clamp(current + UnityEngine.Mathf.Clamp(result.trait_drift.delta, -0.02f, 0.02f), 0f, 1f);
            }

            // 5. Consolidation prunes episodic memories older than ~30 in-game days (Spec §7.4).
            memory.PruneEpisodicOlderThan(nowTick);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnName, "pawnName", "");
            Scribe_Collections.Look(ref traitWeights, "traitWeights", LookMode.Value, LookMode.Value);
            Scribe_Deep.Look(ref memory, "memory");
            Scribe_Collections.Look(ref relationships, "relationships", LookMode.Deep);
            Scribe_Values.Look(ref lastLayer3Tick, "lastLayer3Tick", 0);
            Scribe_Values.Look(ref longTermLonging, "longTermLonging", "");
            Scribe_Values.Look(ref lastInnerVoice, "lastInnerVoice", "");
            Scribe_Values.Look(ref lastInnerVoiceTick, "lastInnerVoiceTick", 0);
        }
    }
}
