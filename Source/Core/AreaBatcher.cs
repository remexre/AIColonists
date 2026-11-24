using System;
using System.Collections.Generic;
using AIColonists.Inference;
using AIColonists.Prompts;
using AIColonists.State;
using RimWorld;
using Verse;

namespace AIColonists.Core
{
    /// <summary>
    /// Area-based batching (Spec §3.4): colonists sharing an enclosed Room (or a 12-tile outdoor cluster) get ONE
    /// Layer 2 multi-agent prompt instead of N individual calls, enabling synchronized social behavior. Phase 3: also
    /// builds the request/prompt and maps impulses to interaction defs.
    /// </summary>
    public static class AreaBatcher
    {
        private const int OutdoorClusterRadius = 12;

        // Phase 4 (Spec §9, 50-colonist stress test): coarse grid for the district batching fallback. A ~30-tile cell
        // merges several adjacent rooms/outdoor clusters so ONE Layer 2 call covers a whole district instead of
        // silently dropping spaces when eligible batches exceed the per-cycle cap.
        public const int DistrictGridSize = 30;

        /// <summary>Groups the map's spawned free colonists into batch keys (Room or outdoor cluster). Main thread only.</summary>
        public static Dictionary<string, List<Pawn>> BuildBatches(Map map)
        {
            var batches = new Dictionary<string, List<Pawn>>();

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Drafted || pawn.InMentalState) continue; // Spec §3.4: only decide for pawns that need to
                if (AIColonists.Prompts.PawnText.IsBaby(pawn)) continue; // babies don't take part in room social (2026-11-22)

                string key;
                // 1.6 verified: Pawn has no room accessor — RegionAndRoomQuery.GetRoom(Thing, RegionType) is the
                // canonical lookup. RegionType is a [Flags] enum in 1.6 (no PassableOnly member); Set_Passable = Normal|Portal|Fence.
                Room room = RegionAndRoomQuery.GetRoom(pawn, RegionType.Set_Passable);
                if (room != null && room.Map == map)
                    key = "room:" + room.ID;
                else
                    key = OutdoorClusterKey(pawn.Position);

