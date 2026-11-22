using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIColonists.Inference;
using AIColonists.Prompts;
using AIColonists.Social;
using AIColonists.State;
using AIColonists.ThinkTree;
using RimWorld;
using Verse;

namespace AIColonists.Core
{
    /// <summary>
    /// Per-map scheduler and async dispatch (Spec §3.3 Dual-Clock Hybrid Scheduling).
    /// All LLM calls run on background Task threads; the Unity main thread only ever touches the
    /// ColonistIntentBuffer via JobGiver_AIColonistDesire (&lt;0.01ms, Spec §6.2) — zero frame impact.
    /// Phase 3: also owns the Layer 2 room-social pipeline (batched calls → decision buffer → TryInteractWith)
    /// and the Layer 3 consolidation pipeline (staggered per-pawn calls → result buffer → mind-state apply).
    /// </summary>
    public class AIColonistGameComponent : MapComponent
    {
        private readonly Stopwatch unpausedClock = new Stopwatch();
        private readonly ColonistIntentBuffer intentBuffer = new ColonistIntentBuffer();

        // Main-thread bookkeeping; the worker only clears inFlight (benign race — worst case one duplicate call).
        private readonly ConcurrentDictionary<string, PawnCallState> callStates =
            new ConcurrentDictionary<string, PawnCallState>();

        // Phase 3 buffers (background → main thread): Layer 2 decisions and Layer 3 results.
        private readonly SocialDecisionBuffer socialBuffer = new SocialDecisionBuffer();
        private readonly ConcurrentDictionary<string, Layer3Result> layer3Results =
            new ConcurrentDictionary<string, Layer3Result>();

        // Map-level Layer 2 clock (one batched call per shared space every ~90 s — Spec §8.2).
        private int lastLayer2Tick;
        private float lastLayer2RealSeconds;
        private int layer2InFlightCount; // Interlocked — background threads decrement on completion

        private const int MaxLayer2BatchesPerCycle = 4;      // cap concurrent room batches per dispatch cycle
        private const float StaggerSecondsPerColonist = 30f; // Spec §8.2: Layer 3 consolidation stagger
        private const int DecisionStalenessTicks = 3600;     // ~1 in-game hour — stale LLM decisions are dropped at consume time

        // Phase 2 (backlog #8, AI-first job priority): hold-job tuning. HoldPollTicks ≈ 7.5 s at 1× speed — long enough
        // to cover the full dispatch budget (3000 ms timeout + 500 ms retry delay + one retried send) so a healthy
        // response always lands before the pawn re-thinks; short enough that an emergency interrupt feels immediate.
        public const int HoldPollTicks = 450;

        private ILlamaBridge bridge;

        // Self-targeting guard (live-observed 2026-11-14): log the first dropped self-decision once, then stay quiet.
        private bool selfTargetDroppedLogged;

        public ColonistIntentBuffer IntentBuffer => intentBuffer;

        // Real RimWorld MapComponent only offers a protected MapComponent(Map) constructor.
        public AIColonistGameComponent(Map map) : base(map) { }

        private class PawnCallState
        {
            public int lastLayer1Tick;
            public float lastLayer1RealSeconds;
            public bool layer1InFlight;

            // Phase 2 (backlog #8, AI-first job priority): circuit breakers for the hold behavior. consecutiveL1Failures
            // counts dispatches that produced no usable desire (server error / timeout / unparseable) in a row — at 2,
            // holds stop and vanilla works until a dispatch succeeds again. staleDrops counts desires discarded at consume
            // time (target moved on) in a row — at 3, one full cycle goes to vanilla before holds resume.
            public int consecutiveL1Failures;
            public int staleDrops;

            // Phase 3: per-pawn Layer 3 consolidation cadence (Spec §7.4). The stagger itself is positional —
            // index * StaggerSecondsPerColonist in MaybeDispatchLayer3 — not stored state.
            public int lastLayer3Tick;
            public float lastLayer3RealSeconds;
            public bool layer3InFlight;
        }

