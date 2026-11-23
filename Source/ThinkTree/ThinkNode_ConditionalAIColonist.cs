using AIColonists.Core;
using Verse;
using Verse.AI;

namespace AIColonists.ThinkTree
{
    /// <summary>
    /// Gate node (Spec §6.1): the AI desire branch only runs for enabled, undrafted, non-mentally-broken colonists.
    /// Drafted / queued-job / emergency nodes sit higher in MainColonistBehaviorCore and are handled natively by the
    /// engine — this gate is a belt-and-braces check so the giver can never fire for them either.
    /// </summary>
    public class ThinkNode_ConditionalAIColonist : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            Settings s = AIColonistMod.Settings;
            if (s == null || !s.EnableAI) return false;

            // 2026-11-15 (user request: "remove LLM from prisoners"): player-faction, non-prisoner pawns only.
            // MainThinkTree is per-RACE (decompiled Pawn_Thinker.MainThinkTree => RaceProps.thinkTreeMain), so prisoners
            // share MainColonistBehaviorCore and DO reach this node — without this check a colony prisoner passed every
            // condition below, got Layer 1 evaluations every 30 s (~40k tokens/pawn-day) and could receive AI-issued jobs.
            // L2/L3 already iterate FreeColonistsSpawned only; this closes the Layer 1 gap. Prisoners keep their home
            // faction in vanilla, so the faction check is primary and IsPrisoner is belt-and-braces. (Slaves stay in:
            // player-faction laborers — open policy question, OPTIONAL-IMPROVEMENTS.md #9.)
            // [CONFIRMED decompiled 2026-11-15] 1.6: the player faction is RimWorld.Faction.OfPlayer — Find has no FactionPlayer member.
            if (pawn.Faction != RimWorld.Faction.OfPlayer || pawn.IsPrisoner) return false;

            if (!pawn.RaceProps.Humanlike) return false;
            if (AIColonists.Prompts.PawnText.IsBaby(pawn)) return false; // newborns/babies: vanilla only (live 2026-11-22)

            return !pawn.Drafted && !pawn.InMentalState;
        }
    }
}
