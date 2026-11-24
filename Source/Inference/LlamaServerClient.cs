using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIColonists.Core;
using Verse; // Log (transport-error retry diagnostics)

namespace AIColonists.Inference
{
    /// <summary>
    /// llama-server client (Spec §9 Phase 1). Speaks the OpenAI-compatible /v1/chat/completions API with:
    ///  - async, pooled HttpClient (one static instance for the whole process), serialized through a single-flight
    ///    gate with one retry on transport failure (Mono concurrent-send fix — see SendGate below)
    ///  - json_schema response_format enforcement — GBNF at the sampler level (Spec §7.2)
    ///  - prefix-first message ordering so --cache-reuse hits the KV cache (Spec §2.3)
    ///  - &lt;think&gt;...&lt;/think&gt; stripping for R1-Distill models (Spec §10 risk table)
    /// WIRE FIELD NAMES ARE SNAKE_CASE ON PURPOSE: llama-server only recognizes OpenAI-style keys
    /// (top_p, min_p, max_tokens, response_format). The request body is BUILT BY HAND and the response
    /// is PARSED BY HAND — Unity's JsonUtility is deliberately not used on this path. Live-verified in-game
    /// on 1.6 (2026-11): ToJson silently dropped BOTH class-typed fields (messages, response_format) even
    /// with [Serializable] on every wire class — llama-server answered HTTP 400 "'messages' is required" for
    /// every dispatch while all scalar fields serialized fine. FromJson shares the same native writer/reader
    /// and drops nested object fields the same way (choices would have been lost). Hand-built JSON has no
    /// type-eligibility rules, is byte-deterministic, and makes both sides of the wire trivially loggable.
    /// </summary>
    public class LlamaServerClient : ILlamaBridge
    {
        /// <summary>
        /// Sent on every request so proxies and server logs can tell this traffic apart: "RimWorld/1.7.5014 AIColonists
        /// (+https://github.com/ptown99/AIColonists)". .NET's HttpClient sends no User-Agent at all by default, so hosted
        /// gateways and shared servers only saw anonymous requests. The game version is read once; if it can't be read
        /// (or isn't a valid product version) the RimWorld token goes without one. Declared before SharedHttp/RetryHttp on
        /// purpose: static fields initialize in declaration order, and NewHttpClient reads this.
        /// </summary>
        public static readonly string UserAgent = BuildUserAgent();

        private static string BuildUserAgent()
        {
            string game = null;
            try { game = RimWorld.VersionControl.CurrentVersionString; } // "1.7.5014" (decompiled 1.7)
            catch { /* never let this block requests — just leave the version off */ }
            if (string.IsNullOrEmpty(game) || !System.Text.RegularExpressions.Regex.IsMatch(game, @"^[0-9A-Za-z.\-]+$"))
                game = null;
            return (game == null ? "RimWorld" : "RimWorld/" + game) + " AIColonists (+https://github.com/ptown99/AIColonists)";
        }

        // One pooled HttpClient for the whole process (socket reuse). We deliberately do NOT mutate
        // SharedHttp.Timeout per instance: once any request has been sent, .NET forbids changing client
        // properties ("This instance has already started one or more requests"). Per-request timeouts go
        // through a CancellationTokenSource below instead — same semantics, no shared-state mutation.
        private static readonly HttpClient SharedHttp = NewHttpClient();

        // SINGLE-FLIGHT GATE (live-verified failure 2026-11-13): Mono's pooled HttpClient throws HttpRequestException
        // ("An error occurred while sending the request") when a second send starts on a client that still has an
        // in-flight request. Every Layer 2/3 dispatch collided with an in-flight Layer 1 call and died at the
        // transport level — the requests never reached the server, while identical concurrent sends via curl and
        // Windows .NET both succeeded (Mono-specific). Serializing ALL outgoing requests makes the collision
        // structurally impossible. The timeout budget starts AFTER acquisition: queue wait is free, the configured
        // timeout applies to the send itself.
        private static readonly System.Threading.SemaphoreSlim SendGate = new System.Threading.SemaphoreSlim(1, 1);

        // Fallback client with its OWN connection pool, used only for the single retry after a transport failure.
        // The shared pool may still hold the half-open connection that just failed (the server closed it but the
        // client hasn't noticed — the socket still looks connected), and reusing it fails identically. Harness-
        // verified 2026-11-13: retrying on SharedHttp after an aborted connection throws the same HttpRequestException.
        private static readonly HttpClient RetryHttp = NewHttpClient();