        // 1.6: MapComponent's post-gen hook is MapGenerated() (MapInitialized no longer exists).
        public override void MapGenerated()
        {
            base.MapGenerated();
            Settings s = AIColonistMod.Settings;
            if (s != null)
                bridge = new LlamaServerClient(s);
        }

        public override void MapComponentTick()
        {
            // 2026-11-16 (§23.3): flush worker-thread log lines onto the main thread FIRST — before any early return,
            // so a paused or AI-off game still drains them (an open in-game log window would otherwise race the queue).
            MainThreadLog.Drain();

            // Dual-Clock: the real-time clock only advances while the game is unpaused, so a 10-minute pause
            // costs zero GPU and 3x/4x speed can't multiply request rate (Spec §3.3).
            if (Find.TickManager.Paused)
            {
                unpausedClock.Reset();
                return;
            }
            if (!unpausedClock.IsRunning)
                unpausedClock.Start();

            // Phase 3 (Spec §7.3/§7.4): apply buffered results first, then open the dispatch gates. Everything here is
            // gated on the master switch — with AI off this method costs one null check per tick.
            Settings s = AIColonistMod.Settings;
            if (s == null || !s.EnableAI) return;

            ConsumeSocialDecisions();
            ConsumeLayer3Results();
            MaybeDispatchLayer2(s);
            MaybeDispatchLayer3(s);
        }

        /// <summary>Dual-Clock gate: true only when BOTH game-tick AND real-time deltas have elapsed (Spec §3.3).</summary>
        public bool ShouldQueueEvaluation(Pawn pawn)
        {
            Settings s = AIColonistMod.Settings;
            if (s == null || !s.EnableAI) return false;

            PawnCallState st = callStates.GetOrAdd(pawn.ThingID, _ => new PawnCallState());

            int nowTick = Find.TickManager.TicksGame;
            float nowReal = (float)unpausedClock.Elapsed.TotalSeconds;

            return !st.layer1InFlight
                   && (nowTick - st.lastLayer1Tick >= s.Layer1MinGameTicks)
                   && (nowReal - st.lastLayer1RealSeconds >= s.Layer1MinRealSeconds);
        }

        /// <summary>Fire-and-forget Layer 1 evaluation. Never blocks the main thread (Spec §6.2 step 2).</summary>
        public void EnqueueAsyncLayer1(SensorySnapshot snapshot)
        {
            PawnCallState st = callStates.GetOrAdd(snapshot.PawnId, _ => new PawnCallState());
            st.layer1InFlight = true;
            st.lastLayer1Tick = Find.TickManager.TicksGame;
            st.lastLayer1RealSeconds = (float)unpausedClock.Elapsed.TotalSeconds;

            MaybeRequestWarmUp(); // one-shot cold-start warmup — must start BEFORE the dispatch task so it wins the SendGate race
            Task.Run(async () => await DispatchLayer1(snapshot));
        }

        private bool warmupRequested;

        /// <summary>
        /// One-shot cold-start warmup (2026-11-15, Ollama field finding — TEST-RESULTS.md §22.7): a cold model load
        /// takes ~8 s and CANCELLING IT MID-LOAD MAKES OLLAMA ABORT THE LOAD (empirically verified). Without this,
        /// every short-timeout dispatch at game start would start and cancel its own model load forever — the AI
        /// would stay dead while unpaused. Fired once per map session from the first Layer 1 dispatch (reached only
        /// with EnableAI on), so a disabled mod never burns an 8 s model load. Fire-and-forget; a failure is quiet
        /// (server not up yet is normal pre-game) and real dispatches self-heal through the usual breakers.
        /// </summary>
        private void MaybeRequestWarmUp()
        {
            if (warmupRequested) return;
            warmupRequested = true;

            ILlamaBridge b = EnsureBridge();
            Task.Run(async () =>
            {
                try { await b.WarmUpAsync(); }
                catch { /* defensive rule — a throwing warmup must never surface outside its own log line */ }
            });
        }

