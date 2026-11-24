using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AIColonists.Inference
{
    /// <summary>
    /// Async boundary between the mod and local inference (Spec §9 Phase 1).
    /// Implementations must NEVER block the Unity main thread — only ever be awaited from worker threads.
    /// </summary>
    public interface ILlamaBridge
    {
        Task<Layer1Result> RequestLayer1Async(Layer1Request request);

        /// <summary>Batched room social call — one decision per colonist in a shared space (Spec §7.3).</summary>
        Task<Layer2Result> RequestLayer2Async(Layer2Request request);

        /// <summary>Per-colonist memory consolidation + trait drift (Spec §7.4).</summary>
        Task<Layer3Result> RequestLayer3Async(Layer3Request request);

        /// <summary>
        /// One-shot cold-start warmup (2026-11-15, Ollama field finding — TEST-RESULTS.md §22.7): fires a minimal
        /// request so the server loads the model in the background BEFORE real dispatches start. Uses a long timeout
        /// by design: cancelling a cold call mid-load makes Ollama ABORT the load (empirically verified), so without
        /// this, every short-timeout dispatch at game start would start and cancel its own model load forever.
        /// Returns true when the server answered (model resident); false on unreachable/failed (silent no-op case).
        /// </summary>
        Task<bool> WarmUpAsync();
    }

    /// <summary>Outcome of one "Test providers" probe (LlamaServerClient.TestProviderAsync). Immutable, so the settings
    /// dialog can read it from the UI thread while the probe runs on a worker thread.</summary>
    public sealed class ProviderTestResult
    {
        public readonly bool ok;
        public readonly long latencyMs;
        public readonly string message;      // the model's reply when ok, otherwise what went wrong
        public readonly int reasoningTokens; // tokens the model spent thinking (0 = none, or not reported)
        public readonly int maxTokens;       // the output budget the probe ran with (0 = unknown)

        private ProviderTestResult(bool ok, long latencyMs, string message, int reasoningTokens, int maxTokens)
        {
            this.ok = ok;
            this.latencyMs = latencyMs;
            this.message = message ?? "";
            this.reasoningTokens = reasoningTokens;
            this.maxTokens = maxTokens;
        }

        public static ProviderTestResult Ok(long latencyMs, string reply, int reasoningTokens = 0, int maxTokens = 0) =>
            new ProviderTestResult(true, latencyMs, reply, reasoningTokens, maxTokens);
        public static ProviderTestResult Fail(long latencyMs, string error, int reasoningTokens = 0, int maxTokens = 0) =>
            new ProviderTestResult(false, latencyMs, error, reasoningTokens, maxTokens);
    }

    /// <summary>Everything a Layer 1 call needs, captured on the main thread (Spec §5.1 / §7.1).</summary>
    public class Layer1Request
    {
        public string model;         // e.g., "qwen2.5-1.5b-instruct-q8_0"
        public string systemPrompt;  // static identity prefix (KV-cache friendly, Spec §2.3)
        public string userPrompt;    // dynamic suffix + numbered sensory affordances
        public float temperature;    // base + mood offset (Spec §3.2), clamped to [0, 2]
        public float minP = 0.05f;   // guardrail: clips garbage tail tokens even when agitated
        public float topP = 1.0f;    // off: min_p trims the tail (2026-11-22; was mood-mapped 0.80-0.95)
        public int maxTokens = 80;   // Spec §3.1 Layer 1 budget
        public int optionCount = 0;  // number of affordances — sizes the impulse_id enum (Spec §7.2)
        public bool disableReasoning; // sends reasoning_effort:"none" (Settings.DisableReasoning)
        public string logLabel;      // not sent: names the call in the prompt log (PromptLog)
    }

    /// <summary>Parsed + validated Layer 1 output (matches GbnfSchemas.Layer1, Spec §7.2).</summary>
    /// FIELD NAMES ARE SNAKE_CASE ON PURPOSE: RimWorld's JsonUtility matches field names exactly and
    /// case-sensitively (no attribute support), and the server emits inner_voice / spoken_mutter / impulse_id.
    [Serializable]
    public class Layer1Result
    {
        public string inner_voice;   // first-person, zero mechanical terms ("Unaware" constraint, Spec §2.2)
        public string spoken_mutter; // optional phrase muttered aloud, may be empty
        public int impulse_id;       // 1..N — must index an AffordanceOption or the desire is discarded
    }

    /// <summary>Everything a Layer 2 call needs (Spec §7.3). One call covers every colonist in a shared space.</summary>
    public class Layer2Request
    {
        public string model;         // e.g., "qwen2.5-3b-instruct-q4_k_m" — ignored by single-model servers
        public string systemPrompt;  // static group-simulation prefix (byte-identical across calls → pure KV reuse)
        public string userPrompt;    // dynamic roster: names, moods, relationship color
        public float temperature = 1.0f;
        public float minP = 0.05f;
        public float topP = 1.0f;    // off: min_p trims the tail
        public int maxTokens = 150;  // Spec §3.1 Layer 2 budget (~120 tokens typical)
        public bool disableReasoning; // sends reasoning_effort:"none" (Settings.DisableReasoning)
        public string logLabel;      // not sent: names the call in the prompt log (PromptLog)
    }

    /// <summary>One decision for one colonist (Spec §7.3). FIELD NAMES ARE SNAKE_CASE ON PURPOSE — the server emits them verbatim.</summary>
    [Serializable]
    public class SocialDecision
    {
        public string colonist_name;   // must match a roster name or the decision is discarded at consume time
        public string inner_feeling;   // first-person feeling behind the impulse (episodic-memory fodder)
        public string social_impulse;  // none | warm_chat | vent_frustration | sharp_remark | quiet_nod | avoid_gaze
        public string target_name;     // roster name of the other colonist, empty for "none"
        public string spoken_line;     // what the initiator says aloud (becomes the letter text), may be empty
    }

    /// <summary>Parsed + validated Layer 2 output (matches GbnfSchemas.Layer2JsonSchema, Spec §7.3).</summary>
    [Serializable]
    public class Layer2Result
    {
        public List<SocialDecision> decisions = new List<SocialDecision>();
    }

    /// <summary>Everything a Layer 3 call needs (Spec §7.4).</summary>
    public class Layer3Request
    {
        public string model;         // e.g., "qwen2.5-14b-instruct-q4_k_m" — ignored by single-model servers
        public string systemPrompt;  // static per-colonist subconscious prefix (KV-cached per pawn)
        public string userPrompt;    // dynamic: raw episodic memories + current self-knowledge
        public float temperature = 1.0f;
        public float minP = 0.05f;
        public float topP = 1.0f;    // off: min_p trims the tail
        public int maxTokens = 350;  // Spec §3.1 Layer 3 budget (~250 tokens typical)
        public bool disableReasoning; // sends reasoning_effort:"none" (Settings.DisableReasoning)
        public string logLabel;      // not sent: names the call in the prompt log (PromptLog)
    }

    /// <summary>One relationship nudge (Spec §7.4). Server values are clamped to ±0.05 at apply time.</summary>
    [Serializable]
    public class RelationshipDelta
    {
        public string target_name;
        public float trust_delta;
        public float affection_delta;
        public float respect_delta;
        public float frustration_delta;
    }

    /// <summary>Continuous trait drift (Spec §7.4). Clamped to ±0.02 at apply time.</summary>
    [Serializable]
    public class TraitDrift
    {
        public string trait_name = "";
        public float delta;
    }

    /// <summary>Parsed + validated Layer 3 output (matches GbnfSchemas.Layer3JsonSchema, Spec §7.4).</summary>
    [Serializable]
    public class Layer3Result
    {
        public string self_reflection = "";
        public string new_semantic_memory = "";
        public List<RelationshipDelta> relationship_deltas = new List<RelationshipDelta>();
        public TraitDrift trait_drift;   // null when nothing usable was extracted (truncation-tolerant parse)
        public string long_term_longing = "";
    }
}
