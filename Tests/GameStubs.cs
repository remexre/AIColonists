// ============================================================================
//  COMPILE-TIME STUBS — NOT SHIPPED WITH THE MOD.
// ----------------------------------------------------------------------------
//  Minimal mirrors of RimWorld / Unity / Harmony types so the mod's C# can be
//  compile-checked and its pure logic unit-tested WITHOUT a game install:
//      dotnet run --project Tests
//
//  Every stub is tagged in comments:
//    [CONFIRMED]  verified against the REAL RimWorld 1.6.4871 Assembly-CSharp.dll
//                 (reflection probes + ILSpy decompiles — see TEST-RESULTS.md, "Verified 1.6 API map")
//    [GUESS]      best-guess signature — MUST be re-verified in-game (README checklist)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace UnityEngine
{
    public struct Color { public float r, g, b; public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; }
        public static readonly Color white = new Color(1f, 1f, 1f); }
    // [CONFIRMED] UnityEngine.Rect — the mod only uses the (x, y, width, height) ctor.
    public struct Rect { public float x, y, width, height; public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; this.width = w; this.height = h; } }
    // [CONFIRMED] 1.6: Vector2 lives in CoreModule (x/y fields used by ITab.size).
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }

    // [CONFIRMED] 1.6: UnityEngine.Vector3 (CoreModule) — MoteMaker.ThrowText takes one (decompiled).
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }

    // [CONFIRMED] enum members used by the mod's UI tab.
    public enum GameFont { Tiny, Small, Medium, Large }
    public static class Text
    {
        public static GameFont Font;
        // [CONFIRMED] decompiled Verse.Text from this install: SmallFontHeight = 22f — the tab's row pitch must be >= this.
        public const float SmallFontHeight = 22f;
        // [CONFIRMED] Verse.Text.CurFontStyle — per-font cached GUIStyle (white text on Arial_small); the tab derives its
        // wrap style from it so wrapped blocks match vanilla color/font exactly.
        public static GUIStyle CurFontStyle => new GUIStyle();
        // [CONFIRMED 2026-11-22 against 1.7] Verse.Text.CalcHeight(string, float). Stub: one tiny-font line per 100 chars.
        public static float CalcHeight(string text, float width) => 18f * (1 + (text ?? "").Length / 100);
    }
    // [CONFIRMED] 1.6: GUI/Text live in UnityEngine.IMGUIModule.dll (NOT CoreModule).
    // [CONFIRMED] 1.6: GUI.color / GUI.DrawTexture(Rect, Texture2D) are the standard bar-drawing primitives —
    // RimWorld 1.6 has no Widgets.FillRect (verified against the real DLL), so bars draw the white texture tinted.
    public static class GUI
    {
        public static Color color;
        public static void Label(Rect rect, string text) { }
        // [CONFIRMED] decompiled from this install's IMGUIModule: the (Rect, string, GUIStyle) overload exists —
        // it is what the tab uses for word-wrapped labels (Verse.Widgets has NO style-taking Label in 1.6).
        public static void Label(Rect rect, string text, GUIStyle style) { }
        public static void DrawTexture(Rect rect, Texture2D tex) { }
    }

    // [CONFIRMED] UnityEngine.Texture2D — decompiled from this install's CoreModule: the built-in white texture is
    // 'whiteTexture' (NOT 'white' — that name does not exist in Unity 2022.3 / RimWorld 1.6).
    public class Texture2D
    {
        public static readonly Texture2D whiteTexture = new Texture2D();
    }

    // [CONFIRMED] UnityEngine.GUIContent — this Unity 2022.3 build's CalcHeight takes a GUIContent (no string overload;
    // verified by decompiling UnityEngine.IMGUIModule.dll from this install).
    public class GUIContent { public string text; public GUIContent(string text) { this.text = text; } }

    // [CONFIRMED] UnityEngine.GUIStyle — the tab uses a cached word-wrap style because 1.6 has no GenText.AutoWrap
    // (verified absent from the real DLL). Only the properties/methods the mod sets are stubbed.
    public class GUIStyle
    {
        public static readonly GUIStyle none = new GUIStyle();
        public GUIStyle() { }
        // [CONFIRMED] Unity's copy ctor — the tab derives its wrap style from Text.CurFontStyle (a real style).
        public GUIStyle(GUIStyle other) { wordWrap = other.wordWrap; richText = other.richText; fontSize = other.fontSize; }
        public bool wordWrap;
        public bool richText;
        public int fontSize;
        // [CONFIRMED] decompiled UnityEngine.IMGUIModule from this install: CalcHeight(GUIContent, float) is the only public
        // height-calc overload (no string variant in Unity 2022.3). Stub returns a fixed one-line small-font height so the
        // tab's layout math is deterministic in the harness.
        public float CalcHeight(GUIContent content, float width) => 22f;
    }

    // [CONFIRMED] UnityEngine.Mathf is Unity's clamp/lerp helper — NOT System.Mathf.
    public static class Mathf
    {
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Max(float a, float b) => a > b ? a : b; // [CONFIRMED] standard Unity Mathf API
    }

    // [CONFIRMED] 1.6: JsonUtility lives in UnityEngine.JSONSerializeModule.dll, namespace UnityEngine.
    // The real Unity JsonUtility serializes PUBLIC FIELDS (which is why every game class uses public fields).
    // This stub reproduces that with Newtonsoft so wire-name tests are meaningful.
    public static class JsonUtility
    {
        private class IncludeFieldsResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization ms)
            {
                var p = base.CreateProperty(member, ms);
                if (member is FieldInfo f && f.IsPublic) p.Ignored = false;
                return p;
            }
        }

        private static JsonSerializerSettings S() => new JsonSerializerSettings
        {
            ContractResolver = new IncludeFieldsResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static string ToJson(object o) => JsonConvert.SerializeObject(o, Formatting.None, S());
        public static T FromJson<T>(string s) => JsonConvert.DeserializeObject<T>(s, S());
    }
}

namespace HarmonyLib
{
    // [CONFIRMED] standard Harmony 2 surface used by the mod's patches (shipped 0Harmony.dll 2.3.3).
    public enum MethodType { Normal, Getter, Setter }

    [AttributeUsage(AttributeTargets.Class)]
    public class HarmonyPatchAttribute : Attribute
    {
        public HarmonyPatchAttribute(Type t) { }
        public HarmonyPatchAttribute(Type t, string name) { }
        public HarmonyPatchAttribute(Type t, string name, MethodType mt) { }
        public HarmonyPatchAttribute(Type t, string name, Type[] parameters) { } // 1.6: pin overloads by parameter types
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyPostfixAttribute : Attribute { }

