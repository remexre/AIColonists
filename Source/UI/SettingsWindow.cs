using System.Collections.Generic;
using AIColonists.Core;
using UnityEngine;
using Verse;

namespace AIColonists.UI
{
    /// <summary>
    /// Mod settings page (Dialog_ModSettings body). Every Settings field has a control here. The dialog is fixed-size
    /// (900x700, decompiled RimWorld.Dialog_ModSettings) with no scrolling of its own, so the page scrolls: content
    /// height is measured while drawing and fed to the next frame's view rect.
    /// Numeric fields use vanilla's buffered Widgets.TextFieldNumeric: a plain TextField re-rendered from the parsed value
    /// every frame makes in-between states like "0." untypeable. Buffers are keyed per field and dropped whenever
    /// values change from outside the field (presets, reset) so the fields re-read them.
    /// </summary>
    public static class SettingsWindow
    {
        private const float RowH = 30f;
        private const float RowGap = 4f;
        private const float LabelW = 340f;
        private const float NumberW = 110f;

        private static readonly Settings Defaults = new Settings();
        private static readonly Dictionary<string, string> buffers = new Dictionary<string, string>();
        private static Vector2 scroll;
        private static float contentHeight = 1400f; // first-frame guess; corrected after every draw

        /// <summary>Height the page measured on its last draw (harness hook: proves the page needs scrolling).</summary>
        public static float ContentHeight => contentHeight;

        public static void Draw(Rect inRect, Settings s)
        {
            if (s == null) return;
            Rect view = new Rect(0f, 0f, inRect.width - 20f, contentHeight); // 20 = room for the scrollbar
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var page = new Page(view.width);

            page.Header("General");
            page.Checkbox("Enable AI", "Master switch. Off means the mod is inert: no requests, vanilla behavior.", ref s.EnableAI, Defaults.EnableAI);
            page.Checkbox("AI-first job priority",
                "On: while an LLM decision is in flight, colonists wait for it instead of letting vanilla work run first. "
                + "Player orders, drafting and emergencies still win; an exhausted colonist is never held. Off: blend mode, vanilla fills the gaps.",
                ref s.AIFirstJobPriority, Defaults.AIFirstJobPriority);
            if (page.Button("Test providers", "Sends one small request to each layer's server below and shows what came back."))
                Find.WindowStack.Add(new Dialog_ProviderTest(s));

            page.Header("Servers & models");
            page.Note("Any OpenAI-compatible /v1/chat/completions server (llama-server, Ollama, an OpenRouter proxy...). Layers can share one server.");
            page.TextEntry("Layer 1 URL (impulses)", "Per-colonist moment-to-moment decisions. The busiest layer.", ref s.Layer1ServerUrl, Defaults.Layer1ServerUrl);
            page.TextEntry("Layer 1 model", "Must match a model the server has loaded or can route to.", ref s.Layer1Model, Defaults.Layer1Model);
            page.TextEntry("Layer 2 URL (room social)", "One batched call per room with several colonists in it.", ref s.Layer2ServerUrl, Defaults.Layer2ServerUrl);
            page.TextEntry("Layer 2 model", "Must match a model the server has loaded or can route to.", ref s.Layer2Model, Defaults.Layer2Model);
            page.TextEntry("Layer 3 URL (memory drift)", "Occasional per-colonist memory consolidation and trait drift. Benefits from a bigger model.", ref s.Layer3ServerUrl, Defaults.Layer3ServerUrl);
            page.TextEntry("Layer 3 model", "Must match a model the server has loaded or can route to.", ref s.Layer3Model, Defaults.Layer3Model);
            page.Note("VRAM presets set the three model names and the request timeout. Your URLs stay as they are.");
            int preset = page.ButtonRow(VramPresets.All.ConvertAll(p => p.name));
            if (preset >= 0)
            {
                VramPresets.Apply(VramPresets.All[preset], s);
                buffers.Clear();
            }

            page.Header("Requests");
            page.Int("Request timeout (ms)",
                "How long a real request may take before it's abandoned. CPU inference needs ~30000; a GPU or hosted model is usually fine with a few thousand.",
                "timeout", ref s.RequestTimeoutMs, 500, 600000, Defaults.RequestTimeoutMs);
            page.Float("Base temperature",
                "Sampling temperature for all layers. Layer 1 then adds a mood offset and clamps the result to 0.50-0.85.",
                "temp", ref s.BaseTemperature, 0f, 2f, Defaults.BaseTemperature);
            page.Float("min_p", "Drops unlikely tokens even at higher temperatures. 0 disables it.", "minp", ref s.MinP, 0f, 1f, Defaults.MinP);
            const string tokensTip = "Most tokens the model may produce per request. Reasoning models count their thinking here too, so they need much more (Test providers shows how much they use).";
            page.Int("Layer 1 max output tokens", tokensTip, "tok1", ref s.Layer1MaxOutputTokens, 16, 32768, Defaults.Layer1MaxOutputTokens);
            page.Int("Layer 2 max output tokens", tokensTip, "tok2", ref s.Layer2MaxOutputTokens, 16, 32768, Defaults.Layer2MaxOutputTokens);
            page.Int("Layer 3 max output tokens", tokensTip, "tok3", ref s.Layer3MaxOutputTokens, 16, 32768, Defaults.Layer3MaxOutputTokens);

            page.Header("Timing");
            page.Note("A layer only fires once BOTH gates have passed: game time (stops while paused) and real time (caps load at 3x/4x speed).");
            const string ticksTip = "Minimum game time between calls. 60 ticks = 1 second at 1x speed.";
            const string realTip = "Minimum real (wall-clock) time between calls, whatever the game speed.";
            page.Int("Layer 1 min game ticks", ticksTip, "gt1", ref s.Layer1MinGameTicks, 60, 600000, Defaults.Layer1MinGameTicks, TicksHint);
            page.Float("Layer 1 min real seconds", realTip, "rs1", ref s.Layer1MinRealSeconds, 5f, 3600f, Defaults.Layer1MinRealSeconds);
            page.Int("Layer 2 min game ticks", ticksTip, "gt2", ref s.Layer2MinGameTicks, 60, 600000, Defaults.Layer2MinGameTicks, TicksHint);
            page.Float("Layer 2 min real seconds", realTip, "rs2", ref s.Layer2MinRealSeconds, 5f, 3600f, Defaults.Layer2MinRealSeconds);
            page.Int("Layer 3 min game ticks", ticksTip, "gt3", ref s.Layer3MinGameTicks, 60, 600000, Defaults.Layer3MinGameTicks, TicksHint);
            page.Float("Layer 3 min real seconds", realTip, "rs3", ref s.Layer3MinRealSeconds, 5f, 3600f, Defaults.Layer3MinRealSeconds);

            page.Gap(12f);
            if (page.Button("Reset tuning to defaults", "Resets temperature, min_p, token budgets and timing. Keeps the switches, servers, models and timeout."))
            {
                s.ResetTuningToDefaults();
                buffers.Clear();
            }
            page.Note("Settings are saved when this window closes.");

            contentHeight = page.y + 8f;
            Widgets.EndScrollView();
        }

