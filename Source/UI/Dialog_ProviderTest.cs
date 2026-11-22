using AIColonists.Core;
using AIColonists.Inference;
using UnityEngine;
using Verse;

namespace AIColonists.UI
{
    /// <summary>
    /// Popup opened by the settings dialog's "Test providers" button. The settings dialog is fixed-size with no scroll
    /// (decompiled RimWorld.Dialog_ModSettings) and already full, so results get their own window. Probes start on
    /// open and run on a worker thread (ProviderTester); this window just re-reads their status every frame.
    /// </summary>
    public class Dialog_ProviderTest : Window
    {
        private static readonly Color Pending = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color Good = new Color(0.55f, 0.9f, 0.55f);
        private static readonly Color Bad = new Color(1f, 0.5f, 0.45f);

        private readonly ProviderTester tester;

        public Dialog_ProviderTest(Settings s)
        {
            tester = new ProviderTester(s);
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            tester.Start();
        }

        public override Vector2 InitialSize => new Vector2(700f, 500f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), "Test providers");
            Text.Font = GameFont.Small;

            float y = inRect.y + 42f;
            foreach (ProviderTester.Entry e in tester.Entries)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), e.label + ": " + e.model + " @ " + e.url);
                y += 24f;

                ProviderTestResult r = e.result;
                GUI.color = r == null ? Pending : (r.ok ? Good : Bad);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(inRect.x + 16f, y, inRect.width - 16f, 64f), tester.StatusLine(e)); // wraps up to ~4 lines
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 72f;
            }

            Text.Font = GameFont.Tiny;
            GUI.color = Pending;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 40f),
                "Each layer gets one tiny chat request, using the URLs and models from the moment you clicked. Close and click again to re-test.");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }
    }
}
