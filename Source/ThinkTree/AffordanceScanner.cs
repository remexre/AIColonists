using System;
using System.Collections.Generic;
using AIColonists.Core;
using AIColonists.Prompts;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIColonists.ThinkTree
{
    /// <summary>One concrete, reachable option the colonist can perceive and act on (Spec §5.1).</summary>
    public struct AffordanceOption
    {
        public int optionId;                // 1..6 — what the LLM picks as impulse_id
        public string sensoryDescription;   // "Eat the hot simple meal on the dining table"
        public JobDef jobDef;               // e.g., JobDefOf.Ingest — must be resolved before use in a Job
        public LocalTargetInfo targetA;     // concrete Thing or IntVec3 reference
        public LocalTargetInfo targetB;     // optional secondary target (chair/table)
        public WorkTypeDef workType;        // origin work type, if from a WorkGiver
        // Work options only: the giver that offered this job and the thing it scanned. The job is REBUILT by this giver
        // at execution time (QueuedDesire.TryMakeJob) — never from def + targets alone, which drops every other field the
        // giver set (live crash 2026-11-22: Pick Up And Haul's HaulToInventory lost job.targetQueueA → NRE in
        // ReservationUtility.ReserveAsManyAsPossible). Plain references; only dereferenced on the main thread.
        public WorkGiver_Scanner workGiver;
        public Thing workThing;

    }

    /// <summary>Extension helpers for affordance option sets (C# requires extension methods in a non-generic static class).</summary>
    public static class AffordanceOptionExtensions
    {
        /// <summary>Validates the LLM's impulse_id against this option set. Main thread only.</summary>
        public static bool IsValidImpulse(this List<AffordanceOption> options, int impulseId) =>
            options != null && impulseId >= 1 && impulseId <= options.Count;
    }

    /// <summary>
    /// Affordance scanner (Spec §5.1): runs on the main thread BEFORE an async LLM call and converts the pawn's
    /// reachable world into up to 6 sensory options. The LLM can only ever pick from what physically exists —
    /// no hallucinated targets, no invalid LocalTargetInfo, no JobMaker crashes (Spec §3 fix).
    /// </summary>
    public static class AffordanceScanner
    {
        public const int MaxOptions = 6;

        public static List<AffordanceOption> Scan(Pawn pawn)
        {
            var options = new List<AffordanceOption>();

            // 1. Physiological: food (Spec §5.1.1) — same lookup vanilla JobGiver_Eat uses.
            if (pawn.needs.food.CurLevelPercentage < 0.4f)
                AddIfValid(options, FoodAffordance(pawn));

            // 2. Rest & comfort (Spec §5.1.2). RestUtility.FindBedFor verified to exist in-game.
            if (pawn.needs.rest != null && pawn.needs.rest.CurLevelPercentage < 0.4f)
                AddIfValid(options, RestAffordance(pawn));

            // 3. Top WorkGivers: nearest reachable, reservable work targets (Spec §5.1.3).
            AddWorkAffordances(pawn, options);

            // 4. Social opportunities: pawns within 15 tiles with line of sight (Spec §5.1.4).
            AddSocialAffordances(pawn, options);

            // 5. Always include one trait-flavored idle/relax option as a safe fallback (Spec §5.1.5).
            if (options.Count < MaxOptions)
            {
                options.Add(new AffordanceOption
                {
                    optionId = options.Count + 1,
                    sensoryDescription = "Stand still for a moment and just breathe.", // TODO(Phase 2): flavor by trait (Spec §4.1 layer 7)
                    jobDef = JobDefOf.Wait,
                    targetA = new LocalTargetInfo(pawn.Position),
                });
            }

            return options;
        }

        private static void AddIfValid(List<AffordanceOption> list, AffordanceOption opt)
        {
            if (opt.sensoryDescription != null && list.Count < MaxOptions)
            {
                // Assign the sequential 1..N id here so food/rest options (which return with the default 0)
                // match the impulse_id range the LLM is told to pick from (Spec §5.1). Without this, a hungry
                // pawn's meal would carry id 0 and every downstream IsValidImpulse check would mis-map.
                opt.optionId = list.Count + 1;
                list.Add(opt);
            }
        }

        private static AffordanceOption FoodAffordance(Pawn pawn)
        {
            // 1.6 signature: (Pawn getter, Pawn eater, bool desperate, out ThingDef foodDef, ...optional).
            Thing meal = FoodUtility.BestFoodSourceOnMap(pawn, pawn, false, out _);
            // 1.6 verified: CanReach is a Pawn extension method (Verse.ReachabilityUtility) — LocalTargetInfo + Danger.
            if (meal == null || !pawn.CanReach(new LocalTargetInfo(meal), PathEndMode.Touch, Danger.Deadly))
                return default(AffordanceOption);

            return new AffordanceOption
            {
                sensoryDescription = "Eat the " + SensoryFramingBuilder.FoodAdjective(meal) + " " + LabelLower(meal) + " nearby.",
                jobDef = JobDefOf.Ingest,
                targetA = new LocalTargetInfo(meal),
                // TODO(Phase 2): resolve a table/chair as targetB for the "ate at a table" thought (Appendix A).
            };
        }

        private static AffordanceOption RestAffordance(Pawn pawn)
        {
            // TODO(Phase 1): verify exact FindBedFor signature/return type against your RimWorld build.
            Thing bed = RestUtility.FindBedFor(pawn);
            if (bed == null || !pawn.CanReach(new LocalTargetInfo(bed), PathEndMode.Touch, Danger.Deadly))
                return default(AffordanceOption);

            return new AffordanceOption
            {
                sensoryDescription = "Lie down on the " + LabelLower(bed) + ".",
                jobDef = JobDefOf.LayDown, // 1.6 verified: vanilla bed sleep uses JobDefOf.LayDown (JobGiver_ForceSleepNow → MakeJob(LayDown, pos)); no LieDown def exists
                targetA = new LocalTargetInfo(bed),
            };
        }

        private static void AddWorkAffordances(Pawn pawn, List<AffordanceOption> options)
        {
            // 1.6: PotentialWorkThingsGlobal is a virtual INSTANCE method on each scheduled WorkGiver_Scanner (no static
            // entry point anymore); the pawn's ordered work-giver list lives on pawn.workSettings (Pawn_WorkSettings).
            if (pawn.workSettings == null) return;

            foreach (WorkGiver giver in pawn.workSettings.WorkGiversInOrderNormal)
            {
                if (!(giver is WorkGiver_Scanner scanner)) continue;

                // 1.6 (verified live on a fresh colony): some WorkGiver_Scanner implementations return NULL instead of an empty list — enumerating that NREs.
                IEnumerable<Thing> workThings = scanner.PotentialWorkThingsGlobal(pawn);
                if (workThings == null) continue;

                foreach (Thing t in workThings)
                {
                    if (options.Count >= MaxOptions) return;
                    if (!pawn.CanReach(new LocalTargetInfo(t), PathEndMode.Touch, Danger.Deadly)) continue;

                    // 2026-11-15 fix (live symptom: "pawns don't do what Layer 1 says" — plus the model noticing its own
                    // "repetitive options"): these entries used to carry a null jobDef and were therefore ALWAYS rejected by
                    // QueuedDesire.IsValidAndReservable, so every work impulse silently fell through to vanilla. Now resolve
                    // the exact Job the WorkGiver would make for this target at scan time, and the giver list is already
                    // schedule-gated — Pawn_WorkSettings.CacheWorkGiversInOrder only keeps work types with GetPriority(w) > 0,
                    // so we never offer work the pawn isn't scheduled for. TODO(Phase 2): richer sensory descriptions (Spec §5.1.3).
                    // [CONFIRMED decompiled 2026-11-15] 1.6 Job stores its def as `public JobDef def` (renamed from pre-1.5 jobDef).
                    //
                    // 2026-11-16 fix (live NREs + IndexOutOfRange, TEST-RESULTS.md §23): honor VANILLA'S TWO-STAGE CONTRACT.
                    // PotentialWorkThingsGlobal is an UNFILTERED candidate stream — decompiled 1.6: WorkGiver_GatherAnimalBodyResources
                    // returns ALL faction pawns (humans included) and WorkGiver_Teach returns EVERY player-faction pawn; the real
                    // validation lives in HasJobOnThing (animal + comp check, NeedsTeacher + desk-spot reservation). Calling
                    // JobOnThing directly produced Milk jobs targeting human colonists (vanilla NRE in JobDriver_Milk's toils) and
                    // Lessongiving jobs built from the target's CURRENT job (IndexOutOfRange in
                    // JobDriver_Lessongiving.TryMakePreToilReservations when the "desk" was a bed at the map edge). Note also that
                    // WorkGiver_Teach.JobOnThing is NOT side-effect-free — it mutates the target pawn's current job
                    // (CurJob.SetTarget(B, ...)) — so JobOnThing must only ever be reached after HasJobOnThing passes.
                    bool hasJob;
                    try
                    {
                        hasJob = scanner.HasJobOnThing(pawn, t);
                    }
                    catch (Exception e)
                    {
                        // Same defensive rule as JobOnThing below: a throwing gate must skip the candidate, not kill the branch.
                        Log.ErrorOnce("[AIColonists] HasJobOnThing threw for " + t.def.label + " via " + scanner.GetType().Name + ": " + e.Message, 4816);
                        continue;
                    }
                    if (!hasJob) continue;

                    Job job;
                    try
                    {
                        job = scanner.JobOnThing(pawn, t);
                    }
                    catch (Exception e)
                    {
                        // Live-observed 2026-11-15: some vanilla JobOnThing implementations NRE when called outside their normal
                        // precondition context (WorkGiver_HaulMechToCharger, WorkGiver_Teach). Skip the candidate — uncaught, the
                        // exception would be caught at ThinkNode_Priority's child loop (logged as "Exception in ... TryIssueJobPackage"),
                        // which kills our branch for that pawn on EVERY evaluation and takes its Layer 1 down with it.
                        Log.ErrorOnce("[AIColonists] JobOnThing threw for " + t.def.label + " via " + scanner.GetType().Name + ": " + e.Message, 4813);
                        continue;
                    }
                    if (job == null || job.def == null) continue;

                    options.Add(new AffordanceOption
                    {
                        optionId = options.Count + 1,
                        // Phase 2 (Spec §5.1.3): verb-matched sensory description — "Mine the exposed steel vein" reads as a
                        // different impulse than "Haul the wooden box to storage", which is what lets the LLM (and the player,
                        // reading the log) tell the choices apart. Mapped by giver class name (no def access needed).
                        sensoryDescription = WorkPhrase(scanner, t),
                        jobDef = job.def,
                        // Use the job's own targets when they carry a Thing (faithful to what vanilla would start); fall back
                        // to the scanned thing so IsValidAndReservable's HasThing/Destroyed/reachability re-check always has one.
                        targetA = job.targetA.HasThing ? job.targetA : new LocalTargetInfo(t),
                        targetB = job.targetB,
                        workGiver = scanner,
                        workThing = t,
                    });
                }
            }
        }

        private static void AddSocialAffordances(Pawn pawn, List<AffordanceOption> options)
        {
            // Phase 2 (Spec §4.2): relational framing — the mind state colors each option ("you two have been getting along").
            // Read-only lookup; a missing world component degrades to plain phrasing, never a crash.
            AIColonists.State.ColonistMindState mind = null;
            try
            {
                if (Find.World != null)
                {
                    AIColonistWorldComponent world = Find.World.GetComponent<AIColonistWorldComponent>();
                    if (world != null) mind = world.GetOrCreateMindState(pawn);
                }
            }
            catch { /* defensive rule — framing is flavor, a throw here must not kill the scan */ }

            foreach (Pawn other in pawn.Map.mapPawns.AllPawnsSpawned) // friendly colonists only (Faction.IsPlayer gate below)
            {
                if (other == pawn || options.Count >= MaxOptions) continue;
                if (!other.RaceProps.Humanlike || !other.Faction.IsPlayer) continue;
                // 1.6 verified: IntVec3.LengthIntApprox is gone — LengthHorizontal (float, Euclidean XZ) is the replacement.
                if ((other.Position - pawn.Position).LengthHorizontal > 15f) continue;
                if (!GenSight.LineOfSight(pawn.Position, other.Position, pawn.Map)) continue;

                // 2026-11-16 (live observation, §23.4): keep the name CAPITALIZED — "Go talk to cook." read as a verb and the
                // model conflated the colonist named Cook with the act of cooking in its inner voices. Names are names; the
                // system prompt now says so explicitly too. LabelCap can throw on half-loaded pawns (ErrorOnce 4814 precedent) —
                // a throwing label skips the candidate instead of killing the scan.
                string name;
                try { name = other.LabelCap; } catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;

                string color = "";
                var rv = mind != null ? mind.GetRelationship(other.ThingID) : null;
                if (rv != null)
                {
                    // Same above-defaults thresholds as the snapshot's nearby framing — a fresh vector stays uncolored.
                    if (rv.affection >= 0.6f && rv.trust >= 0.6f) color = " — you two have been getting along well";
                    else if (rv.frustration >= 0.35f || rv.affection <= 0.4f) color = " — there's some friction between you";
                }

                options.Add(new AffordanceOption
                {
                    optionId = options.Count + 1,
                    sensoryDescription = "Go talk to " + name + color + ".", // Phase 2: relational framing from RelationshipVector (Spec §4.2)
                    jobDef = JobDefOf.Wait, // 1.6 verified: there is NO Talk job def — vanilla talking is an InteractionDef, not a job. Skeleton: wait at the other pawn's cell until Phase 3 swaps in the LLM-driven InteractionDef (Spec §7.3).
                    targetA = new LocalTargetInfo(other),
                });
            }
        }

        // 1.6 verified: Def.labelLower was removed — Def exposes `public string label` + TaggedString LabelCap.
        private static string LabelLower(Thing thing) => thing.def.label.ToLower();

        /// <summary>Phase 2 (Spec §5.1.3): verb-matched work phrasing by giver class name. The class-name match is deliberate —
        /// it needs no def access (safe on half-loaded pawns) and vanilla's WorkGiver_* naming is stable across 1.x.</summary>
        private static string WorkPhrase(WorkGiver_Scanner scanner, Thing t)
        {
            string cls = scanner.GetType().Name; // e.g. "WorkGiver_Mine"
            string what = LabelLower(t);

            // Person-targeting givers (prisoners/patients/babies/animals): T4 grading (§23.8) showed thirteen options all reading
            // "Work on the human nearby." — useless to the LLM and impossible to grade. Each known giver gets its own phrasing
            // with the target's name where safe; givers without a specific match fall through to the thing-based wording below.
            if (t is Pawn tp)
            {
                string person = PersonWorkPhrase(cls, what, tp);
                if (person != null) return person;
            }

            if (cls.Contains("Mine")) return "Mine the " + what + " nearby.";
            if (cls.Contains("Haul")) return "Haul the " + what + " to storage.";
            if (cls.Contains("Clean")) return "Clean up the " + what + ".";
            if (cls.Contains("Construct") || cls.Contains("Repair")) return "Build or repair the " + what + " nearby.";
            if (cls.Contains("Plant") || cls.Contains("Harvest") || cls.Contains("Cut")) return "Tend the " + what + " nearby.";
            if (cls.Contains("Slaughter")) return "Slaughter the " + what + ".";
            return "Work on the " + what + " nearby."; // unknown giver — the Phase 1 phrasing, still valid
        }

        /// <summary>Descriptive phrasing for givers whose target is a Pawn. Every class name below was verified against the 1.6
        /// Core+Biotech WorkGiverDefs and decompiled givers (§23.8 recon) — PatientGoToBed* and BringBabyToSafety are NonScan
        /// (plain WorkGiver, never reach this code), so they need no entry. Returns null when the combination has no specific
        /// phrasing; the caller then falls back to the thing-based wording.</summary>
        private static string PersonWorkPhrase(string cls, string what, Pawn target)
        {
            // Animals: only a handful of givers target them — everything else keeps the race-label wording ("the caribou").
            if (!target.RaceProps.Humanlike)
            {
                if (cls.Contains("Milk")) return "Milk the " + what + " nearby.";
                if (cls.Contains("FeedPatient")) return "Bring food to the " + what + " nearby."; // DoctorFeedAnimals reuses WorkGiver_FeedPatient
                if (cls.Contains("Tend")) return "Tend to the wounded " + what + " nearby.";     // WorkGiver_TendOther_Animal
                return null;
            }

            // LabelCap can throw on half-loaded pawns (ErrorOnce 4814 precedent) — unnamed phrasing instead of a dead scan.
            string name = null;
            try { name = target.LabelCap; } catch { /* unnamed phrasing below */ }
            bool named = !string.IsNullOrEmpty(name);

            // Doctor
            if (cls.Contains("TendSelf")) return cls.Contains("Urgent") || cls.Contains("Emergency") ? "Urgently tend to your own injuries." : "Tend to your own injuries."; // actor == target: WorkGiver_TendSelf yields the pawn itself; Defs use both Urgent and Emergency suffixes
            if (cls.Contains("Tend")) return cls.Contains("Urgent")
                ? (named ? "Urgently tend to patient " + name + "." : "Urgently tend to the wounded nearby.")
                : (named ? "Tend to patient " + name + "." : "Tend to the wounded nearby.");
            if (cls.Contains("FeedPatient")) return named ? "Bring food to " + name + "." : "Bring food to the hungry nearby.";
            if (cls.Contains("RescueDowned")) return named ? "Carry downed " + name + " to a bed." : "Carry the downed ally to a bed.";
            if (cls.Contains("TakeToBed") && !cls.Contains("Warden")) return named ? "Take " + name + " to an operating bed." : "Take the patient to an operating bed."; // WorkGiver_TakeToBedToOperate
            if (cls.Contains("Visit")) return named ? "Visit sick " + name + "." : "Visit the sick nearby.";
            if (cls.Contains("DoBill")) return named ? "Operate on " + name + "." : "Perform surgery on the patient nearby."; // medical bills target the patient pawn (billGiversAllHumanlikes)

            // Warden — targets are prisoners; class names from 1.6 Core+Biotech WorkGiverDefs
            if (cls.Contains("Chat")) return named ? "Chat with prisoner " + name + "." : "Chat with the prisoner nearby.";
            if (cls.Contains("DeliverFood")) return named ? "Deliver a meal to prisoner " + name + "." : "Deliver a meal to the prisoner nearby.";
            if (cls.Contains("Warden_Feed")) return named ? "Hand-feed prisoner " + name + "." : "Hand-feed the prisoner nearby.";
            if (cls.Contains("ReleasePrisoner")) return named ? "Release prisoner " + name + "." : "Release the prisoner nearby.";
            if (cls.Contains("TakeToBed")) return named ? "Carry prisoner " + name + " to a bed." : "Carry the prisoner to a bed."; // WorkGiver_Warden_TakeToBed
            if (cls.Contains("Hemogen")) return named ? "Deliver hemogen to prisoner " + name + "." : "Deliver hemogen to the prisoner nearby.";
            if (cls.Contains("DoExecution")) return named ? "Execute prisoner " + name + "." : "Execute the prisoner nearby.";
            if (cls.Contains("Strip")) return named ? "Strip prisoner " + name + "." : "Strip the prisoner nearby.";

            // BasicWorker execution targets a colonist, not a prisoner
            if (cls.Contains("ExecuteGuiltyColonist")) return named ? "Execute guilty colonist " + name + "." : "Execute the guilty colonist nearby.";

            // Childcare (Biotech) — babies and children in the player faction
            if (cls.Contains("Teach")) return named ? "Teach young " + name + "." : "Teach the child nearby.";
            if (cls.Contains("CarryToMom")) return named ? "Carry hungry baby " + name + " to its mother." : "Carry the hungry baby to its mother."; // before Breastfeed — the class name contains both substrings
            if (cls.Contains("Bottle")) return named ? "Bottle-feed baby " + name + "." : "Bottle-feed the hungry baby nearby.";
            if (cls.Contains("PlayWithBaby")) return named ? "Play with baby " + name + "." : "Play with the baby nearby.";
            if (cls.Contains("Breastfeed")) return named ? "Breastfeed baby " + name + "." : "Nurse the hungry baby nearby.";

            return "Work with the person nearby."; // unknown mod giver targeting a person — still better than "work on the human"
        }
    }
}
