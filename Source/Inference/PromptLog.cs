using System;
using System.Globalization;
using System.IO;
using System.Text;
using AIColonists.Core;
using Verse;

namespace AIColonists.Inference
{
    /// <summary>
    /// Optional full-transcript log of every LLM call (Settings.LogPromptsToFile), written next to Player.log as two
    /// append-only files: <see cref="TextFileName"/> (readable blocks: system prompt, user prompt, reasoning, reply) and
    /// <see cref="JsonlFileName"/> (one JSON object per call: the exact request body plus the raw response). Player.log
    /// only gets one-line summaries; this is for reading what the model actually saw and said. Written from worker
    /// threads under a lock; any I/O failure is reported once and never affects the request.
    /// </summary>
    public static class PromptLog
    {
        public const string TextFileName = "AIColonists-prompts.log";
        public const string JsonlFileName = "AIColonists-prompts.jsonl";

        /// <summary>Folder the files go in, or null when unknown (nothing is written). Set on the main thread at mod load
        /// via <see cref="ResolveDirectory"/>: GenFilePaths reads Unity paths, which worker threads must not touch.</summary>
        public static volatile string Directory;

        /// <summary>Where Player.log lives: the save-data folder's Logs subfolder when it exists (Linux 1.7), else the
        /// save-data folder itself (Windows keeps Player.log there). Main thread only.</summary>
        public static string ResolveDirectory()
        {
            string root = GenFilePaths.SaveDataFolderPath;
            if (string.IsNullOrEmpty(root)) return null;
            string logs = Path.Combine(root, "Logs");
            return System.IO.Directory.Exists(logs) ? logs : root;
        }

        /// <summary>One attempt at one call. The caller fills the context fields (layer, label, model, prompts); the
        /// transport fills the outcome fields.</summary>
        public sealed class Call
        {
            public int layer;            // 1, 2 or 3
            public string label;         // who the call is about: "Albina (Human1006)", or the room's names for Layer 2
            public string model;
            public string systemPrompt;
            public string userPrompt;

            public DateTime time;        // local time the attempt finished
            public string url;
            public int attempt;          // 0 = first try, 1 = the one transport retry
            public long elapsedMs;
            public int status;           // HTTP status, 0 when no response arrived
            public string requestJson;   // the exact body sent
            public string responseBody;  // the raw body received, null when none
            public string content;       // choices[0].message.content, when present
            public string reasoning;     // reasoning_content / reasoning, when the server returned it
            public string finishReason;
            public int completionTokens;
            public string error;         // why the attempt failed (timeout, transport, HTTP, rejected reply); null on success
        }

        private static readonly object FileLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Appends one call to both files. Never throws.</summary>
        public static void Write(Call c)
        {
            string dir = Directory;
            if (c == null || string.IsNullOrEmpty(dir)) return;
            try
            {
                string text = FormatText(c);
                string json = FormatJsonLine(c) + "\n";
                lock (FileLock)
                {
                    File.AppendAllText(Path.Combine(dir, TextFileName), text, Utf8NoBom);
                    File.AppendAllText(Path.Combine(dir, JsonlFileName), json, Utf8NoBom);
                }
            }
            catch (Exception e)
            {
                MainThreadLog.ErrorOnce("[AIColonists] couldn't write the prompt log in " + dir + ": " + e.Message, 4822);
            }
        }

