using UnityEngine;
using Verse;
using HarmonyLib;

namespace AIColonists.Core
{
    /// <summary>
    /// Mod entry point (Spec §9 Phase 1). Hosts mod settings.
    /// RimWorld 1.6 auto-discovers component subclasses via reflection — Map.FillComponents() / World.FillComponents()
    /// iterate AllSubclassesNonAbstract of MapComponent/WorldComponent and instantiate each with a (map)/(world) ctor,
    /// so AIColonistGameComponent / AIColonistWorldComponent need no explicit registration.
    /// All LLM traffic is asynchronous — see AIColonistGameComponent for the dual-clock scheduler.
    /// </summary>
    public class AIColonistMod : Mod
    {
        private static AIColonistMod instance;

        public static AIColonistMod Instance => instance;

        /// <summary>Static accessor used from ThinkTree code (Spec §6.2).</summary>
        public static Settings Settings => instance != null ? instance.settings : null;

        public Settings settings;

        public AIColonistMod(ModContentPack content) : base(content)
        {
            instance = this;
            settings = GetSettings<Settings>();

            // Spec §6.3: apply every [HarmonyPatch] class in this assembly (interaction capture, memory-event
            // flagging, draft purge). The game does NOT bundle Harmony — we ship 0Harmony.dll in Assemblies/.
            // The Mod ctor runs during mod load, before any map exists: the correct point to patch.
            new Harmony("jrhh.AIColonists").PatchAll();

            // The prompt log's folder needs GenFilePaths (Unity paths), so resolve it here on the main thread; requests
            // log from worker threads.
            try { AIColonists.Inference.PromptLog.Directory = AIColonists.Inference.PromptLog.ResolveDirectory(); }
            catch (System.Exception e) { Log.Warning("[AIColonists] prompt log disabled, couldn't find the log folder: " + e.Message); }

            Log.Message("[AIColonists] Loaded (Harmony patches applied). Enable AI in Mod Settings once llama-server is running.");
        }

        // RimWorld 1.6 (verified against decompiled Verse.Mod + RimWorld.Page_ModsConfig): the "Mod Options" button in
        // the Mod List only appears when SettingsCategory() returns a non-empty string, and the dialog body is
        // DoSettingsWindowContents(Rect). GetSettings<T>()/WriteSettings() work regardless — which is why our cfg file
        // existed before this override was added. Pattern matches CharacterEditor's working CharacterEditorMod.

        public override string SettingsCategory()
        {
            return "AI-Integrated Colonists";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Every setting, on one scrolling page (the dialog itself is fixed-size with no scroll) — see UI/SettingsWindow.
            UI.SettingsWindow.Draw(inRect, settings);
        }
    }
}