    // [CONFIRMED] Harmony 2 prefix attribute — first used by IngestibleSourcePatches (live NRE fix, 2026-11-13).
    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyPrefixAttribute : Attribute { }

    // [CONFIRMED] the real Harmony 2 entry point: new Harmony(id).PatchAll() scans the calling assembly for
    // [HarmonyPatch] classes and weaves them. The stub records that it was called so the harness can assert
    // the mod actually applies its patches (a missing PatchAll call would silently leave all three patches dead).
    public class Harmony
    {
        public static bool PatchAllCalled;
        private readonly string id;
        public Harmony(string id) { this.id = id; }
        public void PatchAll() { PatchAllCalled = true; }
    }
}

namespace Verse
{
    // [CONFIRMED] empty marker interface; ExposeData() is invoked via generic constraint.
    public interface IExposable { }

    public enum LoadSaveMode { Unknown, Loading, Saving, PostLoadInit }
    public static class Scribe { public static LoadSaveMode mode = LoadSaveMode.Unknown; } // [GUESS]

    // [CONFIRMED] values used by this mod: Value, Deep.
    public enum LookMode { Value, Deep, Reference }

    // [GUESS] overloads covering every Scribe_Values.Look call shape in the mod.
    public static class Scribe_Values
    {
        public static void Look(ref bool v, string l, bool d = default(bool)) { }
        public static void Look(ref int v, string l, int d = default(int)) { }
        public static void Look(ref float v, string l, float d = default(float)) { }
        public static void Look(ref string v, string l, string d = null) { }
    }

    // [GUESS] real API is a family of overloads; one generic covers this mod's calls.
    public static class Scribe_Collections
    {
        public static void Look<T>(ref T c, string l, params LookMode[] modes) { }
    }

    // [GUESS] deep-look for IExposable members (MemoryStore).
    public static class Scribe_Deep
    {
        public static void Look<T>(ref T o, string l) where T : IExposable { }
    }

    // [CONFIRMED] Log.Message / Log.Error are ubiquitous; ErrorOnce(string, int) verified by compiling the mod against the real 1.6 DLL.
    public static class Log
    {
        public static List<string> Messages = new List<string>(); // test recorder — production code never reads this
        public static List<string> Errors = new List<string>();   // test recorder for Error/ErrorOnce (2026-11-16, §23.3)
        public static readonly HashSet<int> UsedErrorKeys = new HashSet<int>(); // cleared by TestRig.Reset() for cross-test isolation
        public static void Message(string m) { Messages.Add(m); }
        public static void Error(string m) { Errors.Add(m); }
        // Mirrors vanilla semantics: the first call per key logs, later calls with the same key are dropped.
        public static void ErrorOnce(string m, int id) { if (UsedErrorKeys.Add(id)) Errors.Add(m); }
    }

    // [CONFIRMED] standard settings base with virtual ExposeData.
    public abstract class ModSettings { public virtual void ExposeData() { } }
    public class ModContentPack { } // [GUESS]

    // [CONFIRMED] 1.6: Verse.Mod is the entry-point base — ctor(ModContentPack), GetSettings<T>().
    // (Pre-1.5 RimWorld.ModContentLoaderMod is gone; mods derive from Verse.Mod.)
    public abstract class Mod
    {
        protected Mod(ModContentPack content) { }
        public T GetSettings<T>() where T : ModSettings => Activator.CreateInstance<T>(); // [GUESS] exact API

        // [CONFIRMED] decompiled 1.6 Verse.Mod: both virtual with these exact signatures. The "Mod Options"
        // button in the Mod List (RimWorld.Page_ModsConfig) appears ONLY when SettingsCategory() is non-empty;
        // Dialog_ModSettings.DoWindowContents calls DoSettingsWindowContents(Rect).
        public virtual void DoSettingsWindowContents(UnityEngine.Rect inRect) { }
        public virtual string SettingsCategory() => "";
    }

    // [CONFIRMED] decompiled 1.6 Verse.Widgets — signatures match the real DLL exactly:
    //   Label(Rect, string); CheckboxLabeled(Rect, string, ref bool, ...) is VOID (no click return);
    //   TextField(Rect, string) RETURNS the (possibly edited) string — there is no IntText/FloatText in 1.6;
    //   ButtonText(Rect, string, bool drawBackground = true, ...) RETURNS bool (clicked).
    // [CONFIRMED] There is NO style-taking Label overload in Verse.Widgets (1.6) — the full Label family was dumped
    // from the real DLL: (Rect, GUIContent), (Rect, string), (Rect, TaggedString), (float, ref float, float, string),
    // (Rect, ref float, string). Word-wrapped text goes through UnityEngine.GUI.Label(Rect, string, GUIStyle) instead.
    // [CONFIRMED 2026-11-22] BeginScrollView/EndScrollView, TextFieldNumeric<T>(Rect, ref T, ref string, float, float),
    // DrawLineHorizontal, DrawHighlightIfMouseover: compile-checked against the real 1.7.5014 DLL (UI/SettingsWindow).
    // TestRig.UiPoke makes every input control change what it's bound to (checkbox flips, text gets "~poked", numbers jump
    // to max) — the settings-coverage test uses it to prove each Settings field is wired to some control.
    public static class Widgets
    {
        public static void Label(UnityEngine.Rect rect, string label) { }
        public static void CheckboxLabeled(UnityEngine.Rect rect, string label, ref bool checkOn) { if (TestRig.UiPoke) checkOn = !checkOn; }
        public static string TextField(UnityEngine.Rect rect, string text) => TestRig.UiPoke ? text + "~poked" : text;
        public static bool ButtonText(UnityEngine.Rect rect, string label) => false;
        public static void TextFieldNumeric<T>(UnityEngine.Rect rect, ref T val, ref string buffer, float min = 0f, float max = 1E+09f) where T : struct
        {
            if (buffer == null) buffer = val.ToString();
            if (TestRig.UiPoke) { val = (T)Convert.ChangeType(max, typeof(T)); buffer = val.ToString(); }
        }
        public static void BeginScrollView(UnityEngine.Rect outRect, ref UnityEngine.Vector2 scrollPosition, UnityEngine.Rect viewRect, bool showScrollbars = true) { }
        public static void EndScrollView() { }
        public static void DrawLineHorizontal(float x, float y, float length) { }
        public static void DrawHighlightIfMouseover(UnityEngine.Rect rect) { }
    }

    // [CONFIRMED 2026-11-22 against 1.7] Verse.TooltipHandler.TipRegion — the real one takes a TipSignal, which strings convert to.
    public static class TooltipHandler
    {
        public static void TipRegion(UnityEngine.Rect rect, string tip) { }
    }