        // ================================================================ Phase 2: AI-first job priority (backlog #8)

        /// <summary>
        /// Phase 2 (backlog #8 — user end goal: "disable much of the vanilla decisions to prioritize the llm"): true while
        /// a Layer 1 decision is in flight and the pipeline is healthy enough to be worth waiting for. The JobGiver then
        /// holds the pawn with a short wait job instead of returning null — returning null would let the vanilla sorter
        /// BELOW our branch (GetFood/GetRest/Work) issue a job in the 0.5–7 s window before the LLM response lands, which
        /// is exactly the "LLM says X, pawn does Y" behavior observed live on 2026-11-15. Player orders, drafting and
        /// emergency survival sit ABOVE us in the tree and still win natively; an exhausted pawn (rest &lt; 0.1) is never
        /// held so vanilla sleep takes over. Main thread only.
        /// </summary>
        public bool ShouldHoldPawn(Pawn pawn)
        {
            Settings s = AIColonistMod.Settings;
            if (s == null || !s.EnableAI || !s.AIFirstJobPriority) return false;

            PawnCallState st;
            if (!callStates.TryGetValue(pawn.ThingID, out st)) return false; // never dispatched — nothing to wait for

            // Physiological floor: an exhausted pawn must sleep NOW — the vanilla GetRest in the sorter below handles it.
            try { if (pawn.needs?.rest != null && pawn.needs.rest.CurLevelPercentage < 0.1f) return false; } catch { }

            // Circuit breakers: server failing in a row, or the LLM keeps picking targets that go stale → vanilla works.
            if (st.consecutiveL1Failures >= 2) return false;
            if (st.staleDrops >= 3) { st.staleDrops = 0; return false; } // half-reset: one vanilla cycle per three stale drops

            // Hold only while a response is actually coming — waiting out the full 30 s cadence window would idle pawns.
            return st.layer1InFlight;
        }

        /// <summary>Dispatch produced no usable desire (exception / timeout / unparseable) — feeds the hold circuit breaker.</summary>
        public void NoteDispatchFailure(string pawnThingId)
        {
            PawnCallState st;
            if (callStates.TryGetValue(pawnThingId, out st)) st.consecutiveL1Failures++;
        }

        /// <summary>Dispatch produced a buffered desire — the pipeline is healthy, reset the failure breaker.</summary>
        public void NoteDispatchSuccess(string pawnThingId)
        {
            PawnCallState st;
            if (callStates.TryGetValue(pawnThingId, out st)) st.consecutiveL1Failures = 0;
        }

        /// <summary>A desire was discarded at consume time (stale target) — feeds the stale-drop breaker.</summary>
        public void NoteStaleDesire(string pawnThingId)
        {
            PawnCallState st;
            if (callStates.TryGetValue(pawnThingId, out st)) st.staleDrops++;
        }

        /// <summary>An AI desire actually became a job — both breakers reset (fresh start).</summary>
        public void NoteDesireExecuted(string pawnThingId)
        {
            PawnCallState st;
            if (callStates.TryGetValue(pawnThingId, out st))
            {
                st.staleDrops = 0;
                st.consecutiveL1Failures = 0;
            }
        }

