using AIColonists.Core;
using RimWorld; // 1.6: ITab lives in the RimWorld namespace (not Verse)
using System;
using UnityEngine;
using Verse;

namespace AIColonists.UI
{
    /// <summary>
    /// In-game inspection tab (Spec §9 Phase 4): live inner monologue, long-term desire, continuous trait drift bars,
    /// and relationship vectors. RimWorld 1.6 pattern: tabs are shared singletons resolved from the pawn ThingDef's
    /// &lt;inspectorTabs&gt; list via InspectTabManager.GetSharedInstance (Activator.CreateInstance — a parameterless ctor is
    /// required and NO per-pawn state may be stored; everything is read live from SelPawn). Registration lives in
    /// Patches/AIColonistTabs.xml. Content comes from the pure AIMindViewModel (harness-pinned caps/ordering); this class
    /// only draws. Formatting mirrors vanilla pawn tabs (decompiled from this install's Assembly-CSharp):
    ///  - 17px content margin (CharacterCardUtility convention), first row at y=17 — the close button
    ///    (Widgets.CloseButtonFor: 18x18, top-right at y=4..22) is drawn BEFORE FillTab and stays clear of left-aligned text;
    ///  - GameFont.Small everywhere with row pitch &gt;= 24f (Verse.Text.SmallFontHeight = 22f). NO Tiny: if "disable tiny
    ///    text" is on or the language lacks a tiny face, Tiny silently falls back to Small and sub-22px pitch clips every line;
    ///  - wrapped blocks are sized with GUIStyle.CalcHeight so no row ever overlaps another (1.6 has no GenText.AutoWrap);
    ///  - every GUI.color mutation is paired with a restore to Color.white (a leaked color tints all later labels — the
    ///    vanilla idiom seen throughout NeedsCardUtility/CharacterCardUtility);
    ///  - tab height is dynamic (cached layout pass, clamped to screen) like vanilla NeedsCardUtility.GetSize.
    /// RimWorld 1.6 has NO Widgets.RadioList / Widgets.FillRect (verified against the real DLL).
    /// </summary>
    public class ITab_Pawn_AIMind : ITab
    {
        private const float TabWidth = 300f;
        private const float MarginX = 17f;      // vanilla Character card margin
        private const float TopY = 17f;         // first content row (close button sits top-right, y=4..22)
        private const float BottomMargin = 17f;
        private const float RowH = 24f;         // >= Verse.Text.SmallFontHeight (22f) + line spacing
        private const float SectionGap = 8f;    // extra gap before each section after the first

        private static GUIStyle wrapStyle;
        private static readonly Color BarTrack = new Color(0.25f, 0.27f, 0.31f);
        private static readonly Color BarFill = new Color(0.55f, 0.75f, 1f);
        private static readonly Color CaptionColor = new Color(0.6f, 0.6f, 0.6f);

        // Layout height from the last FillTab pass (shared singleton — like vanilla's RecentHeight; resizes within a frame).
        private static float cachedHeight = 320f;

        public ITab_Pawn_AIMind()
        {
            labelKey = "TabAIMind"; // translation key — Languages/English/Keyed/AIColonists_Tabs.xml (1.6 loads the Keyed FOLDER, not a file)
            tutorTag = "AIMind";
        }

        /// <summary>Visible only on player-faction humanlike pawns while AI is enabled (Spec §9 Phase 4). Babies are hidden:
        /// they get no LLM layers, so the tab would only ever say "no data yet".
        /// 2026-11-22 (live NRE every OnGUI frame): Faction is null for factionless pawns — wild men, and the corpses SelPawn
        /// looks into — so it is null-checked before IsPlayer.</summary>
        public override bool IsVisible =>
            SelPawn != null && SelPawn.Faction != null && SelPawn.Faction.IsPlayer && SelPawn.RaceProps.Humanlike
            && !AIColonists.Prompts.PawnText.IsBaby(SelPawn)
            && AIColonistMod.Settings != null && AIColonistMod.Settings.EnableAI;

        protected override void UpdateSize()
        {
            base.UpdateSize();
            try
            {
                // The tab window grows UP from PaneTopY - 30 (MainTabWindow_Inspect) — clamp to what fits on screen.
                // Qualified: bare 'UI' inside namespace AIColonists.UI resolves to the namespace itself, not Verse.UI.
                float maxH = Verse.UI.screenHeight - 240f;
                size = new Vector2(TabWidth, Mathf.Clamp(cachedHeight, 260f, Mathf.Max(260f, maxH)));
            }
            catch (Exception)
            {
                size = new Vector2(TabWidth, 480f); // defensive: a sizing error must never break the inspector
            }
        }

        private static GUIStyle WrapStyle()
        {
            if (wrapStyle == null)
            {
                Text.Font = GameFont.Small;
                // Derive from RimWorld's own small-font style (white text on Arial_small) — NOT GUIStyle.none, which does not
                // inherit the skin's label color/font and rendered dark against the tab background.
                wrapStyle = new GUIStyle(Text.CurFontStyle);
                wrapStyle.wordWrap = true;   // 1.6 has no GenText.AutoWrap — Unity's own wrapping instead
            }
            return wrapStyle;
        }