    // [CONFIRMED 2026-11-22] Verse.Window / WindowStack: compile-checked against the real 1.7.5014 Assembly-CSharp.dll
    // (Dialog_ProviderTest builds against it). Only the members the mod touches are stubbed.
    public abstract class Window
    {
        public bool doCloseButton;
        public bool doCloseX;
        public bool absorbInputAroundWindow;
        public bool closeOnClickedOutside;
        public virtual UnityEngine.Vector2 InitialSize => new UnityEngine.Vector2(500f, 500f);
        public abstract void DoWindowContents(UnityEngine.Rect inRect);
    }

    public class WindowStack
    {
        public readonly List<Window> Windows = new List<Window>(); // harness inspection only
        public void Add(Window window) => Windows.Add(window);
    }

    // --- time / world access ------------------------------------------------------
    // [CONFIRMED] Find.TickManager.Paused and .TicksGame are standard.
    public class TickManager { public bool Paused; public int TicksGame; }

    public static class Find
    {
        public static RimWorld.Planet.World World = new RimWorld.Planet.World(); // [CONFIRMED] 1.6: World moved to RimWorld.Planet
        public static TickManager TickManager = new TickManager();
        public static WindowStack WindowStack = new WindowStack(); // [CONFIRMED 2026-11-22 against 1.7] Find.WindowStack
        // [CONFIRMED decompiled 2026-11-15] 1.6 Find has NO FactionPlayer member — the player faction is
        // RimWorld.Faction.OfPlayer (see MapPawns.FreeColonists => FreeHumanlikesOfFaction(Faction.OfPlayer)).
        // [CONFIRMED decompiled 2026-11-15] 1.6 Find.CurrentMap property → Verse.Map (null in world view). Stub: field, default null.
        public static Map CurrentMap;
    }

    // --- geometry -------------------------------------------------------------------
    // [CONFIRMED] 1.6: IntVec3 has LengthHorizontal (float, Euclidean XZ) — LengthIntApprox is GONE.
    public struct IntVec3
    {
        public int x, z;
        public IntVec3(int x, int z) { this.x = x; this.z = z; }
        public static IntVec3 operator -(IntVec3 a, IntVec3 b) => new IntVec3(a.x - b.x, a.z - b.z);
        public float LengthHorizontal => (float)Math.Sqrt(x * x + z * z);
        // [CONFIRMED decompiled 2026-11-14] EUCLIDEAN: dx²+dz² ≤ max² — "within 6 cells" is a radius, not a square.
        public bool InHorDistOf(IntVec3 otherLoc, float maxDist)
        {
            float num = x - otherLoc.x;
            float num2 = z - otherLoc.z;
            return num * num + num2 * num2 <= maxDist * maxDist;
        }
    }

    // [CONFIRMED] 1.6: Danger enum in Verse namespace (Unspecified/None/Some/Deadly).
    public enum Danger { Unspecified, None, Some, Deadly }

    // [CONFIRMED] 1.6: RegionType is a [Flags] enum — NO PassableOnly member (pre-1.5 name).
    [Flags]
    public enum RegionType
    {
        None = 0,
        ImpassableFreeAirExchange = 1,
        Normal = 2,
        Portal = 4,
        Fence = 8,
        Set_Passable = 0xE,
        Set_Impassable = 1,
        Set_All = 0xF
    }

    // --- things -----------------------------------------------------------------------
    // [CONFIRMED] 1.6: Def.labelLower is GONE — Def exposes `public string label` + TaggedString LabelCap.
    // [CONFIRMED 1.6 decompile] ThingDef.IsIngestible backs Thing.IngestibleNow (false for steel/rocks, true for meals).
    public class ThingDef { public string label = "thing"; public bool IsIngestible = false; } // [GUESS] shape + confirmed flag
    // [CONFIRMED] .Position / .Destroyed / .def are used everywhere in vanilla.
    // [CONFIRMED 1.6 decompile 2026-11-13] Thing.IngestibleNow is a VIRTUAL property: burning check, then def.IsIngestible.
    public class Thing
    {
        public IntVec3 Position;
        public bool Destroyed;
        public ThingDef def = new ThingDef();
        public virtual bool IsBurning() => false; // real: checks for a Burning hediff — never burning in harness scenarios
        public virtual bool IngestibleNow { get { if (IsBurning()) return false; return def.IsIngestible; } }
    }

    // [CONFIRMED] 1.6: LocalTargetInfo is a STRUCT in the Verse namespace with ONLY (Thing) and (IntVec3) ctors —
    // no parameterless ctor, no public Position member. HasThing/Thing are the accessors mods use.
    public struct LocalTargetInfo
    {
        private readonly Thing thing;
        public LocalTargetInfo(Thing t) { thing = t; }
        public LocalTargetInfo(IntVec3 cell) { thing = null; }
        public bool HasThing => thing != null;
        // [CONFIRMED 1.6 semantics, live evidence 2026-11-13] a reference to a DESTROYED thing resolves to NULL — proven by the
        // JobDriver_Ingest NRE on a dead meal target and by vanilla's own DestroyedOrNull() guard in GainingNutritionNow.
        // Mirrored here so the harness exercises exactly that path.
        public Thing Thing => thing != null && !thing.Destroyed ? thing : null;
    }

    // [CONFIRMED] Verse.ThingMaker.MakeThing(ThingDef, ThingDef stuff = null) is the standard unspawned factory (decompiled 1.6).
    public static class ThingMaker { public static Thing MakeThing(ThingDef def) { return new Thing { def = def }; } }

    public class JobDef { public string defName = "StubJob"; } // [CONFIRMED] Verse.JobDef : Def — defName is the def key (used by the §23.5 [job started] attribution suffix)
    public class WorkTypeDef { } // [CONFIRMED] Verse.WorkTypeDef

    // [CONFIRMED] 1.6: interaction/letter plumbing types live in VERSE (verified by reflection probe —
    // only InteractionDef is RimWorld; see the RimWorld namespace section below).
    public class RulePackDef { }
    public class LetterDef { public string defName = "stub letter"; } // defName added so tests can assert WHICH letter def a worker picked
    public class LookTargets { }

    // [CONFIRMED] 1.6: Verse.DefDatabase<T>.GetNamed(string, bool errorOnFail) is the static def lookup (reflection probe on
    // 1.6.4871 — no default value in metadata, so callers always pass both args). The stub keeps a public Registry so tests
    // can register defs before exercising AreaBatcher.DefForImpulse.
    public class DefDatabase<T>
    {
        public static Dictionary<string, T> Registry = new Dictionary<string, T>();

        public static T GetNamed(string defName, bool errorOnFail)
        {
            T result;
            if (Registry.TryGetValue(defName, out result)) return result;
            if (errorOnFail) throw new Exception("Def not found: " + defName);
            return default(T); // unconstrained generic — null only works for reference types
        }
    }