        private async Task DispatchLayer1(SensorySnapshot snapshot)
        {
            try
            {
                // 1.6 (verified live + decompiled): MapGenerated() fires ONLY from Verse.MapGenerator.GenerateMap — i.e., NEW maps.
                // Loaded colonies go through Map ctor/ExposeData FillComponents and never call it, so on a loaded save the bridge
                // would stay null forever and every dispatch silently no-ops (zero requests ever reach the server). Create lazily.
                if (bridge == null)
                {
                    Settings initSettings = AIColonistMod.Settings;
                    if (initSettings == null) return;
                    bridge = new LlamaServerClient(initSettings);
                }

                Settings s = AIColonistMod.Settings;
                Layer1Request request = new Layer1Request
                {
                    model = s.Layer1Model,
                    systemPrompt = PrefixPromptBuilder.BuildSystemPrompt(snapshot),  // Phase 2: real per-pawn identity prefix (KV-cached)
                    userPrompt = PrefixPromptBuilder.BuildUserPrompt(snapshot),
                    // Phase 2 (Spec §3.2): mood → sampling entropy. The worker thread never touches the pawn — the mood
                    // percentage was captured into the snapshot on the main thread at scan time.
                    temperature = MoodEntropyMapper.MoodToTemperature(s.BaseTemperature, snapshot.MoodPercentage),
                    topP = MoodEntropyMapper.MoodToTopP(snapshot.MoodPercentage),
                    minP = s.MinP,
                    maxTokens = s.Layer1MaxOutputTokens,
                    disableReasoning = s.DisableReasoning,
                    optionCount = snapshot.Affordances != null ? snapshot.Affordances.Count : 0,
                };

                Layer1Result result = await bridge.RequestLayer1Async(request);

                // Phase 1 diagnostic (Spec §6.2): until the thought-bubble mote lands, every Layer 1 decision is
                // visible in Player.log — this is what distinguishes "server answered but impulse invalid" from
                // "gate never opened" during in-game testing. 2026-11-15: print the display name with the ThingID in
                // parens — bare IDs ("Human1006") read like a different pawn. 2026-11-16 (§23.3): this runs on a worker
                // thread — MainThreadLog, never Verse.Log directly (open in-game log window would race the queue).
                string who = snapshot.PawnName != null && snapshot.PawnName != snapshot.PawnId
                    ? snapshot.PawnName + " (" + snapshot.PawnId + ")"
                    : (snapshot.PawnName ?? snapshot.PawnId);
                MainThreadLog.Message("[AIColonists] " + who + " Layer 1: impulse_id=" + (result == null ? -1 : result.impulse_id)
                    + " of " + request.optionCount + ", inner_voice=\"" + (result == null ? "" : result.inner_voice) + "\"");

                if (result == null || !snapshot.Affordances.IsValidImpulse(result.impulse_id))
                {
                    NoteDispatchFailure(snapshot.PawnId); // Phase 2: unparseable/out-of-range → feeds the hold circuit breaker
                    return;
                }

                AffordanceOption chosen = snapshot.Affordances[result.impulse_id - 1];
                intentBuffer.Push(snapshot.PawnId, new QueuedDesire(chosen, result.inner_voice));
                NoteDispatchSuccess(snapshot.PawnId); // Phase 2: a usable desire landed — pipeline healthy
            }
            catch (Exception e)
            {
                // Server down / timeout / malformed JSON: fall back to vanilla AI silently (Spec §10). Worker thread — MainThreadLog.
                MainThreadLog.Error("[AIColonists] Layer 1 dispatch failed for " + snapshot.PawnId + ": " + e.Message);
                NoteDispatchFailure(snapshot.PawnId); // Phase 2: transport failure → feeds the hold circuit breaker
            }
            finally
            {
                PawnCallState st;
                if (callStates.TryGetValue(snapshot.PawnId, out st))
                    st.layer1InFlight = false; // map may have unloaded — guarded above
            }
        }

        /// <summary>Spec §6.3: purge pending non-combat desires the instant a pawn is drafted.</summary>
        public void PurgeDesire(string pawnThingId) => intentBuffer.Purge(pawnThingId);

        // ==================================================================== Phase 3: Layer 2 room social (Spec §7.3)

