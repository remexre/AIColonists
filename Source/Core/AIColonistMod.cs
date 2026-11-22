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
            Settings s = settings;
            if (s == null) return;

            // Phases 1–3 surface: master switch + one endpoint per layer. All three URLs feed the same hand-built
            // wire format; point them at different servers (or all at one) as you scale up models.
            float rowH = 35f;
            Rect r = new Rect(0f, 0f, inRect.width - 20f, inRect.height);

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 10f, r.y + 5f, r.width - 20f, rowH), "AI-Integrated Colonists — impulses · room social · memory drift (all phases live)");
            Text.Font = GameFont.Small;

            float y = r.y + 45f;

            Widgets.CheckboxLabeled(new Rect(r.x + 10f, y, r.width - 20f, rowH), "Enable AI (master switch — off means the mod is inert)", ref s.EnableAI);
            y += rowH + 8f;

            // Phase 2 (backlog #8): AI-first job priority. ON (default) — while an LLM decision is in flight the pawn
            // holds position instead of letting the vanilla sorter below issue work first. Player orders, drafting and
            // emergency survival still win natively; an exhausted pawn (rest < 0.1) is never held.
            Widgets.CheckboxLabeled(new Rect(r.x + 10f, y, r.width - 20f, rowH), "AI-first job priority — colonists wait for the LLM's decision instead of falling back to vanilla work first (off = blend mode)", ref s.AIFirstJobPriority);
            y += rowH + 8f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH), "Layer 1 server URL");
            string url = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH), s.Layer1ServerUrl);
            if (!string.IsNullOrEmpty(url) && url != s.Layer1ServerUrl)
            {
                s.Layer1ServerUrl = url;
            }
            y += rowH + 8f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH), "Layer 2 server URL (room social)");
            string url2 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH), s.Layer2ServerUrl);
            if (!string.IsNullOrEmpty(url2) && url2 != s.Layer2ServerUrl)
            {
                s.Layer2ServerUrl = url2;
            }
            y += rowH + 8f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH), "Layer 3 server URL (memory drift)");
            string url3 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH), s.Layer3ServerUrl);
            if (!string.IsNullOrEmpty(url3) && url3 != s.Layer3ServerUrl)
            {
                s.Layer3ServerUrl = url3;
            }
            y += rowH + 8f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH), "Request timeout (ms)");
            string tmo = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH), s.RequestTimeoutMs.ToString());
            if (int.TryParse(tmo, out int ms) && ms > 0 && ms != s.RequestTimeoutMs)
            {
                s.RequestTimeoutMs = ms;
            }

            // Phase 4 (Spec §9): model names + dual-clock real-time ceilings + VRAM presets. The Mod Settings dialog
            // is 900x700 with NO scroll (decompiled RimWorld.Dialog_ModSettings) — row heights below are budgeted to fit.
            y += rowH + 8f;

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 10f, y, r.width - 20f, 16f), "Model names — must match each llama-server's loaded model (or use a VRAM preset below)");
            y += 18f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH - 4f), "Layer 1 model (impulses)");
            string m1 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH - 4f), s.Layer1Model);
            if (!string.IsNullOrEmpty(m1) && m1 != s.Layer1Model) s.Layer1Model = m1;
            y += 35f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH - 4f), "Layer 2 model (room social)");
            string m2 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH - 4f), s.Layer2Model);
            if (!string.IsNullOrEmpty(m2) && m2 != s.Layer2Model) s.Layer2Model = m2;
            y += 35f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH - 4f), "Layer 3 model (memory drift)");
            string m3 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH - 4f), s.Layer3Model);
            if (!string.IsNullOrEmpty(m3) && m3 != s.Layer3Model) s.Layer3Model = m3;
            y += 35f;

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 10f, y, r.width - 20f, 16f), "Dual-Clock real-time ceilings (seconds) — cap GPU load at 3x/4x speed; game-tick gates stay as coded");
            y += 18f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH - 4f), "Layer 1 min real seconds");
            string c1 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH - 4f), s.Layer1MinRealSeconds.ToString("0.#"));
            if (float.TryParse(c1, out float sec1) && sec1 >= 5f && sec1 != s.Layer1MinRealSeconds) s.Layer1MinRealSeconds = sec1;
            y += 35f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH - 4f), "Layer 2 min real seconds");
            string c2 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH - 4f), s.Layer2MinRealSeconds.ToString("0.#"));
            if (float.TryParse(c2, out float sec2) && sec2 >= 5f && sec2 != s.Layer2MinRealSeconds) s.Layer2MinRealSeconds = sec2;
            y += 35f;

            Widgets.Label(new Rect(r.x + 10f, y, 300f, rowH - 4f), "Layer 3 min real seconds");
            string c3 = Widgets.TextField(new Rect(r.x + 320f, y, r.width - 340f, rowH - 4f), s.Layer3MinRealSeconds.ToString("0.#"));
            if (float.TryParse(c3, out float sec3) && sec3 >= 5f && sec3 != s.Layer3MinRealSeconds) s.Layer3MinRealSeconds = sec3;
            y += 35f;

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 10f, y, r.width - 20f, 16f), "VRAM preset — applies model names + timeout (server URLs stay yours)");
            y += 18f;

            float bx = r.x + 10f;
            foreach (VramPresets.Preset p in VramPresets.All)
            {
                if (Widgets.ButtonText(new Rect(bx, y, 200f, 30f), p.name))
                    VramPresets.Apply(p, s); // harness-pinned: models + timeout only, never URLs
                bx += 214f;
            }

            // Right end of the preset row: the dialog has no spare vertical room (see above), so results open in a popup.
            if (Widgets.ButtonText(new Rect(r.x + r.width - 190f, y, 180f, 30f), "Test providers"))
                Find.WindowStack.Add(new UI.Dialog_ProviderTest(s));
            y += 38f;

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 10f, y, r.width - 20f, rowH),
                "Keep the timeout at or above ~30000 ms while using CPU inference (a Layer 1 call takes ~5 s). Settings are written when this window closes.");
        }
    }
}
