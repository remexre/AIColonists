using System.Collections.Generic;
using AIColonists.Core;
using AIColonists.Prompts;
using AIColonists.State;
using AIColonists.UI;
using RimWorld; // JobDefOf (hold job) — lives in the RimWorld namespace, not Verse
using Verse;
using Verse.AI;

namespace AIColonists.ThinkTree
{
    /// <summary>
    /// Non-blocking desire consumer (Spec §6.2). Pops a pre-validated impulse from the async intent buffer in
    /// &lt;0.01ms, or returns null immediately so vanilla AI handles the pawn until the next LLM response lands.
    /// Zero recursion: we never start jobs inside job-start hooks — RimWorld's own job tracker consumes our Job
    /// like any other ThinkTree result (Spec §1 fix).
    /// </summary>
    public class JobGiver_AIColonistDesire : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Settings settings = AIColonistMod.Settings;
            if (settings == null || !settings.EnableAI || pawn.Drafted || pawn.InMentalState)
                return null;

            AIColonistGameComponent manager = pawn.Map.GetComponent<AIColonistGameComponent>();
            if (manager == null) return null;

            // 1. Did the background LLM worker deposit a validated desire?
            if (manager.IntentBuffer.TryPopDesire(pawn.ThingID, out QueuedDesire desire))
            {
                // Re-verify on the main thread — the world may have moved while the LLM was thinking (Spec §10).
                // Work desires are rebuilt by their WorkGiver here (TryMakeJob); null = stale, same as a failed re-check.
                Job job = desire.IsValidAndReservable(pawn) ? desire.TryMakeJob(pawn) : null;
                if (job != null)
                {
                    manager.NoteDesireExecuted(pawn.ThingID); // Phase 2: an AI decision actually became a job — breakers reset
                    // Phase 2 diagnostics (T4 grading): quote the exact sensory phrasing from the affordance list so
                    // "Layer 1 said X" (the dispatch line) correlates with "pawn did Y" (this line) in Player.log alone.
                    // 2026-11-16 (§23.5): suffix the resolved JobDef name — two different work givers can share the same
                    // generic phrasing ("Work on the human nearby" was Milk for one pawn and a medical job for another),
                    // and only the def says which vanilla driver will actually run it.
                    Log.Message("[AIColonists] " + SafeWho(pawn) + " [job started] \"" + desire.sensoryDescription + "\" (" + desire.jobDef.defName + ")");
                    job.expiryInterval = 1800; // re-evaluate after ~30s of game time if long-running (Spec §6.2)
                    MoteBubbleUtility.TryThrowThoughtMote(pawn, desire.innerVoiceShort);

                    // Phase 4: record the monologue for the AI Mind tab (Spec §9). Main thread — safe to touch
                    // mind state here; guarded so a missing world component can never break job giving.
                    if (Find.World != null)
                    {
                        AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
                        if (world != null)
                        {
                            ColonistMindState mind = world.GetOrCreateMindState(pawn);
                            mind.lastInnerVoice = desire.innerVoiceShort ?? "";
                            mind.lastInnerVoiceTick = Find.TickManager.TicksGame;
                        }
                    }

                    return job;
                }
                // Stale target: the world moved on while the LLM was thinking — discard and let the next decision re-roll.
                manager.NoteStaleDesire(pawn.ThingID); // Phase 2: feeds the stale-drop circuit breaker (no silent fall-through)
            }

            // 2. Cooldown expired and no request in flight? Snapshot affordances & queue the async call FIRST, so the
            //    hold below can wait for THIS response instead of falling straight through to vanilla work.
            if (manager.ShouldQueueEvaluation(pawn))
            {
                List<AffordanceOption> affordances = AffordanceScanner.Scan(pawn);
                SensorySnapshot snapshot = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, affordances);
                manager.EnqueueAsyncLayer1(snapshot);
            }

            // 3. Phase 2 (backlog #8): AI-first job priority — while a decision is in flight and the pipeline is healthy,
            //    hold position with a short wait job instead of returning null. Returning null would let the vanilla
            //    sorter below this branch issue work in the window before the response lands ("LLM says X, pawn does Y").
            //    Emergency/drafted/queued nodes sit above us and still win natively; exhausted pawns are never held.
            if (manager.ShouldHoldPawn(pawn))
            {
                var hold = new Job(JobDefOf.Wait);
                hold.targetA = new LocalTargetInfo(pawn.Position); // already standing there — the wait just holds the slot
                hold.expiryInterval = AIColonistGameComponent.HoldPollTicks; // re-evaluate when the wait expires (Spec §6.2 pattern)
                // Phase 2 diagnostics: one line per hold window (the wait job suppresses re-thinks until it expires), so
                // this is low-frequency evidence that AI-first priority actually engaged for this pawn.
                Log.Message("[AIColonists] " + SafeWho(pawn) + " [hold] waiting for LLM decision (" + AIColonistGameComponent.HoldPollTicks + " ticks)");
                return hold;
            }

            // 4. Return null immediately (<0.01ms) — vanilla ThinkTree takes over (blend mode, or unhealthy pipeline).
            return null;
        }

        /// <summary>Log-line identity: LabelCap can throw on half-loaded pawns (ErrorOnce 4814 precedent) — a diagnostic
        /// line must never kill the pawn's think branch.</summary>
        private static string SafeWho(Pawn pawn)
        {
            try { return pawn.LabelCap + " (" + pawn.ThingID + ")"; }
            catch { return pawn.ThingID; }
        }
    }
}