    // --- jobs ---------------------------------------------------------------------------
    // [CONFIRMED] Job.expiryInterval is set by mods/vanilla alike.
    // [CONFIRMED 1.6 decompile 2026-11-13] Job exposes public targetA/targetB LocalTargetInfo fields and GetTarget(TargetIndex) —
    // JobDriver_Ingest reads `job.GetTarget(TargetIndex.A).Thing` for its food source.
    public class Job
    {
        public int expiryInterval;
        // [CONFIRMED decompiled 2026-11-15] 1.6 Verse.AI.Job carries the resolved def as `public JobDef def` (renamed from
        // pre-1.5 jobDef) + targets as plain public fields.
        public JobDef def;
        public LocalTargetInfo targetA;
        public LocalTargetInfo targetB;
        // [CONFIRMED] vanilla Job fields a WorkGiver may set beyond def + targets (Pick Up And Haul fills targetQueueA;
        // hauling/bill givers set count). Used to prove work jobs come from the giver, not a JobMaker rebuild.
        public List<LocalTargetInfo> targetQueueA;
        public int count = -1;

        // [CONFIRMED decompiled 2026-11-15] 1.6 has a Job(JobDef) ctor (the AI hold job uses `new Job(JobDefOf.Wait)`).
        public Job() { }
        public Job(JobDef jobDef) { def = jobDef; }

        public LocalTargetInfo GetTarget(Verse.AI.TargetIndex i) =>
            i == Verse.AI.TargetIndex.A ? targetA : (i == Verse.AI.TargetIndex.B ? targetB : default(LocalTargetInfo));
    }

    // [CONFIRMED decompiled 2026-11-15] JobMaker.MakeJob overloads are PURE factories (Verse.JobMaker contains zero
    // reservation calls) — the stub now populates def/targets exactly like the real one.
    public static class JobMaker
    {
        public static Job MakeJob(JobDef jobDef, LocalTargetInfo a) => new Job { def = jobDef, targetA = a };
        public static Job MakeJob(JobDef jobDef, LocalTargetInfo a, LocalTargetInfo b) => new Job { def = jobDef, targetA = a, targetB = b };
    }

    // --- pawns ------------------------------------------------------------------------------
    // [CONFIRMED] Need.CurLevelPercentage is the standard need-level accessor.
    public class Need { public float CurLevelPercentage = 1f; }
    // [CONFIRMED decompiled 2026-11-15] 1.6: Pawn_NeedsTracker.mood is a PUBLIC FIELD of type RimWorld.Need_Mood (a Need
    // subclass carrying the thought handler); food/rest are plain Needs.
    public class Pawn_NeedsTracker { public RimWorld.Need_Mood mood = new RimWorld.Need_Mood(); public Need food = new Need(); public Need rest = new Need(); }

    // [CONFIRMED decompiled 2026-11-15] 1.6 Verse.Pawn_AgeTracker.AgeBiologicalYears is a public int property (stub: field).
    public class Pawn_AgeTracker { public int AgeBiologicalYears = 30; }

    // [CONFIRMED] 1.6: HediffSet.PainTotal (public float, cached — DirtyPainCache()). PainLevel does NOT exist.
    public class HediffSet { public float PainTotal; }
    public class Pawn_HealthTracker { public HediffSet hediffSet = new HediffSet(); }

    // [CONFIRMED] RaceProps.Humanlike, Faction.IsPlayer are standard filters. (Faction itself lives in the RIMWORLD namespace — see below.)
    public class Pawn_RaceProps { public bool Humanlike = true; }

    // [CONFIRMED] 1.6: Room has `public int ID` + a Map property (used by AreaBatcher keys).
    public class Room { public int ID; public Map Map; }

    // [CONFIRMED] 1.6: RegionAndRoomQuery.GetRoom(Thing, RegionType) is the canonical room lookup —
    // Pawn/Thing have NO room accessor of their own (pre-1.5 InnerRoom/CurrentRoom are gone).
    public static class RegionAndRoomQuery
    {
        public static Room GetRoom(Thing thing, RegionType regionType) =>
            TestRig.RoomsByThing != null && TestRig.RoomsByThing.TryGetValue(thing, out var r) ? r : null;
    }

    // [CONFIRMED] 1.6: pawn.workSettings (Pawn_WorkSettings) holds the ordered work-giver list.
    public class Pawn_WorkSettings
    {
        public List<Verse.AI.WorkGiver> WorkGiversInOrderNormal = new List<Verse.AI.WorkGiver>();
    }

    // [CONFIRMED] Pawn derives from Thing in RimWorld (Position/Destroyed/def inherited).
    // [CONFIRMED] aggregate of the members the mod touches — each verified on 1.6.4871.
    public class Pawn : Thing
    {
        public string ThingID = "stub-pawn";
        // [CONFIRMED] 1.6: Thing.LabelCap => Label.CapitalizeFirst(def) — a virtual property inherited by Pawn (decompiled).
        // Live NRE 2026-11-14: on a half-loaded pawn, Label falls back to KindLabel → GenLabel.BestKindLabel, which derefs
        // kind/race defs still NULL during save load. Stub keeps a plain string but exposes it as VIRTUAL so HalfLoadedPawn
        // can simulate the load-time throw (the real property would throw from inside).
        private string labelCapInt = "StubColonist";
        public virtual string LabelCap { get { return labelCapInt; } set { labelCapInt = value; } }
        // [CONFIRMED 1.7] real LabelShort = Name.ToStringShort ("Paulo"); LabelCap = "Paulo<color=#999999FF>, Dude</color>".
        // Stub: settable; defaults to LabelCap so older tests that only set LabelCap keep working.
        private string labelShortInt;
        public string LabelShort { get { return labelShortInt ?? LabelCap; } set { labelShortInt = value; } }
        // [CONFIRMED decompiled 1.7] Pawn.DevelopmentalStage + Verse.DevelopmentalStageExtensions.Newborn()/Baby().
        public DevelopmentalStage DevelopmentalStage = DevelopmentalStage.Adult;
        public Map Map;
        // [CONFIRMED] 1.6: Pawn.Spawned / Thing.DrawPos / Thing.MapHeld — all used by decompiled MoteMaker
        // (pawn.DrawPos, thing.MapHeld); motes can only anchor to spawned pawns.
        public bool Spawned = true;
        public UnityEngine.Vector3 DrawPos;
        public Map MapHeld => Map;
        public bool Drafted;
        public bool InMentalState;
        // [CONFIRMED decompiled 2026-11-15] Pawn.IsPrisoner is a public (guest-tracker-based) property — the stub keeps a plain field.
        public bool IsPrisoner;
        public Pawn_NeedsTracker needs = new Pawn_NeedsTracker();
        // [CONFIRMED decompiled 2026-11-15] 1.6 Verse.Pawn: public fields ageTracker (Pawn_AgeTracker), story
        // (RimWorld.Pawn_StoryTracker — carries the TraitSet), skills (RimWorld.Pawn_SkillTracker) — the Phase 2
        // identity-capture surface (Spec §7.1).
        public Pawn_AgeTracker ageTracker = new Pawn_AgeTracker();
        public RimWorld.Pawn_StoryTracker story = new RimWorld.Pawn_StoryTracker();
        public RimWorld.Pawn_SkillTracker skills = new RimWorld.Pawn_SkillTracker();
        public Pawn_HealthTracker health = new Pawn_HealthTracker();
        public Pawn_RaceProps RaceProps = new Pawn_RaceProps();
        public RimWorld.Faction Faction = new RimWorld.Faction();
        public Pawn_WorkSettings workSettings = new Pawn_WorkSettings();
        // [CONFIRMED] 1.6: pawn.interactions is a PUBLIC FIELD of type RimWorld.Pawn_InteractionsTracker (reflection probe).
        // Left null on purpose — tests assign it explicitly, which also exercises the mod's null guard.
        public RimWorld.Pawn_InteractionsTracker interactions;
    }

