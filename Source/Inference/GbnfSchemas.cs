using System.Text;

namespace AIColonists.Inference
{
    /// <summary>
    /// Strict JSON schemas for server-side structured output (Spec §7.2–§7.4). Passed via response_format so the
    /// sampler only emits schema-valid tokens — hallucinated action strings or malformed JSON are impossible at the
    /// token level, which is what makes the "Unaware" roleplay constraint safe to enforce (Spec §2.2).
    /// </summary>
    public static class GbnfSchemas
    {
        // Spec §7.2 — impulse_id enum must match the actual affordance count for this call (1..N).
        // PROPERTY ORDER MATTERS: GBNF generation follows schema property order, and a max_tokens cut stops
        // mid-object. Live-verified on the homelab llama.cpp Nov 9 build: with inner_voice first, the 1.5B model
        // spent all 80 tokens on verbose prose and was truncated BEFORE impulse_id (the decision was lost).
        // impulse_id first (~4 tokens) guarantees the decision always survives; prose fields are best-effort.
        private const string Layer1JsonSchemaTemplate = @"{
  ""type"": ""object"",
  ""properties"": {
    ""impulse_id"": {
      ""type"": ""integer"",
      ""enum"": [{IMPULSE_ENUM}]
    },
    ""inner_voice"": {
      ""type"": ""string"",
      ""description"": ""First-person thought in your own plain voice: one or two short sentences, under 30 words, zero mechanical terms.""
    },
    ""spoken_mutter"": {
      ""type"": ""string"",
      ""description"": ""Optional short phrase muttered aloud (or empty string if silent).""
    }
  },
  ""required"": [""impulse_id"", ""inner_voice"", ""spoken_mutter""],
  ""additionalProperties"": false
}";

        /// <summary>Builds the Layer 1 schema with an impulse enum matching the pawn's actual option count.</summary>
        public static string BuildLayer1Schema(int optionCount)
        {
            var sb = new StringBuilder();
            for (int i = 1; i <= optionCount; i++)
            {
                if (i > 1) sb.Append(", ");
                sb.Append(i);
            }
            return Layer1JsonSchemaTemplate.Replace("{IMPULSE_ENUM}", sb.ToString());
        }

        // Spec §7.3 — Layer 2 room social & emotional shift (batched, one decision per colonist in the room).
        public const string Layer2JsonSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""decisions"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""colonist_name"": { ""type"": ""string"" },
          ""inner_feeling"": { ""type"": ""string"" },
          ""social_impulse"": { ""type"": ""string"", ""enum"": [""none"", ""warm_chat"", ""vent_frustration"", ""sharp_remark"", ""quiet_nod"", ""avoid_gaze""] },
          ""target_name"": { ""type"": ""string"" },
          ""spoken_line"": { ""type"": ""string"" }
        },
        ""required"": [""colonist_name"", ""inner_feeling"", ""social_impulse"", ""target_name"", ""spoken_line""]
      }
    }
  },
  ""required"": [""decisions""]
}";

        // Spec §7.4 — Layer 3 memory consolidation & trait drift (±0.02 max per cycle).
        public const string Layer3JsonSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""self_reflection"": { ""type"": ""string"" },
    ""new_semantic_memory"": { ""type"": ""string"" },
    ""relationship_deltas"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""target_name"": { ""type"": ""string"" },
          ""trust_delta"": { ""type"": ""number"", ""minimum"": -0.05, ""maximum"": 0.05 },
          ""affection_delta"": { ""type"": ""number"", ""minimum"": -0.05, ""maximum"": 0.05 },
          ""respect_delta"": { ""type"": ""number"", ""minimum"": -0.05, ""maximum"": 0.05 },
          ""frustration_delta"": { ""type"": ""number"", ""minimum"": -0.05, ""maximum"": 0.05 }
        },
        ""required"": [""target_name"", ""trust_delta"", ""affection_delta"", ""respect_delta"", ""frustration_delta""]
      }
    },
    ""trait_drift"": {
      ""type"": ""object"",
      ""properties"": {
        ""trait_name"": { ""type"": ""string"" },
        ""delta"": { ""type"": ""number"", ""minimum"": -0.02, ""maximum"": 0.02 }
      },
      ""required"": [""trait_name"", ""delta""]
    },
    ""long_term_longing"": { ""type"": ""string"" }
  },
  ""required"": [""self_reflection"", ""new_semantic_memory"", ""relationship_deltas"", ""trait_drift"", ""long_term_longing""]
}";
    }
}
