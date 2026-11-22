// ============================================================================
//  NO-GAME TEST HARNESS — run with:  dotnet run --project Tests
// ----------------------------------------------------------------------------
//  Compiles every REAL mod source file against GameStubs.cs and unit-tests all
//  logic that does not require a live RimWorld process: schema generation,
//  wire-format serialization, think-block stripping, response parsing, mood
//  entropy mapping, intent buffer semantics + concurrency, memory caps/pruning,
//  relationship clamping, affordance scanning, sensory framing, prompt assembly,
//  dual-clock gating (incl. pause behavior), async dispatch resilience, and all
//  Harmony patch postfixes driven directly.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine; // JsonUtility (1.6: lives in UnityEngine.JSONSerializeModule.dll, namespace UnityEngine)
using Newtonsoft.Json.Linq;
using AIColonists.Core;
using AIColonists.Inference;
using AIColonists.Patches;
using AIColonists.Prompts;
using AIColonists.Social;
using AIColonists.State;
using AIColonists.ThinkTree;
using AIColonists.UI;
using AIColonistsTests.Fakes; // fake givers carrying VANILLA 1.6 class names — WorkPhrase matches on GetType().Name (§23.8)
using RimWorld;
using Verse;
using Verse.AI;

namespace AIColonists.Tests
{
    // Expose protected hooks for direct testing.
    public class ExposedJobGiver : JobGiver_AIColonistDesire { public Job Give(Pawn p) => TryGiveJob(p); }
    public class ExposedGate : ThinkNode_ConditionalAIColonist { public bool IsSatisfied(Pawn p) => Satisfied(p); }

    // Scriptable bridge: records every request and returns canned results, so the full dispatch pipeline
    // (gate → Task.Run worker → buffer → main-thread consume) can be tested without a network.
    public class FakeLlamaBridge : ILlamaBridge
    {
        public readonly List<Layer1Request> layer1 = new List<Layer1Request>();
        public readonly List<Layer2Request> layer2 = new List<Layer2Request>();
        public readonly List<Layer3Request> layer3 = new List<Layer3Request>();
        public Func<Layer2Result> onLayer2;
        public Func<Layer3Result> onLayer3;
        public int warmups; // cold-start warmup recorder (one-shot per map session, §22.7)

        public Task<Layer1Result> RequestLayer1Async(Layer1Request request) { layer1.Add(request); return Task.FromResult(new Layer1Result()); }
        public Task<Layer2Result> RequestLayer2Async(Layer2Request request) { layer2.Add(request); var f = onLayer2; return Task.FromResult(f != null ? f() : new Layer2Result()); }
        public Task<Layer3Result> RequestLayer3Async(Layer3Request request) { layer3.Add(request); var f = onLayer3; return Task.FromResult(f != null ? f() : new Layer3Result()); }
        public Task<bool> WarmUpAsync() { warmups++; return Task.FromResult(true); }
    }

    public static class Program
    {
        static int passed, failed;
        static readonly List<string> failures = new List<string>();

        static void Check(bool cond, string name)
        {
            if (cond) { passed++; Console.WriteLine("  PASS  " + name); }
            else { failed++; failures.Add(name); Console.WriteLine("  FAIL  " + name); }
        }

        static void Section(string s) => Console.WriteLine("\n=== " + s + " ===");

        static Pawn MakePawn(string id, Map map = null)
        {
            // 2026-11-15: default to the player faction — the gate now identity-compares against Find.FactionPlayer.
            var p = new Pawn { ThingID = id, Faction = Faction.OfPlayer };
            if (map != null)
            {
                p.Map = map;
                map.mapPawns.AllPawnsSpawned.Add(p);
                map.mapPawns.FreeColonistsSpawned.Add(p);
            }
            return p;
        }

        static AffordanceOption MakeOpt(int id, string desc, JobDef jobDef, Thing target = null) => new AffordanceOption
        {
            optionId = id,
            sensoryDescription = desc,
            jobDef = jobDef,
            // 1.6: LocalTargetInfo is a struct with no parameterless ctor — use default for the no-target case.
            targetA = target != null ? new LocalTargetInfo(target) : default(LocalTargetInfo),
        };

