using Verse;

namespace AIColonists.Core
{
    /// <summary>
    /// Mod settings (Spec §9 Phase 4): server URLs, model names, dual-clock intervals, sampling guardrails.
    /// </summary>
    public class Settings : ModSettings
    {
        // Master toggle — AI is OFF by default so the mod never breaks a save without opt-in.
        public bool EnableAI = false;

        // AI-first job priority (backlog #8, user end goal 2026-11-13: "disable much of the vanilla decisions to
        // prioritize the llm"). ON: while the pawn is waiting on an LLM decision (request in flight or cadence window
        // closed), our think-tree node holds it with a short wait job instead of returning null — which would let the
        // vanilla sorter BELOW us (GetFood/GetRest/Work) issue a job first. Player orders, drafting, and emergency
        // survival nodes sit ABOVE us in the tree and still win natively; an exhausted pawn (rest < 0.1) is never
        // held (vanilla sleep takes over). OFF: blend mode — vanilla fills the gaps between LLM decisions (old behavior).
        public bool AIFirstJobPriority = true;

        // llama-server endpoints (OpenAI-compatible /v1/chat/completions). One per layer, or all identical.
        // Ports 8091/8092 (NOT 8080/8081): the homelab node runs Open WebUI on 8080 — see server/README.md.
        public string Layer1ServerUrl = "http://127.0.0.1:8091";
        public string Layer2ServerUrl = "http://127.0.0.1:8091";
        public string Layer3ServerUrl = "http://127.0.0.1:8092";

        // Model names passed to llama-server (must match the loaded model / slot).
        public string Layer1Model = "qwen2.5-1.5b-instruct-q8_0";
        public string Layer2Model = "qwen2.5-3b-instruct-q4_k_m";
        public string Layer3Model = "qwen2.5-14b-instruct-q4_k_m";

        // Dual-Clock gate (Spec §3.3): BOTH conditions must be met before a call fires.
        // Game-tick deltas stop when paused; real-time deltas cap GPU load at 3x/4x speed.
        public int Layer1MinGameTicks = 1800;      // ~30s of game time
        public float Layer1MinRealSeconds = 30f;   // real-time ceiling regardless of game speed
        public int Layer2MinGameTicks = 5400;      // ~90s per multi-pawn room
        public float Layer2MinRealSeconds = 90f;
        public int Layer3MinGameTicks = 18000;     // ~5 min per colonist (staggered)
        public float Layer3MinRealSeconds = 300f;

        // Sampling guardrails (Spec §3.2): Mood maps to temperature inside [0.50, 0.85]; min_p clips garbage tails.
        public float BaseTemperature = 0.65f;
        public float MinP = 0.08f;

        // Output token budgets (Spec §3.1).
        public int Layer1MaxOutputTokens = 80;
        public int Layer2MaxOutputTokens = 150;
        public int Layer3MaxOutputTokens = 350;

        // Inference timeout — a hung server must never wedge the intent pipeline (Spec §9 Phase 1).
        public int RequestTimeoutMs = 3000;

        /// <summary>Settings page "Reset tuning to defaults": sampling, token budgets and dual-clock timing go back to the
        /// field initializers above. Switches, URLs, models and the timeout are left alone: they describe the player's
        /// server setup (presets own models + timeout), not tuning.</summary>
        public void ResetTuningToDefaults()
        {
            var d = new Settings();
            BaseTemperature = d.BaseTemperature;
            MinP = d.MinP;
            Layer1MaxOutputTokens = d.Layer1MaxOutputTokens;
            Layer2MaxOutputTokens = d.Layer2MaxOutputTokens;
            Layer3MaxOutputTokens = d.Layer3MaxOutputTokens;
            Layer1MinGameTicks = d.Layer1MinGameTicks;
            Layer1MinRealSeconds = d.Layer1MinRealSeconds;
            Layer2MinGameTicks = d.Layer2MinGameTicks;
            Layer2MinRealSeconds = d.Layer2MinRealSeconds;
            Layer3MinGameTicks = d.Layer3MinGameTicks;
            Layer3MinRealSeconds = d.Layer3MinRealSeconds;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EnableAI, "enableAI", false);
            Scribe_Values.Look(ref AIFirstJobPriority, "aiFirstJobPriority", true);
            Scribe_Values.Look(ref Layer1ServerUrl, "layer1ServerUrl", "http://127.0.0.1:8091");
            Scribe_Values.Look(ref Layer2ServerUrl, "layer2ServerUrl", "http://127.0.0.1:8091");
            Scribe_Values.Look(ref Layer3ServerUrl, "layer3ServerUrl", "http://127.0.0.1:8092");
            Scribe_Values.Look(ref Layer1Model, "layer1Model", "qwen2.5-1.5b-instruct-q8_0");
            Scribe_Values.Look(ref Layer2Model, "layer2Model", "qwen2.5-3b-instruct-q4_k_m");
            Scribe_Values.Look(ref Layer3Model, "layer3Model", "qwen2.5-14b-instruct-q4_k_m");
            Scribe_Values.Look(ref Layer1MinGameTicks, "layer1MinGameTicks", 1800);
            Scribe_Values.Look(ref Layer1MinRealSeconds, "layer1MinRealSeconds", 30f);
            Scribe_Values.Look(ref Layer2MinGameTicks, "layer2MinGameTicks", 5400);
            Scribe_Values.Look(ref Layer2MinRealSeconds, "layer2MinRealSeconds", 90f);
            Scribe_Values.Look(ref Layer3MinGameTicks, "layer3MinGameTicks", 18000);
            Scribe_Values.Look(ref Layer3MinRealSeconds, "layer3MinRealSeconds", 300f);
            Scribe_Values.Look(ref BaseTemperature, "baseTemperature", 0.65f);
            Scribe_Values.Look(ref MinP, "minP", 0.08f);
            Scribe_Values.Look(ref Layer1MaxOutputTokens, "layer1MaxOutputTokens", 80);
            Scribe_Values.Look(ref Layer2MaxOutputTokens, "layer2MaxOutputTokens", 150);
            Scribe_Values.Look(ref Layer3MaxOutputTokens, "layer3MaxOutputTokens", 350);
            Scribe_Values.Look(ref RequestTimeoutMs, "requestTimeoutMs", 3000);
        }
    }
}
