using System;
using System.Collections.Generic;
using AIColonists.Core;
using AIColonists.State;
using AIColonists.ThinkTree;
using RimWorld;
using Verse;

namespace AIColonists.Prompts
{
    /// <summary>Value-safe snapshot of a pawn's current experience, captured on the main thread (Spec §5.1 / §7.1).
    /// Phase 2: also carries the STATIC IDENTITY zone data (name/age/colony/traits/skills/memories) so the background
    /// dispatch worker can build the KV-cache-friendly prefix without ever touching pawn or mind state off-thread.</summary>
    public struct SensorySnapshot
    {
        public string PawnId;
        // 2026-11-15: display label for log lines — L1 logs used to print the raw ThingID ("Human1006"), which reads like a
        // different pawn than the LabelCap-based thinks:/mote lines. PawnId stays the buffer/call-state key (stable across renames).
        public string PawnName;
        public int CaptureTick;

        // --- Phase 2: static identity zone (Spec §7.1) — changes only on Layer 3 consolidation / rare events, so it stays KV-cached ---
        public int AgeYears;                  // -1 = unknown (half-loaded pawn)
        public string ColonyName;             // "" = unknown (no map parent)
        public List<string> RecentThoughts;   // the pawn's last Layer 1 inner_voice lines, oldest first (anti-repeat); set by EnqueueAsyncLayer1
        public string Title;                  // full backstory title, lower case ("dreaded dude"), or ""
        public string ChildhoodStory;         // the game's own backstory prose (PawnText.Story), or ""
        public string AdulthoodStory;         // the game's own backstory prose, or "" (children have none)
        public List<string> VoiceLines;       // how their traits sound in their head (PawnText.VoiceFor), ≤3
        public string TraitsLine;             // "Others would describe you as abrasive, pessimist and night owl." or ""
        public string SkillsLine;             // "You are known around here for your mining and cooking." or ""
        public int IntellectualLevel;         // SkillDefOf.Intellectual level — cadence + colony-horizon gating (Spec §3.2); -1 = unknown
        public List<string> SemanticMemoryLines;   // ≤3 distilled self-knowledge lines from the mind state
        public string LongTermLonging;              // "" if none yet
        public List<string> ValueLines;           // top continuous trait weights (Layer 3 drift feeds these — Spec §4.1)
        public List<string> AversionLines;        // bottom continuous trait weights

        // --- Phase 2: mood → sampling entropy (Spec §3.2) — captured here because dispatch runs on a worker thread ---
        public float MoodPercentage;          // -1 = unknown (no mood need / threw)

        // --- dynamic suffix zone (re-evaluated per call) ---
        public List<string> EpisodicLines;            // recent events on the mind (intellect-gated count, Spec §3.2)
        public List<string> NearbyPawnLines;          // relational framing of people nearby (Spec §4.2)
        public List<string> BodyAndSurroundingsLines; // needs, pain, mood thoughts — zero mechanical terms (Appendix A)
        public string ColonyHorizonLine;              // intellect-gated colony awareness (Spec §3.2)
        public List<AffordanceOption> Affordances;    // numbered sensory options (optionId = index + 1)
    }