        // Both pools send the same User-Agent. Default headers are set here, before any request: .NET won't allow
        // changes to a client's properties once it has sent one.
        private static HttpClient NewHttpClient()
        {
            var http = new HttpClient();
            try { http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent); }
            catch { /* a header we can't parse must never stop the mod from talking to its server */ }
            return http;
        }

        private readonly Settings settings;

        public LlamaServerClient(Settings s)
        {
            settings = s;
        }

        public async Task<Layer1Result> RequestLayer1Async(Layer1Request request)
        {
            if (request.optionCount < 1)
                throw new Exception("Cannot build Layer 1 schema: affordance list is empty.");

            // Hand-built body (see class docs for why not JsonUtility). Keep the exact string: on a server
            // rejection we log BOTH sides of the wire in one line.
            string json = BuildChatCompletionsJson(request);
            return await SendChatCompletionAsync(settings.Layer1ServerUrl, json, ParseLayer1);
        }

        /// <summary>Batched room social call (Spec §7.3). Same hand-built wire format as Layer 1, different endpoint + schema.</summary>
        public async Task<Layer2Result> RequestLayer2Async(Layer2Request request) =>
            await SendChatCompletionAsync(settings.Layer2ServerUrl, BuildLayer2Json(request), ParseLayer2);

        /// <summary>Per-colonist memory consolidation call (Spec §7.4).</summary>
        public async Task<Layer3Result> RequestLayer3Async(Layer3Request request) =>
            await SendChatCompletionAsync(settings.Layer3ServerUrl, BuildLayer3Json(request), ParseLayer3);

        // Cold model loads measured at ~8.3 s on the game PC (RX 6800 + Ollama, qwen2.5:3b, 2026-11-15). The warmup
        // must outlive that by a wide margin — it runs once per map session on a background thread, so being generous
        // costs nothing; being too short re-triggers the abort-the-load failure this exists to prevent.
        private const int WarmUpTimeoutMs = 90_000;