        public static async Task<int> Main()
        {
            Console.WriteLine("AIColonists — no-game test harness");
            Test_GbnfSchemas();
            Test_JsonWireNames();
            Test_StripThinkBlocks();
            Test_ParseLayer1();
            Test_MoodEntropy();
            Test_BufferSemantics();
            await Test_BufferConcurrencyAsync();
            Test_MemoryStore();
            Test_RelationshipVector();
            Test_ModEntryPoint(); // wires AIColonistMod.Instance/Settings + world comp; enables AI for later tests
            Test_MindState();
            Test_AffordanceScan();
            Test_QueuedDesireValidation();
            Test_SensoryFraming();
            Test_PrefixPrompt();
            Test_DualClockGate(); // needs Settings from Test_ModEntryPoint
            await Test_AsyncDispatchResilienceAsync();
            Test_JobGiver();
            // Phase 2 (Spec §9): differentiation, sensory filter & prefix caching + AI-first job priority (backlog #8).
            Test_Phase2MoodAndIntellect();
            Test_Phase2TraitSeeding();
            Test_Phase2IdentityPrefix();
            Test_Phase2Hold();
            Test_WarmUpColdStart(); // one-shot cold-start warmup (Ollama cancel-aborts-load finding, §22.7)
            Test_MainThreadLog(); // worker-thread log marshaling (in-game log window race, §23.3)
            Test_DraftPatch();
            Test_InteractionPatch(); // needs EnableAI=true
            Test_ThoughtPatch();     // needs EnableAI=true
            Test_IngestibleSourcePatch(); // live NRE fix: JobDriver_Ingest dead-target guard (2026-11-13 stress test)
            Test_MindStateLoadTimeSelfHeal(); // live NRE fix: LabelCap on half-loaded pawn during save load (2026-11-14)
            Test_TabPatch();         // needs EnableAI=true (Phase 4: also draws a populated mind)
            Test_AreaBatcher();      // Phase 4: includes the district batching fallback cases
            // Phase 4 (Spec §9): UI inspector + settings polish.
            Test_AIMindViewModel();  // pure view model — caps/ordering/name resolution pinned without Unity
            Test_VramPresets();      // preset table values + null-safe Apply, URLs untouched
            // Phase 3 (Spec §7.3/§7.4): Layer 2 room social + Layer 3 memory drift.
            Test_Layer2Parsing();
            Test_Layer3Parsing();
            Test_Layer23WireFormat();
            Test_DefForImpulseMapping(); // populates the DefDatabase<InteractionDef> stub registry for later tests
            Test_SocialWorkerPayload();
            Test_SocialConsumePaths();
            await Test_Layer2EndToEndAsync();
            await Test_Layer3EndToEndAsync();
            Test_ApplyLayer3Result();
            await Test_Layer2ServerDownResilienceAsync();
            await Test_TransportSingleFlightAsync();      // Mono concurrent-send fix: gate serializes all layers
            await Test_TransportRetryOnSendFailureAsync(); // one retry on transport failure only
            await Test_ProviderTestAsync(); // settings "Test providers" button: probe outcomes + tester + dialog smoke
            Test_SettingsPage(); // every Settings field has a control; scrolling; reset-to-defaults scope
            Test_MemoryThoughtDefs(); // mood bridge: def wiring + calibration (no code — granted by vanilla machinery)
            Test_UiSmoke();

            Console.WriteLine("\n========================================");
            Console.WriteLine(passed + " passed, " + failed + " failed");
            if (failures.Count > 0)
            {
                Console.WriteLine("Failed:");
                foreach (var f in failures) Console.WriteLine("  - " + f);
            }
            return failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ schemas (Spec §7.2-§7.4)
        static void Test_GbnfSchemas()
        {
            Section("JSON schema generation (Spec §7.2-§7.4)");
            for (int n = 1; n <= 6; n++)
            {
                var obj = JObject.Parse(GbnfSchemas.BuildLayer1Schema(n));
                var e = (JArray)obj["properties"]["impulse_id"]["enum"];
                Check(e.Count == n && (int)e[0] == 1 && (int)e[n - 1] == n,
                    "Layer1 schema: valid JSON, impulse_id enum [1.." + n + "]");
            }
            var l2 = JObject.Parse(GbnfSchemas.Layer2JsonSchema);
            Check(((JArray)l2["properties"]["decisions"]["items"]["properties"]["social_impulse"]["enum"]).Count == 6,
                "Layer2 schema: social_impulse enum has 6 values");
            var l3 = JObject.Parse(GbnfSchemas.Layer3JsonSchema);
            Check((double)l3["properties"]["trait_drift"]["properties"]["delta"]["maximum"] == 0.02 &&
                  (double)l3["properties"]["relationship_deltas"]["items"]["properties"]["trust_delta"]["minimum"] == -0.05,
                "Layer3 schema: trait drift ±0.02 and relationship deltas ±0.05 bounds (Spec §7.4)");
        }

        // ------------------------------------------------------------ wire format
        static void Test_JsonWireNames()
        {
            Section("OpenAI-compatible wire format (hand-built body, llama-server field names)");
            var req = new Layer1Request
            {
                model = "aic-test",
                systemPrompt = "SYS",
                userPrompt = "USR",
                temperature = 0.6f,
                topP = 0.85f,
                minP = 0.08f,
                maxTokens = 80,
                optionCount = 3,
            };
            string json = LlamaServerClient.BuildChatCompletionsJson(req);
            Check(json.Contains("\"top_p\"") && !json.Contains("\"topP\""), "top_p serialized snake_case (not topP)");
            Check(json.Contains("\"min_p\"") && !json.Contains("\"minP\""), "min_p serialized snake_case");
            Check(json.Contains("\"max_tokens\"") && !json.Contains("\"maxTokens\""), "max_tokens serialized snake_case");
            var j = JObject.Parse(json); // throws if escaping is broken anywhere
            // REGRESSION GUARD — the exact fields Unity's JsonUtility dropped in-game (llama-server 400 "'messages' is required"):
            Check(j["messages"] != null && j["messages"].Count() == 2, "messages array present with 2 entries (JsonUtility drop regression)");
            Check((string)j["messages"][0]["role"] == "system" && (string)j["messages"][1]["role"] == "user", "message roles intact");
            Check((bool)j["stream"] == false, "stream=false");
            var rf = (JObject)j["response_format"];
            Check(rf != null && (string)rf["type"] == "json_schema", "response_format.type = json_schema");
            // OBJECT form {name, schema} — the Nov 9 llama.cpp build silently ignores the string form (200 + unconstrained output).
            var js = (JObject)rf["json_schema"];
            Check((string)js["name"] == "layer1" && js["schema"] != null, "json_schema is {name, schema} object");
            var inner = (JObject)js["schema"];
            Check(((JArray)inner["properties"]["impulse_id"]["enum"]).Count == 3, "embedded schema has enum [1,2,3]");
            // Truncation safety: impulse_id must be the FIRST property (GBNF follows schema order; max_tokens cuts mid-object).
            var propNames = new List<string>();
            foreach (var p in ((JObject)inner["properties"]).Properties()) propNames.Add(p.Name);
            Check(propNames[0] == "impulse_id", "impulse_id is first property (survives max_tokens truncation)");

            // Escaping: quotes, newlines and backslashes in prompts must produce valid, round-trippable JSON.
            var nasty = new Layer1Request
            {
                model = "m", systemPrompt = "He said \"hi\"\nline2 \\ backslash", userPrompt = "", temperature = 0.5f, optionCount = 1,
            };
            string json2 = LlamaServerClient.BuildChatCompletionsJson(nasty);
            var j2 = JObject.Parse(json2);
            Check((string)j2["messages"][0]["content"] == "He said \"hi\"\nline2 \\ backslash", "quotes/newlines/backslashes escaped + round-trip");
        }

        // ------------------------------------------------------------ think stripping (Spec §10)
        static void Test_StripThinkBlocks()
        {
            Section("Think-block stripping (Spec §10)");
            var mi = typeof(LlamaServerClient).GetMethod("StripThinkBlocks", BindingFlags.NonPublic | BindingFlags.Static);
            string R(string s) => (string)mi.Invoke(null, new object[] { s });
            const string json = "{\"inner_voice\":\"I am starving.\",\"spoken_mutter\":\"ugh,\",\"impulse_id\":1}";
            Check(R("plain text") == "plain text", "no think block: unchanged");
            Check(R("") == "", "empty string safe");
            Check(R("<think>hmm... let me reason</think>" + json) == json, "leading think block stripped");
            Check(R(json + "\n<think>a\nb\n}brace{\nc</think>") == json, "trailing multi-line think block with braces stripped");
        }

        // ------------------------------------------------------------ response parsing
        static void Test_ParseLayer1()
        {
            Section("Layer 1 response parsing (snake_case fields)");
            var client = new LlamaServerClient(new Settings());
            var mi = typeof(LlamaServerClient).GetMethod("ParseLayer1", BindingFlags.NonPublic | BindingFlags.Instance);

            const string body = "{\"choices\":[{\"message\":{\"content\":\"   <think>starving</think>{\\\"inner_voice\\\":\\\"I am starving.\\\",\\\"spoken_mutter\\\":\\\"ugh,\\\",\\\"impulse_id\\\":1}\"}}]}";
            var res = (Layer1Result)mi.Invoke(client, new object[] { body });
            Check(res != null && res.inner_voice == "I am starving." && res.spoken_mutter == "ugh," && res.impulse_id == 1,
                "envelope → think-strip → snake_case JSON parse");

            bool threw = false;
            try { mi.Invoke(client, new object[] { "{\"choices\":[]}" }); } catch (TargetInvocationException) { threw = true; }
            Check(threw, "empty choices throws");

            threw = false;
            try { mi.Invoke(client, new object[] { "{\"choices\":[{\"message\":{\"content\":\"not json at all\"}}]}" }); } catch (TargetInvocationException) { threw = true; }
            Check(threw, "non-JSON content throws");

            // Truncation tolerance: max_tokens cut after impulse_id (schema order puts it first) must still yield the decision.
            var res2 = (Layer1Result)mi.Invoke(client, new object[] { "{\"choices\":[{\"message\":{\"content\":\"{\\\"impulse_id\\\":2}\"}}]}" });
            Check(res2 != null && res2.impulse_id == 2 && res2.inner_voice == "" && res2.spoken_mutter == "",
                "truncated response: impulse survives missing prose fields");
        }

        // ------------------------------------------------------------ mood entropy (Spec §3.2)
        static void Test_MoodEntropy()
        {
            Section("Mood → entropy mapping (Spec §3.2)");
            Check(MoodEntropyMapper.TempFloor == 0.5f && MoodEntropyMapper.TempCeiling == 0.85f, "hard bounds [0.50, 0.85] per spec");
            Check(Math.Abs(MoodEntropyMapper.MoodToTemperature(0.65f) - 0.60f) < 1e-4f, "content tier: 0.65 + (-0.05) = 0.60");
            Check(MoodEntropyMapper.MoodToTemperature(0.25f) == MoodEntropyMapper.TempFloor, "low base clamps to floor 0.50");
            Check(MoodEntropyMapper.MoodToTemperature(1.0f) == MoodEntropyMapper.TempCeiling, "high base clamps to ceiling 0.85");
            Check(Math.Abs(MoodEntropyMapper.MoodToTopP() - 0.85f) < 1e-4f, "content top_p = 0.85");
        }

        // ------------------------------------------------------------ intent buffer (Spec §6.2)
        static void Test_BufferSemantics()
        {
            Section("Intent buffer semantics (Spec §6.2)");
            var buf = new ColonistIntentBuffer();
            var opt = MakeOpt(1, "eat", JobDefOf.Ingest);

            Check(!buf.TryPopDesire("p1", out _), "empty buffer: pop returns false");
            buf.Push("p1", new QueuedDesire(opt, "yum"));
            Check(buf.TryPopDesire("p1", out var d) && d.innerVoiceShort == "yum" && d.jobDef == JobDefOf.Ingest, "pop returns the pushed desire with jobDef");
            Check(!buf.TryPopDesire("p1", out _), "second pop false (desire removed)");

            buf.Push("p1", new QueuedDesire(opt, "v1"));
            buf.Push("p1", new QueuedDesire(opt, "v2"));
            Check(buf.TryPopDesire("p1", out var d2) && d2.innerVoiceShort == "v2", "latest push wins (stale desire replaced)");

            buf.Push("p1", new QueuedDesire(opt, "x"));
            buf.Purge("p1");
            Check(!buf.TryPopDesire("p1", out _), "purge removes pending desire (drafting override, Spec §6.3)");
        }

        static async Task Test_BufferConcurrencyAsync()
        {
            Section("Intent buffer concurrency (Spec §6.2)");
            var buf = new ColonistIntentBuffer();
            var opt = MakeOpt(1, "eat", JobDefOf.Ingest);
            var tasks = new List<Task>();
            for (int t = 0; t < 8; t++)
                tasks.Add(Task.Run(() => { for (int i = 0; i < 5000; i++) buf.Push("pawn1", new QueuedDesire(opt, "v" + i)); }));
            await Task.WhenAll(tasks);
            Check(buf.TryPopDesire("pawn1", out var last) && last != null, "40k concurrent pushes → exactly one desire survives (latest-wins)");
            Check(!buf.TryPopDesire("pawn1", out _), "buffer empty after pop");
        }

        // ------------------------------------------------------------ memory store (Spec §4.1)
        static void Test_MemoryStore()
        {
            Section("Memory store caps & pruning (Spec §4.1)");
            var ms = new MemoryStore();
            for (int i = 0; i < 41; i++) ms.AddEpisodic("e" + i, 1000 + i);
            Check(ms.episodic.Count == 40 && ms.episodic[0].text == "e1", "episodic cap 40 drops oldest");

            for (int i = 0; i < 26; i++) ms.AddSemantic("s" + i);
            Check(ms.semantic.Count == 25 && ms.semantic[0] == "s1", "semantic cap 25 drops oldest");
            for (int i = 0; i < 16; i++) ms.AddProcedural("p" + i);
            Check(ms.procedural.Count == 15, "procedural cap 15");

            var before = ms.episodic.Count;
            ms.AddEpisodic("", 5);
            Check(ms.episodic.Count == before, "empty episodic text ignored");

            int now = 1_000_000, maxAge = 30 * MemoryStore.TicksPerDay;
            var ms2 = new MemoryStore();
            ms2.AddEpisodic("old", now - maxAge - 1); // strictly older than 30 days → pruned
            ms2.AddEpisodic("exact", now - maxAge);   // exactly 30 days → kept (strict >)
            int removed = ms2.PruneEpisodicOlderThan(now);
            Check(removed == 1 && ms2.episodic.Count == 1 && ms2.episodic[0].text == "exact",
                "prune boundary: strictly older than 30 in-game days pruned, exact-age kept");
        }

        // ------------------------------------------------------------ relationships (Spec §4.2/§7.4)
        static void Test_RelationshipVector()
        {
            Section("Relationship vector clamping (Spec §4.2)");
            var rv = new RelationshipVector();
            rv.ApplyDeltas(0.2f, 0.2f, 0.2f, 0.2f);
            Check(Math.Abs(rv.trust - 0.7f) < 1e-4f && Math.Abs(rv.affection - 0.7f) < 1e-4f &&
                  Math.Abs(rv.respect - 0.7f) < 1e-4f && Math.Abs(rv.frustration - 0.3f) < 1e-4f,
                "deltas applied to the four mutable axes");

            var rv2 = new RelationshipVector { trust = 0.9f, affection = 0.9f, respect = 0.9f, frustration = 0.9f };
            rv2.ApplyDeltas(0.5f, 0.5f, 0.5f, 0.5f);
            Check(rv2.trust == 1f && rv2.affection == 1f && rv2.respect == 1f && rv2.frustration == 1f, "clamps at 1.0");

            var rv3 = new RelationshipVector { trust = 0.1f, affection = 0.1f, respect = 0.1f, frustration = 0.1f };
            rv3.ApplyDeltas(-0.3f, -0.3f, -0.3f, -0.3f);
            Check(rv3.trust == 0f && rv3.affection == 0f && rv3.respect == 0f && rv3.frustration == 0f, "clamps at 0.0");

            Check(rv.jealousy == 0.1f && rv.dependency == 0.2f, "jealousy/dependency untouched by ApplyDeltas (Layer 3 TODO)");
        }

        // ------------------------------------------------------------ mod entry point (Spec §9 Phase 1)
        static void Test_ModEntryPoint()
        {
            Section("Mod entry point wiring (Spec §9 Phase 1)");
            var mod = new AIColonistMod(new ModContentPack());
            Check(AIColonistMod.Instance == mod, "Instance set");
            Check(AIColonistMod.Settings != null && !AIColonistMod.Settings.EnableAI, "Settings created; AI OFF by default (opt-in)");

            // 1.6: World.FillComponents() auto-discovers component subclasses via reflection and instantiates them
            // with a (world) ctor — there is no registry API. Mimic that discovery here so downstream tests can
            // resolve the component the same way in-game code does.
            Find.World.RegisterForTests(new AIColonistWorldComponent(Find.World));
            Check(Find.World.GetComponent<AIColonistWorldComponent>() != null, "world component resolvable after FillComponents-style registration");

            Check(HarmonyLib.Harmony.PatchAllCalled, "Harmony PatchAll invoked during mod construction (all 3 patches applied)");

            AIColonistMod.Settings.EnableAI = true; // enable for downstream tests
        }

        // ------------------------------------------------------------ mind state (Spec §4.1/§5.2)
        static void Test_MindState()
        {
            Section("ColonistMindState (Spec §4.1/§5.2)");
            var pawn = MakePawn("m1"); pawn.LabelCap = "Alice";
            var ms = new ColonistMindState(pawn);
            Check(ms.pawnName == "Alice" && ms.memory != null && ms.traitWeights != null, "initialized from pawn identity");
            var wc = Find.World.GetComponent<AIColonistWorldComponent>();
            var a = wc.GetOrCreateMindState(pawn);
            var b = wc.GetOrCreateMindState(pawn);
            Check(ReferenceEquals(a, b), "same mind state instance per ThingID (save-safe index)");
        }

        // ------------------------------------------------------------ affordance scan (Spec §5.1)
        static void Test_AffordanceScan()
        {
            Section("Affordance scanner (Spec §5.1)");

            // food + rest
            TestRig.Reset();
            var map = new Map();
            var pawn = MakePawn("p1", map);
            // 1.6: work affordances come from the pawn's scheduled WorkGivers (pawn.workSettings) — vanilla pawns always have some.
            pawn.workSettings.WorkGiversInOrderNormal.Add(new WorkGiver_Scanner());
            pawn.needs.food.CurLevelPercentage = 0.2f;
            pawn.needs.rest.CurLevelPercentage = 0.9f;
            var meal = new Thing { Position = new IntVec3(3, 0), def = new ThingDef { label = "simple meal" } };
            TestRig.BestFoodSourceResult = meal;
            var opts = AffordanceScanner.Scan(pawn);
            Check(opts.Any(o => o.jobDef == JobDefOf.Ingest && o.targetA.HasThing && o.targetA.Thing == meal),
                "hungry pawn: food affordance with concrete meal target");

            pawn.needs.rest.CurLevelPercentage = 0.1f;
            var bed = new Thing { Position = new IntVec3(4, 0), def = new ThingDef { label = "bed" } };
            TestRig.FindBedForResult = bed;
            opts = AffordanceScanner.Scan(pawn);
            Check(opts.Any(o => o.jobDef == JobDefOf.LayDown && o.targetA.Thing == bed), "tired pawn: rest affordance with bed target (JobDefOf.LayDown)");

            // work (2026-11-15 fix): the option carries the Job the WorkGiver would actually make — decompiled 1.6
            // JobOnThing is a pure factory, so resolving at scan time is side-effect-free. Previously these carried a null
            // jobDef and were ALWAYS rejected by QueuedDesire.IsValidAndReservable → "pawns don't do what Layer 1 says".
            TestRig.WorkThings.Add(new Thing { Position = new IntVec3(2, 0), def = new ThingDef { label = "steel vein" } });
            opts = AffordanceScanner.Scan(pawn);
            Check(opts.Any(o => o.sensoryDescription.Contains("steel vein") && o.jobDef != null && o.targetA.HasThing),
                "work affordance carries the resolved job (JobOnThing)");

            TestRig.WorkJobOnThing = t => null; // giver can't make a job for this target (missing skill/tool/etc.)
            opts = AffordanceScanner.Scan(pawn);
            Check(!opts.Any(o => o.sensoryDescription.Contains("steel vein")), "giver returning null JobOnThing: option not offered");

            // 2026-11-15 live incident witness: vanilla JobOnThing impls (WorkGiver_HaulMechToCharger, WorkGiver_Teach)
            // NRE when called outside their normal precondition context. Uncaught, the exception propagated to
            // ThinkNode_Priority's child loop and killed our branch for that pawn on EVERY evaluation (Layer 1 dead).
            // The scanner must now swallow it per-candidate: scan completes, candidate skipped, other options survive.
            TestRig.WorkJobOnThing = t => throw new Exception("simulated vanilla JobOnThing NRE");
            opts = AffordanceScanner.Scan(pawn); // must not throw
            Check(!opts.Any(o => o.sensoryDescription.Contains("steel vein")), "throwing JobOnThing: candidate skipped, scan completes");
            Check(opts.Any(o => o.jobDef == JobDefOf.Ingest), "throwing JobOnThing: unrelated options (food) still offered");
            TestRig.WorkJobOnThing = TestRig.DefaultWorkJobOnThing;

            // 2026-11-16 fix (TEST-RESULTS.md §23): VANILLA'S TWO-STAGE CONTRACT. PotentialWorkThingsGlobal is an UNFILTERED
            // candidate stream — decompiled 1.6: WorkGiver_GatherAnimalBodyResources returns ALL faction pawns (humans
            // included) and WorkGiver_Teach returns EVERY player-faction pawn; the real per-target validation lives in
            // HasJobOnThing (animal+comp check, NeedsTeacher + desk-spot reservation). Before this fix the scanner called
            // JobOnThing directly, producing Milk jobs targeting human colonists (vanilla NRE in JobDriver_Milk's toils) and
            // Lessongiving jobs built from the target's CURRENT job (IndexOutOfRange in TryMakePreToilReservations when the
            // "desk" was a bed at the map edge). The gate must refuse before JobOnThing is ever reached.
            TestRig.WorkHasJobOnThing = (p, t) => false; // e.g. WorkGiver_Milk.HasJobOnThing(human) → !(IsAnimal) → refused
            opts = AffordanceScanner.Scan(pawn);
            Check(!opts.Any(o => o.sensoryDescription.Contains("steel vein")), "HasJobOnThing=false: candidate refused, option not offered (milk→human / teach→adult gate)");
            Check(opts.Any(o => o.jobDef == JobDefOf.Ingest), "HasJobOnThing=false: unrelated options (food) still offered");
            TestRig.WorkHasJobOnThing = TestRig.DefaultWorkHasJobOnThing;

            // Same defensive rule as JobOnThing: a throwing gate skips the candidate, never kills the scan (ErrorOnce 4816).
            TestRig.WorkHasJobOnThing = (p, t) => throw new Exception("simulated vanilla HasJobOnThing NRE");
            opts = AffordanceScanner.Scan(pawn); // must not throw
            Check(!opts.Any(o => o.sensoryDescription.Contains("steel vein")), "throwing HasJobOnThing: candidate skipped, scan completes");
            TestRig.WorkHasJobOnThing = TestRig.DefaultWorkHasJobOnThing;

            // social: near vs far
            var other = MakePawn("p2", map); other.LabelCap = "Bob"; other.Position = new IntVec3(5, 0);
            opts = AffordanceScanner.Scan(pawn);
            // 1.6: no Talk job def exists — the social skeleton uses Wait at the other pawn's cell; distinguish it from
            // the idle fallback (also Wait) by targetA.Thing.
            Check(opts.Any(o => o.jobDef == JobDefOf.Wait && o.targetA.Thing == other), "social affordance for pawn within 15 tiles");
            other.Position = new IntVec3(20, 0);
            opts = AffordanceScanner.Scan(pawn);
            Check(!opts.Any(o => o.targetA.Thing == other), "no social affordance beyond 15 tiles");

            // cap + sequential ids
            TestRig.Reset();
            var map2 = new Map();
            var p2a = MakePawn("cap", map2);
            p2a.workSettings.WorkGiversInOrderNormal.Add(new WorkGiver_Scanner());
            p2a.needs.food.CurLevelPercentage = 0.1f;
            p2a.needs.rest.CurLevelPercentage = 0.1f;
            TestRig.BestFoodSourceResult = new Thing { Position = new IntVec3(1, 0), def = new ThingDef { label = "meal" } };
            TestRig.FindBedForResult = new Thing { Position = new IntVec3(1, 0), def = new ThingDef { label = "bed" } };
            for (int i = 0; i < 5; i++)
                TestRig.WorkThings.Add(new Thing { Position = new IntVec3(i, 0), def = new ThingDef { label = "vein" + i } });
            var o2 = MakePawn("o1", map2); o2.Position = new IntVec3(1, 0); // would be a social option if room allowed
            var opts2 = AffordanceScanner.Scan(p2a);
            Check(opts2.Count <= AffordanceScanner.MaxOptions, "option count capped at " + AffordanceScanner.MaxOptions);
            bool seq = true;
            for (int i = 0; i < opts2.Count; i++) if (opts2[i].optionId != i + 1) seq = false;
            Check(seq, "optionIds sequential 1..N");

            // unreachable world → only idle fallback
            TestRig.Reset();
            var map3 = new Map();
            var p3 = MakePawn("p3", map3);
            p3.needs.food.CurLevelPercentage = 0.1f;
            TestRig.BestFoodSourceResult = new Thing { Position = new IntVec3(9, 0), def = new ThingDef { label = "meal" } };
            TestRig.CanReachDefault = false;
            var opts3 = AffordanceScanner.Scan(p3);
            Check(!opts3.Any(o => o.jobDef == JobDefOf.Ingest) && opts3.Count == 1 && opts3[0].jobDef == JobDefOf.Wait,
                "unreachable world: only idle fallback remains");

            // healthy pawn in empty world → exactly one Wait option
            TestRig.Reset();
            var map4 = new Map();
            var p4 = MakePawn("p4", map4);
            var opts4 = AffordanceScanner.Scan(p4);
            Check(opts4.Count == 1 && opts4[0].jobDef == JobDefOf.Wait && opts4[0].sensoryDescription.Contains("breathe"),
                "idle pawn: single Wait fallback option");

            // §23.8 (T4 grading): person-targeting work used to all read "Work on the human nearby." — each vanilla giver
            // class now gets its own phrasing. Fakes carry verbatim 1.6 class names; WorkPhrase keys off GetType().Name.
            Pawn targetHuman = MakePawn("target-human"); // no map → never enters the social scan; CanReach via TestRig default
            targetHuman.def.label = "human";
            targetHuman.LabelCap = "Ava";
            Pawn targetPrisoner = MakePawn("target-prisoner");
            targetPrisoner.def.label = "human";
            targetPrisoner.LabelCap = "Axe";
            targetPrisoner.IsPrisoner = true;
            Pawn targetAnimal = MakePawn("target-animal");
            targetAnimal.RaceProps.Humanlike = false;
            targetAnimal.def.label = "caribou";

            void CheckPhrase(WorkGiver_Scanner giver, Pawn target, string expected)
            {
                TestRig.Reset();
                var m = new Map();
                var actor = MakePawn("actor-" + giver.GetType().Name, m);
                actor.workSettings.WorkGiversInOrderNormal.Add(giver);
                TestRig.WorkThings.Add(target); // the stub scanner yields exactly this candidate
                var o = AffordanceScanner.Scan(actor);
                // opts[0] is deterministically the first work option: Scan() order is food → rest → work → social → Wait fallback.
                Check(o.Count >= 1 && o[0].sensoryDescription == expected,
                    "phrase: " + giver.GetType().Name + " → \"" + expected + "\" (got \"" + (o.Count > 0 ? o[0].sensoryDescription : "<none>") + "\")");
            }

            // Warden — prisoners
            CheckPhrase(new WorkGiver_Warden_Chat(), targetPrisoner, "Chat with prisoner Axe.");
            CheckPhrase(new WorkGiver_Warden_DeliverFood(), targetPrisoner, "Deliver a meal to prisoner Axe.");
            CheckPhrase(new WorkGiver_Warden_Feed(), targetPrisoner, "Hand-feed prisoner Axe.");
            CheckPhrase(new WorkGiver_Warden_ReleasePrisoner(), targetPrisoner, "Release prisoner Axe.");
            CheckPhrase(new WorkGiver_Warden_TakeToBed(), targetPrisoner, "Carry prisoner Axe to a bed.");
            CheckPhrase(new WorkGiver_Warden_DoExecution(), targetPrisoner, "Execute prisoner Axe.");
            CheckPhrase(new WorkGiver_Strip(), targetPrisoner, "Strip prisoner Axe."); // targets a prisoner pawn OR its corpse
            CheckPhrase(new WorkGiver_ExecuteGuiltyColonist(), targetHuman, "Execute guilty colonist Ava."); // BasicWorker — not a prisoner

            // Doctor — patients and self
            CheckPhrase(new WorkGiver_TendOther_Humanlike(), targetHuman, "Tend to patient Ava.");
            CheckPhrase(new WorkGiver_TendOtherUrgent(), targetHuman, "Urgently tend to patient Ava.");
            CheckPhrase(new WorkGiver_FeedPatient(), targetHuman, "Bring food to Ava.");
            CheckPhrase(new WorkGiver_RescueDowned(), targetHuman, "Carry downed Ava to a bed.");
            CheckPhrase(new WorkGiver_TakeToBedToOperate(), targetHuman, "Take Ava to an operating bed.");
            CheckPhrase(new WorkGiver_VisitSickPawn(), targetHuman, "Visit sick Ava.");
            CheckPhrase(new WorkGiver_DoBill(), targetHuman, "Operate on Ava."); // medical bills target the patient pawn

            // WorkGiver_TendSelf yields the pawn ITSELF — actor-aware phrasing names no one.
            TestRig.Reset();
            var mapSelf = new Map();
            var selfActor = MakePawn("actor-tendself", mapSelf);
            selfActor.workSettings.WorkGiversInOrderNormal.Add(new WorkGiver_TendSelf());
            TestRig.WorkThings.Add(selfActor); // the giver yields the pawn itself
            var optsSelf = AffordanceScanner.Scan(selfActor);
            Check(optsSelf.Count >= 1 && optsSelf[0].sensoryDescription == "Tend to your own injuries.",
                "phrase: WorkGiver_TendSelf → actor-aware self-tending (got \"" + (optsSelf.Count > 0 ? optsSelf[0].sensoryDescription : "<none>") + "\")");

            // Childcare (Biotech) — babies and children in the player faction
            CheckPhrase(new WorkGiver_Teach(), targetHuman, "Teach young Ava.");
            CheckPhrase(new WorkGiver_BreastfeedCarryToMom(), targetHuman, "Carry hungry baby Ava to its mother."); // class name contains BOTH substrings — CarryToMom must win
            CheckPhrase(new WorkGiver_BottleFeedBaby(), targetHuman, "Bottle-feed baby Ava.");
            CheckPhrase(new WorkGiver_PlayWithBaby(), targetHuman, "Play with baby Ava.");
            CheckPhrase(new WorkGiver_Breastfeed(), targetHuman, "Breastfeed baby Ava.");

            // Hemogen (Biotech) — prisoners
            CheckPhrase(new WorkGiver_Warden_DeliverHemogen(), targetPrisoner, "Deliver hemogen to prisoner Axe.");

            // Animals keep race-label phrasing for the handful of person-shaped givers that target them.
            CheckPhrase(new WorkGiver_Milk(), targetAnimal, "Milk the caribou nearby.");
            CheckPhrase(new WorkGiver_FeedPatient(), targetAnimal, "Bring food to the caribou nearby."); // DoctorFeedAnimals reuses WorkGiver_FeedPatient

            // Half-loaded target (LabelCap throws) → unnamed phrasing; the scan must survive.
            var halfLoadedTarget = new HalfLoadedPawn { ThingID = "target-halfloaded", Broken = true };
            CheckPhrase(new WorkGiver_Warden_Chat(), halfLoadedTarget, "Chat with the prisoner nearby.");

            // Unknown giver targeting a person → generic-but-person phrasing, not "work on the human".
            CheckPhrase(new WorkGiver_Scanner(), targetHuman, "Work with the person nearby.");
        }

        // ------------------------------------------------------------ desire re-validation (Spec §10 target desync)
        static void Test_QueuedDesireValidation()
        {
            Section("QueuedDesire main-thread re-validation (Spec §10)");
            var map = new Map();
            var pawn = MakePawn("q", map);

            var meal = new Thing { Position = new IntVec3(3, 0), def = new ThingDef { label = "meal" } };
            pawn.needs.food.CurLevelPercentage = 0.2f; // hungry — the scanner would have offered the meal
            Check(new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest, meal), "x").IsValidAndReservable(pawn),
                "valid desire passes main-thread re-check");

