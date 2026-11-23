using System.Collections.Generic;
using System.Text;

namespace AIColonists.Prompts
{
    /// <summary>
    /// Assembles Layer 1 prompts in strict two-zone order (Spec §2.3, §7.1): static identity prefix FIRST
    /// (KV-cache shared across calls via llama-server --cache-reuse / Ollama's automatic prefix caching), dynamic
    /// experience suffix LAST. This ordering is what makes the ~70% prefill-compute reduction real (Spec §8.2).
    /// Phase 2: the identity zone is now REAL per-pawn data (name, age, colony, traits, skills, memories, drift-fed
    /// values/fears) instead of placeholder text — two colonists no longer share one generic "a colonist" prefix.
    /// </summary>
    public static class PrefixPromptBuilder
    {
        // Spec §7.1 — system message = static prefix zone. [Brackets] are placeholders.
        // 2026-11-22 persona rework (live review of Inkling 2 mini outputs): every pawn spoke in the same gentle, literary,
        // therapy-flavored voice, ignored the length rule, and titles leaked in as fake people. The prefix now carries the
        // game's own backstory prose, the full title, per-trait voice lines and an explicit plain-speech rule. Engineering
        // labels ("STATIC PREFIX - KV CACHED") are gone from the text the model reads; the static/dynamic split is unchanged.
        private const string SystemTemplate = @"You are [Name], [Age].[TitleClause] You live in the settlement of [ColonyName].
This is your real life. You know nothing of games, stats, or simulations.

## YOUR PAST
[StoryLines]

## WHO YOU ARE
[WhoLines]

## HOW YOU THINK AND TALK
[VoiceLines]
- [Cadence]
- Your thought is one or two short sentences, under 30 words, in your own words: plain and specific, the way someone with your past really talks. No poetic or therapy-style language.
[TemperamentBlock]
## WHAT YOU'VE COME TO BELIEVE
[MemoryLines]

Capitalized names are people: someone called Cook is a person, not cooking.";

        private const string UserTemplate = @"## ON YOUR MIND
[EpisodicLines]
[RecentThoughtsBlock]
## PEOPLE NEARBY
[NearbyPawnLines]

## YOUR BODY & SURROUNDINGS
[BodyAndSurroundingsLines]
[IntellectGatedColonyHorizon]

## WHAT YOU COULD DO RIGHT NOW
[AffordanceList]

Give your fleeting inner thought[FreshClause], anything you mutter aloud, and the number of what you do.";

        public static string BuildSystemPrompt(SensorySnapshot snapshot)
        {
            string s = SystemTemplate;

            s = s.Replace("[Name]", string.IsNullOrEmpty(snapshot.PawnName) ? "a colonist" : snapshot.PawnName);
            s = s.Replace("[Age]", snapshot.AgeYears > 0 ? "age " + snapshot.AgeYears : "of uncertain age");
            s = s.Replace("[TitleClause]", string.IsNullOrEmpty(snapshot.Title) ? "" : " People know you as " + PawnText.WithArticle(snapshot.Title) + ".");
            s = s.Replace("[ColonyName]", string.IsNullOrEmpty(snapshot.ColonyName) ? "your settlement" : snapshot.ColonyName);

            var story = new StringBuilder();
            if (!string.IsNullOrEmpty(snapshot.ChildhoodStory)) story.Append("Growing up: ").Append(snapshot.ChildhoodStory).Append('\n');
            if (!string.IsNullOrEmpty(snapshot.AdulthoodStory)) story.Append("Since then: ").Append(snapshot.AdulthoodStory).Append('\n');
            s = s.Replace("[StoryLines]", story.Length > 0 ? story.ToString().TrimEnd() : "You don't dwell on where you came from.");

            var who = new StringBuilder();
            if (!string.IsNullOrEmpty(snapshot.TraitsLine)) who.Append(snapshot.TraitsLine).Append('\n');
            if (!string.IsNullOrEmpty(snapshot.SkillsLine)) who.Append(snapshot.SkillsLine);
            s = s.Replace("[WhoLines]", who.Length > 0 ? who.ToString().TrimEnd() : "You are a practical, self-reliant person.");

            var voice = new StringBuilder();
            if (snapshot.VoiceLines != null)
                foreach (string v in snapshot.VoiceLines) voice.Append("- ").Append(v).Append('\n');
            s = s.Replace("[VoiceLines]\n", voice.ToString());
            s = s.Replace("[Cadence]", CadenceRuleFor(snapshot.IntellectualLevel));

            // Continuous trait weights (Layer 3 drift) — shown only once they've actually moved past the thresholds.
            var temperament = new StringBuilder();
            if (snapshot.ValueLines != null && snapshot.ValueLines.Count > 0)
                temperament.Append("- Lately you've been more and more: ").Append(string.Join(", ", snapshot.ValueLines)).Append('\n');
            if (snapshot.AversionLines != null && snapshot.AversionLines.Count > 0)
                temperament.Append("- Less and less: ").Append(string.Join(", ", snapshot.AversionLines)).Append('\n');
            s = s.Replace("[TemperamentBlock]", temperament.Length > 0 ? "\n## LATELY\n" + temperament.ToString() : "");

            var mem = new StringBuilder();
            if (snapshot.SemanticMemoryLines != null)
                foreach (string line in snapshot.SemanticMemoryLines) mem.Append("- ").Append(line).Append('\n');
            if (!string.IsNullOrEmpty(snapshot.LongTermLonging))
                mem.Append("- Deep down, you keep wanting ").Append(snapshot.LongTermLonging).Append(".\n");
            s = s.Replace("[MemoryLines]", mem.Length > 0 ? mem.ToString().TrimEnd() : "- Nothing settled yet. Your days here are still new.");

            return s;
        }