    // [CONFIRMED decompiled 1.7] Verse.DevelopmentalStage flags + extension predicates.
    [Flags]
    public enum DevelopmentalStage { None = 0, Newborn = 1, Baby = 2, Child = 4, Adult = 8 }
    public static class DevelopmentalStageExtensions
    {
        public static bool Newborn(this DevelopmentalStage s) => (s & DevelopmentalStage.Newborn) != 0;
        public static bool Baby(this DevelopmentalStage s) => (s & DevelopmentalStage.Baby) != 0;
    }

    // --- maps / components --------------------------------------------------------------------
    // [CONFIRMED] mapPawns.AllPawnsSpawned / FreeColonistsSpawned are standard.
    public class MapPawns
    {
        public List<Pawn> AllPawnsSpawned = new List<Pawn>();
        public List<Pawn> FreeColonistsSpawned = new List<Pawn>();
    }

    // [GUESS] shape; GetComponent<T> is the standard mod access pattern. [CONFIRMED]
    public class Map
    {
        public MapPawns mapPawns = new MapPawns();
        // [CONFIRMED decompiled 2026-11-15] 1.6: Map.Parent → WorldObject (info.parent); colony name =
        // (Find.CurrentMap?.Parent as RimWorld.Planet.Settlement)?.Name. Stub: field, default null (no settlement).
        public RimWorld.Planet.WorldObject Parent;
        private readonly List<MapComponent> components = new List<MapComponent>();
        public void RegisterForTests(MapComponent c) => components.Add(c);

        public T GetComponent<T>() where T : MapComponent
        {
            foreach (var c in components) if (c is T t) return t;
            return null;
        }
    }

    // [CONFIRMED] 1.6: MapComponent has a readonly `map` field, protected ctor(Map), and virtuals
    // MapGenerated()/MapComponentTick() — MapInitialized() does NOT exist in 1.6.
    public abstract class MapComponent
    {
        public readonly Map map;
        protected MapComponent(Map map) { this.map = map; }
        public virtual void MapGenerated() { }
        public virtual void MapComponentTick() { }
    }


    // --- sensory utility hooks (scriptable for tests) ---------------------------------------------------
    public static class TestRig
    {
        public static bool UiPoke; // see Widgets: input controls mutate their bound values while set
        public static bool CanReachDefault = true;
        public static Thing BestFoodSourceResult;
        public static Thing FindBedForResult;
        public static List<Thing> WorkThings = new List<Thing>();
        // 2026-11-15: scriptable WorkGiver_Scanner.JobOnThing — default mimics vanilla (a job targeting the scanned thing);
        // tests substitute `t => null` to exercise the "giver can't work this target" path.
        public static Func<Thing, Job> WorkJobOnThing = DefaultWorkJobOnThing;
        public static Job DefaultWorkJobOnThing(Thing t) => new Job { def = new JobDef(), targetA = new LocalTargetInfo(t) };
        // 2026-11-16: scriptable WorkGiver_Scanner.HasJobOnThing — vanilla's two-stage contract gate (§23). Default true
        // (mimics a satisfied gate); tests substitute `(p, t) => false` to exercise the "giver refuses this target" path.
        public static Func<Pawn, Thing, bool> WorkHasJobOnThing = DefaultWorkHasJobOnThing;
        public static bool DefaultWorkHasJobOnThing(Pawn p, Thing t) => true;
        public static bool LineOfSightDefault = true;
        public static Dictionary<Thing, Room> RoomsByThing = new Dictionary<Thing, Room>();
        public static List<string> ThrownBubbles = new List<string>(); // MoteMaker.ThrowText call recorder
        public static bool TryInteractWithResult = true;
        public static bool CanInteractNowWithResult = true;

        // Sub-check toggles for the drop-gate diagnostic (DescribeDropGate) — each mirrors one decompiled 1.6 gate element.
        public static bool CooldownActive = false;                    // InteractedTooRecentlyToInteract()
        public static bool CanInitiateInteractionResult = true;       // SocialInteractionUtility.CanInitiateInteraction
        public static bool CanReceiveInteractionResult = true;        // SocialInteractionUtility.CanReceiveInteraction
        public static bool CarryingPawnResult = false;                // PawnUtility.IsCarryingPawn extension

        public static void Reset()
        {
            CanReachDefault = true;
            BestFoodSourceResult = null;
            FindBedForResult = null;
            WorkThings.Clear();
            WorkJobOnThing = DefaultWorkJobOnThing;
            WorkHasJobOnThing = DefaultWorkHasJobOnThing;
            LineOfSightDefault = true;
            RoomsByThing.Clear();
            ThrownBubbles.Clear();
            TryInteractWithResult = true;
            CanInteractNowWithResult = true;
            CooldownActive = false;
            CanInitiateInteractionResult = true;
            CanReceiveInteractionResult = true;
            CarryingPawnResult = false;
            Log.Messages.Clear();
            Log.Errors.Clear();
            Log.UsedErrorKeys.Clear();
        }
    }

    // [CONFIRMED] 1.6: FoodUtility.BestFoodSourceOnMap(Pawn getter, Pawn eater, bool desperate, out ThingDef foodDef, ...optional).
    public static class FoodUtility
    {
        public static Thing BestFoodSourceOnMap(Pawn getter, Pawn eater, bool desperate, out ThingDef foodDef)
        {
            foodDef = null;
            return TestRig.BestFoodSourceResult;
        }
    }

    // [CONFIRMED name] (Petification log); return type is a GUESS — verify in-game.
    public static class RestUtility { public static Thing FindBedFor(Pawn pawn) => TestRig.FindBedForResult; }