    /// <summary>
    /// Translates vanilla state into sensory prose (Spec §4.1 layer 7, Appendix A). Mechanical terms NEVER reach the
    /// prompt — this is what keeps the "Unaware" constraint intact on the input side (Spec §2.2). Main thread only.
    /// </summary>
    public static class SensoryFramingBuilder
    {
        public static SensorySnapshot CaptureMainThreadSnapshot(Pawn pawn, List<AffordanceOption> affordances)
        {
            var snap = new SensorySnapshot
            {
                PawnId = pawn.ThingID,
                PawnName = PawnText.ShortName(pawn), // prompt name + log label; may be null (save-load) — falls back to the ID
                CaptureTick = Find.TickManager.TicksGame,
                Affordances = affordances,
                BodyAndSurroundingsLines = FrameBody(pawn),
            };

            // Phase 2 (Spec §9): identity zone + dynamic relational lines. Every external read is guarded — a throwing
            // accessor on a half-loaded pawn must degrade to an empty line, never kill the pawn's think branch.
            try { CaptureIdentity(pawn, ref snap); }
            catch (Exception e) { Log.ErrorOnce("[AIColonists] identity capture threw for " + pawn.ThingID + ": " + e.Message, 4815); }

            ColonistMindState mind = SafeMindState(pawn);
            if (mind != null)
            {
                // Self-heal for old saves: trait weights were empty before Phase 2 — seed them from the vanilla traits once.
                if (mind.traitWeights.Count == 0)
                    ColonistMindState.SeedTraitWeightsFromPawn(mind, pawn);

                snap.SemanticMemoryLines = LastN(mind.memory.semantic, 3);
                snap.LongTermLonging = mind.longTermLonging ?? "";
                snap.EpisodicLines = LastN(mind.memory.EpisodicTexts(), EpisodicLineCount(snap.IntellectualLevel));
                // Continuous trait weights (drifted by Layer 3) → the Values/Fears lines. This is the feedback loop that
                // makes slow personality drift actually change how the pawn talks and chooses, not just a UI bar.
                WeightExtremes(mind.traitWeights, out snap.ValueLines, out snap.AversionLines);
            }

            snap.NearbyPawnLines = FrameNearbyPawns(pawn, mind);
            return snap;
        }

        // ---------------------------------------------------------------- Phase 2: identity zone (Spec §7.1)

        /// <summary>Fills the static-identity fields from pawn + world state. Main thread only; caller guards exceptions.</summary>
        private static void CaptureIdentity(Pawn pawn, ref SensorySnapshot snap)
        {
            // Name/age/colony — all cheap reads that can still throw on half-loaded pawns (LabelCap precedent, 2026-11-14).
            int age = -1;
            try { if (pawn.ageTracker != null) age = pawn.ageTracker.AgeBiologicalYears; } catch { }
            snap.AgeYears = age;

            string colony = "";
            try
            {
                var settlement = Find.CurrentMap?.Parent as RimWorld.Planet.Settlement;
                if (settlement != null) colony = settlement.Name ?? "";
            }
            catch { }
            snap.ColonyName = colony;

            // Title + the game's own backstory prose (2026-11-22: neither reached the prompt before, so every pawn sounded
            // the same). Static per pawn, so it stays in the KV-cached prefix.
            snap.Title = PawnText.Title(pawn);
            snap.ChildhoodStory = PawnText.Story(pawn.story?.Childhood, pawn);
            snap.AdulthoodStory = PawnText.Story(pawn.story?.Adulthood, pawn);

            // Traits → plain trait list + voice lines (how each trait sounds in their head).
            string traitsLine = "";
            var voices = new List<string>();
            try
            {
                // [CONFIRMED decompiled 2026-11-15] 1.6: the TraitSet lives on the story tracker — pawn.story.traits.allTraits.
                var all = pawn.story?.traits?.allTraits;
                if (all != null && all.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (Trait t in all)
                    {
                        if (t == null || t.Suppressed) continue;
                        // Spectrum traits already name their degree ("pessimist", "iron-willed") — no strength adverb needed.
                        parts.Add(((string)t.LabelCap ?? "").ToLowerInvariant());
                        string voice = t.def != null ? PawnText.VoiceFor(t.def.defName, t.Degree) : null;
                        if (voice != null && voices.Count < 3) voices.Add(voice);
                        if (parts.Count >= 4) break; // keep the prefix short — KV cache is the point
                    }
                    if (parts.Count > 0) traitsLine = "Others would describe you as " + JoinAnd(parts) + ".";
                }
            }
            catch { }
            snap.TraitsLine = traitsLine;
            snap.VoiceLines = voices;

            // Top skills → one stable line (names only, NOT levels — level-ups must not churn the cached prefix).
            string skillsLine = "";
            try
            {
                var records = pawn.skills?.skills;
                if (records != null)
                {
                    int best1 = -1, best2 = -1;
                    string name1 = "", name2 = "";
                    foreach (SkillRecord r in records)
                    {
                        if (r == null || r.def == null || r.levelInt < 2) continue; // only real proficiency belongs in identity
                        if (r.def == SkillDefOf.Intellectual) continue; // meta-stat — it drives cadence/horizon below, and "known for your intellectual" reads wrong
                        if (r.levelInt > best1) { best2 = best1; name2 = name1; best1 = r.levelInt; name1 = r.def.label.ToLower(); }
                        else if (r.levelInt > best2) { best2 = r.levelInt; name2 = r.def.label.ToLower(); }
                    }
                    if (!string.IsNullOrEmpty(name1))
                        skillsLine = "You are known around here for your " + (string.IsNullOrEmpty(name2) ? name1 : name1 + " and " + name2) + ".";
                }
            }
            catch { }
            snap.SkillsLine = skillsLine;

            // Intellect → cadence + colony-horizon gating (Spec §3.2). 1.6: the stat is the Intellectual skill (learning system).
            int intellect = -1;
            try
            {
                var rec = pawn.skills?.GetSkill(SkillDefOf.Intellectual);
                if (rec != null) intellect = rec.levelInt;
            }
            catch { }
            snap.IntellectualLevel = intellect;

            // Mood → entropy mapping happens at dispatch time from this percentage (worker thread never touches the pawn).
            float moodPct = -1f;
            try { if (pawn.needs?.mood != null) moodPct = pawn.needs.mood.CurLevelPercentage; } catch { }
            snap.MoodPercentage = moodPct;

            // Colony horizon — pure function of intellect tier, rendered here so the dynamic suffix stays one place.
            snap.ColonyHorizonLine = PrefixPromptBuilder.ColonyHorizonFor(snap.IntellectualLevel);
        }