        public static string BuildUserPrompt(SensorySnapshot snap)
        {
            string u = UserTemplate;
            u = u.Replace("[EpisodicLines]", JoinOrDash(snap.EpisodicLines, "nothing much"));
            u = u.Replace("[RecentThoughtsBlock]", RecentThoughtsBlock(snap.RecentThoughts));
            // Live 2026-11-22: with the rule only in the block, Inkling 2 mini still repeated a thought verbatim; restating it
            // in the final instruction (the last thing the model reads) is what made it stick.
            bool hasRecent = snap.RecentThoughts != null && snap.RecentThoughts.Count > 0;
            u = u.Replace("[FreshClause]", hasRecent ? " (new words, not what you were just thinking)" : "");
            u = u.Replace("[NearbyPawnLines]", JoinOrDash(snap.NearbyPawnLines, "no one"));
            u = u.Replace("[BodyAndSurroundingsLines]", JoinOrDash(snap.BodyAndSurroundingsLines, "nothing unusual"));
            u = u.Replace("[IntellectGatedColonyHorizon]", string.IsNullOrEmpty(snap.ColonyHorizonLine) ? "" : snap.ColonyHorizonLine);

            var sb = new StringBuilder();
            if (snap.Affordances != null)
            {
                for (int i = 0; i < snap.Affordances.Count; i++)
                    sb.Append("[").Append(i + 1).Append("] ").Append(snap.Affordances[i].sensoryDescription).Append("\n");
            }
            u = u.Replace("[AffordanceList]", sb.ToString().TrimEnd());

            return u;
        }

        public static string CadenceRuleFor(int intellectLevel)
        {
            if (intellectLevel < 0) return "You think in plain, everyday words."; // unknown — neutral default
            if (intellectLevel < 5) return "You think in short, blunt bursts and act on the first impulse that feels right.";
            if (intellectLevel < 10) return "You think in plain, everyday words and notice practical details.";
            return "You think things through and weigh consequences, but still in your own plain words.";
        }

        /// <summary>Colony-horizon awareness by Intellectual tier: low intellect lives in the immediate moment, high
        /// intellect keeps track of the settlement's wider state (Spec §3.2 horizon table).</summary>
        public static string ColonyHorizonFor(int intellectLevel)
        {
            if (intellectLevel < 0 || intellectLevel < 5) return ""; // no horizon — only immediate urges
            if (intellectLevel < 10) return "You keep a loose sense of how the settlement is doing today.";
            return "You track the settlement's wider state — who is hurt, what is stored, whether danger has passed.";
        }

        /// <summary>2026-11-22 (live: one pawn thought "Stomach's screaming…" call after call): the last thoughts go in the
        /// dynamic zone with an explicit don't-repeat rule. Sampling alone can't fix this — each call is independent.</summary>
        private static string RecentThoughtsBlock(List<string> thoughts)
        {
            if (thoughts == null || thoughts.Count == 0) return "";
            var sb = new StringBuilder("\n## WHAT YOU WERE JUST THINKING\n");
            foreach (string t in thoughts) sb.Append("- \"").Append(t).Append("\"\n");
            sb.Append("Your next thought is new: don't repeat or reword these. If nothing has changed, notice something else: a smell, a person, a worry, a plan.\n");
            return sb.ToString();
        }

        private static string JoinOrDash(List<string> lines, string empty)
        {
            if (lines == null || lines.Count == 0) return "- " + empty;
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
                sb.Append("- ").Append(lines[i]).Append("\n");
            return sb.ToString().TrimEnd();
        }
    }
}
