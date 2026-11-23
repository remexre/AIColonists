using System.Collections.Generic;
using AIColonists.State;
using RimWorld.Planet; // 1.6: WorldComponent/World moved out of the Verse namespace
using Verse;

namespace AIColonists.Core
{
    /// <summary>
    /// World-level persistence for all colonist mind states (Spec §5.2). Indexed by pawn.ThingID so the mod can be
    /// added or removed mid-save without touching Pawn.ExposeData — 100% save-safe.
    /// </summary>
    public class AIColonistWorldComponent : WorldComponent
    {
        private Dictionary<string, ColonistMindState> mindStates = new Dictionary<string, ColonistMindState>();

        public AIColonistWorldComponent(World world) : base(world) { }

        /// <summary>Returns the pawn's mind state, creating a fresh one on first sight. Main thread only.</summary>
        public ColonistMindState GetOrCreateMindState(Pawn pawn)
        {
            ColonistMindState ms;
            if (!mindStates.TryGetValue(pawn.ThingID, out ms))
            {
                ms = new ColonistMindState(pawn);
                mindStates[pawn.ThingID] = ms;
            }

            // Self-heal for a pawn that was half-loaded when its mind state was first created (LabelCap NRE'd during save load):
            // our systems touch every pawn constantly on normal ticks, so the real name lands on the next call.
            if (string.IsNullOrEmpty(ms.pawnName))
            {
                ms.pawnName = AIColonists.Prompts.PawnText.ShortName(pawn) ?? ""; // null while still loading — retry on a later tick
            }

            return ms;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref mindStates, "colonistMindStates", LookMode.Value, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && mindStates == null)
            {
                mindStates = new Dictionary<string, ColonistMindState>();
            }
        }
    }
}