        /// <summary>One-shot cold-start warmup (TEST-RESULTS.md §22.7). Minimal payload — the goal is loading the
        /// model, not getting an answer (max_tokens=1 keeps inference negligible). Serialized through the same
        /// SendGate as real dispatches, so it can never collide with them at the transport level.</summary>
        public async Task<bool> WarmUpAsync()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendJsonStringField(sb, "model", settings.Layer1Model ?? "");
            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":\"Warm up.\"}],\"max_tokens\":1,\"stream\":false}");
            string json = sb.ToString();

            string url = TrimSlash(settings.Layer1ServerUrl) + "/v1/chat/completions";
            await SendGate.WaitAsync(); // queue wait is free — same contract as the dispatch path
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(WarmUpTimeoutMs)))
                {
                    HttpResponseMessage resp;
                    try
                    {
                        resp = await SharedHttp.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), cts.Token);
                    }
                    catch (System.Threading.Tasks.TaskCanceledException)
                    {
                        // Worker thread (§23.3): MainThreadLog, never Verse.Log directly — an open in-game log window races the queue.
                        MainThreadLog.Message("[AIColonists] warmup timed out after " + WarmUpTimeoutMs + " ms (" + url + ")");
                        return false;
                    }
                    catch (HttpRequestException e)
                    {
                        // Server not up yet is a NORMAL pre-game state — one quiet line, no retry (dispatches self-heal).
                        MainThreadLog.Message("[AIColonists] warmup skipped, inference server unreachable: " + e.Message);
                        return false;
                    }

                    using (resp)
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string errBody = await resp.Content.ReadAsStringAsync();
                            MainThreadLog.Message("[AIColonists] warmup HTTP " + (int)resp.StatusCode + " from " + url
                                + " | server: " + Truncate(errBody));
                            return false;
                        }
                        MainThreadLog.Message("[AIColonists] inference backend warm — model loaded, dispatches run at full speed");
                        return true;
                    }
                }
            }
            finally
            {
                SendGate.Release();
            }
        }

        /// <summary>
        /// "Test providers" button (settings dialog): one tiny chat completion against an endpoint, reporting reachability,
        /// latency and the reply, or the error a real dispatch would hit. Goes through the same SendGate as real
        /// dispatches (Mono concurrent-send fix) and uses the warmup's long timeout: the first call may include a cold
        /// model load, and cancelling that mid-load makes Ollama abort it (§22.7). The caller compares latency against
        /// the configured RequestTimeoutMs. Never throws: every failure comes back as ok=false with a readable message.
        /// maxTokens should be the layer's real output budget: reasoning models (live finding 2026-11-22, Inkling 2 mini
        /// via OpenRouter) spend most of a small budget thinking, and at 16 tokens returned content:null with
        /// finish_reason "length". Probing with the real budget answers "will this model fit", and the reported
        /// reasoning_tokens shows how close it came.
        /// </summary>
        public static async Task<ProviderTestResult> TestProviderAsync(string baseUrl, string model, int maxTokens, bool disableReasoning = false, int timeoutMs = WarmUpTimeoutMs)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendJsonStringField(sb, "model", model ?? "");
            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":\"Reply with the single word OK.\"}],\"max_tokens\":");
            sb.Append(Math.Max(1, maxTokens).ToString(CultureInfo.InvariantCulture));
            AppendReasoningOff(sb, disableReasoning); // mirror real requests
            sb.Append(",\"stream\":false}");
            string json = sb.ToString();

            string url = TrimSlash(baseUrl ?? "") + "/v1/chat/completions";
            await SendGate.WaitAsync(); // queue wait is free — latency below measures the request itself
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs)))
                using (HttpResponseMessage resp = await SharedHttp.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), cts.Token))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    long ms = clock.ElapsedMilliseconds;
                    if (!resp.IsSuccessStatusCode)
                        return ProviderTestResult.Fail(ms, "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ": " + Truncate(body));

                    // usage.completion_tokens_details.reasoning_tokens (OpenAI/OpenRouter); 0 when the server doesn't report it.
                    int reasoning = ExtractJsonInt(body, "reasoning_tokens");
                    string content = ExtractJsonString(body, "content");
                    content = content == null ? "" : StripThinkBlocks(content);
                    if (content.Length > 0)
                        return ProviderTestResult.Ok(ms, Truncate(content), reasoning, maxTokens);

                    if (FindKeyValueColon(body, "choices") < 0)
                        return ProviderTestResult.Fail(ms, "answered, but not like an OpenAI-compatible chat server: " + Truncate(body));
                    if (ExtractJsonString(body, "finish_reason") == "length")
                        return ProviderTestResult.Fail(ms, "used its whole " + maxTokens + "-token budget without answering"
                            + (reasoning > 0 ? " (" + reasoning + " tokens went to reasoning). It's a reasoning model: turn on \"Turn off model reasoning\" in the settings, or raise this layer's max output tokens" : ""), reasoning, maxTokens);
                    return ProviderTestResult.Fail(ms, "replied with no content: " + Truncate(body), reasoning, maxTokens);
                }
            }
            catch (TaskCanceledException)
            {
                return ProviderTestResult.Fail(clock.ElapsedMilliseconds, "no answer within " + (timeoutMs / 1000) + " s");
            }
            catch (HttpRequestException e)
            {
                string inner = e.InnerException != null ? e.InnerException.Message : null;
                return ProviderTestResult.Fail(clock.ElapsedMilliseconds, "unreachable: " + e.Message
                    + (inner != null && !e.Message.Contains(inner) ? " (" + inner + ")" : ""));
            }
            catch (Exception e)
            {
                // e.g. a URL without http:// (UriFormatException / InvalidOperationException from HttpClient).
                return ProviderTestResult.Fail(clock.ElapsedMilliseconds, e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                SendGate.Release();
            }
        }

        /// <summary>Shared /v1/chat/completions transport for ALL layers (Spec §7.1–§7.4): single-flight gate,
        /// per-request timeout, one retry on transport failure, both-sides-of-the-wire error logging.</summary>
        private async Task<T> SendChatCompletionAsync<T>(string baseUrl, string json, Func<string, T> parser)
        {
            string url = TrimSlash(baseUrl) + "/v1/chat/completions";
            int timeoutMs = Math.Max(500, settings.RequestTimeoutMs);

            await SendGate.WaitAsync(); // queue wait is free — the budget below applies to the send itself
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs)))
                    {
                        HttpResponseMessage resp;
                        try
                        {
                            HttpClient http = attempt == 0 ? SharedHttp : RetryHttp; // retry on a fresh pool — see RetryHttp
                            resp = await http.PostAsync(url,
                                new StringContent(json, Encoding.UTF8, "application/json"), cts.Token);
                        }
                        catch (System.Threading.Tasks.TaskCanceledException)
                        {
                            throw new Exception("llama-server request timed out after " + timeoutMs + "ms (" + url + ")");
                        }
                        catch (HttpRequestException e) when (attempt == 0)
                        {
                            // Mono concurrent-send transport failure — retry ONCE with a fresh budget. Log both
                            // exception levels so the next occurrence is diagnosable from Player.log alone.
                            // Worker thread (§23.3): MainThreadLog, never Verse.Log directly.
                            MainThreadLog.Message("[AIColonists] transport error, retrying once: " + e.Message
                                + (e.InnerException != null ? " | inner: " + e.InnerException.Message : ""));
                            await Task.Delay(500);
                            continue;
                        }

                        using (resp)
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                // Live-verified on 1.6: the server answered HTTP 400 "'messages' is required" — include both sides of the wire in the exception so one log line diagnoses it.
                                string errBody = await resp.Content.ReadAsStringAsync();
                                throw new Exception("llama-server HTTP " + (int)resp.StatusCode + " from " + url
                                    + " | server: " + Truncate(errBody)
                                    + " | sent: " + Truncate(json));
                            }

                            string body = await resp.Content.ReadAsStringAsync();
                            return parser(body);
                        }
                    }
                }
            }
            finally
            {
                SendGate.Release();
            }
        }

        /// <summary>
        /// Builds the exact /v1/chat/completions request body (Spec §7.2). Public + static so the harness can
        /// assert on the byte-for-byte wire format without a Unity runtime. Prefix-first message ordering so
        /// llama-server --cache-reuse hits the KV cache (Spec §2.3).
        /// </summary>
        public static string BuildChatCompletionsJson(Layer1Request request)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonStringField(sb, "model", request.model ?? "");
            sb.Append(",\"messages\":[");
            AppendMessage(sb, "system", request.systemPrompt ?? ""); // static identity prefix first (KV-cache friendly)
            sb.Append(',');
            AppendMessage(sb, "user", request.userPrompt ?? "");     // dynamic suffix + numbered sensory affordances second
            sb.Append("],\"temperature\":").Append(request.temperature.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"top_p\":").Append(request.topP.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"min_p\":").Append(request.minP.ToString(CultureInfo.InvariantCulture)); // llama-server extension; strict OpenAI servers ignore unknown keys
            sb.Append(",\"max_tokens\":").Append(request.maxTokens);
            AppendReasoningOff(sb, request.disableReasoning);
            // Live-verified on the homelab llama.cpp Nov 9 build (:8091, 2026-11): json_schema must be an OBJECT
            // {name, schema} — the string form (which earlier E2E validated against an older build) is SILENTLY
            // IGNORED: HTTP 200 with completely unconstrained output. The Layer 1 doc is already serialized JSON,
            // so embed it raw as the "schema" member (no extra escaping — newlines/quotes are structure, not data).
            sb.Append(",\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"layer1\",\"schema\":");
            sb.Append(GbnfSchemas.BuildLayer1Schema(request.optionCount));
            sb.Append("}},\"stream\":false}");
            return sb.ToString();
        }

        /// <summary>Shared body builder for the schema-constrained calls (Layer 2/3). Same wire contract as
        /// BuildChatCompletionsJson — object-form json_schema, prefix-first message order (see class docs).</summary>
        private static string BuildBody(string model, string systemPrompt, string userPrompt, float temperature, float topP, float minP, int maxTokens, bool disableReasoning, string schemaName, string schemaDoc)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonStringField(sb, "model", model ?? "");
            sb.Append(",\"messages\":[");
            AppendMessage(sb, "system", systemPrompt ?? ""); // static prefix first (KV-cache friendly)
            sb.Append(',');
            AppendMessage(sb, "user", userPrompt ?? "");     // dynamic suffix last
            sb.Append("],\"temperature\":").Append(temperature.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"top_p\":").Append(topP.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"min_p\":").Append(minP.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"max_tokens\":").Append(maxTokens);
            AppendReasoningOff(sb, disableReasoning);
            // Object-form json_schema (live-verified on the Nov 9 build — see BuildChatCompletionsJson). The schema doc is
            // already serialized JSON, so embed it raw as the "schema" member (no extra escaping).
            sb.Append(",\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"").Append(schemaName).Append("\",\"schema\":");
            sb.Append(schemaDoc);
            sb.Append("}},\"stream\":false}");
            return sb.ToString();
        }

        /// <summary>Layer 2 wire body (Spec §7.3). Public + static so the harness can assert on the bytes.</summary>
        public static string BuildLayer2Json(Layer2Request request) =>
            BuildBody(request.model, request.systemPrompt, request.userPrompt, request.temperature, request.topP, request.minP, request.maxTokens, request.disableReasoning, "layer2", GbnfSchemas.Layer2JsonSchema);

        /// <summary>Layer 3 wire body (Spec §7.4).</summary>
        public static string BuildLayer3Json(Layer3Request request) =>
            BuildBody(request.model, request.systemPrompt, request.userPrompt, request.temperature, request.topP, request.minP, request.maxTokens, request.disableReasoning, "layer3", GbnfSchemas.Layer3JsonSchema);

        /// <summary>
        /// Settings "Turn off model reasoning". Live finding 2026-11-22 (Inkling 2 mini via OpenRouter): a reasoning model
        /// spent all 80 Layer 1 tokens thinking and returned content:null on every dispatch. reasoning_effort:"none" (the
        /// OpenAI-standard field, which OpenRouter maps for every reasoning model) brought it to ~1 s, 0 reasoning tokens,
        /// schema-valid JSON. Opt-in: OpenAI's own API rejects the field for non-reasoning models. llama-server ignores it.
        /// </summary>
        private static void AppendReasoningOff(StringBuilder sb, bool disableReasoning)
        {
            if (disableReasoning) sb.Append(",\"reasoning_effort\":\"none\"");
        }

        /// <summary>Exception text for a 2xx reply with no content string. Spells out the reasoning-model case (budget spent
        /// thinking, finish_reason "length") instead of a bare "malformed", since that's the common cause and fixable in settings.</summary>
        private static string MissingContentMessage(string responseBody)
        {
            int reasoning = ExtractJsonInt(responseBody, "reasoning_tokens");
            if (ExtractJsonString(responseBody, "finish_reason") == "length")
                return "model used its whole output-token budget without answering"
                    + (reasoning > 0 ? " (" + reasoning + " tokens went to reasoning)" : "")
                    + ". In settings, turn on \"Turn off model reasoning\" or raise this layer's max output tokens.";
            return "Malformed llama-server response: " + Truncate(responseBody);
        }

        private static void AppendMessage(StringBuilder sb, string role, string content)
        {
            sb.Append("{\"role\":\"").Append(role).Append("\",\"content\":");
            AppendJsonString(sb, content);
            sb.Append('}');
        }

        private static void AppendJsonStringField(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            AppendJsonString(sb, value);
        }

        /// <summary>JSON-escapes a string (quotes, backslashes, control chars — prompts contain newlines) and appends it with surrounding quotes.</summary>
        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        private Layer1Result ParseLayer1(string responseBody)
        {
            // Manual extraction (see class docs): FromJson<ChatCompletionsEnvelope> would drop the nested
            // choices/message object fields just as ToJson dropped messages on the way out. The non-streaming
            // response has exactly one choice, and "content" is its only key in the whole document.
            string content = ExtractJsonString(responseBody, "content");
            if (content == null)
                throw new Exception(MissingContentMessage(responseBody));

            content = StripThinkBlocks(content);

            // The schema-constrained inner document is FLAT (string/int only) — parse it manually too, so no
            // Unity JSON dependency remains anywhere on this path. TOLERANT OF TRUNCATION: with impulse_id first
            // in the schema (see GbnfSchemas), a max_tokens cut can still yield the decision without the prose
            // fields — live-verified failure mode on the 1.5B model. Only throw when NOTHING usable was extracted;
            // a missing/invalid impulse_id is rejected by AffordanceOptionExtensions.IsValidImpulse at the dispatch
            // site (desire discarded, vanilla AI takes over, no crash path).
            var result = new Layer1Result();
            result.inner_voice = ExtractJsonString(content, "inner_voice") ?? "";
            result.impulse_id = ExtractJsonInt(content, "impulse_id");
            result.spoken_mutter = ExtractJsonString(content, "spoken_mutter") ?? "";

            if (string.IsNullOrEmpty(result.inner_voice) && result.impulse_id == 0)
                throw new Exception("Failed to parse Layer 1 JSON: " + Truncate(content));
            return result;
        }

        private Layer2Result ParseLayer2(string responseBody)
        {
            string content = ExtractJsonString(responseBody, "content");
            if (content == null)
                throw new Exception(MissingContentMessage(responseBody));

            content = StripThinkBlocks(content);

            // TOLERANT OF TRUNCATION (Spec §7.3): keep only decision items carrying BOTH a colonist_name and a
            // social_impulse — a max_tokens cut mid-array still yields the complete decisions before it, and a
            // zero-decision "quiet cycle" is a VALID result (no throw). Name/impulse validity is re-checked at
            // consume time in AIColonistGameComponent.
            var result = new Layer2Result();
            foreach (string body in ReadJsonArrayObjectBodies(content, "decisions"))
            {
                string name = ExtractJsonString(body, "colonist_name");
                string impulse = ExtractJsonString(body, "social_impulse");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(impulse)) continue;

                var d = new SocialDecision();
                d.colonist_name = name;
                d.inner_feeling = ExtractJsonString(body, "inner_feeling") ?? "";
                d.social_impulse = impulse;
                d.target_name = ExtractJsonString(body, "target_name") ?? "";
                d.spoken_line = ExtractJsonString(body, "spoken_line") ?? "";
                result.decisions.Add(d);
            }
            return result;
        }

        private Layer3Result ParseLayer3(string responseBody)
        {
            string content = ExtractJsonString(responseBody, "content");
            if (content == null)
                throw new Exception(MissingContentMessage(responseBody));

            content = StripThinkBlocks(content);

            var result = new Layer3Result();
            result.self_reflection = ExtractJsonString(content, "self_reflection") ?? "";
            result.new_semantic_memory = ExtractJsonString(content, "new_semantic_memory") ?? "";
            result.long_term_longing = ExtractJsonString(content, "long_term_longing") ?? "";

            foreach (string body in ReadJsonArrayObjectBodies(content, "relationship_deltas"))
            {
                string target = ExtractJsonString(body, "target_name");
                if (string.IsNullOrEmpty(target)) continue;
                var delta = new RelationshipDelta();
                delta.target_name = target;
                delta.trust_delta = (float)ExtractJsonDouble(body, "trust_delta");
                delta.affection_delta = (float)ExtractJsonDouble(body, "affection_delta");
                delta.respect_delta = (float)ExtractJsonDouble(body, "respect_delta");
                delta.frustration_delta = (float)ExtractJsonDouble(body, "frustration_delta");
                result.relationship_deltas.Add(delta);
            }

            // trait_drift is a nested object — read its body raw, then the two fields out of it. Null when absent or truncated.
            string driftBody = ReadJsonObjectBody(content, "trait_drift");
            if (driftBody != null)
            {
                string traitName = ExtractJsonString(driftBody, "trait_name");
                if (!string.IsNullOrEmpty(traitName))
                {
                    result.trait_drift = new TraitDrift();
                    result.trait_drift.trait_name = traitName;
                    result.trait_drift.delta = (float)ExtractJsonDouble(driftBody, "delta");
                }
            }

            // Only throw when NOTHING usable was extracted — a truncated consolidation still keeps its memories.
            if (string.IsNullOrEmpty(result.self_reflection) && string.IsNullOrEmpty(result.new_semantic_memory))
                throw new Exception("Failed to parse Layer 3 JSON: " + Truncate(content));
            return result;
        }

        /// <summary>R1-Distill models emit &lt;think&gt;…&lt;/think&gt; reasoning blocks — strip before JSON parsing (Spec §10).
        /// TODO(Phase 2): also handle Qwen3-style  if we switch model families.</summary>
        private static string StripThinkBlocks(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"<think\b[\s\S]*?</think>", "").Trim();

        // --- Minimal escape-aware JSON value readers (response side; no external dependencies) ---

        /// <summary>Locates "key" followed by a colon, skipping occurrences inside string values. Returns the index of ':', or -1.</summary>
        private static int FindKeyValueColon(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int idx = 0;
            while ((idx = json.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                if (idx > 0 && json[idx - 1] == '\\') // escaped quote — this is text inside a string value, not a key
                {
                    idx += needle.Length;
                    continue;
                }
                int i = idx + needle.Length;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i < json.Length && json[i] == ':') return i;
                idx += needle.Length;
            }
            return -1;
        }

        /// <summary>Reads the string value for a key, unescaping JSON escapes. Returns null when absent or not a string.</summary>
        private static string ExtractJsonString(string json, string key)
        {
            int colon = FindKeyValueColon(json, key);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            return ReadJsonStringAt(json, i);
        }

        /// <summary>Reads the integer value for a key. Returns 0 when absent or not an int (documented: 0 fails IsValidImpulse → vanilla fallback).</summary>
        private static int ExtractJsonInt(string json, string key)
        {
            int colon = FindKeyValueColon(json, key);
            if (colon < 0) return 0;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && char.IsDigit(json[i])) i++;
            if (i == start) return 0;
            int value;
            return int.TryParse(json.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>Reads the double value for a key (integers, decimals, exponents). Returns 0 when absent or not a number.</summary>
        private static double ExtractJsonDouble(string json, string key)
        {
            int colon = FindKeyValueColon(json, key);
            if (colon < 0) return 0;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            bool isNumber = false;
            while (i < json.Length)
            {
                char c = json[i];
                if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E') { isNumber = true; i++; }
                else if ((c == '-' || c == '+') && i > start + 1) { isNumber = true; i++; } // exponent sign
                else break;
            }
            if (!isNumber) return 0;
            double value;
            return double.TryParse(json.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>Locates "key": [ ... ] and returns each top-level {...} object body in the array as raw JSON text.
        /// String-aware brace-depth scanning (braces inside string values don't count); TRUNCATION-TOLERANT — a cut
        /// mid-object keeps every complete object that came before it.</summary>
        private static List<string> ReadJsonArrayObjectBodies(string json, string key)
        {
            var bodies = new List<string>();
            int colon = FindKeyValueColon(json, key);
            if (colon < 0) return bodies;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '[') return bodies;
            i++; // past '['

            while (true)
            {
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) break;   // truncated before the closing ']' — keep what we have
                if (json[i] == ']') break;     // end of array
                if (json[i] != '{') { i++; continue; } // skip commas / unexpected tokens

                int depth = 0;
                bool inString = false, escaped = false;
                int start = i;
                while (i < json.Length)
                {
                    char c = json[i++];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == '"') inString = false;
                    }
                    else
                    {
                        if (c == '"') inString = true;
                        else if (c == '{') depth++;
                        else if (c == '}' && --depth == 0) break;
                    }
                }

                if (inString || depth != 0) break; // truncated inside THIS object — discard it, keep the prior complete ones
                bodies.Add(json.Substring(start, i - start));
            }
            return bodies;
        }

        /// <summary>Locates "key": { ... } and returns the object body as raw JSON text. Null when absent, null-valued, or truncated.</summary>
        private static string ReadJsonObjectBody(string json, string key)
        {
            int colon = FindKeyValueColon(json, key);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;

            int depth = 0;
            bool inString = false, escaped = false;
            int start = i;
            while (i < json.Length)
            {
                char c = json[i++];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) return json.Substring(start, i - start);
                }
            }
            return null; // truncated mid-object
        }

        /// <summary>Reads a JSON string literal starting at the opening quote. Returns null on an unterminated string.</summary>
        private static string ReadJsonStringAt(string json, int i)
        {
            var sb = new StringBuilder();
            i++; // past opening quote
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '\\')
                {
                    if (i >= json.Length) break;
                    char e = json[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= json.Length)
                            {
                                string hex = json.Substring(i, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                else
                                    sb.Append("\\u").Append(hex); // malformed escape: keep raw rather than corrupt
                                i += 4;
                            }
                            break;
                        default: sb.Append('\\').Append(e); break;
                    }
                }
                else if (c == '"') return sb.ToString();
                else sb.Append(c);
            }
            return null; // unterminated string value
        }

        private static string TrimSlash(string s) => s.EndsWith("/") ? s.Substring(0, s.Length - 1) : s;
        private static string Truncate(string s) => (s == null || s.Length <= 200) ? (s ?? "(null)") : s.Substring(0, 200) + "…";
    }
}