                List<Pawn> list;
                if (!batches.TryGetValue(key, out list))
                {
                    list = new List<Pawn>();
                    batches[key] = list;
                }
                list.Add(pawn);
            }
            return batches;
        }

        /// <summary>Quantizes outdoor positions into 12-tile grid cells so nearby pawns share a key (Spec §3.4).</summary>
        private static string OutdoorClusterKey(IntVec3 pos)
        {
            int gx = pos.x / OutdoorClusterRadius;
            int gz = pos.z / OutdoorClusterRadius;
            return "outdoor:" + gx + "," + gz;
        }

        /// <summary>
        /// Phase 4 (Spec §9, 50-colonist stress test): picks the batches to dispatch this cycle. Normal path — one
        /// batch per shared space (room / outdoor cluster) with 2+ colonists. When eligible spaces exceed maxBatches
        /// (a large colony with many small rooms), DYNAMIC DISTRICT BATCHING takes over: every eligible pawn is re-keyed
        /// onto the coarse ~30-tile grid, so one call covers several adjacent spaces; districts are ordered by pawn
        /// count (biggest first) and truncated to the cap — coverage instead of silent drops. Note: cross-space pairs
        /// in a merged district simply fail the 6-cell proximity pre-check at consume time and re-roll next cycle, so
        /// merging is safe. Pure function of map state — main thread only; harness-tested.
        /// </summary>
        public static List<List<Pawn>> SelectBatchesForDispatch(Map map, int maxBatches)
        {
            var eligible = new List<List<Pawn>>();
            foreach (KeyValuePair<string, List<Pawn>> kv in BuildBatches(map))
                if (kv.Value.Count >= 2) eligible.Add(kv.Value);

            if (eligible.Count <= maxBatches) return eligible; // normal path — no fallback needed

            var districts = new Dictionary<string, List<Pawn>>();
            foreach (List<Pawn> batch in eligible)
                foreach (Pawn pawn in batch)
                {
                    int gx = pawn.Position.x / DistrictGridSize;
                    int gz = pawn.Position.z / DistrictGridSize;
                    string key = "district:" + gx + "," + gz;
                    List<Pawn> list;
                    if (!districts.TryGetValue(key, out list))
                    {
                        list = new List<Pawn>();
                        districts[key] = list;
                    }
                    list.Add(pawn);
                }

            var result = new List<List<Pawn>>();
            foreach (KeyValuePair<string, List<Pawn>> kv in districts)
                if (kv.Value.Count >= 2) result.Add(kv.Value);

            result.Sort(delegate (List<Pawn> a, List<Pawn> b) { return b.Count.CompareTo(a.Count); }); // biggest district first — max coverage per call
            if (result.Count > maxBatches) result.RemoveRange(maxBatches, result.Count - maxBatches);
            return result;
        }

        /// <summary>Assembles the Layer 2 request for one shared-space batch (Spec §7.3). Main thread only — reads pawn state.</summary>
        public static Layer2Request BuildLayer2Request(List<Pawn> batch, Dictionary<string, string> thingIdToName)
        {
            Settings s = AIColonistMod.Settings;

            var req = new Layer2Request();
            req.model = s != null ? s.Layer2Model : "qwen2.5-1.5b-instruct-q4_k_m"; // ignored by single-model servers (live-verified)
            req.systemPrompt = Layer2PromptBuilder.BuildSystemPrompt();
            req.temperature = s != null ? s.BaseTemperature : 1.0f;
            req.minP = s != null ? s.MinP : 0.05f;
            req.maxTokens = s != null ? s.Layer2MaxOutputTokens : 150;
            req.disableReasoning = s != null && s.DisableReasoning;

            var notes = new Dictionary<string, string>();
            foreach (Pawn pawn in batch)
                notes[pawn.ThingID] = BuildPawnNote(pawn, thingIdToName);

            req.userPrompt = Layer2PromptBuilder.BuildUserPrompt(batch, notes);

            var names = new List<string>();
            foreach (Pawn pawn in batch) names.Add(PawnText.ShortName(pawn) ?? pawn.ThingID);
            req.logLabel = string.Join(", ", names); // prompt log only
            return req;
        }

        /// <summary>Maps a Layer 2 social_impulse to its InteractionDef (Spec §7.3). Null = memory-only impulse
        /// (none / quiet_nod / avoid_gaze) — those never call TryInteractWith.</summary>
        public static InteractionDef DefForImpulse(string impulse)
        {
            switch (impulse)
            {
                case "warm_chat": return DefDatabase<InteractionDef>.GetNamed("AIColonist_WarmChat", false);
                case "vent_frustration": return DefDatabase<InteractionDef>.GetNamed("AIColonist_VentFrustration", false);
                case "sharp_remark": return DefDatabase<InteractionDef>.GetNamed("AIColonist_SharpRemark", false);
                default: return null; // none | quiet_nod | avoid_gaze | unknown → episodic memory + nudge only
            }
        }

        /// <summary>One relationship-color note per pawn for the Layer 2 roster (Spec §7.3): strongest feeling wins —
        /// frustration ≥0.55 beats affection ≥0.65. Empty string when there is nothing worth saying.</summary>
        public static string BuildPawnNote(Pawn pawn, Dictionary<string, string> thingIdToName)
        {
            try
            {
                if (pawn == null || Find.World == null) return "";
                AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
                if (world == null) return "";

                ColonistMindState mind = world.GetOrCreateMindState(pawn);
                if (mind == null || mind.relationships.Count == 0) return "";

                string irritatedBy = null, warmToward = null;
                float bestFrustration = 0f, bestAffection = 0f;
                foreach (RelationshipVector rv in mind.relationships)
                {
                    if (rv == null || string.IsNullOrEmpty(rv.targetPawnId)) continue;
                    if (rv.frustration >= 0.55f && rv.frustration > bestFrustration)
                    {
                        bestFrustration = rv.frustration;
                        irritatedBy = NameOf(thingIdToName, rv.targetPawnId);
                    }
                    else if (rv.affection >= 0.65f && rv.affection > bestAffection)
                    {
                        bestAffection = rv.affection;
                        warmToward = NameOf(thingIdToName, rv.targetPawnId);
                    }
                }

                if (irritatedBy != null) return "feels irritated by " + irritatedBy;
                if (warmToward != null) return "feels warm toward " + warmToward;
                return "";
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[AIColonists] BuildPawnNote: " + e.Message, 4813); // observational only — a bad note must never break the batch
                return "";
            }
        }

        private static string NameOf(Dictionary<string, string> thingIdToName, string thingId)
        {
            string name;
            if (thingIdToName != null && thingIdToName.TryGetValue(thingId, out name) && !string.IsNullOrEmpty(name))
                return name;
            return "someone";
        }
    }
}