        /// <summary>Drains the Layer 2 decision buffer and executes each decision. Main thread only.</summary>
        private void ConsumeSocialDecisions()
        {
            List<QueuedSocialDecision> pending = socialBuffer.Drain();
            if (pending.Count == 0) return;

            int nowTick = Find.TickManager.TicksGame;

            foreach (QueuedSocialDecision d in pending)
            {
                try
                {
                    if (nowTick - d.pushedGameTick > DecisionStalenessTicks) continue; // world moved on — drop stale decision

                    Pawn initiator = FindColonistByName(d.colonistName);
                    if (initiator == null || initiator.Drafted || initiator.InMentalState) continue;

                    // Self-targeting guard (live-observed 2026-11-14: "Noble vent_frustration -> Noble"): the model occasionally
                    // returns the initiator's own name — the roster includes self and nothing else enforces "one OTHER person".
                    // Vanilla fails it safely at TryInteractWith, but a self-targeted QUIET decision would also pollute memory
                    // ("shared a quiet moment with myself"), so drop early for ALL impulse types. Re-rolls next ~90 s cycle.
                    Pawn target = FindColonistByName(d.targetName);
                    if (target == initiator)
                    {
                        if (!selfTargetDroppedLogged)
                        {
                            selfTargetDroppedLogged = true; // main thread only — no locking needed
                            Log.Message("[AIColonists] " + d.colonistName + " Layer 2: " + d.socialImpulse + " -> self [dropped: LLM targeted the initiator itself — re-rolls next cycle]");
                        }
                        continue;
                    }

                    InteractionDef def = AreaBatcher.DefForImpulse(d.socialImpulse);
                    if (def == null)
                    {
                        RecordQuietMoment(initiator, d, nowTick); // quiet_nod / avoid_gaze / none: memory + nudge only
                        continue;
                    }

                    if (target == null || target.Drafted || target.InMentalState) continue;

                    // Live-verified 2026-11-13 (decompiled 1.6 Pawn_InteractionsTracker): TryInteractWith silently returns
                    // false when the pair is not interactable — 120-tick cooldown, ≤6-cell horizontal distance + line of
                    // sight, spawned/awake/talking/burning state. The batcher groups by ROOM, not proximity, so pairs in a
                    // large room are usually out of range and every attempt died silently (zero play-log entries in the
                    // first live session despite 9 dispatched decisions). Pre-check with the exact gate vanilla itself uses
                    // (public CanInteractNowWith) and drop non-interactable decisions — they re-roll next ~90 s cycle.
                    if (initiator.interactions == null || !initiator.interactions.CanInteractNowWith(target, def))
                    {
                        // 2026-11-14: decompose the gate for the log — a flat "distance/LOS/cooldown" was unattributable and
                        // blocked H4 debugging (8 consecutive drops with no way to tell which sub-check failed).
                        Log.Message("[AIColonists] " + d.colonistName + " Layer 2: " + d.socialImpulse + " -> " + (d.targetName ?? "(none)")
                            + " [dropped: not interactable (" + DescribeDropGate(initiator, target, def) + ") — re-rolls next cycle]");
                        continue;
                    }

                    // Stash the LLM line for this exact pair, then let VANILLA do the rest: the def-symbol mote bubble,
                    // the play-log entry, and letter composition (play-log sentence + "\n\n" + our text) all happen
                    // inside TryInteractWith.
                    PendingLetterPayload.Set(initiator.ThingID, target.ThingID, d.spokenLine, d.innerFeeling);
                    bool ok = initiator.interactions.TryInteractWith(target, def);
                    if (!ok)
                    {
                        PendingLetterPayload.Remove(initiator.ThingID, target.ThingID); // preconditions flipped between check and call — the line must not leak
                        Log.Message("[AIColonists] " + d.colonistName + " Layer 2: " + d.socialImpulse + " -> " + (d.targetName ?? "(none)")
                            + " [attempt failed at the last moment]");
                    }
                    else
                    {
                        Log.Message("[AIColonists] " + d.colonistName + " Layer 2: " + d.socialImpulse + " -> " + (d.targetName ?? "(none)")
                            + (string.IsNullOrEmpty(d.spokenLine) ? "" : " \"" + d.spokenLine + "\"") + " [executed]");
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[AIColonists] ConsumeSocialDecisions: " + e.Message, 4811);
                }
            }
        }

        /// <summary>Decomposes the vanilla interaction gate for LOGGING ONLY (added 2026-11-14 after a run of unattributable
        /// drops). Mirrors the decompiled 1.6 CanInteractNowWith chain exactly — 120-tick cooldown → carrying exception →
        /// recipient spawned → ≤6-cell EUCLIDEAN distance (IntVec3.InHorDistOf is dx²+dz²≤max²) + GenSight.LineOfSight
        /// (only FULL-fill buildings block: walls and CLOSED doors — furniture like tables/beds/chairs/shelves has
        /// fillPercent 0 → FillCategory.None → see-through, verified against Core ThingDefs) → initiate/receive state —
        /// and reports the FIRST failing sub-check. Never influences behavior: CanInteractNowWith itself remains the gate.</summary>
        private static string DescribeDropGate(Pawn initiator, Pawn target, InteractionDef def)
        {
            try
            {
                if (initiator.interactions == null) return "no interaction tracker";
                if (initiator.interactions.InteractedTooRecentlyToInteract()) return "cooldown (120 ticks since last interaction)";
                bool carrying = initiator.IsCarryingPawn(target); // vanilla skips the position check entirely while carrying
                if (!carrying)
                {
                    if (!target.Spawned) return "recipient not spawned";
                    if (!initiator.Position.InHorDistOf(target.Position, 6f)) return "distance>6cells";
                    if (!GenSight.LineOfSight(initiator.Position, target.Position, initiator.Map, skipFirstCell: true))
                        return "LOS blocked (wall/closed door between)";
                }
                if (!SocialInteractionUtility.CanInitiateInteraction(initiator, def)) return "initiator state (awake/talking/burning)";
                if (!SocialInteractionUtility.CanReceiveInteraction(target, def)) return "recipient state (awake/burning)";
                return "all sub-checks passed (vanilla gate flipped between check and consume)";
            }
            catch (Exception e)
            {
                return "diagnostic unavailable (" + e.GetType().Name + ")"; // a bad diagnostic must never break the consume loop
            }
        }

        /// <summary>Memory-only impulses (quiet_nod / avoid_gaze): record the moment and nudge the bond — no interaction.</summary>
        private void RecordQuietMoment(Pawn initiator, QueuedSocialDecision d, int nowTick)
        {
            if (Find.World == null) return;
            AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
            if (world == null) return;

            ColonistMindState mind = world.GetOrCreateMindState(initiator);

            string line = !string.IsNullOrEmpty(d.targetName) ? "Shared a quiet wordless moment with " + d.targetName + "." : "";
            if (!string.IsNullOrEmpty(line)) mind.memory.AddEpisodic(line, nowTick);

            Pawn target = FindColonistByName(d.targetName);
            if (target == null) return;

            RelationshipVector rv = mind.GetOrCreateRelationship(target.ThingID);
            if (d.socialImpulse == "quiet_nod")
                rv.ApplyDeltas(0.01f, 0.01f, 0f, 0f);      // a nod warms the bond slightly
            else
                rv.ApplyDeltas(-0.01f, -0.01f, 0f, 0.02f);   // avoiding someone's gaze cools it
        }

        /// <summary>Opens the Layer 2 gate (90 s per shared space — Spec §8.2) and dispatches batched room calls.</summary>
        private void MaybeDispatchLayer2(Settings s)
        {
            int headroom = MaxLayer2BatchesPerCycle - layer2InFlightCount; // in-flight cap
            if (headroom <= 0) return;

            int nowTick = Find.TickManager.TicksGame;
            float nowReal = (float)unpausedClock.Elapsed.TotalSeconds;
            if (nowTick - lastLayer2Tick < s.Layer2MinGameTicks) return;
            if (nowReal - lastLayer2RealSeconds < s.Layer2MinRealSeconds) return;

            // One batched call per shared space with 2+ colonists (Spec §7.3); when a large colony has MORE eligible
            // spaces than headroom, SelectBatchesForDispatch falls back to dynamic district batching (Phase 4, Spec §9)
            // so pawns in dropped-no-more spaces still get covered by a merged ~30-tile-grid call.
            var eligible = AreaBatcher.SelectBatchesForDispatch(map, headroom);
            if (eligible.Count == 0) return;

            // Clock advances ONLY when we actually dispatch — a lone colonist costs nothing (Spec §3.3).
            lastLayer2Tick = nowTick;
            lastLayer2RealSeconds = nowReal;

            var thingIdToName = new Dictionary<string, string>();
            foreach (List<Pawn> batch in eligible)
                foreach (Pawn pawn in batch) thingIdToName[pawn.ThingID] = pawn.LabelCap;

            ILlamaBridge b = EnsureBridge();
            if (b == null) return; // settings vanished mid-flight — skip this cycle

            for (int i = 0; i < eligible.Count; i++)
            {
                Layer2Request req = AreaBatcher.BuildLayer2Request(eligible[i], thingIdToName);
                Interlocked.Increment(ref layer2InFlightCount);

                ILlamaBridge bridgeForTask = b;
                Task.Run(async () =>
                {
                    try
                    {
                        Layer2Result result = await bridgeForTask.RequestLayer2Async(req);
                        if (result == null) return;
                        foreach (SocialDecision d in result.decisions)
                        {
                            if (string.IsNullOrEmpty(d.colonist_name) || string.IsNullOrEmpty(d.social_impulse)) continue; // belt & braces — parser already filters
                            var q = new QueuedSocialDecision();
                            q.colonistName = d.colonist_name;
                            q.targetName = d.target_name ?? "";
                            q.socialImpulse = d.social_impulse;
                            q.spokenLine = d.spoken_line ?? "";
                            q.innerFeeling = d.inner_feeling ?? "";
                            q.pushedGameTick = nowTick; // captured on the main thread — workers never read game state
                            socialBuffer.Enqueue(q);
                        }
                    }
                    catch (Exception e)
                    {
                        MainThreadLog.Error("[AIColonists] Layer 2 dispatch failed: " + e.Message); // worker thread (§23.3); server down → silent vanilla fallback (Spec §10)
                    }
                    finally
                    {
                        Interlocked.Decrement(ref layer2InFlightCount);
                    }
                });
            }
        }

        // ==================================================================== Phase 3: Layer 3 consolidation (Spec §7.4)

        /// <summary>Applies completed Layer 3 consolidation results to the pawns' mind states. Main thread only.</summary>
        private void ConsumeLayer3Results()
        {
            if (layer3Results.IsEmpty) return;

            int nowTick = Find.TickManager.TicksGame;
            Dictionary<string, string> nameToThingId = BuildNameMap(); // case-insensitive — the model may vary casing

            // Keys is a snapshot on ConcurrentDictionary; TryRemove makes each claim atomic.
            foreach (string thingId in layer3Results.Keys)
            {
                Layer3Result result;
                if (!layer3Results.TryRemove(thingId, out result)) continue;

                try
                {
                    Pawn pawn = FindPawnByThingId(thingId);
                    if (pawn == null || Find.World == null) continue;

                    AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
                    if (world == null) continue;

                    ColonistMindState mind = world.GetOrCreateMindState(pawn);
                    mind.ApplyLayer3Result(result, nameToThingId, nowTick);
                    Log.Message("[AIColonists] " + pawn.LabelCap + " Layer 3: consolidation applied"
                        + (string.IsNullOrEmpty(result.new_semantic_memory) ? "" : " — \"" + result.new_semantic_memory + "\""));
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[AIColonists] ConsumeLayer3Results: " + e.Message, 4812);
                }
            }
        }

        /// <summary>Case-insensitive name → ThingID map for this map's free colonists (Layer 3 name resolution).</summary>
        private Dictionary<string, string> BuildNameMap()
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                if (!string.IsNullOrEmpty(pawn.LabelCap)) m[pawn.LabelCap] = pawn.ThingID;
            return m;
        }

