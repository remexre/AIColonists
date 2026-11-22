using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AIColonists.Inference;

namespace AIColonists.Core
{
    /// <summary>
    /// Backs the "Test providers" button: probes each layer's endpoint + model once, in order, on a worker thread.
    /// URLs/models are snapshotted at construction so edits made while a test runs can't mix two configs in one report.
    /// The UI thread only ever reads Entry fields, which are written once each (volatile refs to immutable results).
    /// </summary>
    public class ProviderTester
    {
        public class Entry
        {
            public readonly string label;
            public readonly string url;
            public readonly string model;
            public readonly int maxTokens;
            internal readonly Stopwatch clock = new Stopwatch();
            public volatile bool started;
            public volatile ProviderTestResult result; // null until this probe finishes

            public Entry(string label, string url, string model, int maxTokens)
            {
                this.label = label;
                this.url = url ?? "";
                this.model = model ?? "";
                this.maxTokens = maxTokens;
            }

            /// <summary>Seconds since this probe started (for the live "testing… N s" line).</summary>
            public double ElapsedSeconds => clock.Elapsed.TotalSeconds;
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public readonly int requestTimeoutMs;
        public readonly bool disableReasoning;

        private readonly Func<string, string, int, Task<ProviderTestResult>> probe;
        private Task running;

        public ProviderTester(Settings s) : this(s, null) { }

        /// <summary>Probe injection point for the harness.</summary>
        public ProviderTester(Settings s, Func<string, string, int, Task<ProviderTestResult>> probe)
        {
            requestTimeoutMs = s.RequestTimeoutMs;
            disableReasoning = s.DisableReasoning;
            bool noReasoning = disableReasoning; // snapshot for the closure
            this.probe = probe ?? ((url, model, maxTokens) => LlamaServerClient.TestProviderAsync(url, model, maxTokens, noReasoning));
            // Each layer probes with its real output budget, so reasoning models that can't fit show up here.
            Entries.Add(new Entry("Layer 1 (impulses)", s.Layer1ServerUrl, s.Layer1Model, s.Layer1MaxOutputTokens));
            Entries.Add(new Entry("Layer 2 (room social)", s.Layer2ServerUrl, s.Layer2Model, s.Layer2MaxOutputTokens));
            Entries.Add(new Entry("Layer 3 (memory drift)", s.Layer3ServerUrl, s.Layer3Model, s.Layer3MaxOutputTokens));
        }

        /// <summary>Starts the probes (idempotent). Returns the task so the harness can await completion.</summary>
        public Task Start()
        {
            if (running == null) running = Task.Run(RunAll);
            return running;
        }

        public bool Done
        {
            get
            {
                foreach (Entry e in Entries)
                    if (e.result == null) return false;
                return true;
            }
        }

        private async Task RunAll()
        {
            foreach (Entry e in Entries)
            {
                e.clock.Start();
                e.started = true;
                try { e.result = await probe(e.url, e.model, e.maxTokens); }
                catch (Exception ex) { e.result = ProviderTestResult.Fail(e.clock.ElapsedMilliseconds, ex.Message); } // defensive: probes shouldn't throw
                e.clock.Stop();
            }
        }

        /// <summary>One-line status for an entry, shared by the dialog and the harness.</summary>
        public string StatusLine(Entry e)
        {
            ProviderTestResult r = e.result;
            if (r == null)
                return e.started ? "testing… " + e.ElapsedSeconds.ToString("0") + " s" : "waiting";
            if (!r.ok)
                return "FAILED: " + r.message;
            string line = "OK in " + (r.latencyMs / 1000.0).ToString("0.0") + " s — replied \"" + r.message + "\"";
            // Over half the budget on thinking for a one-word answer: real (much longer) answers will likely be cut off.
            if (r.maxTokens > 0 && r.reasoningTokens * 2 > r.maxTokens)
                line += ". Spent " + r.reasoningTokens + " of its " + r.maxTokens + " tokens reasoning just to say that, so real answers will likely get cut off: turn on \"Turn off model reasoning\" in the settings, or raise this layer's max output tokens.";
            if (r.latencyMs > requestTimeoutMs)
                line += ". Slower than your " + requestTimeoutMs + " ms request timeout, so real calls would time out. A first call can include model load time; try again.";
            return line;
        }
    }
}