            meal.Destroyed = true;
            Check(!new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest, meal), "x").IsValidAndReservable(pawn),
                "destroyed target rejected (meal got eaten)");

            TestRig.Reset();
            TestRig.CanReachDefault = false;
            Check(!new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest, new Thing { Position = new IntVec3(3, 0) }), "x").IsValidAndReservable(pawn),
                "unreachable target rejected");
            TestRig.CanReachDefault = true;

            Check(!new QueuedDesire(MakeOpt(1, "work", null, new Thing { Position = new IntVec3(1, 0) }), "x").IsValidAndReservable(pawn),
                "null jobDef (defense in depth) rejected — never reaches JobMaker");

            // 2026-11-15 fix: work desires with RESOLVED jobs now pass the re-check — previously every work impulse died at
            // the null-jobDef line above, which is why pawns "didn't do what Layer 1 says".
            var vein = new Thing { Position = new IntVec3(1, 0), def = new ThingDef { label = "steel vein" } };
            Check(new QueuedDesire(MakeOpt(1, "work", new JobDef(), vein), "x").IsValidAndReservable(pawn),
                "resolved work desire passes main-thread re-check (2026-11-15 fix)");

            // Staleness: the driving need was satisfied while the LLM was thinking (2026-11-13 live observation:
            // "I need food" fired right after a finished meal). Mirrors AffordanceScanner.Scan's own <0.4 gates.
            pawn.needs.food.CurLevelPercentage = 0.9f;
            Check(!new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest, new Thing { Position = new IntVec3(3, 0) }), "x").IsValidAndReservable(pawn),
                "stale eat desire rejected once the pawn is no longer hungry");

            var bed = new Thing { Position = new IntVec3(2, 0), def = new ThingDef { label = "bed" } };
            pawn.needs.rest.CurLevelPercentage = 0.1f; // exhausted — scanner would have offered the bed
            Check(new QueuedDesire(MakeOpt(1, "rest", JobDefOf.LayDown, bed), "x").IsValidAndReservable(pawn),
                "tired pawn: rest desire passes main-thread re-check");

            pawn.needs.rest.CurLevelPercentage = 0.8f; // slept in the meantime
            Check(!new QueuedDesire(MakeOpt(1, "rest", JobDefOf.LayDown, bed), "x").IsValidAndReservable(pawn),
                "stale rest desire rejected once the pawn is no longer tired");
        }

        // ------------------------------------------------------------ sensory framing (Appendix A)
        static void Test_SensoryFraming()
        {
            Section("Sensory framing thresholds (Appendix A)");
            TestRig.Reset();
            var map = new Map();
            var pawn = MakePawn("s1", map);

            var snap = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, null);
            Check(snap.PawnId == "s1" && snap.Affordances == null && snap.BodyAndSurroundingsLines.Count == 0,
                "healthy pawn: empty body frame, snapshot identity correct");

            pawn.needs.food.CurLevelPercentage = 0.2f;
            snap = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, null);
            Check(snap.BodyAndSurroundingsLines.Contains("Your stomach cramps sharply; you haven't eaten in hours."),
                "severe hunger (<0.25) line");

            pawn.needs.food.CurLevelPercentage = 0.3f;
            snap = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, null);
            Check(snap.BodyAndSurroundingsLines.Contains("You're getting hungry.") &&
                  !snap.BodyAndSurroundingsLines.Any(l => l.Contains("cramps")),
                "mild hunger (0.25-0.4) line, exclusive thresholds");

            pawn.needs.rest.CurLevelPercentage = 0.15f;
            snap = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, null);
            Check(snap.BodyAndSurroundingsLines.Any(l => l.Contains("eyelids")), "exhaustion (<0.2) line");

            pawn.health.hediffSet.PainTotal = 0.4f; // 1.6: HediffSet.PainLevel is gone — PainTotal is the cached total pain
            snap = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, null);
            Check(snap.BodyAndSurroundingsLines.Any(l => l.Contains("wound")), "pain (>0.3) line");
        }

        // ------------------------------------------------------------ prompt assembly (Spec §2.3/§7.1)
        static void Test_PrefixPrompt()
        {
            Section("Prefix-first prompt assembly (Spec §2.3, §7.1)");
            var snap = new SensorySnapshot
            {
                PawnId = "x",
                EpisodicLines = new List<string> { "Ate at the table.", "Bob helped me." },
                NearbyPawnLines = null,
                BodyAndSurroundingsLines = new List<string> { "hungry" },
                ColonyHorizonLine = "",
                Affordances = new List<AffordanceOption>
                {
                    MakeOpt(1, "Eat the hot simple meal nearby.", JobDefOf.Ingest),
                    MakeOpt(2, "Lie down on the bed.", JobDefOf.LayDown),
                    MakeOpt(3, "Stand still for a moment and just breathe.", JobDefOf.Wait),
                },
            };

            string sys = PrefixPromptBuilder.BuildSystemPrompt(snap);
            Check(sys.Contains("# IDENTITY (STATIC PREFIX - KV CACHED)") && !sys.Contains("["),
                "system prompt: static prefix zone, no unreplaced placeholders");

            string usr = PrefixPromptBuilder.BuildUserPrompt(snap);
            Check(usr.Contains("[1] Eat the hot simple meal nearby.") && usr.Contains("[2] Lie down on the bed.") && usr.Contains("[3] Stand still"),
                "user prompt: numbered affordances in order 1..N");
            Check(usr.Contains("- Ate at the table.") && usr.Contains("- (nothing in particular)"),
                "episodic lines joined; empty zone renders dash placeholder");
        }

        // ------------------------------------------------------------ dual-clock gate (Spec §3.3)
        static void Test_DualClockGate()
        {
            Section("Dual-Clock gate & pause behavior (Spec §3.3)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);
            Check(map.GetComponent<AIColonistGameComponent>() == comp, "Map.GetComponent<T> finds registered component");

            Find.TickManager.TicksGame = 0;
            Find.TickManager.Paused = false;
            comp.MapGenerated(); // 1.6 post-gen hook (MapInitialized is gone); creates bridge (Settings non-null)
            var pawn = MakePawn("g1", map);

            Check(!comp.ShouldQueueEvaluation(pawn), "fresh pawn: gate closed (real-time clock just started)");

            // Open the gate arithmetically via reflection on the private per-pawn state.
            var dictField = typeof(AIColonistGameComponent).GetField("callStates", BindingFlags.NonPublic | BindingFlags.Instance);
            System.Collections.IDictionary dict = (System.Collections.IDictionary)dictField.GetValue(comp);
            comp.ShouldQueueEvaluation(pawn); // creates the state entry
            object st = dict[pawn.ThingID];
            var t = st.GetType();
            t.GetField("lastLayer1RealSeconds").SetValue(st, -35f); // "last call 35 real seconds ago"
            t.GetField("lastLayer1Tick").SetValue(st, Find.TickManager.TicksGame - 2000); // > 1800 tick delta
            Check(comp.ShouldQueueEvaluation(pawn), "gate opens when BOTH game-tick and real-time deltas elapsed");

            t.GetField("layer1InFlight").SetValue(st, true);
            Check(!comp.ShouldQueueEvaluation(pawn), "in-flight request blocks re-evaluation");
            t.GetField("layer1InFlight").SetValue(st, false);

            // Pause behavior: the unpaused real-time clock must reset while paused (pauses cost zero GPU).
            var sw = (Stopwatch)typeof(AIColonistGameComponent).GetField("unpausedClock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(comp);
            Find.TickManager.Paused = true;
            comp.MapComponentTick();
            Check(sw.Elapsed == TimeSpan.Zero, "pause resets the unpaused real-time clock");
            Find.TickManager.Paused = false;
            System.Threading.Thread.Sleep(30);
            comp.MapComponentTick();
            Check(sw.Elapsed > TimeSpan.Zero, "unpaused ticks advance the real-time clock");

            // Post-pause scenario: a lastReal stamp recorded on the OLD epoch (before reset) keeps the gate closed.
            t.GetField("lastLayer1RealSeconds").SetValue(st, 35f);
            Check(!comp.ShouldQueueEvaluation(pawn), "stale pre-pause real-time stamp keeps gate closed after clock reset");
        }

        // ------------------------------------------------------------ async dispatch resilience (Spec §10)
        static async Task Test_AsyncDispatchResilienceAsync()
        {
            Section("Async dispatch resilience — server down (Spec §10)");
            AIColonistMod.Settings.Layer1ServerUrl = "http://127.0.0.1:9"; // closed port → connection refused (measured ~2.1 s on this machine, not instant)
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);
            comp.MapGenerated(); // 1.6 post-gen hook; creates bridge (Settings non-null)
            MakePawn("a1", map);

            var snap = new SensorySnapshot { PawnId = "a1", Affordances = new List<AffordanceOption> { MakeOpt(1, "eat", JobDefOf.Ingest) } };
            comp.EnqueueAsyncLayer1(snap); // fire-and-forget; must self-heal

            var dictField = typeof(AIColonistGameComponent).GetField("callStates", BindingFlags.NonPublic | BindingFlags.Instance);
            System.Collections.IDictionary dict = (System.Collections.IDictionary)dictField.GetValue(comp);
            bool cleared = false;
            // 2026-11-15: window widened 5 s → 15 s. On this machine a connection to closed port 9 is refused after
            // ~2.1 s (not instant RST), and the one-shot cold-start warmup (§22.7) now serializes ahead of the
            // dispatch through SendGate: warmup (~2.5 s) + attempt 0 (~2.1 s) + 500 ms retry delay + attempt 1
            // (~2.1 s) ≈ 7 s worst case. A TRUE wedge never clears, so a longer window only adds robustness.
            for (int i = 0; i < 300 && !cleared; i++)
            {
                await Task.Delay(50);
                object st = dict["a1"];
                if (st != null && !(bool)st.GetType().GetField("layer1InFlight").GetValue(st)) cleared = true;
            }
            Check(cleared, "in-flight flag cleared after server-down dispatch (no permanent wedge)");
            Check(!comp.IntentBuffer.TryPopDesire("a1", out _), "no desire deposited when server unreachable → vanilla AI fallback");
        }

        // ------------------------------------------------------------ job giver + gate node (Spec §6.1/§6.2)
        static void Test_JobGiver()
        {
            Section("JobGiver_AIColonistDesire & gate node (Spec §6.1/§6.2)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);
            comp.MapGenerated(); // 1.6 post-gen hook; creates bridge (Settings non-null)
            var pawn = MakePawn("j1", map);

            Check(new ExposedJobGiver().Give(pawn) == null, "no desire + gate closed: returns null (vanilla takes over, <0.01ms)");

            var meal = new Thing { Position = new IntVec3(2, 0), def = new ThingDef { label = "meal" } };
            pawn.needs.food.CurLevelPercentage = 0.2f; // hungry — the scanner only offers a meal below 0.4
            comp.IntentBuffer.Push("j1", new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest, meal), "yum"));
            var job = new ExposedJobGiver().Give(pawn);
            Check(job != null && job.expiryInterval == 1800, "deposited desire consumed as Job with ~30s expiry");

            comp.IntentBuffer.Push("j1", new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest, new Thing { Position = new IntVec3(2, 0), Destroyed = true }), "stale"));
            Check(new ExposedJobGiver().Give(pawn) == null, "stale desire (destroyed target) silently discarded");

            // 2026-11-15 fix witness — end to end: the LLM picks a WORK impulse (previously always silently discarded at the
            // null-jobDef gate) → TryGiveJob must now return that job with its resolved def, not fall through to vanilla.
            var veinJ = new Thing { Position = new IntVec3(2, 0), def = new ThingDef { label = "steel vein" } };
            comp.IntentBuffer.Push("j1", new QueuedDesire(MakeOpt(1, "work", new JobDef(), veinJ), "time to dig"));
            var workJob = new ExposedJobGiver().Give(pawn);
            Check(workJob != null && workJob.def != null, "deposited WORK desire consumed as Job (no longer silently discarded)");
            Check(Log.Messages.Any(m => m.Contains("[job started]") && m.Contains("\"work\"") && m.Contains("(StubJob)")),
                "[job started] log line quotes the sensory phrasing + resolved JobDef (T4 decision→execution correlation, §23.5 attribution)");

            var gate = new ExposedGate();
            pawn.Drafted = true;
            Check(!gate.IsSatisfied(pawn), "gate node: drafted pawn excluded");
            pawn.Drafted = false;
            pawn.InMentalState = true;
            Check(!gate.IsSatisfied(pawn), "gate node: mental-state pawn excluded");
            pawn.InMentalState = false;
            Check(gate.IsSatisfied(pawn), "gate node: healthy colonist passes");

            // 2026-11-15 (user request "remove LLM from prisoners"): MainThinkTree is per-RACE (decompiled Pawn_Thinker),
            // so prisoners share MainColonistBehaviorCore and reach this node — the gate must reject them explicitly, or a
            // colony prisoner gets Layer 1 evaluations (~40k tokens/pawn-day) and could receive AI-issued jobs.
            var pris = MakePawn("j2", map); pris.IsPrisoner = true;
            Check(!gate.IsSatisfied(pris), "gate node: prisoner excluded (no LLM for prisoners)");
            var vis = MakePawn("j3"); vis.Faction = new Faction { IsPlayer = false };
            Check(!gate.IsSatisfied(vis), "gate node: non-player-faction pawn excluded");

            // 2026-11-15: snapshot now carries a display name so L1 log lines read "Cook (Human1006)" instead of a bare
            // ThingID that looked like a different pawn.
            var snapN = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, new List<AffordanceOption>());
            Check(snapN.PawnName == pawn.LabelCap && snapN.PawnId == pawn.ThingID, "snapshot carries display name + stable ThingID key");
        }

        // ------------------------------------------------------------ Phase 2: mood → entropy + intellect tiers (Spec §3.2)
        static void Test_Phase2MoodAndIntellect()
        {
            Section("Phase 2 — mood bands, entropy mapping & intellect tiers (Spec §3.2)");

            // Mood tier bands over the 0..1 percentage from Need_Mood.CurLevelPercentage.
            Check(MoodEntropyMapper.MoodTier(0.9f) == "ecstatic", ">=0.75 ecstatic");
            Check(MoodEntropyMapper.MoodTier(0.6f) == "happy", ">=0.6 happy");
            Check(MoodEntropyMapper.MoodTier(0.45f) == "content", ">=0.45 content");
            Check(MoodEntropyMapper.MoodTier(0.3f) == "stressed", ">=0.3 stressed");
            Check(MoodEntropyMapper.MoodTier(0.15f) == "depressed", ">=0.15 depressed");
            Check(MoodEntropyMapper.MoodTier(0.1f) == "furious", "<0.15 furious (vanilla berserk zone)");
            Check(MoodEntropyMapper.MoodTier(-1f) == "content" && MoodEntropyMapper.MoodTier(1.5f) == "content",
                "out-of-range / unknown mood falls back to the content baseline");

            // Float overloads for the worker thread: base + tier offset, hard-clamped to [0.50, 0.85].
            Check(Math.Abs(MoodEntropyMapper.MoodToTemperature(0.7f, 0.9f) - 0.75f) < 1e-4f, "ecstatic: +0.05 → 0.75");
            Check(Math.Abs(MoodEntropyMapper.MoodToTemperature(0.7f, 0.2f) - 0.55f) < 1e-4f, "depressed: -0.15 → 0.55");
            Check(MoodEntropyMapper.MoodToTemperature(0.7f, 0.1f) == MoodEntropyMapper.TempCeiling, "furious: +0.20 clamps to ceiling 0.85");
            Check(Math.Abs(MoodEntropyMapper.MoodToTopP(0.9f) - 0.92f) < 1e-4f, "ecstatic top_p 0.92");
            Check(Math.Abs(MoodEntropyMapper.MoodToTopP(0.2f) - 0.80f) < 1e-4f, "depressed top_p 0.80");
            Check(Math.Abs(MoodEntropyMapper.MoodToTopP(-1f) - 0.85f) < 1e-4f, "unknown mood top_p = content baseline 0.85");

            // Intellect (SkillDefOf.Intellectual level in 1.6) → cadence + colony-horizon tiers.
            Check(PrefixPromptBuilder.CadenceRuleFor(-1).Contains("plain"), "unknown intellect: neutral cadence");
            Check(PrefixPromptBuilder.CadenceRuleFor(3).Contains("blunt"), "low (<5): blunt cadence");
            Check(PrefixPromptBuilder.CadenceRuleFor(7).Contains("notice details"), "mid (5-9): detail-pausing cadence");
            Check(PrefixPromptBuilder.CadenceRuleFor(12).Contains("layered"), "high (>=10): layered cadence");
            Check(PrefixPromptBuilder.ColonyHorizonFor(3) == "", "low intellect: no colony horizon line");
            Check(PrefixPromptBuilder.ColonyHorizonFor(7).Contains("loose sense"), "mid intellect: loose settlement awareness");
            Check(PrefixPromptBuilder.ColonyHorizonFor(12).Contains("wider state"), "high intellect: tracks the wider state");
        }

        // ------------------------------------------------------------ Phase 2: trait weight seeding (Spec §4.1 layer 2)
        static void Test_Phase2TraitSeeding()
        {
            Section("Phase 2 — continuous trait weight seeding (Spec §4.1 layer 2)");
            var pawn = MakePawn("seed1");
            pawn.story.traits.allTraits.Add(new Trait { LabelCap = "Stoic", Degree = 1 });
            pawn.story.traits.allTraits.Add(new Trait { LabelCap = "Nervous", Degree = -4 });
            pawn.story.traits.allTraits.Add(new Trait { LabelCap = "SuppressedOne", Degree = 3, Suppressed = true });

            var mind = new ColonistMindState(pawn);
            ColonistMindState.SeedTraitWeightsFromPawn(mind, pawn);
            Check(Math.Abs(mind.traitWeights["stoic"] - 0.65f) < 1e-4f, "degree +1 → weight 0.65 (0.5 + 0.15·degree)");
            Check(Math.Abs(mind.traitWeights["nervous"] - 0.05f) < 1e-4f, "degree -4 clamps to the floor 0.05");
            Check(!mind.traitWeights.ContainsKey("suppressedone"), "suppressed traits are not seeded");

            mind.traitWeights["stoic"] = 0.9f; // simulate Layer 3 drift having moved the weight
            ColonistMindState.SeedTraitWeightsFromPawn(mind, pawn);
            Check(Math.Abs(mind.traitWeights["stoic"] - 0.9f) < 1e-4f, "idempotent: re-seeding never overwrites drifted weights");

            var fresh = new ColonistMindState(pawn);
            ColonistMindState.SeedTraitWeightsFromPawn(fresh, null);
            Check(fresh.traitWeights.Count == 0, "null pawn: seeding is a safe no-op");
        }

        // ------------------------------------------------------------ Phase 2: identity prefix (Spec §7.1)
        static void Test_Phase2IdentityPrefix()
        {
            Section("Phase 2 — per-pawn identity prefix & KV-cache byte stability (Spec §7.1)");
            var map = new Map();
            map.Parent = new RimWorld.Planet.Settlement { Name = "Home" };
            Find.CurrentMap = map;

            var pawn = MakePawn("id1", map);
            pawn.LabelCap = "Alice";
            pawn.ageTracker.AgeBiologicalYears = 42;
            pawn.story.traits.allTraits.Add(new Trait { LabelCap = "Stoic", Degree = 1 });
            pawn.story.traits.allTraits.Add(new Trait { LabelCap = "Quick-Tempered", Degree = -2 });
            var miningDef = new SkillDef { label = "mining" };
            var cookingDef = new SkillDef { label = "cooking" };
            pawn.skills.skills.Add(new SkillRecord { def = miningDef, levelInt = 10 });
            pawn.skills.skills.Add(new SkillRecord { def = cookingDef, levelInt = 8 }); // above intellect so top-2 is mining+cooking
            pawn.skills.skills.Add(new SkillRecord { def = SkillDefOf.Intellectual, levelInt = 7 });

            var wc = Find.World.GetComponent<AIColonistWorldComponent>();

            var snap1 = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, new List<AffordanceOption>());
            Check(snap1.AgeYears == 42 && snap1.ColonyName == "Home" && snap1.IntellectualLevel == 7,
                "identity capture: age / colony name / intellect level from pawn + map");
            Check(snap1.TraitsLine.Contains("stoic (clearly)") && snap1.TraitsLine.Contains("quick-tempered (deeply)"),
                "trait line: lower-cased label + degree descriptor (mildly/clearly/deeply)");
            Check(snap1.SkillsLine == "You are known around here for your mining and cooking.",
                "skill line: top-2 skill NAMES only — levels excluded so the cached prefix stays byte-stable");

            string sys1 = PrefixPromptBuilder.BuildSystemPrompt(snap1);
            Check(sys1.Contains("You are Alice, age 42, living in the settlement of Home."), "prefix: real name/age/colony line");
            Check(!sys1.Contains("[") && !sys1.Contains("a colonist"), "prefix: no unreplaced placeholders, no generic fallback for a known pawn");
            Check(sys1.Contains("- Values: stoic") && sys1.Contains("- Dislikes: quick-tempered"),
                "values/fears from continuous weights (seeded 0.65 / 0.2 cross the 0.55 / 0.35 thresholds)");

            // KV-cache byte stability: an unchanged pawn must produce a byte-identical prefix on the next capture.
            var snap2 = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, new List<AffordanceOption>());
            Check(PrefixPromptBuilder.BuildSystemPrompt(snap2) == sys1, "byte-stable prefix across captures (KV-cache friendly)");

            // Layer 3 drift feedback: the prefix changes ONLY when a weight crosses a threshold.
            var mind = wc.GetOrCreateMindState(pawn);
            mind.traitWeights["quick-tempered"] = 0.6f; // drift pushes it over the value threshold
            var snap3 = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, new List<AffordanceOption>());
            string sys3 = PrefixPromptBuilder.BuildSystemPrompt(snap3);
            Check(sys3 != sys1 && sys3.Contains("- Values: stoic, quick-tempered") && sys3.Contains("- Dislikes: pain, hunger"),
                "prefix updates when drift crosses the 0.55 threshold (aversion falls back to defaults)");

            // Negative mood thoughts → first-person grudge prose (Appendix A).
            var other = MakePawn("id2", map); other.LabelCap = "Bob";
            pawn.needs.mood.thoughts.memories.Memories.Add(
                new Thought_Memory { moodOffset = -3, otherPawn = other, LabelCap = "Ate off the cold floor" });
            var snap4 = SensoryFramingBuilder.CaptureMainThreadSnapshot(pawn, new List<AffordanceOption>());
            Check(snap4.BodyAndSurroundingsLines.Any(l => l.Contains("bothered about Bob") && l.Contains("ate off the cold floor")),
                "negative mood thought → first-person grudge line (zero mechanical terms)");

            Find.CurrentMap = null; // restore — other tests assume no settlement
        }

        // ------------------------------------------------------------ Phase 2: AI-first hold (backlog #8)
        static void Test_Phase2Hold()
        {
            Section("Phase 2 — AI-first job priority & hold circuit breakers (backlog #8)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);
            comp.MapGenerated(); // creates the bridge; gate stays closed (fresh real-time clock) so nothing dispatches
            var pawn = MakePawn("h1", map);

            var dictField = typeof(AIColonistGameComponent).GetField("callStates", BindingFlags.NonPublic | BindingFlags.Instance);
            System.Collections.IDictionary dict = (System.Collections.IDictionary)dictField.GetValue(comp);
            comp.ShouldQueueEvaluation(pawn); // creates the per-pawn state entry
            object st = dict[pawn.ThingID];
            var t = st.GetType();

            Check(!comp.ShouldHoldPawn(pawn), "nothing in flight: no hold (vanilla handles the pawn)");
            t.GetField("layer1InFlight").SetValue(st, true);
            Check(comp.ShouldHoldPawn(pawn), "request in flight + healthy pipeline: hold the pawn");

            pawn.needs.rest.CurLevelPercentage = 0.05f;
            Check(!comp.ShouldHoldPawn(pawn), "physiological floor: exhausted (rest < 0.1) is never held — vanilla sleep wins");
            pawn.needs.rest.CurLevelPercentage = 1f;

            t.GetField("consecutiveL1Failures").SetValue(st, 2);
            Check(!comp.ShouldHoldPawn(pawn), "circuit breaker: 2 consecutive dispatch failures → no hold (vanilla works)");
            t.GetField("consecutiveL1Failures").SetValue(st, 1);
            Check(comp.ShouldHoldPawn(pawn), "one failure still tolerates a hold");

            t.GetField("staleDrops").SetValue(st, 3);
            Check(!comp.ShouldHoldPawn(pawn), "circuit breaker: 3 stale drops → one vanilla cycle (no hold)");
            Check((int)t.GetField("staleDrops").GetValue(st) == 0, "stale-drop breaker resets after tripping");

            t.GetField("consecutiveL1Failures").SetValue(st, 3);
            t.GetField("staleDrops").SetValue(st, 5);
            comp.NoteDesireExecuted(pawn.ThingID);
            Check((int)t.GetField("consecutiveL1Failures").GetValue(st) == 0 && (int)t.GetField("staleDrops").GetValue(st) == 0,
                "an executed AI desire resets both breakers (fresh start)");

            AIColonistMod.Settings.AIFirstJobPriority = false;
            Check(!comp.ShouldHoldPawn(pawn), "setting OFF: blend mode — never hold even while a request is in flight");
            AIColonistMod.Settings.AIFirstJobPriority = true;

            // End-to-end JobGiver flow: in-flight + empty buffer → a short Wait hold job, not null.
            t.GetField("layer1InFlight").SetValue(st, true);
            var job = new ExposedJobGiver().Give(pawn);
            Check(job != null && job.def == JobDefOf.Wait && job.expiryInterval == AIColonistGameComponent.HoldPollTicks,
                "hold job: JobDefOf.Wait with " + AIColonistGameComponent.HoldPollTicks + "-tick expiry (pawn re-thinks when it lands)");
            Check(Log.Messages.Any(m => m.Contains("[hold]")), "[hold] log line emitted when the hold engages (T4 correlation)");

            t.GetField("consecutiveL1Failures").SetValue(st, 2);
            Check(new ExposedJobGiver().Give(pawn) == null, "breaker tripped: JobGiver falls through to vanilla (null)");
        }

        // ------------------------------------------------------------ Phase 2: one-shot cold-start warmup (§22.7)
        static void Test_WarmUpColdStart()
        {
            Section("Phase 2 — one-shot cold-start warmup (Ollama finding, §22.7)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);

            var fake = new FakeLlamaBridge();
            typeof(AIColonistGameComponent).GetField("bridge", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(comp, fake);

            Check(fake.warmups == 0, "no warmup before the first dispatch");

            comp.EnqueueAsyncLayer1(new SensorySnapshot { PawnId = "w1", Affordances = new List<AffordanceOption> { MakeOpt(1, "eat", JobDefOf.Ingest) } });
            bool fired = false;
            for (int i = 0; i < 150 && !fired; i++) { System.Threading.Thread.Sleep(20); fired = fake.warmups >= 1; }
            Check(fired, "first Layer 1 dispatch fires the warmup");

            int afterFirst = fake.warmups;
            comp.EnqueueAsyncLayer1(new SensorySnapshot { PawnId = "w2", Affordances = new List<AffordanceOption> { MakeOpt(1, "eat", JobDefOf.Ingest) } });
            System.Threading.Thread.Sleep(300);
            Check(fake.warmups == afterFirst, "second dispatch does NOT re-warm (one-shot per map session)");

            // The real client's warmup degrades quietly when the server is down (normal pre-game state).
            string savedUrl = AIColonistMod.Settings.Layer1ServerUrl;
            AIColonistMod.Settings.Layer1ServerUrl = "http://127.0.0.1:9"; // closed port → immediate connection refused
            try
            {
                var real = new LlamaServerClient(AIColonistMod.Settings);
                Check(!real.WarmUpAsync().Result, "real client: warmup against a closed port returns false (silent no-op)");
            }
            finally { AIColonistMod.Settings.Layer1ServerUrl = savedUrl; }
        }

        // ------------------------------------------------------------ Phase 2: worker-thread log marshaling (§23.3)
        static void Test_MainThreadLog()
        {
            Section("Worker-thread log marshaling (in-game log window race, §23.3)");
            MainThreadLog.Drain(); // flush residue from earlier tests (the warmup test's port-9 skip line now routes here too)
            Log.Messages.Clear();
            Log.Errors.Clear();

            // Enqueue from a background thread — the lines must NOT reach Verse.Log until the main-thread drain runs.
            var task = Task.Run(() =>
            {
                MainThreadLog.Message("[AIColonists] worker line");
                MainThreadLog.Error("[AIColonists] worker error");
                MainThreadLog.ErrorOnce("[AIColonists] worker once", 4901);
                MainThreadLog.ErrorOnce("[AIColonists] worker once", 4901); // duplicate key — deduped at drain time
            });
            task.Wait();

            Check(Log.Messages.Count == 0, "worker-thread Message is deferred (not logged until the main-thread drain)");
            Check(Log.Errors.Count == 0, "worker-thread Error/ErrorOnce are deferred too");
            // The queue holds RAW lines — ErrorOnce dedupe is applied at drain time by Verse.Log itself (process-global keys).
            Check(MainThreadLog.PendingCount == 4, "four raw lines pending for the drain (dedupe happens at drain time)");

            MainThreadLog.Drain(); // stands in for AIColonistGameComponent.MapComponentTick (main thread)

            Check(Log.Messages.Contains("[AIColonists] worker line"), "Message line delivered on drain");
            Check(Log.Errors.Contains("[AIColonists] worker error"), "Error line delivered on drain");
            Check(Log.Errors.Count(m => m.Contains("worker once")) == 1, "ErrorOnce dedupe applied at drain time (duplicate key logged once)");
            Check(MainThreadLog.PendingCount == 0, "queue empty after drain");

            int after = Log.Messages.Count + Log.Errors.Count;
            MainThreadLog.Drain();
            Check(Log.Messages.Count + Log.Errors.Count == after, "empty drain is a no-op");
        }

        // ------------------------------------------------------------ draft patch (Spec §6.3)
        static void Test_DraftPatch()
        {
            Section("DraftController patch — drafting override (Spec §6.3)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);
            var pawn = MakePawn("d1", map);

            comp.IntentBuffer.Push("d1", new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest), "x"));
            DraftControllerPatches.Drafted_Postfix(new Pawn_DraftController { pawn = pawn }, true);
            Check(!comp.IntentBuffer.TryPopDesire("d1", out _), "drafting purges the pending desire");

            comp.IntentBuffer.Push("d1", new QueuedDesire(MakeOpt(1, "eat", JobDefOf.Ingest), "y"));
            DraftControllerPatches.Drafted_Postfix(new Pawn_DraftController { pawn = pawn }, false);
            Check(comp.IntentBuffer.TryPopDesire("d1", out var d) && d.innerVoiceShort == "y", "un-drafting does NOT purge");

            DraftControllerPatches.Drafted_Postfix(new Pawn_DraftController { pawn = new Pawn { ThingID = "orphan" } }, true);
            Check(true, "pawn without map: patch is a safe no-op (no exception)");
        }

        // ------------------------------------------------------------ interaction patch (Spec §6.3)
        static void Test_InteractionPatch()
        {
            Section("Pawn_InteractionsTracker patch (Spec §6.3)");
            var wc = Find.World.GetComponent<AIColonistWorldComponent>();
            var a = MakePawn("ia");
            var b = MakePawn("ib");
            b.LabelCap = "Bob";

            // 1.6: the postfix receives the tracker instance; its private `pawn` field is recovered inside the patch
            // via a cached reflection FieldInfo (verified on 1.6.4871: no accessor exists).
            var tracker = new Pawn_InteractionsTracker(a);

            int before = wc.GetOrCreateMindState(a).memory.episodic.Count;
            InteractionTrackerPatches.TryInteractWith_Postfix(tracker, b, null, true);
            var mind = wc.GetOrCreateMindState(a);
            Check(mind.memory.episodic.Count == before + 1 && mind.memory.episodic[before].text.Contains("Bob"),
                "successful interaction recorded as episodic memory (pawn recovered from private field)");

            InteractionTrackerPatches.TryInteractWith_Postfix(tracker, b, null, false);
            Check(mind.memory.episodic.Count == before + 1, "__result=false (failed interaction) not recorded");
        }

        // ------------------------------------------------------------ thought patch (Spec §6.3)
        static void Test_ThoughtPatch()
        {
            Section("MemoryThoughtHandler patch (Spec §6.3)");
            var wc = Find.World.GetComponent<AIColonistWorldComponent>();

            var pawn = MakePawn("t1");
            pawn.Faction = new Faction { IsPlayer = true };
            var mind = wc.GetOrCreateMindState(pawn);
            int before = mind.memory.episodic.Count;
            // otherPawn is NULL for every non-social memory — this is the exact call shape that NRE'd live on 1.6
            // during InitNewGame (map-gen freeze). The patch must key on the OWNING pawn (__instance.pawn) instead.
            ThoughtHandlerPatches.TryGainMemory_Postfix(new MemoryThoughtHandler { pawn = pawn }, new Thought_Memory(), null);
            Check(mind.memory.episodic.Count == before + 1 && mind.memory.episodic[before].text.Contains("Something happened"),
                "memory thought captured as episodic event (otherPawn=null, keyed on owning pawn)");

            var enemy = MakePawn("e1");
            enemy.Faction = new Faction { IsPlayer = false };
            ThoughtHandlerPatches.TryGainMemory_Postfix(new MemoryThoughtHandler { pawn = enemy }, new Thought_Memory(), null);
            Check(wc.GetOrCreateMindState(enemy).memory.episodic.Count == 0, "non-player pawns not tracked");

            // Round 2 (live NRE 2026-11-14): ThoughtDef.Label is computed and derefs stages[0] unguarded — during save load
            // the stages list can hold a NULL first element. Witness that the unpatched path dies, then prove the postfix survives.
            var brokenDef = new ThoughtDef { label = "", defName = "HalfLoadedThought" };
            brokenDef.stages.Add(null); // live state: list exists, first element still null mid-load
            bool labelNred = false;
            try { string lbl = brokenDef.Label; object _o = lbl; } catch (NullReferenceException) { labelNred = true; }
            Check(labelNred, "unpatched def.Label NREs on half-loaded stages (mirrors live Player.log round 2)");

            int before2 = mind.memory.episodic.Count;
            bool threw2 = false;
            try { ThoughtHandlerPatches.TryGainMemory_Postfix(new MemoryThoughtHandler { pawn = pawn }, new Thought_Memory { def = brokenDef }, null); }
            catch (Exception) { threw2 = true; }
            Check(!threw2, "postfix survives half-loaded thought def (no longer calls computed Label)");
            Check(mind.memory.episodic.Count == before2 + 1 && mind.memory.episodic[before2].text.Contains("HalfLoadedThought"),
                "episodic text falls back to plain defName field");
        }

        // ------------------------------------------------ IngestibleSource null-guard (live NRE fix, 2026-11-13 stress test)
        static void Test_IngestibleSourcePatch()
        {
            Section("IngestibleSourcePatches — JobDriver_Ingest dead-target guard");

            // Healthy target: prefix must let the vanilla getter run and leave __result untouched.
            var meal = new Thing();
            var driver = new JobDriver_Ingest { job = new Job { targetA = new LocalTargetInfo(meal) } };
            Thing result = null;
            bool ranOriginal = IngestibleSourcePatches.IngestibleSource_Prefix(driver, ref result);
            Check(ranOriginal, "healthy meal → original getter runs (zero behavior change)");
            Check(result == null, "healthy meal → __result untouched");

            // The live crash: the meal is destroyed while the pawn walks to it. In 1.6 the reference then RESOLVES TO NULL —
            // witness that the unpatched path dies exactly like Player.log did: the getter itself merely resolves (returns
            // null, as in vanilla), and it is the FailOn lambda's FIRST DEREFERENCE (`!IngestibleSource.Destroyed`, b__19_0)
            // that NREs.
            meal.Destroyed = true;
            bool nred = false;
            try
            {
                object src = typeof(JobDriver_Ingest).GetProperty("IngestibleSource", BindingFlags.NonPublic | BindingFlags.Instance)
                                                              .GetValue(driver); // → null (dead meal), same as vanilla
                bool _ = ((Thing)src).Destroyed; // the lambda's first dereference — the exact crash site
            }
            catch (NullReferenceException) { nred = true; }                    // direct deref of null (our witness line)
            catch (TargetInvocationException tie)                              // reflection-wrapped variant
            {
                if (tie.InnerException is NullReferenceException) nred = true;
            }
            Check(nred, "unpatched path NREs on dead target (mirrors live Player.log crash b__19_0)");

            // The prefix must skip the original and hand back a live non-ingestible sentinel so vanilla's own first
            // FailOn (`!src.Destroyed && !src.IngestibleNow`) evaluates TRUE and fails the job gracefully.
            result = null;
            ranOriginal = IngestibleSourcePatches.IngestibleSource_Prefix(driver, ref result);
            Check(!ranOriginal, "destroyed meal → original getter skipped (NRE site never reached)");
            Check(result != null && !result.Destroyed, "sentinel is a live thing");
            Check(result.def.IsIngestible == false, "sentinel is non-ingestible (steel)");
            Check(!result.Destroyed && !result.IngestibleNow, "vanilla first-FailOn predicate now TRUE → job fails gracefully");

            // Never-set targetA (default LocalTargetInfo): same substitution.
            var bareDriver = new JobDriver_Ingest { job = new Job() };
            result = null;
            ranOriginal = IngestibleSourcePatches.IngestibleSource_Prefix(bareDriver, ref result);
            Check(!ranOriginal && result != null, "unset targetA → sentinel substitution");

            // Null driver: defensive pass-through to vanilla.
            ranOriginal = IngestibleSourcePatches.IngestibleSource_Prefix(null, ref result);
            Check(ranOriginal, "null driver → vanilla path (defensive)");
        }

        // ------------------------------------------------ LabelCap load-time NRE (live fix, 2026-11-14 save load)
        static void Test_MindStateLoadTimeSelfHeal()
        {
            Section("ColonistMindState — LabelCap NRE on half-loaded pawn (save-load fix)");

            var wc = Find.World.GetComponent<AIColonistWorldComponent>();

            // The live crash: TryGainMemory fires during save load while the pawn is only half-deserialized —
            // LabelCap throws internally (KindLabel derefs kind/race defs still NULL at that point).
            var halfLoaded = new HalfLoadedPawn { ThingID = "halfloaded-1", Broken = true };
            ColonistMindState ms = null;
            bool threw = false;
            try { ms = wc.GetOrCreateMindState(halfLoaded); } catch (Exception) { threw = true; }
            Check(!threw, "GetOrCreateMindState survives a half-loaded pawn (no NRE propagates to the thought postfix)");
            Check(ms != null && string.IsNullOrEmpty(ms.pawnName), "mind state created with empty name when LabelCap throws");

            // The pawn finishes deserializing — our systems touch it again on normal ticks; the name must self-heal.
            halfLoaded.Broken = false;
            var healed = wc.GetOrCreateMindState(halfLoaded);
            Check(healed == ms && healed.pawnName == "Repaired", "name self-heals on the next call after the pawn recovers");

            // A normal pawn is unaffected by the guard.
            var healthy = MakePawn("healthy-1");
            healthy.LabelCap = "Healthy";
            Check(wc.GetOrCreateMindState(healthy).pawnName == "Healthy", "normal pawn name resolution unchanged");
        }

        // Simulates a pawn mid-save-load: LabelCap throws until deserialization completes.
        class HalfLoadedPawn : Pawn
        {
            public bool Broken;
            public override string LabelCap
            {
                get { if (Broken) throw new NullReferenceException("simulated half-loaded pawn"); return "Repaired"; }
                set { } // real property has no setter — stub base does; ignored here
            }
        }

        // ------------------------------------------------------------ AI Mind tab (Spec §9 Phase 4)
        static void Test_TabPatch()
        {
            Section("AI Mind inspection tab — shared singleton (Spec §9 Phase 4)");

            // 1.6: tabs are registered in the pawn ThingDef's <inspectorTabs> XML and resolved by
            // InspectTabManager.GetSharedInstance via Activator.CreateInstance → parameterless ctor required,
            // NO per-pawn state may be stored; everything reads SelPawn live at draw time.
            var tab = new ITab_Pawn_AIMind();
            Check(tab.labelKey == "TabAIMind" && tab.tutorTag == "AIMind", "translation key + tutor tag set in ctor");

            var playerPawn = MakePawn("tp"); // stub defaults: player faction, humanlike
            tab.SelPawn = playerPawn;
            Check(tab.IsVisible, "visible on player-faction humanlike pawn with AI enabled");

            var animal = MakePawn("ta");
            animal.RaceProps.Humanlike = false;
            tab.SelPawn = animal;
            Check(!tab.IsVisible, "hidden for non-humanlike pawns");

            var enemy = MakePawn("te");
            enemy.Faction = new Faction { IsPlayer = false };
            tab.SelPawn = enemy;
            Check(!tab.IsVisible, "hidden for non-player-faction pawns");

            tab.SelPawn = null;
            Check(!tab.IsVisible, "hidden when no pawn selected");

            bool savedEnable = AIColonistMod.Settings.EnableAI;
            AIColonistMod.Settings.EnableAI = false;
            tab.SelPawn = playerPawn;
            Check(!tab.IsVisible, "hidden while AI disabled (opt-in)");
            AIColonistMod.Settings.EnableAI = savedEnable; // restore for remaining tests

            tab.TestFill(); // empty mind → "no data yet" hint path must not throw against stubs
            Check(true, "FillTab runs without exception (empty mind)");

            // Phase 4 (Spec §9): populated mind — every section (monologue + age, longing, memories, self-knowledge,
            // trait bars, relationship axes) must draw. The mind is created THROUGH the world component so
            // AIMindViewModel.Build resolves the very object we populate (ThingID keying).
            var wc = Find.World.GetComponent<AIColonistWorldComponent>();
            var pmind = wc.GetOrCreateMindState(playerPawn);
            playerPawn.Map = new Map();
            var bobTwin = MakePawn("tp-bob", playerPawn.Map); bobTwin.LabelCap = "Bob";
            pmind.lastInnerVoice = "I should check on the stove before it blows.";
            pmind.lastInnerVoiceTick = 100; // Find.TickManager.TicksGame is 0 in the stub → age clamps to 0 ("just now")
            pmind.longTermLonging = "A quiet workshop of my own, where I can tinker without being interrupted.";
            pmind.memory.AddEpisodic("Alice pulled me out of the fire.", 50);
            pmind.memory.AddSemantic("I am better at machines than at people.");
            pmind.traitWeights["Neuroticism"] = 0.8f;
            pmind.traitWeights["Bravery"] = 0.2f;
            pmind.relationships.Add(new RelationshipVector { targetPawnId = "zz-gone", lastInteractionTick = 300 });
            pmind.relationships.Add(new RelationshipVector { targetPawnId = "tp-bob", trust = 0.8f, lastInteractionTick = 200 });

            tab.SelPawn = playerPawn; // still visible: player faction + humanlike + AI enabled (restored above)
            tab.TestFill();
            Check(true, "FillTab runs without exception (populated mind — all sections draw)");
        }

        // ------------------------------------------------------------ area batching (Spec §3.4)
        static void Test_AreaBatcher()
        {
            Section("Area batcher grouping (Spec §3.4)");
            var map = new Map();
            var a1 = MakePawn("b1", map); a1.Position = new IntVec3(0, 0);
            var a2 = MakePawn("b2", map); a2.Position = new IntVec3(5, 0);  // same 12-tile cell as a1
            var a3 = MakePawn("b3", map); a3.Position = new IntVec3(20, 0); // different cluster (gx=1)
            var drafted = MakePawn("b4", map); drafted.Drafted = true;
            var mental = MakePawn("b5", map); mental.InMentalState = true;

            var batches = AreaBatcher.BuildBatches(map);
            Check(batches.ContainsKey("outdoor:0,0") && batches["outdoor:0,0"].Count == 2, "same outdoor cluster grouped (12-tile grid)");
            Check(batches.ContainsKey("outdoor:1,0") && batches["outdoor:1,0"].Count == 1, "distant pawn in separate cluster");
            Check(batches.Values.Sum(l => l.Count) == 3, "drafted + mental-state pawns excluded from batching");

            var room = new Room { ID = 7, Map = map };
            TestRig.RoomsByThing[a1] = room; // 1.6: RegionAndRoomQuery.GetRoom(pawn, ...) is the lookup — stub hook stands in for it
            batches = AreaBatcher.BuildBatches(map);
            Check(batches.ContainsKey("room:7") && batches["room:7"].Count == 1, "indoor pawn keyed by Room.ID");

            // Phase 4 (Spec §9, 50-colonist stress test): district batching fallback.
            var map2 = new Map();
            int n = 0;
            foreach (IntVec3 pos in new[] { new IntVec3(0, 0), new IntVec3(5, 0), new IntVec3(40, 0), new IntVec3(45, 0), new IntVec3(75, 0), new IntVec3(80, 0) })
            { var p = MakePawn("df" + (n++), map2); p.Position = pos; }
            var small = AreaBatcher.SelectBatchesForDispatch(map2, 4);
            Check(small.Count == 3, "pass-through: eligible spaces <= cap dispatch every space (no fallback)");

            // 6 eligible outdoor clusters (2 pawns each) > cap 4 → districts on the 30-tile grid merge A+B (gx=0).
            var map3 = new Map();
            n = 0;
            foreach (IntVec3 pos in new[] {
                new IntVec3(0, 0),   new IntVec3(5, 0),     // cluster A → district gx=0
                new IntVec3(14, 0),  new IntVec3(19, 0),    // cluster B → district gx=0 (merges with A → 4 pawns)
                new IntVec3(40, 0),  new IntVec3(45, 0),    // cluster C → district gx=1
                new IntVec3(75, 0),  new IntVec3(80, 0),    // cluster D → district gx=2
                new IntVec3(110, 0), new IntVec3(115, 0),   // cluster E → district gx=3
                new IntVec3(145, 0), new IntVec3(150, 0)    // cluster F → district gx=4
            })
            { var p = MakePawn("fb" + (n++), map3); p.Position = pos; }
            var big = AreaBatcher.SelectBatchesForDispatch(map3, 4);
            Check(big.Count == 4, "fallback: 6 eligible spaces > cap 4 → district merge, capped at 4");
            Check(big[0].Count == 4, "biggest district (merged A+B) dispatched first");
            Check(big.Sum(l => l.Count) == 10, "fallback covers 10 of 12 pawns (smallest district deferred, none silently lost)");
        }

        // ------------------------------------------------------------ AI Mind view model (Spec §9 Phase 4)
        static void Test_AIMindViewModel()
        {
            Section("AI Mind view model — caps & ordering (Spec §9 Phase 4)");

            // Empty mind → the tab shows a "no data yet" hint.
            Check(!AIMindViewModel.BuildFromMind(MakePawn("avm-empty"), new ColonistMindState(), 1000).hasAnyData,
                "empty mind → hasAnyData=false (hint path)");

            // Populated mind created THROUGH the world component so Build() resolves the same object (ThingID keying).
            var wc = Find.World.GetComponent<AIColonistWorldComponent>();
            var map = new Map();
            var me = MakePawn("avm-me", map);
            var alice = MakePawn("t1", map); alice.LabelCap = "Alice";
            var bob = MakePawn("t2", map); bob.LabelCap = "Bob";
            var carl = MakePawn("t3", map); carl.LabelCap = "Carl";
            var mind = wc.GetOrCreateMindState(me);

            mind.lastInnerVoice = "The furnace is going to blow if I don't stoke it.";
            mind.lastInnerVoiceTick = 900; // now=1000 → age 100 ticks
            mind.longTermLonging = "A workshop of my own.";
            mind.memory.AddEpisodic("m-oldest", 10);
            mind.memory.AddEpisodic("m-mid", 20);
            mind.memory.AddEpisodic("m-newest-3", 30);
            mind.memory.AddEpisodic("m-newest-2", 40);
            mind.memory.AddEpisodic("m-newest-1", 50); // cap 3 → newest three, newest first
            mind.memory.AddSemantic("I am better at machines than people.");
            mind.memory.AddSemantic("I keep the stove lit so others sleep warm."); // latest semantic wins

            // Trait drift: most drifted from neutral (0.5) first; name breaks ties. Values are exact binary fractions
            // (multiples of 1/16) so the drifts are EXACT — 0.9f/0.1f would NOT tie in float (~1e-8 asymmetry).
            mind.traitWeights["a"] = 1f;     // |d|=0.5    → rank 1
            mind.traitWeights["h"] = 0.9375f;// |d|=0.4375 → rank 2
            mind.traitWeights["e"] = 0.875f; // |d|=0.375  → rank 3
            mind.traitWeights["b"] = 0.75f;  // |d|=0.25   → ties d, name "b" < "d" → rank 4
            mind.traitWeights["d"] = 0.25f;  // |d|=0.25   → rank 5
            mind.traitWeights["c"] = 0.625f; // |d|=0.125  → rank 6
            mind.traitWeights["f"] = 0.5625f;// |d|=0.0625 → cut by the cap of 6
            mind.traitWeights["g"] = 0.5f;   // |d|=0      → cut by the cap of 6

            // Relationships: most recently interacted first; ThingID→LabelCap among free colonists, "unknown" when gone.
            mind.relationships.Add(new RelationshipVector { targetPawnId = "zz-gone", lastInteractionTick = 400 });
            mind.relationships.Add(new RelationshipVector { targetPawnId = "t2", trust = 0.8f, lastInteractionTick = 300 });
            mind.relationships.Add(new RelationshipVector { targetPawnId = "t3", affection = 0.3f, lastInteractionTick = 200 });
            mind.relationships.Add(new RelationshipVector { targetPawnId = "t1", respect = 0.9f, lastInteractionTick = 100 });
            mind.relationships.Add(new RelationshipVector { targetPawnId = "t5-absent", lastInteractionTick = 50 }); // cut by cap 4

            var v = AIMindViewModel.Build(me, 1000);
            Check(v.hasAnyData, "populated mind → hasAnyData=true");
            Check(v.innerMonologue == "The furnace is going to blow if I don't stoke it." && v.monologueAgeTicks == 100,
                "inner monologue + tick age (100 ticks)");
            Check(v.longing == "A workshop of my own.", "long-term desire surfaced");
            Check(v.recentMemories.Count == 3 && v.recentMemories[0] == "m-newest-1" && v.recentMemories[2] == "m-newest-3",
                "episodic capped at 3, newest first (m-oldest/m-mid cut)");
            Check(v.latestSemantic == "I keep the stove lit so others sleep warm.", "latest semantic entry wins");

            var traitNames = new List<string>();
            foreach (AIMindViewModel.TraitBar t in v.traits) traitNames.Add(t.name);
            Check(traitNames.Count == 6 && traitNames[0] == "a" && traitNames[1] == "h" && traitNames[2] == "e"
                  && traitNames[3] == "b" && traitNames[4] == "d" && traitNames[5] == "c",
                "trait bars capped at 6, most-drifted-from-0.5 first; exact tie (b/d @ |d|=0.25) broken by name; f/g cut");

            Check(v.relationships.Count == 4, "relationships capped at 4");
            Check(v.relationships[0].targetName == "unknown" && v.relationships[1].targetName == "Bob"
                  && v.relationships[2].targetName == "Carl" && v.relationships[3].targetName == "Alice",
                "relationships most-recent-first; ThingID→LabelCap, 'unknown' for gone targets");
            Check(v.relationships[1].trust == 0.8f && v.relationships[2].affection == 0.3f && v.relationships[3].respect == 0.9f,
                "relationship axes carried through to the view");
        }

        // ------------------------------------------------------------ VRAM presets (Spec §9 Phase 4)
        static void Test_VramPresets()
        {
            Section("VRAM presets (Spec §9 Phase 4 — hardware table + §10 mitigation)");

            Check(VramPresets.All.Count == 3, "three presets in display order: CPU / 1x P100 / 2x P100");
            var cpu = VramPresets.All[0];
            var p1 = VramPresets.All[1];
            var p2 = VramPresets.All[2];

            Check(cpu.name == "CPU homelab" && cpu.layer1Model == "qwen2.5-1.5b-instruct-q4_k_m"
                  && cpu.layer2Model == "qwen2.5-1.5b-instruct-q4_k_m" && cpu.layer3Model == "qwen2.5-1.5b-instruct-q4_k_m"
                  && cpu.requestTimeoutMs == 30000,
                "CPU homelab: 1.5B q4_k_m on all three layers, 30 s timeout (live-verified Phase 1–3 config)");

            Check(p1.name == "1x P100 16GB" && p1.layer1Model == "qwen2.5-3b-instruct-q8_0"
                  && p1.layer2Model == "qwen2.5-3b-instruct-q8_0" && p1.layer3Model == "qwen2.5-7b-instruct-q4_k_m"
                  && p1.requestTimeoutMs == 15000,
                "1x P100 16GB: 3B q8_0 (L1/L2) + 7B q4_k_m (L3), 15 s timeout (Spec §10)");

            Check(p2.name == "2x P100 32GB" && p2.layer1Model == "qwen2.5-3b-instruct-q8_0"
                  && p2.layer2Model == "qwen2.5-3b-instruct-q8_0" && p2.layer3Model == "qwen2.5-14b-instruct-q4_k_m"
                  && p2.requestTimeoutMs == 15000,
                "2x P100 32GB: 3B q8_0 (L1/L2) + 14B q4_k_m (L3), 15 s timeout (hardware table Option A)");

            // Apply writes model names + timeout ONLY — server URLs stay user-owned.
            var s = new Settings();
            string u1 = s.Layer1ServerUrl, u2 = s.Layer2ServerUrl, u3 = s.Layer3ServerUrl;
            VramPresets.Apply(p1, s);
            Check(s.Layer1Model == "qwen2.5-3b-instruct-q8_0" && s.Layer2Model == "qwen2.5-3b-instruct-q8_0"
                  && s.Layer3Model == "qwen2.5-7b-instruct-q4_k_m" && s.RequestTimeoutMs == 15000,
                "Apply writes the preset's model names + timeout");
            Check(s.Layer1ServerUrl == u1 && s.Layer2ServerUrl == u2 && s.Layer3ServerUrl == u3,
                "Apply never touches server URLs (user-owned)");

            var sentinel = new Settings { Layer1Model = "sentinel-model" };
            VramPresets.Apply(null, sentinel);
            Check(sentinel.Layer1Model == "sentinel-model", "null-safe: Apply(null preset) is a no-op");
            VramPresets.Apply(p2, null); // must not throw
            Check(true, "null-safe: Apply(_, null settings) does not throw");
        }

        // ============================================================ Phase 3: Layer 2 room social (Spec §7.3)

        /// <summary>Wraps raw content in the llama-server /v1/chat/completions envelope with proper JSON escaping.</summary>
        static string LlmEnvelope(string content) =>
            JObject.FromObject(new { choices = new[] { new { message = new { content } } } }).ToString(Newtonsoft.Json.Formatting.None);

        static void Test_Layer2Parsing()
        {
            Section("Layer 2 response parsing (Spec §7.3)");
            var client = new LlamaServerClient(new Settings());
            var mi = typeof(LlamaServerClient).GetMethod("ParseLayer2", BindingFlags.NonPublic | BindingFlags.Instance);

            const string full = "{\"decisions\":[{\"colonist_name\":\"Alice\",\"inner_feeling\":\"I feel close to her.\",\"social_impulse\":\"warm_chat\",\"target_name\":\"Bob\",\"spoken_line\":\"The soup is ready.\"},{\"colonist_name\":\"Bob\",\"inner_feeling\":\"\",\"social_impulse\":\"none\",\"target_name\":\"\",\"spoken_line\":\"\"}]}";
            var res = (Layer2Result)mi.Invoke(client, new object[] { LlmEnvelope(full) });
            Check(res.decisions.Count == 2, "full envelope: both decisions parsed");
            Check(res.decisions[0].colonist_name == "Alice" && res.decisions[0].social_impulse == "warm_chat" &&
                  res.decisions[0].target_name == "Bob" && res.decisions[0].spoken_line == "The soup is ready." &&
                  res.decisions[0].inner_feeling == "I feel close to her.", "decision fields round-trip (snake_case)");
            Check(res.decisions[1].social_impulse == "none" && res.decisions[1].target_name == "", "\"none\" decision kept with empty target/line");

            // Truncation mid-object: the complete decision survives, the cut one is dropped.
            const string cut = "{\"decisions\":[{\"colonist_name\":\"Alice\",\"inner_feeling\":\"I feel close to her.\",\"social_impulse\":\"warm_chat\",\"target_name\":\"Bob\",\"spoken_line\":\"The soup is ready.\"},{\"colonist_name\":\"Bo";
            var res2 = (Layer2Result)mi.Invoke(client, new object[] { LlmEnvelope(cut) });
            Check(res2.decisions.Count == 1 && res2.decisions[0].social_impulse == "warm_chat",
                "max_tokens cut mid-object: complete decision kept, partial one dropped");

            // Quiet cycle: zero decisions is a VALID result (no throw).
            var res3 = (Layer2Result)mi.Invoke(client, new object[] { LlmEnvelope("{\"decisions\":[]}") });
            Check(res3.decisions.Count == 0, "empty decisions array → quiet cycle, no throw");

            // A decision missing either colonist_name or social_impulse is unusable → filtered.
            var res4 = (Layer2Result)mi.Invoke(client, new object[] { LlmEnvelope("{\"decisions\":[{\"colonist_name\":\"Alice\",\"inner_feeling\":\"x\",\"target_name\":\"Bob\",\"spoken_line\":\"\"}]}") });
            Check(res4.decisions.Count == 0, "decision without social_impulse filtered out");
        }

        static void Test_Layer3Parsing()
        {
            Section("Layer 3 response parsing (Spec §7.4)");
            var client = new LlamaServerClient(new Settings());
            var mi = typeof(LlamaServerClient).GetMethod("ParseLayer3", BindingFlags.NonPublic | BindingFlags.Instance);

            const string full = "{\"self_reflection\":\"I am learning to hold my ground.\",\"new_semantic_memory\":\"The storm teaches patience.\",\"relationship_deltas\":[{\"target_name\":\"Bob\",\"trust_delta\":0.03,\"affection_delta\":-0.02,\"respect_delta\":0.01,\"frustration_delta\":0.0}],\"trait_drift\":{\"trait_name\":\"stoic\",\"delta\":0.01},\"long_term_longing\":\"A quiet place of my own.\"}";
            var res = (Layer3Result)mi.Invoke(client, new object[] { LlmEnvelope(full) });
            Check(res.self_reflection == "I am learning to hold my ground." && res.new_semantic_memory == "The storm teaches patience.",
                "self_reflection + new_semantic_memory parsed");
            Check(res.relationship_deltas.Count == 1 && Math.Abs(res.relationship_deltas[0].trust_delta - 0.03f) < 1e-6f &&
                  Math.Abs(res.relationship_deltas[0].affection_delta + 0.02f) < 1e-6f, "relationship deltas parsed (negative values survive)");
            Check(res.trait_drift != null && res.trait_drift.trait_name == "stoic" && Math.Abs(res.trait_drift.delta - 0.01f) < 1e-6f,
                "nested trait_drift object parsed");
            Check(res.long_term_longing == "A quiet place of my own.", "long_term_longing parsed");

            // Truncation inside trait_drift: memories survive, drift is null (not a failure).
            const string cut = "{\"self_reflection\":\"Still here.\",\"new_semantic_memory\":\"\",\"relationship_deltas\":[{\"target_name\":\"Bob\",\"trust_delta\":0.01,\"affection_delta\":0.0,\"respect_delta\":0.0,\"frustration_delta\":0.0}],\"trait_drift\":{\"trait_name\":\"sto";
            var res2 = (Layer3Result)mi.Invoke(client, new object[] { LlmEnvelope(cut) });
            Check(res2.self_reflection == "Still here." && res2.trait_drift == null && res2.relationship_deltas.Count == 1,
                "truncated trait_drift: memories kept, drift null-safe");

            // Nothing usable at all → throw.
            bool threw = false;
            try { mi.Invoke(client, new object[] { LlmEnvelope("{\"relationship_deltas\":[]}") }); } catch (TargetInvocationException) { threw = true; }
            Check(threw, "no reflection AND no semantic memory → parse throws");
        }

        static void Test_Layer23WireFormat()
        {
            Section("Layer 2/3 wire format (object-form json_schema)");
            var req2 = new Layer2Request { model = "m", systemPrompt = "SYS2", userPrompt = "USR2", temperature = 0.6f, topP = 0.85f, minP = 0.08f, maxTokens = 150 };
            var o2 = JObject.Parse(LlamaServerClient.BuildLayer2Json(req2));
            Check(o2["top_p"] != null && o2["min_p"] != null && o2["max_tokens"] != null, "snake_case sampling fields");
            Check((string)o2["messages"][0]["role"] == "system" && (string)o2["messages"][0]["content"] == "SYS2", "static system zone first (KV reuse)");
            var js2 = (JObject)((JObject)o2["response_format"])["json_schema"];
            Check((string)js2["name"] == "layer2" && ((JObject)js2["schema"])["properties"]["decisions"] != null,
                "json_schema {name: layer2} with the decisions array embedded");

            var req3 = new Layer3Request { model = "m", systemPrompt = "SYS3", userPrompt = "USR3", temperature = 0.6f, topP = 0.85f, minP = 0.08f, maxTokens = 350 };
            var o3 = JObject.Parse(LlamaServerClient.BuildLayer3Json(req3));
            var js3 = (JObject)((JObject)o3["response_format"])["json_schema"];
            Check((string)js3["name"] == "layer3" && ((JObject)js3["schema"])["properties"]["trait_drift"] != null,
                "json_schema {name: layer3} with trait_drift embedded");
        }

        static void Test_DefForImpulseMapping()
        {
            Section("Impulse → InteractionDef mapping (Spec §7.3)");
            Verse.DefDatabase<InteractionDef>.Registry.Clear();
            Verse.DefDatabase<InteractionDef>.Registry["AIColonist_WarmChat"] = new InteractionDef { defName = "AIColonist_WarmChat", label = "Warm chat" };
            Verse.DefDatabase<InteractionDef>.Registry["AIColonist_VentFrustration"] = new InteractionDef { defName = "AIColonist_VentFrustration", label = "Vent frustration" };
            Verse.DefDatabase<InteractionDef>.Registry["AIColonist_SharpRemark"] = new InteractionDef { defName = "AIColonist_SharpRemark", label = "Sharp remark" };

            Check(AreaBatcher.DefForImpulse("warm_chat").defName == "AIColonist_WarmChat", "warm_chat → AIColonist_WarmChat");
            Check(AreaBatcher.DefForImpulse("vent_frustration").defName == "AIColonist_VentFrustration", "vent_frustration → AIColonist_VentFrustration");
            Check(AreaBatcher.DefForImpulse("sharp_remark").defName == "AIColonist_SharpRemark", "sharp_remark → AIColonist_SharpRemark");
            Check(AreaBatcher.DefForImpulse("quiet_nod") == null && AreaBatcher.DefForImpulse("avoid_gaze") == null &&
                  AreaBatcher.DefForImpulse("none") == null && AreaBatcher.DefForImpulse("garbage") == null,
                "memory-only impulses (and unknowns) map to NO interaction def");

            // Relationship-color notes: strongest feeling wins; frustration beats affection.
            var wc = Find.World.GetComponent<AIColonistWorldComponent>();
            var alice = MakePawn("note1"); alice.LabelCap = "Alice";
            var mind = wc.GetOrCreateMindState(alice);
            mind.relationships.Add(new RelationshipVector { targetPawnId = "nb", affection = 0.8f });
            Check(AreaBatcher.BuildPawnNote(alice, new Dictionary<string, string> { ["nb"] = "Bob" }) == "feels warm toward Bob",
                "affection ≥0.65 → warm note");
            mind.relationships.Add(new RelationshipVector { targetPawnId = "nc", frustration = 0.7f });
            Check(AreaBatcher.BuildPawnNote(alice, new Dictionary<string, string> { ["nb"] = "Bob", ["nc"] = "Carl" }) == "feels irritated by Carl",
                "frustration ≥0.55 beats affection");

            // Layer 2 request assembly: byte-identical system zone + prose roster (never percentages).
            var map = new Map();
            var a2 = MakePawn("ro1", map); a2.LabelCap = "Alice";
            var b2 = MakePawn("ro2", map); b2.LabelCap = "Bob";
            b2.needs.food.CurLevelPercentage = 0.2f; // hungry
            b2.needs.rest.CurLevelPercentage = 0.2f; // tired
            var req = AreaBatcher.BuildLayer2Request(new List<Pawn> { a2, b2 }, new Dictionary<string, string> { ["ro1"] = "Alice", ["ro2"] = "Bob" });
            Check(req.systemPrompt == Layer2PromptBuilder.BuildSystemPrompt(), "system zone byte-identical to the static template (KV-cache hot)");
            Check(req.userPrompt.Contains("- Alice:") && req.userPrompt.Contains("- Bob:") && req.userPrompt.Contains("hungry") && req.userPrompt.Contains("tired"),
                "roster: every colonist listed, needs as prose words");
            Check(!req.userPrompt.Contains("%"), "no percentages anywhere in the roster (Unaware constraint)");
            Check(req.model == AIColonistMod.Settings.Layer2Model && req.maxTokens == AIColonistMod.Settings.Layer2MaxOutputTokens,
                "model + token budget from settings");
        }

        static void Test_SocialWorkerPayload()
        {
            Section("InteractionWorker_AISocial letter mapping (Spec §7.3)");
            var w = new InteractionWorker_AISocial();
            var a = MakePawn("w1"); var b = MakePawn("w2");
            string lt, ll; LetterDef ld; LookTargets lk;

            w.interaction = new InteractionDef { defName = "AIColonist_WarmChat", label = "Warm chat" };
            w.Interacted(a, b, null, out lt, out ll, out ld, out lk);
            Check(lt == null && ll == null && ld == null && lk == null, "no pending payload: vanilla no-letter default");

            PendingLetterPayload.Set("w1", "w2", "The soup is ready.", "I feel close to her.");
            w.Interacted(a, b, null, out lt, out ll, out ld, out lk);
            Check(ld != null && ld.defName == "PositiveEvent" && lt == "The soup is ready." && ll == "Warm chat",
                "warm_chat → PositiveEvent letter carrying the LLM line + def label");

            w.Interacted(a, b, null, out lt, out ll, out ld, out lk);
            Check(lt == null && ld == null, "payload is one-shot: a stale line can't leak into a later interaction");

            PendingLetterPayload.Set("w1", "w2", "", "I am grumbling about the cold.");
            w.interaction = new InteractionDef { defName = "AIColonist_VentFrustration", label = "Vent frustration" };
            w.Interacted(a, b, null, out lt, out ll, out ld, out lk);
            Check(ld != null && ld.defName == "NeutralEvent" && lt == "I am grumbling about the cold.",
                "vent_frustration → NeutralEvent; empty spoken_line falls back to inner_feeling");

            PendingLetterPayload.Set("w1", "w2", "Watch it.", "");
            w.interaction = new InteractionDef { defName = "AIColonist_SharpRemark", label = "Sharp remark" };
            w.Interacted(a, b, null, out lt, out ll, out ld, out lk);
            Check(ld != null && ld.defName == "NegativeEvent" && lt == "Watch it.", "sharp_remark → NegativeEvent");

            // One-shot semantics live in the buffer itself.
            PendingLetterPayload.Set("x1", "x2", "a", "b");
            string s, f;
            Check(PendingLetterPayload.TryGet("x1", "x2", out s, out f) && s == "a" && f == "b", "TryGet reads the payload");
            Check(!PendingLetterPayload.TryGet("x1", "x2", out _, out _), "second TryGet false (consumed on read)");
            PendingLetterPayload.Set("y1", "y2", "c", "d");
            PendingLetterPayload.Remove("y1", "y2");
            Check(!PendingLetterPayload.TryGet("y1", "y2", out _, out _), "Remove drops a pending payload without consuming it");
        }

        static void Test_SocialConsumePaths()
        {
            Section("Layer 2 decision consumption (Spec §7.3)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);
            Find.TickManager.TicksGame = 10000;

            var alice = MakePawn("c1", map); alice.LabelCap = "Alice";
            alice.interactions = new Pawn_InteractionsTracker(alice); // stub: returns TestRig.TryInteractWithResult
            var bob = MakePawn("c2", map); bob.LabelCap = "Bob";

            Check(comp.FindColonistByName("alice") == alice, "FindColonistByName: case-insensitive LabelCap match");
            Check(comp.FindColonistByName("BOB") == bob, "case-insensitive match on the second pawn");
            Check(comp.FindColonistByName("Zed") == null && comp.FindColonistByName("") == null, "unknown/empty name → null");

            var sbuf = (SocialDecisionBuffer)typeof(AIColonistGameComponent).GetField("socialBuffer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(comp);
            var consumeMi = typeof(AIColonistGameComponent).GetMethod("ConsumeSocialDecisions", BindingFlags.NonPublic | BindingFlags.Instance);

            // Success: payload stashed for the exact pair, TryInteractWith called.
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "The soup is ready.", innerFeeling = "I feel close to her.", pushedGameTick = 9990 });
            TestRig.TryInteractWithResult = true;
            consumeMi.Invoke(comp, null);
            string line, feeling;
            Check(PendingLetterPayload.TryGet("c1", "c2", out line, out feeling) && line == "The soup is ready." && feeling == "I feel close to her.",
                "successful interaction: one-shot payload stashed for the exact pair");

            // Precondition failure: TryInteractWith false → payload removed (the line must not leak).
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Leak?", pushedGameTick = 9995 });
            TestRig.TryInteractWithResult = false;
            consumeMi.Invoke(comp, null);
            Check(!PendingLetterPayload.TryGet("c1", "c2", out _, out _), "preconditions failed: payload removed, no line leak");

            // Pre-check failure (live-verified 2026-11-13): pair not interactable — >6 cells / no LOS / 120-tick cooldown.
            // CanInteractNowWith false → decision dropped BEFORE any payload stash; re-rolls next cycle.
            TestRig.TryInteractWithResult = true;
            TestRig.CanInteractNowWithResult = false;
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Too far.", pushedGameTick = 9996 });
            consumeMi.Invoke(comp, null);
            Check(!PendingLetterPayload.TryGet("c1", "c2", out _, out _), "not interactable (distance/LOS/cooldown): dropped before payload stash");
            TestRig.CanInteractNowWithResult = true;

            // Stale decision (>3600 game ticks old) dropped without any interaction.
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Stale.", pushedGameTick = 100 });
            TestRig.TryInteractWithResult = true;
            consumeMi.Invoke(comp, null);
            Check(!PendingLetterPayload.TryGet("c1", "c2", out _, out _), "stale decision (>3600 ticks) dropped");

            // Quiet impulse: no TryInteractWith — episodic memory + bond nudge only.
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "quiet_nod", pushedGameTick = 9998 });
            consumeMi.Invoke(comp, null);
            var mindA = Find.World.GetComponent<AIColonistWorldComponent>().GetOrCreateMindState(alice);
            Check(mindA.memory.episodic.Any(e => e.text.Contains("quiet wordless moment with Bob")), "quiet_nod: episodic memory recorded");
            var rvA = mindA.relationships.FirstOrDefault(r => r.targetPawnId == "c2");
            Check(rvA != null && rvA.trust > 0.5f && rvA.affection > 0.5f, "quiet_nod: bond nudged warmer without an interaction def");

            // Self-targeting guard (live-observed 2026-11-14: "Noble vent_frustration -> Noble"): the LLM returned the
            // initiator's own name — dropped for ALL impulse types, before any payload stash or quiet-moment memory.
            TestRig.TryInteractWithResult = true;
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Alice", socialImpulse = "vent_frustration", spokenLine = "At myself?", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(!PendingLetterPayload.TryGet("c1", "c2", out _, out _), "self-targeted decision dropped before payload stash");
            int episodicBeforeSelfQuiet = mindA.memory.episodic.Count;
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "alice", socialImpulse = "quiet_nod", pushedGameTick = 9999 }); // case-insensitive self-match must also be caught
            consumeMi.Invoke(comp, null);
            Check(mindA.memory.episodic.Count == episodicBeforeSelfQuiet && !mindA.memory.episodic.Any(e => e.text.Contains("with Alice")),
                "self-targeted quiet decision dropped: no 'quiet moment with myself' memory");

            // Drop-gate decomposition (2026-11-14): after a run of unattributable "[dropped: distance/LOS/cooldown]" lines,
            // each sub-check failure must be named in the drop log. Behavior is unchanged — CanInteractNowWith is still
            // the gate; DescribeDropGate only explains it. (Verified against decompiled 1.6: cooldown → carrying exception
            // → spawned → EUCLIDEAN ≤6-cell radius + GenSight LOS [only Full-fill buildings block] → state checks.)
            TestRig.CanInteractNowWithResult = false;

            TestRig.CooldownActive = true;
            Verse.Log.Messages.Clear();
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Cd.", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(Verse.Log.Messages.Any(m => m.Contains("cooldown (120 ticks since last interaction)")), "drop diagnostic: cooldown attributed");
            TestRig.CooldownActive = false;

            bob.Position = new IntVec3(20, 0); // Euclidean distance 20 > radius 6
            Verse.Log.Messages.Clear();
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Far.", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(Verse.Log.Messages.Any(m => m.Contains("distance>6cells")), "drop diagnostic: distance attributed");

            bob.Position = new IntVec3(0, 0);
            TestRig.LineOfSightDefault = false; // a Full-fill building (wall/closed door) between the pair
            Verse.Log.Messages.Clear();
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Wall.", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(Verse.Log.Messages.Any(m => m.Contains("LOS blocked")), "drop diagnostic: LOS attributed");
            TestRig.LineOfSightDefault = true;

            TestRig.CanReceiveInteractionResult = false; // recipient asleep/burning/downed-state
            Verse.Log.Messages.Clear();
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Asleep.", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(Verse.Log.Messages.Any(m => m.Contains("recipient state (awake/burning)")), "drop diagnostic: recipient state attributed");
            TestRig.CanReceiveInteractionResult = true;

            // Carrying exception: vanilla skips the position check entirely while carrying — even 20 cells away, no distance/LOS blame.
            TestRig.CarryingPawnResult = true;
            bob.Position = new IntVec3(20, 0);
            Verse.Log.Messages.Clear();
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Carried.", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(Verse.Log.Messages.Any(m => m.Contains("all sub-checks passed")), "drop diagnostic: carrying skips position checks");
            TestRig.CarryingPawnResult = false;
            bob.Position = new IntVec3(0, 0);

            // Furniture regression witness (user-reported 2026-11-14): tables/chairs/beds/shelves have fillPercent 0 →
            // FillCategory.None → see-through in GenGrid.CanBeSeenOver. Only walls/closed doors are Full-fill. So a pair
            // with furniture between them but clear of walls must NOT be blamed on LOS — only distance/cooldown/state may be.
            TestRig.LineOfSightDefault = true; // "furniture" present: not a Full-fill blocker (verified against Core ThingDefs)
            Verse.Log.Messages.Clear();
            sbuf.Enqueue(new QueuedSocialDecision { colonistName = "Alice", targetName = "Bob", socialImpulse = "warm_chat", spokenLine = "Furn.", pushedGameTick = 9999 });
            consumeMi.Invoke(comp, null);
            Check(!Verse.Log.Messages.Any(m => m.Contains("LOS blocked")), "furniture does not block interaction LOS (FillCategory.None is see-through)");

            TestRig.CanInteractNowWithResult = true; // restore — later end-to-end tests expect the gate open
        }

        static async Task Test_Layer2EndToEndAsync()
        {
            Section("Layer 2 end-to-end: gate → dispatch → buffer → TryInteractWith (Spec §7.3/§8.2)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);

            Find.TickManager.Paused = false;
            comp.MapComponentTick(); // start the unpaused clock
            var alice = MakePawn("e2a", map); alice.LabelCap = "Alice"; alice.Position = new IntVec3(0, 0);
            alice.interactions = new Pawn_InteractionsTracker(alice);
            var bob = MakePawn("e2b", map); bob.LabelCap = "Bob"; bob.Position = new IntVec3(1, 0); // same outdoor cluster

            Find.TickManager.TicksGame = 6000; // > Layer2MinGameTicks (5400)
            typeof(AIColonistGameComponent).GetField("lastLayer2RealSeconds", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(comp, -200f); // real-time gate open

            var fake = new FakeLlamaBridge();
            fake.onLayer2 = () =>
            {
                var r = new Layer2Result();
                r.decisions.Add(new SocialDecision { colonist_name = "Alice", inner_feeling = "I feel close to her.", social_impulse = "warm_chat", target_name = "Bob", spoken_line = "The soup is ready." });
                return r;
            };
            typeof(AIColonistGameComponent).GetField("bridge", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(comp, fake);

            comp.MapComponentTick(); // gate open → one batched call for the shared space
            await Task.Delay(300);   // let the worker task run to completion
            comp.MapComponentTick(); // consume: resolve names, stash payload, TryInteractWith

            Check(fake.layer2.Count == 1, "exactly ONE batched call covers the whole shared space (Spec §3.4)");
            Check(fake.layer2[0].systemPrompt == Layer2PromptBuilder.BuildSystemPrompt(), "static system zone byte-identical (pure KV reuse)");
            Check(fake.layer2[0].userPrompt.Contains("- Alice:") && fake.layer2[0].userPrompt.Contains("- Bob:"), "dynamic roster lists every colonist in the room");

            string line, feeling;
            Check(PendingLetterPayload.TryGet("e2a", "e2b", out line, out feeling) && line == "The soup is ready." && feeling == "I feel close to her.",
                "full loop: decision → buffer → name resolution → one-shot payload for the pair");

            // Second cycle within the 90s window must NOT re-dispatch.
            comp.MapComponentTick();
            Check(fake.layer2.Count == 1, "gate stays closed until the next 90s/5400-tick window");
        }

        static async Task Test_Layer3EndToEndAsync()
        {
            Section("Layer 3 end-to-end: staggered dispatch → buffer → mind-state apply (Spec §7.4/§8.2)");
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);

            Find.TickManager.Paused = false;
            comp.MapComponentTick(); // start the unpaused clock
            var alice = MakePawn("e3a", map); alice.LabelCap = "Alice";
            var bob = MakePawn("e3b", map); bob.LabelCap = "Bob";

            var wc = Find.World.GetComponent<AIColonistWorldComponent>();
            var mind = wc.GetOrCreateMindState(alice);
            mind.memory.AddEpisodic("The storm broke the window.", 500);
            mind.traitWeights["stoic"] = 0.7f;

            var fake = new FakeLlamaBridge();
            fake.onLayer3 = () =>
            {
                var r = new Layer3Result { self_reflection = "I am learning to hold my ground.", new_semantic_memory = "The storm teaches patience." };
                r.relationship_deltas.Add(new RelationshipDelta { target_name = "Bob", trust_delta = 0.03f });
                return r;
            };
            typeof(AIColonistGameComponent).GetField("bridge", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(comp, fake);

            Find.TickManager.TicksGame = 20000; // > Layer3MinGameTicks (18000)
            comp.MapComponentTick(); // creates per-pawn state; real-time gates still closed (fresh clock)

            var dictField = typeof(AIColonistGameComponent).GetField("callStates", BindingFlags.NonPublic | BindingFlags.Instance);
            System.Collections.IDictionary dict = (System.Collections.IDictionary)dictField.GetValue(comp);
            var stAlice = dict["e3a"];
            stAlice.GetType().GetField("lastLayer3RealSeconds").SetValue(stAlice, -400f); // real-time gate open for Alice only

            comp.MapComponentTick(); // dispatches Alice (stagger 0s); Bob's 30s stagger keeps him out this cycle
            await Task.Delay(300);
            comp.MapComponentTick(); // ConsumeLayer3Results applies the result to Alice's mind state

            Check(fake.layer3.Count == 1, "exactly one consolidation call this cycle (30s stagger, Spec §8.2)");
            var req = fake.layer3[0];
            Check(req.systemPrompt.Contains("quiet subconscious of Alice") && !req.systemPrompt.Contains("[Name]"),
                "system zone: colonist name substituted once (per-pawn KV prefix)");
            Check(req.userPrompt.Contains("The storm broke the window."), "user zone: raw episodic memories fed in");
            Check(req.userPrompt.Contains("strongly stoic"), "temperament words from continuous trait weights");

            var mindAfter = wc.GetOrCreateMindState(alice);
            Check(mindAfter.memory.semantic.Count == 1 && mindAfter.memory.semantic[0] == "The storm teaches patience.", "semantic memory applied to the mind state");
            var rvB = mindAfter.relationships.FirstOrDefault(r => r.targetPawnId == "e3b");
            Check(rvB != null && Math.Abs(rvB.trust - 0.53f) < 1e-4f, "relationship delta resolved by name → ThingID and applied");

            var stBob = dict["e3b"];
            Check((int)stBob.GetType().GetField("lastLayer3Tick").GetValue(stBob) == 0, "Bob's consolidation untouched this cycle (stagger)");
        }

        static void Test_ApplyLayer3Result()
        {
            Section("Layer 3 result application — clamps, dedup, prune (Spec §7.4)");
            var mind = new ColonistMindState();
            mind.traitWeights["stoic"] = 0.9f;
            mind.memory.AddSemantic("I am not easily rattled.");

            int nowTick = 1_000_000;
            mind.memory.AddEpisodic("old event", nowTick - 31 * MemoryStore.TicksPerDay); // older than 30 in-game days → pruned
            mind.memory.AddEpisodic("recent event", nowTick - 100);

            var result = new Layer3Result
            {
                self_reflection = "I am becoming steady.",
                new_semantic_memory = "I am not easily rattled.", // duplicate of existing → must be deduped
                long_term_longing = "A quiet corner of the world.",
            };
            result.relationship_deltas.Add(new RelationshipDelta { target_name = "Bob", trust_delta = 0.5f, affection_delta = -0.4f, respect_delta = 0.01f, frustration_delta = 0.9f }); // oversized → clamped
            result.relationship_deltas.Add(new RelationshipDelta { target_name = "Nobody", trust_delta = 0.5f }); // unresolvable → skipped
            result.trait_drift = new TraitDrift { trait_name = "stoic", delta = 0.5f }; // oversized → clamped to +0.02

            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Bob"] = "bob-id" };
            mind.ApplyLayer3Result(result, nameMap, nowTick);

            Check(mind.memory.semantic.Count == 1 && mind.memory.semantic[0] == "I am not easily rattled.", "semantic dedup: repeated consolidation doesn't stack memories");
            Check(mind.longTermLonging == "A quiet corner of the world.", "long-term longing fed back into the identity prefix");
            var rv = mind.relationships.First(r => r.targetPawnId == "bob-id");
            Check(Math.Abs(rv.trust - 0.55f) < 1e-4f && Math.Abs(rv.affection - 0.45f) < 1e-4f &&
                  Math.Abs(rv.respect - 0.51f) < 1e-4f && Math.Abs(rv.frustration - 0.15f) < 1e-4f,
                "relationship deltas clamped to ±0.05 per axis (server said +0.5/-0.4/+0.9)");
            Check(mind.relationships.Count == 1, "unresolvable target name skipped (no phantom vector)");
            Check(Math.Abs(mind.traitWeights["stoic"] - 0.92f) < 1e-4f, "trait drift clamped to +0.02 per cycle, still ≤1");

            mind.ApplyLayer3Result(new Layer3Result { self_reflection = "x", trait_drift = new TraitDrift { trait_name = "anxious", delta = -0.5f } }, nameMap, nowTick);
            Check(mind.traitWeights.ContainsKey("anxious") && mind.traitWeights["anxious"] == 0f, "negative drift on an unseen trait clamps at 0");

            Check(mind.memory.episodic.Count == 1 && mind.memory.episodic[0].text == "recent event",
                "consolidation prunes episodic memories older than ~30 in-game days");
        }

        static async Task Test_Layer2ServerDownResilienceAsync()
        {
            Section("Layer 2 dispatch resilience — server down (Spec §10)");
            AIColonistMod.Settings.Layer2ServerUrl = "http://127.0.0.1:9"; // closed port → immediate connection refused
            var map = new Map();
            var comp = new AIColonistGameComponent(map);
            map.RegisterForTests(comp);

            Find.TickManager.Paused = false;
            comp.MapComponentTick(); // start the unpaused clock
            var a = MakePawn("sd1", map); a.LabelCap = "Alice"; a.Position = new IntVec3(0, 0);
            var b = MakePawn("sd2", map); b.LabelCap = "Bob"; b.Position = new IntVec3(1, 0);

            Find.TickManager.TicksGame = 6000;
            typeof(AIColonistGameComponent).GetField("lastLayer2RealSeconds", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(comp, -200f);
            comp.MapComponentTick(); // dispatches the room batch against the dead server (real LlamaServerClient)

            var countField = typeof(AIColonistGameComponent).GetField("layer2InFlightCount", BindingFlags.NonPublic | BindingFlags.Instance);
            bool settled = false;
            for (int i = 0; i < 100 && !settled; i++)
            {
                await Task.Delay(50);
                settled = (int)countField.GetValue(comp) == 0;
            }
            Check(settled, "in-flight batch counter returns to zero after a server-down dispatch (no permanent wedge)");

            var sbuf = (SocialDecisionBuffer)typeof(AIColonistGameComponent).GetField("socialBuffer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(comp);
            Check(sbuf.Drain().Count == 0, "no decisions deposited when the server is unreachable → vanilla social AI fallback");
        }

        // ------------------------------------------------------------ transport (single-flight gate + one retry)
        // Live-verified failure 2026-11-13: Mono's pooled HttpClient throws HttpRequestException ("An error occurred
        // while sending the request") when a second send starts on a client that still has an in-flight request —
        // every Layer 2/3 dispatch collided with an in-flight Layer 1 call and died at the transport level (requests
        // never reached the server), while identical concurrent sends via curl and Windows .NET both succeeded. The fix
        // serializes ALL outgoing requests through LlamaServerClient.SendGate and retries once on HttpRequestException.
        // These tests run the REAL client against a raw TCP loopback server (no http.sys, no ACLs): without the gate
        // maxOpen would exceed 1; without the retry the aborted first connection would propagate as a failure.

        static int FreeLoopbackPort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static byte[] HttpOkResponse(string json)
        {
            string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                + System.Text.Encoding.UTF8.GetByteCount(json) + "\r\nConnection: close\r\n\r\n";
            return System.Text.Encoding.UTF8.GetBytes(head + json);
        }

        /// <summary>Embeds an inner JSON document as the escaped "content" string of a /v1/chat/completions envelope.</summary>
        static string ChatEnvelope(string innerJson) =>
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + innerJson.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}]}";

        /// <summary>Consumes the client's request before we respond/close, like a real HTTP server does. Observed 2026-11-14:
        /// closing a socket with UNREAD data in its receive buffer makes Windows send an RST instead of a FIN, which aborts the
        /// client's read even though our response was already delivered — a sub-ms race that failed this host's runs 4/5.
        /// Blocking until the request arrives (the client always sends) then draining whatever else is queued.</summary>
        static void DrainRequest(System.Net.Sockets.NetworkStream s)
        {
            var buf = new byte[8192];
            try
            {
                int rd;
                do { rd = s.Read(buf, 0, buf.Length); } while (rd > 0 && s.DataAvailable);
            }
            catch { /* connection died mid-request — nothing to drain */ }
        }

        static async Task Test_TransportSingleFlightAsync()
        {
            Section("Transport: single-flight gate serializes concurrent sends (Mono fix 2026-11-13)");
            int port = FreeLoopbackPort();
            var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            server.Start();

            int hits = 0, open = 0, maxOpen = 0;
            string inner2 = "{\"decisions\":[{\"colonist_name\":\"Alice\",\"inner_feeling\":\"warm\",\"social_impulse\":\"warm_chat\",\"target_name\":\"Bob\",\"spoken_line\":\"hello\"}]}";
            byte[] resp = HttpOkResponse(ChatEnvelope(inner2));

            var serverTask = Task.Run(async () =>
            {
                while (true)
                {
                    System.Net.Sockets.TcpClient client;
                    try { client = await server.AcceptTcpClientAsync(); }
                    catch { break; } // listener stopped after the test
                    System.Threading.Interlocked.Increment(ref hits);
                    int now = System.Threading.Interlocked.Increment(ref open);
                    lock (server) if (now > maxOpen) maxOpen = now;
                    try
                    {
                        await Task.Delay(200); // hold the connection: overlapping sends would show up as 2+ open at once
                        using (var s = client.GetStream())
                        {
                            DrainRequest(s); // request arrived during the hold — drain it so Close() stays a FIN, not an RST
                            await s.WriteAsync(resp, 0, resp.Length);
                        }
                    }
                    finally
                    {
                        System.Threading.Interlocked.Decrement(ref open);
                        client.Close();
                    }
                }
            });

            var settings = new Settings { Layer2ServerUrl = "http://127.0.0.1:" + port, RequestTimeoutMs = 5000 };
            var client = new LlamaServerClient(settings);

            var tasks = new List<Task<Layer2Result>>();
            for (int i = 0; i < 5; i++)
                tasks.Add(client.RequestLayer2Async(new Layer2Request { model = "m", systemPrompt = "S", userPrompt = "U" }));

            Layer2Result[] results;
            try { results = await Task.WhenAll(tasks); }
            catch (Exception e)
            {
                server.Stop();
                Check(false, "five concurrent Layer 2 calls completed (" + e.Message + ")");
                return;
            }

            bool allParsed = true;
            foreach (var r in results)
                if (r == null || r.decisions.Count != 1 || r.decisions[0].social_impulse != "warm_chat") allParsed = false;

            Check(allParsed, "all five concurrent Layer 2 calls parsed a valid decision");
            Check(hits == 5, "server saw all five requests (none dropped)");
            Check(maxOpen == 1, "at most ONE request in flight at a time — the gate serializes (would be >1 without it)");

            server.Stop();
            await serverTask;
        }

        static async Task Test_TransportRetryOnSendFailureAsync()
        {
            Section("Transport: one retry on send failure (Mono fix 2026-11-13)");
            int port = FreeLoopbackPort();
            var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            server.Start();

            string inner1 = "{\"impulse_id\":2,\"inner_voice\":\"transport retry test\",\"spoken_mutter\":\"ok\"}";
            byte[] resp = HttpOkResponse(ChatEnvelope(inner1));

            int accepts = 0;
            var serverTask = Task.Run(async () =>
            {
                while (true)
                {
                    System.Net.Sockets.TcpClient c;
                    try { c = await server.AcceptTcpClientAsync(); }
                    catch { break; } // listener stopped after the test
                    int n = System.Threading.Interlocked.Increment(ref accepts);
                    if (n == 1) { c.Close(); continue; } // connection #1: dies without any HTTP response — the exact Mono failure signature
                    using (var s = c.GetStream())
                    {
                        DrainRequest(s); // read the request first (real servers do) — keeps Close() a FIN, not an RST
                        await s.WriteAsync(resp, 0, resp.Length);
                    }
                    c.Close();
                }
            });

            var settings = new Settings { Layer1ServerUrl = "http://127.0.0.1:" + port, RequestTimeoutMs = 5000 };
            var client = new LlamaServerClient(settings);

            Layer1Result result = null; string err = null;
            try { result = await client.RequestLayer1Async(new Layer1Request { model = "m", systemPrompt = "S", userPrompt = "U", optionCount = 3 }); }
            catch (Exception e) { err = e.Message + " | inner: " + (e.InnerException != null ? e.InnerException.Message : "(none)"); }

            Check(result != null && result.impulse_id == 2 && result.inner_voice == "transport retry test",
                err == null ? "client survived the first transport failure via one retry" : "no retry on transport failure: " + err);
            Check(accepts >= 2, "a fresh connection was made after the first failure (the retry actually retried)");

            server.Stop();
        }

        /// <summary>Raw HTTP response with an arbitrary status line (for error-path probes).</summary>
        static byte[] HttpRawResponse(string statusLine, string body)
        {
            string head = "HTTP/1.1 " + statusLine + "\r\nContent-Type: application/json\r\nContent-Length: "
                + System.Text.Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n";
            return System.Text.Encoding.UTF8.GetBytes(head + body);
        }

        /// <summary>Serves exactly one canned response on a fresh loopback port, then stops. Returns (baseUrl, serverTask).</summary>
        static (string, Task) ServeOnce(byte[] response)
        {
            int port = FreeLoopbackPort();
            var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            server.Start();
            var t = Task.Run(async () =>
            {
                try
                {
                    using (var c = await server.AcceptTcpClientAsync())
                    using (var st = c.GetStream())
                    {
                        DrainRequest(st);
                        await st.WriteAsync(response, 0, response.Length);
                    }
                }
                finally { server.Stop(); }
            });
            return ("http://127.0.0.1:" + port, t);
        }

        static async Task Test_ProviderTestAsync()
        {
            Section("Settings: Test providers button (probe, tester, dialog)");

            // 1. Healthy server: ok + the reply text (think blocks stripped like real dispatches).
            var (okUrl, okSrv) = ServeOnce(HttpOkResponse(ChatEnvelope("<think>hmm</think>OK")));
            var okRes = await LlamaServerClient.TestProviderAsync(okUrl, "m", 80, timeoutMs: 5000);
            await okSrv;
            Check(okRes.ok && okRes.message == "OK", "healthy server: ok, reply 'OK' with <think> stripped (got: " + okRes.message + ")");
            Check(okRes.latencyMs >= 0, "healthy server: latency measured");

            // 2. Server answers with an error (e.g. Ollama's unknown-model 404): status + body surface verbatim.
            var (e404Url, e404Srv) = ServeOnce(HttpRawResponse("404 Not Found", "{\"error\":\"model 'nope' not found\"}"));
            var e404 = await LlamaServerClient.TestProviderAsync(e404Url, "nope", 80, timeoutMs: 5000);
            await e404Srv;
            Check(!e404.ok && e404.message.Contains("404") && e404.message.Contains("model 'nope' not found"),
                "HTTP 404: failure carries the status and the server's own error (got: " + e404.message + ")");

            // 3. Something that answers 200 but isn't a chat server (wrong port, e.g. a health endpoint or web UI).
            var (oddUrl, oddSrv) = ServeOnce(HttpOkResponse("{\"status\":\"ok\"}"));
            var odd = await LlamaServerClient.TestProviderAsync(oddUrl, "m", 80, timeoutMs: 5000);
            await oddSrv;
            Check(!odd.ok && odd.message.Contains("OpenAI-compatible"), "200 without chat content: flagged as not a chat server");

            // 3b. Reasoning model that burns the whole budget thinking (live: Inkling 2 mini via OpenRouter at 16 tokens).
            string burnt = "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"role\":\"assistant\",\"content\":null,\"reasoning\":\"The user asks\"}}],"
                + "\"usage\":{\"completion_tokens\":16,\"completion_tokens_details\":{\"reasoning_tokens\":16}}}";
            var (burntUrl, burntSrv) = ServeOnce(HttpOkResponse(burnt));
            var burntRes = await LlamaServerClient.TestProviderAsync(burntUrl, "m", 16, timeoutMs: 5000);
            await burntSrv;
            Check(!burntRes.ok && burntRes.message.Contains("whole 16-token budget") && burntRes.message.Contains("reasoning model"),
                "content:null + finish_reason length: explained as a reasoning model out of budget (got: " + burntRes.message + ")");

            // 3c. Answers, but reports heavy reasoning usage (live: 58 of 80 tokens to say "OK").
            string heavy = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}],"
                + "\"usage\":{\"completion_tokens\":60,\"completion_tokens_details\":{\"reasoning_tokens\":58}}}";
            var (heavyUrl, heavySrv) = ServeOnce(HttpOkResponse(heavy));
            var heavyRes = await LlamaServerClient.TestProviderAsync(heavyUrl, "m", 80, timeoutMs: 5000);
            await heavySrv;
            Check(heavyRes.ok && heavyRes.reasoningTokens == 58 && heavyRes.maxTokens == 80, "reasoning_tokens + budget carried on an ok result");

            // 4. Nothing listening, and a URL missing its scheme: both come back as failures, never exceptions.
            var down = await LlamaServerClient.TestProviderAsync("http://127.0.0.1:9", "m", 80, timeoutMs: 5000);
            Check(!down.ok && down.message.StartsWith("unreachable"), "closed port: 'unreachable' failure (got: " + down.message + ")");
            ProviderTestResult noScheme = null;
            try { noScheme = await LlamaServerClient.TestProviderAsync("localhost:8091", "m", 80, timeoutMs: 5000); } catch { }
            Check(noScheme != null && !noScheme.ok, "URL without http://: failure result, no exception");

            // 4b. "Turn off model reasoning": every body carries reasoning_effort:"none" only when asked.
            var l1Off = LlamaServerClient.BuildChatCompletionsJson(new Layer1Request { model = "m", optionCount = 2 });
            var l1On = LlamaServerClient.BuildChatCompletionsJson(new Layer1Request { model = "m", optionCount = 2, disableReasoning = true });
            var l2On = LlamaServerClient.BuildLayer2Json(new Layer2Request { model = "m", disableReasoning = true });
            var l3On = LlamaServerClient.BuildLayer3Json(new Layer3Request { model = "m", disableReasoning = true });
            Check(!l1Off.Contains("reasoning_effort"), "reasoning flag off: body unchanged (no reasoning_effort)");
            Check(l1On.Contains(",\"reasoning_effort\":\"none\",") && l2On.Contains("\"reasoning_effort\":\"none\"") && l3On.Contains("\"reasoning_effort\":\"none\""),
                "reasoning flag on: Layer 1/2/3 bodies all send reasoning_effort:\"none\"");
            try { Newtonsoft.Json.Linq.JObject.Parse(l1On); Newtonsoft.Json.Linq.JObject.Parse(l2On); Newtonsoft.Json.Linq.JObject.Parse(l3On); Check(true, "reasoning flag on: bodies are still valid JSON"); }
            catch (Exception ex) { Check(false, "reasoning flag on: bodies are still valid JSON (" + ex.Message + ")"); }

            // 4c. Real dispatch hitting the live failure (content:null, budget spent reasoning): actionable error, not "malformed".
            var (burnUrl, burnSrv) = ServeOnce(HttpOkResponse(burnt));
            string dispatchErr = null;
            try { await new LlamaServerClient(new Settings { Layer1ServerUrl = burnUrl, RequestTimeoutMs = 5000 }).RequestLayer1Async(new Layer1Request { model = "m", optionCount = 2 }); }
            catch (Exception ex) { dispatchErr = ex.Message; }
            await burnSrv;
            Check(dispatchErr != null && dispatchErr.Contains("16 tokens went to reasoning") && dispatchErr.Contains("Turn off model reasoning") && !dispatchErr.Contains("Malformed"),
                "dispatch with content:null + finish_reason length: error explains the reasoning budget and names the setting (got: " + dispatchErr + ")");

            // 5. ProviderTester: snapshots settings, probes all three layers in order, formats status lines.
            var s = new Settings
            {
                Layer1ServerUrl = "http://a", Layer1Model = "m1",
                Layer2ServerUrl = "http://b", Layer2Model = "m2",
                Layer3ServerUrl = "http://c", Layer3Model = "m3",
                RequestTimeoutMs = 3000,
            };
            var order = new List<string>();
            var tester = new ProviderTester(s, (url, model, maxTokens) =>
            {
                lock (order) order.Add(url + "|" + model + "|" + maxTokens);
                if (url == "http://a") return Task.FromResult(ProviderTestResult.Ok(1200, "OK", 10, maxTokens));
                if (url == "http://b") return Task.FromResult(ProviderTestResult.Ok(4500, "OK"));
                return Task.FromResult(ProviderTestResult.Fail(5, "unreachable: refused"));
            });
            s.Layer1ServerUrl = "http://edited-after-click";
            Check(!tester.Done && tester.StatusLine(tester.Entries[0]) == "waiting", "before Start: not done, entries waiting");
            await tester.Start();
            Check(tester.Done, "after Start completes: Done");
            Check(string.Join(",", order) == "http://a|m1|80,http://b|m2|150,http://c|m3|350",
                "probes ran once per layer, in order, with each layer's URL/model/token budget snapshotted at construction (got: " + string.Join(",", order) + ")");
            Check(tester.StatusLine(tester.Entries[0]).StartsWith("OK in 1.2 s") && !tester.StatusLine(tester.Entries[0]).Contains("Slower"),
                "fast OK: plain success line (got: " + tester.StatusLine(tester.Entries[0]) + ")");
            Check(tester.StatusLine(tester.Entries[1]).Contains("Slower than your 3000 ms request timeout"),
                "OK but slower than RequestTimeoutMs: warns real calls would time out");
            Check(tester.StatusLine(tester.Entries[2]) == "FAILED: unreachable: refused", "failure: FAILED + message");
            var heavyTester = new ProviderTester(s, (url, model, maxTokens) => Task.FromResult(ProviderTestResult.Ok(900, "OK", 58, 80)));
            await heavyTester.Start();
            Check(heavyTester.StatusLine(heavyTester.Entries[0]).Contains("Spent 58 of its 80 tokens reasoning"),
                "OK but >half the budget on reasoning: warns real answers will get cut off");

            Check(!tester.disableReasoning && new ProviderTester(new Settings { DisableReasoning = true }).disableReasoning,
                "tester snapshots the reasoning flag (its real probe sends it, like real requests)");
            var throwing = new ProviderTester(s, (url, model, maxTokens) => throw new InvalidOperationException("boom"));
            await throwing.Start();
            Check(throwing.Done && !throwing.Entries[0].result.ok, "a throwing probe becomes a FAILED entry, tester still finishes");

            // 6. Dialog smoke: constructs, starts probing, draws pending + finished states without throwing.
            var ds = new Settings { Layer1ServerUrl = "http://127.0.0.1:9", Layer2ServerUrl = "http://127.0.0.1:9", Layer3ServerUrl = "http://127.0.0.1:9" };
            bool drew = true;
            try
            {
                var dlg = new AIColonists.UI.Dialog_ProviderTest(ds);
                dlg.DoWindowContents(new UnityEngine.Rect(0f, 0f, 664f, 400f));
                System.Threading.Thread.Sleep(300); // closed port fails fast
                dlg.DoWindowContents(new UnityEngine.Rect(0f, 0f, 664f, 400f));
                Check(dlg.doCloseButton && dlg.InitialSize.y >= 450f, "dialog: has a close button and room for three result blocks");
            }
            catch (Exception e) { drew = false; Console.WriteLine("    " + e); }
            Check(drew, "dialog: DoWindowContents draws pending and finished states without throwing");
        }

        static void Test_SettingsPage()
        {
            Section("Settings page: every field has a control, page scrolls, reset scope");
            var fields = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Check(fields.Length >= 20, "Settings exposes its fields (found " + fields.Length + ")");

            // Poke mode: one draw where every control changes its bound value. Any field still at its default has no control.
            var poked = new Settings();
            var defaults = new Settings();
            AIColonists.UI.SettingsWindow.Draw(new UnityEngine.Rect(0f, 0f, 860f, 560f), new Settings()); // warm-up: measures height
            TestRig.UiPoke = true;
            try { AIColonists.UI.SettingsWindow.Draw(new UnityEngine.Rect(0f, 0f, 860f, 560f), poked); }
            finally { TestRig.UiPoke = false; }
            var untouched = new List<string>();
            foreach (var f in fields)
                if (Equals(f.GetValue(poked), f.GetValue(defaults))) untouched.Add(f.Name);
            Check(untouched.Count == 0, "every public Settings field is wired to a UI control (no control: " + string.Join(", ", untouched) + ")");

            Check(AIColonists.UI.SettingsWindow.ContentHeight > 560f,
                "content (" + AIColonists.UI.SettingsWindow.ContentHeight + " px) is taller than the dialog body, so the page scrolls");

            AIColonists.UI.SettingsWindow.Draw(new UnityEngine.Rect(0f, 0f, 860f, 560f), null); // must not throw

            // Reset tuning: sampling/tokens/timing back to defaults; switches, servers, models, timeout kept.
            var s = new Settings
            {
                EnableAI = true, AIFirstJobPriority = false,
                Layer1ServerUrl = "https://x", Layer1Model = "mx", RequestTimeoutMs = 12345,
                BaseTemperature = 1.3f, MinP = 0.5f, Layer1MaxOutputTokens = 999, Layer3MaxOutputTokens = 4000,
                Layer2MinGameTicks = 60, Layer3MinRealSeconds = 5f,
            };
            s.ResetTuningToDefaults();
            Check(s.BaseTemperature == defaults.BaseTemperature && s.MinP == defaults.MinP
                && s.Layer1MaxOutputTokens == defaults.Layer1MaxOutputTokens && s.Layer3MaxOutputTokens == defaults.Layer3MaxOutputTokens
                && s.Layer2MinGameTicks == defaults.Layer2MinGameTicks && s.Layer3MinRealSeconds == defaults.Layer3MinRealSeconds,
                "reset: temperature, min_p, token budgets and timing back to defaults");
            Check(s.EnableAI && !s.AIFirstJobPriority && s.Layer1ServerUrl == "https://x" && s.Layer1Model == "mx" && s.RequestTimeoutMs == 12345,
                "reset: switches, URLs, models and timeout untouched");
        }

        // ------------------------------------------------------------ mood bridge defs (Spec §7.3 emotional shift)
        // The grant itself happens inside vanilla Pawn_InteractionsTracker.TryInteractWith via the interaction def's
        // <initiatorThought>/<recipientThought> fields (decompiled 1.6: AddInteractionThought → memories.TryGainMemory),
        // so there is no mod code to unit-test — but a typo'd defName would fail SILENTLY in-game (Log.Error + skipped
        // thought). This test pins the cross-file wiring and the calibration values against vanilla's own defs.
        static void Test_MemoryThoughtDefs()
        {
            Section("Mood bridge def wiring (vanilla-granted social memory thoughts)");

            string root = FindModRoot();
            var interXml = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Defs", "InteractionDefs", "AIColonist_Interactions.xml"));
            var memXml = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Defs", "MemoryThoughts", "AIColonist_Memories.xml"));

            string Get(System.Xml.Linq.XElement el, string tag) => el != null && el.Element(tag) != null ? el.Element(tag).Value : null;

            var interDefs = new Dictionary<string, System.Xml.Linq.XElement>();
            foreach (var d in interXml.Descendants("InteractionDef")) { var n = Get(d, "defName"); if (n != null) interDefs[n] = d; }
            var memDefs = new Dictionary<string, System.Xml.Linq.XElement>();
            foreach (var d in memXml.Descendants("ThoughtDef")) { var n = Get(d, "defName"); if (n != null) memDefs[n] = d; }

            Check(interDefs.ContainsKey("AIColonist_WarmChat") && interDefs.ContainsKey("AIColonist_VentFrustration")
                && interDefs.ContainsKey("AIColonist_SharpRemark"), "all three AI interaction defs present in AIColonist_Interactions.xml");
            Check(memDefs.Count == 5, "exactly five mood-bridge ThoughtDefs defined (3 opinion memories + 2 mood blips)");
            if (!interDefs.ContainsKey("AIColonist_WarmChat") || memDefs.Count == 0) return;

            // 1. Every thought reference inside the interaction defs resolves to a defined ThoughtDef.
            bool allResolve = true;
            foreach (var kv in interDefs)
                foreach (var tag in new[] { "initiatorThought", "recipientThought" })
                    if (Get(kv.Value, tag) != null && !memDefs.ContainsKey(Get(kv.Value, tag))) allResolve = false;
            Check(allResolve, "every <initiatorThought>/<recipientThought> reference resolves to a defined ThoughtDef");

            // 2. Every thoughtToMake chain resolves within the def set (thoughts referencing thoughts).
            bool chainsResolve = true;
            foreach (var kv in memDefs) { var ttm = Get(kv.Value, "thoughtToMake"); if (ttm != null && !memDefs.ContainsKey(ttm)) chainsResolve = false; }
            Check(chainsResolve, "every <thoughtToMake> chain resolves to a defined ThoughtDef");

            // 3. Exact grant mapping — who gains what.
            Check(Get(interDefs["AIColonist_WarmChat"], "initiatorThought") == "AIColonist_WarmChatMemory"
                && Get(interDefs["AIColonist_WarmChat"], "recipientThought") == "AIColonist_WarmChatMemory",
                "warm chat is MUTUAL: both initiator and recipient gain AIColonist_WarmChatMemory");
            Check(Get(interDefs["AIColonist_VentFrustration"], "initiatorThought") == null
                && Get(interDefs["AIColonist_VentFrustration"], "recipientThought") == "AIColonist_VentFrustrationMemory",
                "venting is listener-only: recipient gains AIColonist_VentFrustrationMemory, initiator gains nothing");
            Check(Get(interDefs["AIColonist_SharpRemark"], "initiatorThought") == null
                && Get(interDefs["AIColonist_SharpRemark"], "recipientThought") == "AIColonist_SharpRemarkMemory",
                "sharp remark is target-only: recipient gains AIColonist_SharpRemarkMemory, initiator gains nothing");

            // 4. Shape: opinion memories carry exactly one numeric baseOpinionOffset; mood blips one baseMoodEffect + 2-day expiry.
            bool opinionsOk = true;
            foreach (var name in new[] { "AIColonist_WarmChatMemory", "AIColonist_VentFrustrationMemory", "AIColonist_SharpRemarkMemory" })
            {
                var stages = memDefs[name].Descendants("baseOpinionOffset").Select(e => e.Value).ToList();
                if (stages.Count != 1 || !float.TryParse(stages[0], out _)) opinionsOk = false;
            }
            Check(opinionsOk, "all three opinion memories define exactly one numeric baseOpinionOffset");

            bool blipsOk = true;
            foreach (var name in new[] { "AIColonist_WarmChatMood", "AIColonist_SharpRemarkMood" })
            {
                var d = memDefs[name];
                var mood = d.Descendants("baseMoodEffect").Select(e => e.Value).ToList();
                if (mood.Count != 1 || !float.TryParse(mood[0], out _) || Get(d, "durationDays") != "2") blipsOk = false;
            }
            Check(blipsOk, "both mood blips define exactly one numeric baseMoodEffect and expire in 2 days");

            // 5. Calibration: milder than the vanilla counterparts they mirror (KindWords +15 / Slighted -5 / Insulted -15, mood ±5).
            float warm = ParseStageOffset(memDefs, "AIColonist_WarmChatMemory", "baseOpinionOffset");
            float vent = ParseStageOffset(memDefs, "AIColonist_VentFrustrationMemory", "baseOpinionOffset");
            float sharp = ParseStageOffset(memDefs, "AIColonist_SharpRemarkMemory", "baseOpinionOffset");
            Check(warm > 0 && warm < 15f, "warm chat opinion offset (" + warm + ") positive and milder than KindWords (+15)");
            Check(vent < 0 && vent > -5f, "venting opinion offset (" + vent + ") negative but milder than Slighted (-5)");
            Check(sharp < 0 && sharp > -15f, "sharp remark opinion offset (" + sharp + ") negative but milder than Insulted (-15)");
            float warmMood = ParseStageOffset(memDefs, "AIColonist_WarmChatMood", "baseMoodEffect");
            float sharpMood = ParseStageOffset(memDefs, "AIColonist_SharpRemarkMood", "baseMoodEffect");
            Check(warmMood > 0 && warmMood < 5f, "warm chat mood blip (" + warmMood + ") positive and milder than KindWordsMood (+5)");
            Check(sharpMood < 0 && sharpMood > -5f, "sharp remark mood blip (" + sharpMood + ") negative but milder than InsultedMood (-5)");

            // 6. thoughtToMake wiring: warm + sharp chain their blips; venting stays purely neutral (opinion only).
            Check(Get(memDefs["AIColonist_WarmChatMemory"], "thoughtToMake") == "AIColonist_WarmChatMood", "warm chat memory chains its 2-day mood lift via thoughtToMake");
            Check(Get(memDefs["AIColonist_SharpRemarkMemory"], "thoughtToMake") == "AIColonist_SharpRemarkMood", "sharp remark memory chains its 2-day mood blip via thoughtToMake");
            Check(Get(memDefs["AIColonist_VentFrustrationMemory"], "thoughtToMake") == null, "venting has no thoughtToMake → purely neutral (opinion only)");

            // 7. Vanilla-convention details: mutual warmth shows a mood bubble; blips nullify Inhumanized (DLC-safe ref).
            Check(Get(memDefs["AIColonist_WarmChatMemory"], "showBubble") == "true", "warm chat memory sets showBubble (KindWords convention)");
            bool inhumanized = true;
            foreach (var name in new[] { "AIColonist_WarmChatMood", "AIColonist_SharpRemarkMood" })
                if (!memDefs[name].Descendants("nullifyingHediffs").Any(e => e.Descendants().Any(li => li.Value == "Inhumanized"))) inhumanized = false;
            Check(inhumanized, "both mood blips nullify Inhumanized (MayRequire Anomaly) like vanilla InsultedMood/KindWordsMood");
        }

        static float ParseStageOffset(Dictionary<string, System.Xml.Linq.XElement> memDefs, string name, string tag)
        {
            var v = memDefs[name].Descendants(tag).First().Value;
            return float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Walks up from the test binary's directory to the mod root (the folder holding both Defs/ and Source/).
        static string FindModRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Defs")) && Directory.Exists(Path.Combine(dir.FullName, "Source"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new Exception("AIColonists mod root not found above " + AppContext.BaseDirectory);
        }

        // ------------------------------------------------------------ UI / social smoke
        static void Test_UiSmoke()
        {
            Section("UI & social worker smoke (Phase 3/4 skeletons)");
            MoteBubbleUtility.TryThrowThoughtMote(null, null); // must not throw
            TestRig.ThrownBubbles.Clear();
            MoteBubbleUtility.TryThrowThoughtMote(MakePawn("m"), "I am hungry.");
            Check(TestRig.ThrownBubbles.Count == 1 && TestRig.ThrownBubbles[0] == "I am hungry.",
                "bubble: spawned via RimWorld.MoteMaker.ThrowText (1.6 API)");
            MoteBubbleUtility.TryThrowThoughtMote(MakePawn("long"), new string('x', 80));
            Check(TestRig.ThrownBubbles.Count == 2 && TestRig.ThrownBubbles[1].Length <= 48 && TestRig.ThrownBubbles[1].EndsWith("…"),
                "bubble: long inner voice truncated (GenMapUI.DrawText does not wrap)");
            var carried = MakePawn("carried"); carried.Spawned = false;
            MoteBubbleUtility.TryThrowThoughtMote(carried, "no anchor");
            Check(TestRig.ThrownBubbles.Count == 2, "bubble: unspawned (carried) pawn gets no mote");

            // 1.6: InteractionWorker declares exactly two virtuals — RandomSelectionWeight + Interacted(out-params).
            var w = new InteractionWorker_AISocial();
            Check(w.RandomSelectionWeight(MakePawn("sa"), MakePawn("sb")) == 0f, "RandomSelectionWeight always 0 (baseChance=0 defs)");
            w.Interacted(null, null, null, out string letterText, out string letterLabel, out LetterDef letterDef, out LookTargets lookTargets);
            Check(letterText == null && letterLabel == null && letterDef == null && lookTargets == null,
                "Interacted with no pending payload: vanilla no-letter default (LLM path covered in Test_SocialWorkerPayload)");

            var tab = new ITab_Pawn_AIMind(); // parameterless ctor required by InspectTabManager.GetSharedInstance
            tab.SelPawn = MakePawn("tab"); // stub defaults: player faction, humanlike; AI enabled since Test_ModEntryPoint
            Check(tab.IsVisible, "tab IsVisible for selected player pawn with AI on");
        }
    }
}