        private static string TicksHint(int ticks) => "~" + (ticks / 60f).ToString("0.#") + " s of game time at 1x";

        private static string Buffer(string key)
        {
            buffers.TryGetValue(key, out string b);
            return b; // null makes TextFieldNumeric initialize from the current value
        }

        /// <summary>Top-to-bottom row layout inside the scroll view.</summary>
        private class Page
        {
            public float y;
            private readonly float width;

            public Page(float width) { this.width = width; }

            public void Gap(float h) { y += h; }

            public void Header(string text)
            {
                if (y > 0f) y += 10f;
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, y, width, 34f), text);
                Text.Font = GameFont.Small;
                y += 34f;
                Widgets.DrawLineHorizontal(0f, y, width);
                y += 6f;
            }

            public void Note(string text)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                float h = Text.CalcHeight(text, width);
                Widgets.Label(new Rect(0f, y, width, h), text);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += h + RowGap;
            }

            private Rect Row(string tip)
            {
                Rect r = new Rect(0f, y, width, RowH);
                Widgets.DrawHighlightIfMouseover(r);
                TooltipHandler.TipRegion(r, tip);
                y += RowH + RowGap;
                return r;
            }

            private static string WithDefault(string tip, string def) => tip + "\n\nDefault: " + def;

            public void Checkbox(string label, string tip, ref bool value, bool def)
            {
                Rect r = Row(WithDefault(tip, def ? "on" : "off"));
                Widgets.CheckboxLabeled(r, label, ref value);
            }

            public bool Button(string label, string tip)
            {
                Rect r = Row(tip);
                return Widgets.ButtonText(new Rect(r.x, r.y, 240f, r.height), label);
            }

            /// <summary>One button per label in a row; returns the clicked index, or -1.</summary>
            public int ButtonRow(List<string> labels)
            {
                int clicked = -1;
                float x = 0f;
                for (int i = 0; i < labels.Count; i++)
                {
                    if (Widgets.ButtonText(new Rect(x, y, 200f, RowH), labels[i])) clicked = i;
                    x += 210f;
                }
                y += RowH + RowGap;
                return clicked;
            }

            public void TextEntry(string label, string tip, ref string value, string def)
            {
                Rect r = Row(WithDefault(tip, def));
                Widgets.Label(new Rect(r.x, r.y + 4f, LabelW, r.height), label);
                string edited = Widgets.TextField(new Rect(r.x + LabelW, r.y, r.width - LabelW, r.height), value ?? "");
                if (!string.IsNullOrEmpty(edited)) value = edited; // an empty URL/model is never useful; keep the last one
            }

            public void Int(string label, string tip, string key, ref int value, int min, int max, int def, System.Func<int, string> hint = null)
            {
                Rect r = Row(WithDefault(tip, def.ToString()) + "\nRange: " + min + " to " + max);
                Widgets.Label(new Rect(r.x, r.y + 4f, LabelW, r.height), label);
                string buf = Buffer(key);
                Widgets.TextFieldNumeric(new Rect(r.x + LabelW, r.y, NumberW, r.height), ref value, ref buf, min, max);
                buffers[key] = buf;
                if (hint != null) Hint(r, hint(value));
            }

            public void Float(string label, string tip, string key, ref float value, float min, float max, float def)
            {
                Rect r = Row(WithDefault(tip, def.ToString("0.###")) + "\nRange: " + min + " to " + max);
                Widgets.Label(new Rect(r.x, r.y + 4f, LabelW, r.height), label);
                string buf = Buffer(key);
                Widgets.TextFieldNumeric(new Rect(r.x + LabelW, r.y, NumberW, r.height), ref value, ref buf, min, max);
                buffers[key] = buf;
            }

            private void Hint(Rect r, string text)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(new Rect(r.x + LabelW + NumberW + 10f, r.y + 7f, r.width - LabelW - NumberW - 10f, r.height), text);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
        }
    }
}