        /// <summary>0..1 bar on a dark track, drawn with the always-available white texture (no FillRect in 1.6). Always restores GUI.color.</summary>
        private static void DrawBar(Rect rect, float frac)
        {
            GUI.color = BarTrack;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            float w = rect.width * Mathf.Clamp01(frac);
            if (w > 1f)
            {
                GUI.color = BarFill;
                GUI.DrawTexture(new Rect(rect.x, rect.y + (rect.height - 7f) / 2f, w, 7f), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
        }

        protected override void FillTab()
        {
            AIMindViewModel.View v = AIMindViewModel.Build(SelPawn, Find.TickManager.TicksGame);

            float x = MarginX;
            float y = TopY;
            float w = TabWidth - 2f * MarginX; // 266f
            bool firstSection = true;

            if (!v.hasAnyData)
            {
                Text.Font = GameFont.Small;
                GUI.Label(new Rect(x, y, w, 90f),
                    "No AI mind data yet — this colonist hasn't acted on an LLM decision, had a social moment, or a Layer 3 consolidation in this save.", WrapStyle());
                cachedHeight = TopY + 90f + BottomMargin;
                return;
            }

            // --- Inner monologue (last one that actually turned into a job) ---
            BeginSection(ref y, ref firstSection, "Inner monologue");
            if (!string.IsNullOrEmpty(v.innerMonologue))
            {
                y = DrawWrapped(x, w, y, "\"" + v.innerMonologue + "\"");
                Text.Font = GameFont.Small;
                GUI.color = CaptionColor;
                Widgets.Label(new Rect(x, y, w, RowH), AgeText(v.monologueAgeTicks));
                GUI.color = Color.white;
                y += RowH;
            }
            else
            {
                y = DrawCaption(x, w, y, "— none yet —");
            }

            // --- Long-term desire (Layer 3) ---
            BeginSection(ref y, ref firstSection, "Long-term desire");
            if (!string.IsNullOrEmpty(v.longing))
            {
                y = DrawWrapped(x, w, y, v.longing);
            }
            else
            {
                y = DrawCaption(x, w, y, "— none yet —");
            }

            // --- Recent episodic memories (newest first) ---
            BeginSection(ref y, ref firstSection, "Recent memories");
            if (v.recentMemories.Count == 0)
            {
                y = DrawCaption(x, w, y, "— none yet —");
            }
            else
            {
                foreach (string m in v.recentMemories)
                {
                    y = DrawWrapped(x, w, y, "• " + m);
                }
            }

            // --- Newest distilled self-knowledge (semantic memory) ---
            if (!string.IsNullOrEmpty(v.latestSemantic))
            {
                BeginSection(ref y, ref firstSection, "Self-knowledge");
                y = DrawWrapped(x, w, y, v.latestSemantic);
            }

            // --- Continuous trait drift bars (0..1) ---
            if (v.traits.Count > 0)
            {
                BeginSection(ref y, ref firstSection, "Trait drift");
                foreach (AIMindViewModel.TraitBar t in v.traits)
                {
                    Text.Font = GameFont.Small;
                    Widgets.Label(new Rect(x, y + 1f, 120f, RowH), t.name);
                    DrawBar(new Rect(x + 128f, y + 8f, w - 128f, 8f), t.value);
                    y += 26f;
                }
            }

            // --- Relationship vectors (4 main axes per target) ---
            if (v.relationships.Count > 0)
            {
                BeginSection(ref y, ref firstSection, "Relationships");
                foreach (AIMindViewModel.RelationshipLine r in v.relationships)
                {
                    Text.Font = GameFont.Small;
                    Widgets.Label(new Rect(x, y, w, RowH), r.targetName);
                    y += 24f;
                    float lx = x + 4f;
                    DrawAxis(lx, y, "T", r.trust);
                    DrawAxis(lx + 62f, y, "A", r.affection);
                    DrawAxis(lx + 124f, y, "R", r.respect);
                    DrawAxis(lx + 186f, y, "F", r.frustration);
                    y += 26f;
                }
            }

            cachedHeight = y + BottomMargin;
        }

        /// <summary>Vanilla-style section header: small font, title case (CharacterCardUtility draws "Traits"/"Abilities" this way).</summary>
        private static void BeginSection(ref float y, ref bool firstSection, string name)
        {
            if (!firstSection)
            {
                y += SectionGap;
            }
            firstSection = false;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(MarginX, y, TabWidth - 2f * MarginX, RowH), name);
            y += RowH + 2f;
        }

        /// <summary>Gray single-line caption (vanilla NoEffectColor idiom) — always restores GUI.color.</summary>
        private static float DrawCaption(float x, float w, float y, string text)
        {
            Text.Font = GameFont.Small;
            GUI.color = CaptionColor;
            Widgets.Label(new Rect(x, y, w, RowH), text);
            GUI.color = Color.white;
            return y + RowH;
        }

        /// <summary>Word-wrapped block sized with CalcHeight so the next row can never overlap it (no AutoWrap in 1.6).</summary>
        private static float DrawWrapped(float x, float w, float y, string text)
        {
            Text.Font = GameFont.Small;
            GUIStyle style = WrapStyle();
            float h = style.CalcHeight(new GUIContent(text), w);
            GUI.Label(new Rect(x, y, w, h), text, style);
            return y + h + 6f;
        }

        private static void DrawAxis(float lx, float y, string letter, float frac)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(lx, y + 1f, 10f, RowH), letter);
            DrawBar(new Rect(lx + 14f, y + 8f, 38f, 8f), frac);
        }

        /// <summary>Game-tick age in RimWorld units: 3600 ticks/hour, 86400/day (Spec §5.2 tick convention).</summary>
        private static string AgeText(int ageTicks)
        {
            if (ageTicks <= 0) return "(just now)";
            if (ageTicks < 7200) return "(" + (int)(ageTicks / 60f) + " min ago)";
            if (ageTicks < 172800) return ("~" + ((float)ageTicks / 3600f).ToString("0.#") + " h ago)");
            return ("~" + ((float)ageTicks / 86400f).ToString("0.#") + " d ago)");
        }
    }
}