    // [CONFIRMED decompiled 2026-11-14] GenSight.LineOfSight(start, end, map, skipFirstCell: false, validator: null, ...) —
    // walks the line cell-by-cell; a cell blocks iff it holds a BUILDING with FillCategory.Full (walls, closed doors).
    public static class GenSight
    {
        public static bool LineOfSight(IntVec3 a, IntVec3 b, Map map = null, bool skipFirstCell = false) => TestRig.LineOfSightDefault;
    }

    // [CONFIRMED] 1.6: CanReach is an EXTENSION method on Pawn (Verse.ReachabilityUtility) taking
    // LocalTargetInfo + Danger — there is no instance CanReach(IntVec3, PathEndMode) anymore.
    public static class ReachabilityUtility
    {
        public static bool CanReach(this Pawn pawn, LocalTargetInfo dest, Verse.AI.PathEndMode peMode, Danger maxDanger) => TestRig.CanReachDefault;
    }

    // --- UI ---------------------------------------------------------------------------------------------
    // [CONFIRMED] 1.6: InspectTabBase is the tab foundation — public labelKey/tutorTag/size fields,
    // virtual IsVisible + UpdateSize(), abstract FillTab(). Tabs are shared singletons created via
    // Activator.CreateInstance (parameterless ctor required; NO per-pawn state).
    public abstract class InspectTabBase
    {
        public string labelKey;
        public string tutorTag;
        public UnityEngine.Vector2 size = new UnityEngine.Vector2(300f, 250f);

        public virtual bool IsVisible => true;

        protected virtual void UpdateSize() { }

        protected abstract void FillTab();

        // Test hook: the real game drives FillTab() from DoTabGUI()'s immediate-window callback.
        public void TestFill() { if (IsVisible) FillTab(); }
    }

    // [CONFIRMED] decompiled RimWorld.MainTabWindow_Inspect from this install: PaneTopY = UI.screenHeight - 165f - 35f —
    // the inspect pane is bottom-anchored and tab windows grow UP from it; the AI Mind tab clamps its dynamic height against this.
    public static class UI { public static int screenHeight => 1080; }
}

namespace Verse.AI
{
    // [CONFIRMED] 1.6: PathEndMode lives in the Verse.AI namespace.
    // Touch is the mode used for reachability checks.
    public enum PathEndMode { ClosestTouch, Touch, EnterBuilding, OnCell }

    // [CONFIRMED 1.6 decompile 2026-11-13] TargetIndex (Verse.AI) indexes Job targets; JobDriver_Ingest declares
    // IngestibleSourceInd = TargetIndex.A and TableCellInd = TargetIndex.B.
    public enum TargetIndex { A, B, C }

    // [CONFIRMED] JobDriver exposes `public Job job` — mods read driver.job everywhere (and our prefix does too).
    public class JobDriver { public Job job; }

    // [CONFIRMED] derived nodes override these (ThinkNode_Conditional / ThinkNode_JobGiver pattern).
    public abstract class ThinkNode_Conditional { protected abstract bool Satisfied(Pawn pawn); }
    public abstract class ThinkNode_JobGiver { protected abstract Job TryGiveJob(Pawn pawn); }

    // [CONFIRMED] 1.6: WorkGiver is the base; WorkGiver_Scanner.PotentialWorkThingsGlobal(Pawn) is a
    // virtual INSTANCE method (no static entry point) — iterate via pawn.workSettings.WorkGiversInOrderNormal.
    public abstract class WorkGiver { }

    public class WorkGiver_Scanner : WorkGiver
    {
        // [CONFIRMED decompiled 2026-11-15] virtual INSTANCE method returning an UNFILTERED candidate stream — e.g. 1.6
        // WorkGiver_GatherAnimalBodyResources returns ALL faction pawns (humans included); the real gate is HasJobOnThing.
        public virtual IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => TestRig.WorkThings;

        // [CONFIRMED decompiled 2026-11-16] public virtual bool HasJobOnThing(Pawn, Thing, bool forced = false) — vanilla's
        // two-stage contract: the actual per-target validation (animal+comp check for milk/shear/slaughter, NeedsTeacher +
        // desk-spot reservation for teach). Scriptable via TestRig.WorkHasJobOnThing; default true (gate satisfied).
        public virtual bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) => TestRig.WorkHasJobOnThing(pawn, t);

        // [CONFIRMED decompiled 2026-11-15] public virtual Job JobOnThing(Pawn, Thing, bool forced = false) — the exact job
        // this WorkGiver would give for that target. NOT universally a pure factory: WorkGiver_Teach mutates the target's
        // current job (CurJob.SetTarget(B, ...)) and builds its targets from that job — must only be reached after
        // HasJobOnThing passes (§23). Scriptable via TestRig.WorkJobOnThing; default mimics vanilla
        // (a job targeting the scanned thing).
        public virtual Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => TestRig.WorkJobOnThing(t);
    }
}

namespace RimWorld.Planet
{
    // [CONFIRMED] 1.6: World + WorldComponent moved from Verse to RimWorld.Planet.
    // [GUESS] shape; Find.World.GetComponent<T>() is the standard mod access pattern. [CONFIRMED]
    public class World
    {
        private readonly List<WorldComponent> components = new List<WorldComponent>();
        public void RegisterForTests(WorldComponent c) => components.Add(c);

        public T GetComponent<T>() where T : WorldComponent
        {
            foreach (var c in components) if (c is T t) return t;
            return null;
        }
    }

    // [CONFIRMED] 1.6: WorldComponent base with (World) ctor — auto-discovered by World.FillComponents().
    public abstract class WorldComponent
    {
        protected WorldComponent(World world) { }
        public virtual void ExposeData() { }
    }

    // [CONFIRMED decompiled 2026-11-15] 1.6: Settlement : MapParent (a WorldObject) with a public string Name field —
    // the colony's settlement name for the identity prefix (Spec §7.1).
    public class WorldObject { }
    public class Settlement : WorldObject { public string Name; }
}

namespace RimWorld
{
    // [CONFIRMED decompiled 2026-11-15] Faction is in the RIMWORLD namespace (Verse.Faction does not exist in 1.6);
    // OfPlayer is a static property — MapPawns.FreeColonists => FreeHumanlikesOfFaction(Faction.OfPlayer).
    public class Faction
    {
        public bool IsPlayer = true;
        public static readonly Faction OfPlayer = new Faction { IsPlayer = true };
    }

