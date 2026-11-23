using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace AIColonists.Prompts
{
    /// <summary>
    /// Prompt-facing text about pawns: names, titles, backstories, voice. Main thread only (reads pawn state); every
    /// accessor is guarded because pawn labels can throw on half-loaded pawns (LabelCap precedent, 2026-11-14).
    /// </summary>
    public static class PawnText
    {
        /// <summary>
        /// The name to use inside prompts: LabelShort ("Albina"). NEVER LabelCap — in 1.6/1.7 that is
        /// "Albina&lt;color=#999999FF&gt;, Forester&lt;/color&gt;" (name + short backstory title + rich-text tags), and the model
        /// read the title as a second person (live 2026-11-22: "Albina and Forester are nearby"). Null when unreadable.
        /// </summary>
        public static string ShortName(Pawn pawn)
        {
            if (pawn == null) return null;
            try { return StripTags(pawn.LabelShort); }
            catch { return null; }
        }

        /// <summary>
        /// The pawn's full backstory title in lower case ("dreaded dude"), or "" when unknown. Pawn_StoryTracker.Title
        /// (decompiled 1.7) is the player's custom title if set, else the adulthood/childhood title — NOT the short title
        /// LabelCap carries ("Dude").
        /// </summary>
        public static string Title(Pawn pawn)
        {
            try
            {
                string t = StripTags(pawn?.story?.TitleCap);
                return string.IsNullOrEmpty(t) ? "" : char.ToLowerInvariant(t[0]) + t.Substring(1);
            }
            catch { return ""; }
        }

        /// <summary>"a dreaded dude" / "an urbworld urchin" — for "People know you as …".</summary>
        public static string WithArticle(string noun)
        {
            if (string.IsNullOrEmpty(noun)) return "";
            return ("aeiou".IndexOf(char.ToLowerInvariant(noun[0])) >= 0 ? "an " : "a ") + noun;
        }

        /// <summary>
        /// One backstory's prose, pronouns and name resolved by the game, without the skill/work-disable lines that
        /// BackstoryDef.FullDescriptionFor appends after a blank line (decompiled 1.7). The prose itself can contain blank
        /// lines (vanilla DreadedDude58 has two paragraphs), so the cut keeps as many paragraphs as the raw
        /// <c>description</c> has — token resolution never adds line breaks. Capped so two of them stay a small,
        /// cache-stable part of the identity prefix. "" when absent.
        /// </summary>
        public static string Story(BackstoryDef backstory, Pawn pawn)
        {
            if (backstory == null || pawn == null) return "";
            try
            {
                string full = backstory.FullDescriptionFor(pawn);
                if (string.IsNullOrEmpty(full)) return "";
                full = full.Replace("\r", "");
                int paragraphs = Count((backstory.description ?? "").Replace("\r", ""), "\n\n") + 1;
                string[] parts = full.Split(new[] { "\n\n" }, StringSplitOptions.None);
                string prose = string.Join(" ", parts, 0, Math.Min(paragraphs, parts.Length));
                string text = StripTags(prose).Replace('\n', ' ').Trim();
                return text.Length <= MaxStoryChars ? text : text.Substring(0, MaxStoryChars).TrimEnd() + "…";
            }
            catch { return ""; }
        }

        private const int MaxStoryChars = 420;

        private static int Count(string s, string needle)
        {
            int n = 0;
            for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>Newborns and babies get no LLM layers and are never offered as conversation partners (live 2026-11-22: a
        /// newborn got Layer 3 consolidation, and a colonist wanted to "talk or play" with it).</summary>
        public static bool IsBaby(Pawn pawn)
        {
            try
            {
                DevelopmentalStage stage = pawn.DevelopmentalStage;
                return stage.Newborn() || stage.Baby();
            }
            catch { return false; }
        }

        /// <summary>How a trait sounds in someone's head, second person. Keyed by vanilla TraitDef defName + degree (read from
        /// the 1.7 Core/Biotech/Anomaly TraitDefs XML). Traits with no clear effect on voice (beauty, speed, sexuality,
        /// immunity...) have no entry; misandrist/misogynist are left out on purpose rather than prompting bigoted lines.</summary>
        public static string VoiceFor(string defName, int degree)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            string key = defName + ":" + degree;
            return Voices.TryGetValue(key, out string v) ? v : null;
        }

        private static readonly Dictionary<string, string> Voices = new Dictionary<string, string>
        {
            { "Abrasive:0", "You're blunt to the point of rude and never soften what you think." },
            { "Kind:0", "You're warm and think of other people first." },
            { "Psychopath:0", "You feel little for other people; your thoughts are cold and practical." },
            { "Bloodlust:0", "Violence excites you more than it should." },
            { "Greedy:0", "You keep a sharp eye on what you have and what you're owed." },
            { "Jealous:0", "You notice who has it better than you, and it rankles." },
            { "Ascetic:0", "You need very little and distrust comfort." },
            { "TooSmart:0", "Your mind races ahead; you overthink and get restless." },
            { "Pyromaniac:0", "Fire fascinates you." },
            { "Wimp:0", "Pain scares you and you'd rather avoid any fight." },
            { "Brawler:0", "You'd rather settle things with your fists." },
            { "Gourmand:0", "Food is never far from your mind." },
            { "Masochist:0", "Pain doesn't bother you the way it should; part of you likes it." },
            { "Nudist:0", "Clothes feel like a cage." },
            { "NightOwl:0", "You come alive at night and drag through the day." },
            { "Recluse:0", "You prefer your own company; crowds wear you down." },
            { "Joyous:0", "Joy comes easily to you." },
            { "TorturedArtist:0", "You feel everything heavily and brood over it." },
            { "Undergrounder:0", "You feel safest under rock, away from open sky." },
            { "Occultist:0", "The strange and the dark pull at your curiosity." },
            { "VoidFascination:0", "The strange and the dark pull at your curiosity." },
            { "Disturbing:0", "Your thoughts drift to unsettling places." },
            { "NaturalMood:2", "You're cheerful almost no matter what." },
            { "NaturalMood:1", "You tend to look on the bright side." },
            { "NaturalMood:-1", "You expect the worst and grumble about it." },
            { "NaturalMood:-2", "A grey heaviness sits on most of your thoughts." },
            { "Nerves:2", "Nothing rattles you." },
            { "Nerves:1", "You keep your head under pressure." },
            { "Nerves:-1", "You're jumpy and worry easily." },
            { "Nerves:-2", "Your temper and nerves snap without warning." },
            { "Neurotic:1", "You fret over details and can't let things go." },
            { "Neurotic:2", "You're wound tight, always anxious about getting things right." },
            { "Industriousness:2", "You can't sit still while there's work to do." },
            { "Industriousness:1", "You take pride in hard work." },
            { "Industriousness:-1", "You'd rather take it easy than work." },
            { "Industriousness:-2", "Work is the last thing you want; any excuse to rest will do." },
            { "DrugDesire:2", "You crave chemicals and think about getting high." },
            { "DrugDesire:1", "You enjoy the occasional drink or drug." },
            { "DrugDesire:-1", "You won't touch drugs or alcohol." },
        };

        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        private static string StripTags(string s) => string.IsNullOrEmpty(s) ? s : Tags.Replace(s, "").Trim();
    }
}