        /// <summary>The readable block for one call (ends with a blank line).</summary>
        public static string FormatText(Call c)
        {
            var sb = new StringBuilder(1024 + Len(c.systemPrompt) + Len(c.userPrompt) + Len(c.responseBody));
            sb.Append("===== ").Append(c.time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(" · Layer ").Append(c.layer);
            if (!string.IsNullOrEmpty(c.label)) sb.Append(" · ").Append(c.label);
            if (c.attempt > 0) sb.Append(" · retry");
            sb.Append(" · ").Append(c.status > 0 ? "HTTP " + c.status : "no response")
              .Append(" in ").Append(c.elapsedMs).Append(" ms =====\n");
            sb.Append("model: ").Append(c.model ?? "?").Append(" · ").Append(c.url ?? "?").Append('\n');

            Section(sb, "system", c.systemPrompt);
            Section(sb, "user", c.userPrompt);
            if (!string.IsNullOrEmpty(c.reasoning)) Section(sb, "reasoning", c.reasoning);
            if (c.content != null)
            {
                string meta = (c.finishReason != null ? "finish: " + c.finishReason : "")
                    + (c.completionTokens > 0 ? (c.finishReason != null ? ", " : "") + c.completionTokens + " tokens" : "");
                Section(sb, meta.Length > 0 ? "reply (" + meta + ")" : "reply", c.content);
            }
            else if (c.responseBody != null)
            {
                Section(sb, "raw response", c.responseBody);
            }
            if (c.error != null) Section(sb, "error", c.error);
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>The JSONL record for one call, on a single line. "request" is the request body embedded as JSON (we
        /// build it, so it's valid; the json_schema parts are multi-line, so whitespace is compacted first);
        /// "response_body" stays a string because servers may send non-JSON errors (jq: <c>.response_body | fromjson</c>).</summary>
        public static string FormatJsonLine(Call c)
        {
            var sb = new StringBuilder(512 + Len(c.requestJson) + 2 * Len(c.responseBody));
            sb.Append('{');
            Field(sb, "time", c.time.ToString("o", CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append("\"layer\":").Append(c.layer).Append(',');
            Field(sb, "label", c.label); sb.Append(',');
            Field(sb, "model", c.model); sb.Append(',');
            Field(sb, "url", c.url); sb.Append(',');
            sb.Append("\"attempt\":").Append(c.attempt).Append(',');
            sb.Append("\"elapsed_ms\":").Append(c.elapsedMs).Append(',');
            sb.Append("\"status\":").Append(c.status > 0 ? c.status.ToString(CultureInfo.InvariantCulture) : "null").Append(',');
            Field(sb, "error", c.error); sb.Append(',');
            Field(sb, "content", c.content); sb.Append(',');
            Field(sb, "reasoning", c.reasoning); sb.Append(',');
            Field(sb, "finish_reason", c.finishReason); sb.Append(',');
            sb.Append("\"completion_tokens\":").Append(c.completionTokens > 0 ? c.completionTokens.ToString(CultureInfo.InvariantCulture) : "null").Append(',');
            sb.Append("\"request\":");
            string req = CompactJson(c.requestJson);
            if (!string.IsNullOrEmpty(req) && req[0] == '{' && req.IndexOf('\n') < 0) sb.Append(req);
            else LlamaServerClient.AppendJsonString(sb, req); // never expected, but keeps the line one valid object
            sb.Append(',');
            Field(sb, "response_body", c.responseBody);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Drops whitespace outside string literals. Only for JSON we built ourselves (known valid).</summary>
        public static string CompactJson(string json)
        {
            if (json == null) return null;
            var sb = new StringBuilder(json.Length);
            bool inString = false, escaped = false;
            foreach (char ch in json)
            {
                if (inString)
                {
                    sb.Append(ch);
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                }
                else if (ch == '"') { inString = true; sb.Append(ch); }
                else if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') sb.Append(ch);
            }
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, string body)
        {
            sb.Append("--- ").Append(title).Append(" ---\n");
            if (!string.IsNullOrEmpty(body))
            {
                sb.Append(body.Replace("\r\n", "\n"));
                if (body[body.Length - 1] != '\n') sb.Append('\n');
            }
        }

        private static void Field(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            if (value == null) sb.Append("null");
            else LlamaServerClient.AppendJsonString(sb, value);
        }

        private static int Len(string s) => s != null ? s.Length : 0;
    }
}