    // [CONFIRMED decompiled 2026-11-15] 1.6: TraitSet.allTraits is a PUBLIC List<Trait> FIELD; Trait exposes LabelCap
    // (computed), Degree (int) and Suppressed (bool) — the identity-prefix trait line + weight-seeding source.
    public class Trait { public string LabelCap = "stub trait"; public int Degree; public bool Suppressed; public TraitDef def; }
    // [CONFIRMED] Trait.def (TraitDef) with defName — keys the voice table (vanilla defNames read from the 1.7 TraitDefs XML).
    public class TraitDef { public string defName = ""; }
    // [CONFIRMED decompiled 1.7] BackstoryDef.FullDescriptionFor(Pawn) → TaggedString: resolved prose, then a blank line,
    // then skill/work-disable lines. Stub returns `fullDescription` verbatim so tests can include that tail.
    public class BackstoryDef
    {
        public string description = ""; // [CONFIRMED] Def.description: raw prose with [PAWN_*] tokens
        public string fullDescription = "";
        public string FullDescriptionFor(Verse.Pawn p) => fullDescription;
    }
    public class TraitSet { public List<Trait> allTraits = new List<Trait>(); }

    // [CONFIRMED decompiled 2026-11-15] 1.6: the TraitSet lives on the STORY TRACKER — `public TraitSet traits` field on
    // RimWorld.Pawn_StoryTracker (Verse.Pawn has NO direct traits field; vanilla code reads story?.traits.allTraits).
    // [CONFIRMED decompiled 1.7] Childhood/Adulthood (BackstoryDef) + TitleCap (custom title, else adulthood/childhood title).
    public class Pawn_StoryTracker
    {
        public TraitSet traits = new TraitSet();
        public BackstoryDef Childhood;
        public BackstoryDef Adulthood;
        public string TitleCap = "";
    }

    // [CONFIRMED decompiled 2026-11-15] 1.6: Pawn_SkillTracker.skills is a PUBLIC List<SkillRecord> FIELD + GetSkill(SkillDef);
    // SkillRecord carries public def (SkillDef) + levelInt (int). SkillDefOf fields include Intellectual (the learning-system
    // stat — there is NO "Intellect" field in 1.6; verified against the real DLL's field listing).
    public class SkillDef { public string label = "stub skill"; }
    public static class SkillDefOf
    {
        public static readonly SkillDef Intellectual = new SkillDef { label = "intellectual" };
    }
    public class SkillRecord { public SkillDef def; public int levelInt; }
    public class Pawn_SkillTracker
    {
        public List<SkillRecord> skills = new List<SkillRecord>();
        // Real GetSkill ADDS a record for missing defs (def-DB driven); the stub returns null — the mod null-guards it.
        public SkillRecord GetSkill(SkillDef skillDef)
        {
            foreach (var r in skills) if (r != null && r.def == skillDef) return r;
            return null;
        }
    }

    // [CONFIRMED] 1.6: all *DefOf classes live in the RimWorld namespace. Ingest/Wait/LayDown verified on
    // install 1.6.4871 (LayDown is what vanilla JobGiver_ForceSleepNow uses for bed sleep).
    public static class JobDefOf
    {
        public static readonly Verse.JobDef Ingest = new Verse.JobDef();
        public static readonly Verse.JobDef Wait = new Verse.JobDef();
        public static readonly Verse.JobDef LayDown = new Verse.JobDef();
    }

    // [CONFIRMED 1.6 decompile 2026-11-13] JobDriver_Ingest: `private Thing IngestibleSource => job.GetTarget(TargetIndex.A).Thing`
    // with NO null guard — the live NRE site (Player.log: Exception in CheckCurrentToilEndOrFail ... b__19_0, 50-colonist stress
    // test). The stub mirrors the getter so tests can prove the unpatched path dies and the prefix intercepts first.
    public class JobDriver_Ingest : Verse.AI.JobDriver
    {
        private Verse.Thing IngestibleSource => job.GetTarget(Verse.AI.TargetIndex.A).Thing;
    }

    // [CONFIRMED] RimWorld.ThingDefOf.Steel exists (decompiled field listing); steel has no <ingestible> def — safe sentinel.
    public static class ThingDefOf
    {
        public static readonly Verse.ThingDef Steel = new Verse.ThingDef { label = "steel" };
    }

    // [CONFIRMED] 1.6 (decompiled 2026-11-12): MoteMaker moved to the RIMWORLD namespace and is a STATIC class —
    // Verse.MoteMaker does NOT exist in this build. ThrowText spawns the vanilla Mote_Text bubble at loc
    // (Mote_Special.xml: thingClass=MoteText, realTime=true, solidTime=2.2); the stub records calls so tests
    // can assert the spawn actually happened (TestRig.ThrownBubbles).
    public static class MoteMaker
    {
        public static void ThrowText(UnityEngine.Vector3 loc, Verse.Map map, string text, float timeBeforeStartFadeout = -1f)
        {
            Verse.TestRig.ThrownBubbles.Add(text);
        }
    }

    // [CONFIRMED] 1.6: InteractionWorker declares exactly two virtuals — RandomSelectionWeight(Pawn,Pawn) and
    // Interacted(Pawn,Pawn,List<RulePackDef>, out string, out string, out LetterDef, out LookTargets).
    // RulePackDef/LetterDef/LookTargets live in the VERSE namespace (verified); InteractionDef is RimWorld.
    public class InteractionWorker
    {
        public InteractionDef interaction;

        public virtual float RandomSelectionWeight(Verse.Pawn initiator, Verse.Pawn recipient) => 0f;

        public virtual void Interacted(Verse.Pawn initiator, Verse.Pawn recipient, List<Verse.RulePackDef> extraSentencePacks,
            out string letterText, out string letterLabel, out Verse.LetterDef letterDef, out Verse.LookTargets lookTargets)
        {
            letterText = null;
            letterLabel = null;
            letterDef = null;
            lookTargets = null;
        }
    }

    // [CONFIRMED] 1.6: InteractionDef is in the RimWorld namespace (the other interaction plumbing types are Verse — see above).
    // [CONFIRMED] 1.6: Def exposes `public string label` + a LabelCap property (reflection probe); defName is the def key.
    public class InteractionDef
    {
        public string defName = "stub interaction";
        public string label = "stub interaction";
        public string LabelCap => label; // real 1.6: TaggedString property; the stub keeps a plain string (implicit conversion makes both compile)
    }

    // [CONFIRMED] 1.6: LetterDefOf.PositiveEvent / NeutralEvent / NegativeEvent exist as static readonly fields
    // (reflection probe). There is NO SocialInteraction member — vanilla letter defs are event-flavored only.
    public static class LetterDefOf
    {
        public static readonly Verse.LetterDef PositiveEvent = new Verse.LetterDef { defName = "PositiveEvent" };
        public static readonly Verse.LetterDef NeutralEvent = new Verse.LetterDef { defName = "NeutralEvent" };
        public static readonly Verse.LetterDef NegativeEvent = new Verse.LetterDef { defName = "NegativeEvent" };
    }