        /// <summary>Opens the Layer 3 gate per colonist (5 min, staggered 30 s each — Spec §8.2) and dispatches consolidation.</summary>
        private void MaybeDispatchLayer3(Settings s)
        {
            if (Find.World == null) return;
            AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
            if (world == null) return;

            ILlamaBridge b = EnsureBridge();
            if (b == null) return;

            int nowTick = Find.TickManager.TicksGame;
            float nowReal = (float)unpausedClock.Elapsed.TotalSeconds;

            int index = 0;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                float stagger = index * StaggerSecondsPerColonist; // Spec §8.2: spread consolidation across the colony
                index++;

                PawnCallState st = callStates.GetOrAdd(pawn.ThingID, _ => new PawnCallState());
                if (st.layer3InFlight) continue;
                if (nowTick - st.lastLayer3Tick < s.Layer3MinGameTicks) continue;
                if (nowReal - st.lastLayer3RealSeconds < s.Layer3MinRealSeconds + stagger) continue;

                st.lastLayer3Tick = nowTick;
                st.lastLayer3RealSeconds = nowReal;
                st.layer3InFlight = true;

                // Build the request on the MAIN thread — mind state is not thread-safe.
                ColonistMindState mind = world.GetOrCreateMindState(pawn);
                Layer3Request req = new Layer3Request
                {
                    model = s.Layer3Model,
                    systemPrompt = Layer3PromptBuilder.BuildSystemPrompt(mind.pawnName),
                    userPrompt = Layer3PromptBuilder.BuildUserPrompt(
                        mind.memory.EpisodicTexts(),
                        mind.memory.semantic,
                        mind.longTermLonging,
                        mind.traitWeights),
                    temperature = s.BaseTemperature,
                    minP = s.MinP,
                    maxTokens = s.Layer3MaxOutputTokens,
                    disableReasoning = s.DisableReasoning,
                };

                string thingId = pawn.ThingID;
                ILlamaBridge bridgeForTask = b;
                Task.Run(async () =>
                {
                    try
                    {
                        Layer3Result result = await bridgeForTask.RequestLayer3Async(req);
                        if (result != null) layer3Results[thingId] = result; // latest wins per pawn
                    }
                    catch (Exception e)
                    {
                        MainThreadLog.Error("[AIColonists] Layer 3 dispatch failed for " + thingId + ": " + e.Message); // worker thread (§23.3)
                    }
                    finally
                    {
                        PawnCallState st2;
                        if (callStates.TryGetValue(thingId, out st2))
                            st2.layer3InFlight = false; // map may have unloaded — guarded above
                    }
                });
            }
        }

        /// <summary>Resolves a roster name to a pawn on this map — case-insensitive LabelCap/LabelShort match.</summary>
        public Pawn FindColonistByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                string cap = pawn.LabelCap;
                if (!string.IsNullOrEmpty(cap) && cap.Equals(name, StringComparison.OrdinalIgnoreCase)) return pawn;

                string shortLabel = pawn.LabelShort;
                if (!string.IsNullOrEmpty(shortLabel) && shortLabel.Equals(name, StringComparison.OrdinalIgnoreCase)) return pawn;
            }
            return null;
        }

        private Pawn FindPawnByThingId(string thingId)
        {
            if (string.IsNullOrEmpty(thingId)) return null;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                if (pawn.ThingID == thingId) return pawn;
            return null;
        }

        /// <summary>Lazily creates the bridge — same rationale as DispatchLayer1 (loaded colonies never call MapGenerated).</summary>
        private ILlamaBridge EnsureBridge()
        {
            if (bridge != null) return bridge;
            Settings s = AIColonistMod.Settings;
            if (s == null) return null;
            bridge = new LlamaServerClient(s);
            return bridge;
        }
    }
}
