using System.Collections.Generic;
using System.Text;
using Verse;

namespace AIColonists.Prompts
{
    /// <summary>
    /// Assembles Layer 2 prompts in strict two-zone order (Spec §7.3): a BYTE-IDENTICAL group-simulation system prefix
    /// FIRST (pure KV reuse across every room batch via llama-server --cache-reuse), dynamic roster LAST. One call covers
    /// the whole shared space, so the model sees everyone at once and decides who talks to whom — max 2 actors per cycle
    /// keeps letters sparse, and "none" is always the preferred answer (most moments are quiet).
    /// </summary>
    public static class Layer2PromptBuilder
    {
        // Spec §7.3 — system message = static prefix zone. Never varies between calls: that is what makes the KV cache hit.
        private const string SystemTemplate = @"You are the social fabric of a small group of people sharing one room or outdoor space.
Describe only what they would plausibly do and say RIGHT NOW, as physical reality. You have no concept of games, stats, or simulations.

## RULES
- Most moments are quiet. Prefer ""none"" — only act when the situation genuinely calls for it.
- At most TWO people may take an action this moment; everyone else is ""none"".
- Each acting person chooses exactly one social_impulse toward one OTHER person in the room:
  - warm_chat: a friendly, easy conversation (light topics, comfort)
  - vent_frustration: complaining or grumbling about something bothering them
  - sharp_remark: a cutting or sarcastic comment directed at the other person
  - quiet_nod: a small silent acknowledgment (no words)
  - avoid_gaze: deliberately looking away / giving space (no words)
  - none: does nothing social this moment
- spoken_line is what they say ALOUD, at most 10 words, in plain everyday speech that fits who they are (their background is in parentheses). No poetic phrasing. Empty string for quiet_nod, avoid_gaze and none.
- inner_feeling is one short first-person sentence about why the acting person feels like this (empty string when none).
- colonist_name and target_name are the name BEFORE the parentheses, exactly as given (""Paulo"", not ""Paulo (dreaded dude)"").
- target_name must be ANOTHER colonist's roster name — NEVER the acting colonist's own name (no one can socially act toward themselves); empty string when social_impulse is none.
- Never invent people who are not in the room. Never mention stats, numbers, moods as percentages, or anything mechanical.";

        /// <summary>The static system zone — byte-identical for every Layer 2 call (KV-cache hot).</summary>
        public static string BuildSystemPrompt() => SystemTemplate;

        /// <summary>Dynamic zone: one line per pawn — name, felt state in prose words (never percentages), relationship color.</summary>
        public static string BuildUserPrompt(IList<Pawn> pawns, Dictionary<string, string> notes)
        {
            var sb = new StringBuilder(512);
            sb.Append("# WHO IS HERE\n");
            foreach (Pawn pawn in pawns)
            {
                if (pawn == null || pawn.needs == null) continue;

                // Short name + full title in parentheses (2026-11-22): LabelCap ("Paulo<color>, Dude</color>") put markup in the
                // roster and read as two people. FindColonistByName matches LabelShort, so the replies still resolve.
                string name = PawnText.ShortName(pawn);
                if (string.IsNullOrEmpty(name)) continue;
                string title = PawnText.Title(pawn);
                sb.Append("- ").Append(name);
                if (!string.IsNullOrEmpty(title)) sb.Append(" (").Append(title).Append(')');
                sb.Append(": ");
                AppendNeedProse(sb, pawn);

                string note;
                if (notes != null && notes.TryGetValue(pawn.ThingID, out note) && !string.IsNullOrEmpty(note))
                    sb.Append(' ').Append(note);
                sb.Append('\n');
            }
            sb.Append("\nDecide what happens socially in this space right now.\n");
            return sb.ToString();
        }

        /// <summary>Zero-mechanic need prose (Spec §2.2 Unaware constraint): words, never percentages.</summary>
        private static void AppendNeedProse(StringBuilder sb, Pawn pawn)
        {
            float mood = pawn.needs.mood != null ? pawn.needs.mood.CurLevelPercentage : 0.5f;
            if (mood < 0.35f) sb.Append("in a bad mood");
            else if (mood > 0.75f) sb.Append("in good spirits");
            else sb.Append("feeling okay");

            float food = pawn.needs.food != null ? pawn.needs.food.CurLevelPercentage : 1f;
            if (food < 0.35f) sb.Append(", hungry");
            else if (food > 0.85f) sb.Append(", well fed");

            float rest = pawn.needs.rest != null ? pawn.needs.rest.CurLevelPercentage : 1f;
            if (rest < 0.35f) sb.Append(", tired");
        }
    }
}