    // [CONFIRMED] 1.6: ThoughtDef / Thought_Memory live in the RimWorld namespace (verified by reflection probe).
    // [CONFIRMED decompiled 2026-11-14] ThoughtDef.Label is a COMPUTED property: label, else stages[0].label / labelSocial,
    // else defName — and the stage fallback derefs stages[0] UNGUARDED. Live NRE round 2 (2026-11-14): during save load the
    // stages list can hold a NULL first element while memory thoughts are re-granted mid-load, so Label throws. The stub
    // mirrors the real getter exactly so the harness can witness the unpatched path dying.
    public class ThoughtStage { public string label; public string labelSocial; }
    public class ThoughtDef
    {
        public string label;
        public string defName = "StubMemoryThought";
        public List<ThoughtStage> stages = new List<ThoughtStage>();

        public string Label
        {
            get
            {
                if (!string.IsNullOrEmpty(label)) return label;
                if (stages != null && stages.Count > 0)
                {
                    if (!string.IsNullOrEmpty(stages[0].label)) return stages[0].label; // NREs when stages[0] is null — as in vanilla
                    if (!string.IsNullOrEmpty(stages[0].labelSocial)) return stages[0].labelSocial;
                }
                return defName;
            }
        }
    }
    // [CONFIRMED decompiled 2026-11-15] 1.6 Thought_Memory: PUBLIC FIELDS moodOffset (int) + otherPawn (Pawn); LabelCap is a
    // COMPUTED property formatted against the def stages + otherPawn — can throw on half-loaded data (ErrorOnce 4814 precedent).
    public class Thought_Memory
    {
        public ThoughtDef def = new ThoughtDef();
        public int moodOffset;
        public Verse.Pawn otherPawn;
        private string labelCapInt = "stub thought";
        public virtual string LabelCap { get { return labelCapInt; } set { labelCapInt = value; } } // stub: settable for tests (real: computed)
    }

    // [CONFIRMED decompiled 2026-11-15] 1.6: Need_Mood : Need_Seeker carries a PUBLIC `thoughts` FIELD of type ThoughtHandler,
    // which holds a PUBLIC `memories` FIELD of type MemoryThoughtHandler (both initialized in the real ctor).
    public class ThoughtHandler { public MemoryThoughtHandler memories = new MemoryThoughtHandler(); }
    public class Need_Mood : Verse.Need { public ThoughtHandler thoughts = new ThoughtHandler(); }

    // [CONFIRMED] 1.6: MemoryThoughtHandler.TryGainMemory has TWO overloads ((ThoughtDef,Pawn,Precept) and
    // (Thought_Memory,Pawn)); the mod pins the latter via HarmonyPatch parameter types. Instance method.
    public class MemoryThoughtHandler
    {
        public Verse.Pawn pawn; // [CONFIRMED] public field in 1.6 (decompiled); the patch keys on this, not otherPawn

        // [CONFIRMED decompiled 2026-11-15] 1.6: Memories is a COMPUTED property over the private list — stub: public field
        // with the same name so `thoughts.memories.Memories` compiles identically.
        public List<Thought_Memory> Memories = new List<Thought_Memory>();

        public bool TryGainMemory(Thought_Memory thought, Verse.Pawn pawn) => false;
    }

    // [CONFIRMED] 1.6: Pawn_InteractionsTracker.TryInteractWith(Pawn, InteractionDef) is the vanilla social
    // entry point; its `pawn` field is PRIVATE (no accessor) — the mod recovers it via cached reflection.
    public class Pawn_InteractionsTracker
    {
        private Verse.Pawn pawn;

        public Pawn_InteractionsTracker(Verse.Pawn p) { pawn = p; }

        // [CONFIRMED] 1.6 (decompiled 2026-11-13): public bool CanInteractNowWith(Pawn, InteractionDef = null) — the
        // exact gate TryInteractWith applies internally (120-tick cooldown + ≤6-cell/LOS + spawned/awake/talking/burning).
        // The mod pre-checks with it before attempting, because the batcher groups by room, not proximity.
        public bool CanInteractNowWith(Verse.Pawn other, InteractionDef intDef) => Verse.TestRig.CanInteractNowWithResult;

        // [CONFIRMED decompiled 2026-11-14] public bool InteractedTooRecentlyToInteract() — TicksGame < lastInteractionTime + 120.
        public bool InteractedTooRecentlyToInteract() => Verse.TestRig.CooldownActive;

        // Scriptable result so tests can exercise both the success and precondition-failed (false) paths.
        public bool TryInteractWith(Verse.Pawn other, InteractionDef intDef) => Verse.TestRig.TryInteractWithResult;
    }

    // [CONFIRMED decompiled 2026-11-14] SocialInteractionUtility — the state half of the interaction gate.
    // CanInitiate: Talking capacity + Awake + !IsBurning (+ mutant/interaction-block filters). CanReceive: Awake + !IsBurning.
    public static class SocialInteractionUtility
    {
        public static bool CanInitiateInteraction(Verse.Pawn pawn, InteractionDef interactionDef = null) => Verse.TestRig.CanInitiateInteractionResult;
        public static bool CanReceiveInteraction(Verse.Pawn pawn, InteractionDef interactionDef = null) => Verse.TestRig.CanReceiveInteractionResult;

        // [CONFIRMED decompiled 2026-11-14] position gate: InHorDistOf(6f) — EUCLIDEAN radius — then GenSight.LineOfSight(skipFirstCell: true).
        public static bool IsGoodPositionForInteraction(Verse.Pawn p, Verse.Pawn recipient)
            => p.Position.InHorDistOf(recipient.Position, 6f) && Verse.GenSight.LineOfSight(p.Position, recipient.Position, p.Map, skipFirstCell: true);
    }

    // [CONFIRMED decompiled 2026-11-14] PawnUtility.IsCarryingPawn is a public EXTENSION (this Pawn) — carrying a child
    // pawn makes vanilla skip the position check entirely.
    public static class PawnUtility
    {
        public static bool IsCarryingPawn(this Verse.Pawn pawn, Verse.Pawn carryPawn = null) => Verse.TestRig.CarryingPawnResult;
    }

    // [CONFIRMED] 1.6: components expose their pawn; Drafted is a settable property (override hook).
    public class Pawn_DraftController { public Verse.Pawn pawn; public bool Drafted { get; set; } }

    // [CONFIRMED] 1.6: RimWorld.ITab derives from Verse.InspectTabBase and carries the live selection —
    // InspectPane sets SelPawn/SelThing on the shared singleton before drawing.
    public abstract class ITab : Verse.InspectTabBase
    {
        public Verse.Pawn SelPawn;
        public Verse.Thing SelThing;
    }
}