        /// <summary>Top-2 weights (≥0.55) become Values, bottom-2 (≤0.35) become Aversions — labels only, so the prefix
        /// bytes change only when Layer 3 drift actually crosses a threshold (KV-cache friendly).</summary>
        private static void WeightExtremes(Dictionary<string, float> weights, out List<string> values, out List<string> aversions)
        {
            values = new List<string>();
            aversions = new List<string>();
            if (weights == null || weights.Count == 0) return;

            var sorted = new List<KeyValuePair<string, float>>(weights);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value)); // descending by weight
            foreach (KeyValuePair<string, float> kv in sorted)
                if (kv.Value >= 0.55f && values.Count < 2) values.Add(kv.Key);
            for (int i = sorted.Count - 1; i >= 0 && aversions.Count < 2; i--)
                if (sorted[i].Value <= 0.35f) aversions.Add(sorted[i].Key);
        }

        /// <summary>"a", "a and b", "a, b and c".</summary>
        public static string JoinAnd(List<string> parts)
        {
            if (parts == null || parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[parts.Count - 1];
        }

        private static int EpisodicLineCount(int intellect)
        {
            // Spec §3.2: low intellect relives only the last moment; high intellect keeps a running thread.
            if (intellect < 0) return 2;
            if (intellect < 5) return 1;
            if (intellect < 10) return 2;
            return 4;
        }

        private static List<string> LastN(List<string> src, int n)
        {
            var outList = new List<string>();
            if (src == null || n <= 0) return outList;
            for (int i = Math.Max(0, src.Count - n); i < src.Count; i++)
                if (!string.IsNullOrEmpty(src[i])) outList.Add(src[i]);
            return outList;
        }

        // ---------------------------------------------------------------- Phase 2: relational framing (Spec §4.2)

        /// <summary>People within ~10 tiles, colored by the pawn's relationship vector toward each (Spec §4.2).</summary>
        private static List<string> FrameNearbyPawns(Pawn pawn, ColonistMindState mind)
        {
            var lines = new List<string>();
            if (pawn.Map == null) return lines;

            foreach (Pawn other in pawn.Map.mapPawns.AllPawnsSpawned)
            {
                if (lines.Count >= 3) break;
                if (other == pawn || !other.RaceProps.Humanlike || other.Faction != Faction.OfPlayer) continue;
                if ((other.Position - pawn.Position).LengthHorizontal > 10f) continue;

                string name = PawnText.ShortName(other);
                if (string.IsNullOrEmpty(name)) continue;
                if (PawnText.IsBaby(other)) { lines.Add(name + " (a baby)."); continue; } // present, but not someone to talk with

                string color = "";
                // READ-ONLY lookup — GetOrCreateRelationship would plant a fresh (default-valued) vector as a side effect.
                RelationshipVector rv = mind != null ? mind.GetRelationship(other.ThingID) : null;
                if (rv != null)
                {
                    // Thresholds are set ABOVE the fresh-vector defaults (affection 0.5 / trust 0.5 / frustration 0.1), so a
                    // relationship only gets phrased once Layer 3 drift has actually moved it.
                    if (rv.affection >= 0.6f && rv.trust >= 0.6f) color = " — you two have been getting along well lately";
                    else if (rv.frustration >= 0.35f || rv.affection <= 0.4f) color = " — there is some friction between you lately";
                }
                lines.Add(name + color + ".");
            }
            return lines;
        }

        /// <summary>Appendix A translation rules: game state → first-person sensory prose.</summary>
        private static List<string> FrameBody(Pawn pawn)
        {
            var lines = new List<string>();

            if (pawn.needs.food.CurLevelPercentage < 0.25f)
                lines.Add("Your stomach cramps sharply; you haven't eaten in hours.");
            else if (pawn.needs.food.CurLevelPercentage < 0.4f)
                lines.Add("You're getting hungry.");

            if (pawn.needs.rest != null && pawn.needs.rest.CurLevelPercentage < 0.2f)
                lines.Add("Your eyelids feel like lead and your legs ache with every step.");

            // 1.6 verified: HediffSet.PainTotal (public float, cached with DirtyPainCache()).
            if (pawn.health.hediffSet.PainTotal > 0.3f)
                lines.Add("A dull, throbbing heat radiates from your worst wound.");

            // Phase 2 (Appendix A): negative mood thoughts → first-person prose ("still annoyed about eating off the cold floor").
            // [CONFIRMED decompiled 2026-11-15] 1.6: pawn.needs.mood (Need_Mood).thoughts (ThoughtHandler).memories
            // (MemoryThoughtHandler).Memories → List<Thought_Memory> with public moodOffset/otherPawn + computed LabelCap.
            try
            {
                var memories = pawn.needs.mood?.thoughts?.memories;
                if (memories != null)
                {
                    int added = 0;
                    foreach (Thought_Memory m in memories.Memories)
                    {
                        if (added >= 2) break; // two grudges is enough prose — keep the prompt budget tight
                        if (m == null || m.moodOffset >= 0) continue;

                        string label = null;
                        try { label = m.LabelCap; } catch { } // LabelCap formats against otherPawn defs — can throw mid-load
                        if (string.IsNullOrEmpty(label)) continue;

                        string who = "";
                        try { if (m.otherPawn != null && m.otherPawn != pawn) who = " about " + PawnText.ShortName(m.otherPawn); } catch { }
                        lines.Add("You're still bothered" + who + " — " + label.ToLower() + ".");
                        added++;
                    }
                }
            }
            catch (Exception e) { Log.ErrorOnce("[AIColonists] mood-thought framing threw: " + e.Message, 4815); }

            return lines;
        }

        /// <summary>Taste/temperature flavor for food affordances. TODO(Phase 2): derive from CompQuality + recipe.</summary>
        public static string FoodAdjective(Thing meal) => "hot";

        /// <summary>LabelCap with a save-load guard: on a half-loaded pawn the computed label can throw (live-observed 2026-11-14);
        /// an uncaught throw here would kill the pawn's think branch for every evaluation.</summary>
        private static string SafeLabel(Pawn pawn)
        {
            try { return pawn.LabelCap; }
            catch (Exception e) { Log.ErrorOnce("[AIColonists] LabelCap threw during snapshot capture (save load?): " + e.Message, 4814); return null; }
        }

        /// <summary>Main-thread mind-state lookup with a full guard — the world component can be absent on edge timings.</summary>
        private static ColonistMindState SafeMindState(Pawn pawn)
        {
            try
            {
                if (Find.World == null) return null;
                AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
                if (world == null) return null;
                return world.GetOrCreateMindState(pawn);
            }
            catch (Exception e) { Log.ErrorOnce("[AIColonists] mind-state lookup threw: " + e.Message, 4815); return null; }
        }
    }
}
