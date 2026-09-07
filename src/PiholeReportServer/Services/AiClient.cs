using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

public sealed record AiSqlSuggestion(string Sql, string? Notes);

/// <summary>One message in a multi-turn exchange with the model.</summary>
public sealed record AiChatMessage(string Role, string Content);

/// <summary>
/// Talks to the local Ollama server.
/// <para>
/// Everything the model returns is untrusted text. Generated SQL is never executed
/// directly: it goes through <see cref="SqlGuard"/> and then runs under the same
/// read-only login as the console, so a bad or hostile completion is refused by the
/// same two mechanisms that already guard hand-written SQL.
/// </para>
/// </summary>
public sealed class AiClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AiOptions _opt;
    private readonly AiEndpointSelector _hosts;
    private readonly ILogger<AiClient> _log;

    public AiClient(
        HttpClient http, IOptions<AiOptions> opt, AiEndpointSelector hosts, ILogger<AiClient> log)
    {
        _opt = opt.Value;
        _hosts = hosts;
        _log = log;
        _http = http;

        // No BaseAddress: which host a request goes to is decided per request now, and
        // BaseAddress is shared mutable state on a client the factory pools.
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(30, _opt.TimeoutSeconds));
    }

    public bool Enabled => _opt.Enabled && !string.IsNullOrWhiteSpace(_opt.Endpoint);

    /// <summary>The model currently in use, which differs when the fallback is active.</summary>
    public string Model => _hosts.Current.Model;

    /// <summary>The host currently in use, so the UI can say where an answer came from.</summary>
    public string ActiveEndpoint => _hosts.Current.Endpoint;

    /// <summary>True while the preferred host is unreachable and the standby is serving.</summary>
    public bool UsingFallback => _hosts.Current.IsFallback;

    public int MaxSummaryRows => _opt.MaxSummaryRows;

    /// <summary>Shared with the agent, so both describe the same schema.</summary>
    internal const string SchemaForAgent = SchemaPrompt;

    /// <summary>
    /// The schema the model is given. Still kept tight, but no longer for latency:
    /// the GPU host evaluates prompt tokens at roughly 1,700/sec, so schema length
    /// costs milliseconds rather than the seconds it cost on CPU. It stays terse
    /// because a shorter schema is one the model follows more reliably.
    /// </summary>
    private const string SchemaPrompt = """
        You write Microsoft SQL Server (T-SQL) SELECT queries over a Pi-hole DNS warehouse.
        Reply ONLY with JSON: {"sql": "...", "notes": "one short sentence"}

        SCHEMA
        dbo.PiholeQueries(id bigint, ts datetime2 UTC, type int, status int, status_text varchar,
          domain varchar(255), client varchar(255) = IP, forward varchar(255), reply_type int,
          reply_time float SECONDS, dnssec int, ede int)
        dbo.vClient(ip, mac, mac_vendor, interface, num_queries, last_query,
          display_name, name_source, ftl_name, name_ambiguous bit)
          -- Devices. USE THIS, never dbo.DimClient: display_name resolves the name
          -- from the Omada controller (the DHCP server, and where devices are named
          -- by hand) and then AD DNS, before falling back to FTL's reverse DNS,
          -- which gave 34 different devices the name "ALIEN01". display_name is
          -- never null. name_source says which source won.
          -- ONE ROW PER IP, so a device with IPv4 plus several IPv6 addresses has
          -- several rows. Join dc.ip = q.client.
          -- num_queries is a LIFETIME PER-DEVICE total that FTL copies onto every
          -- one of that device's IP rows. NEVER SUM it - that multiplies by the
          -- number of addresses (52 devices here, one inflated 6x). Use
          -- MAX(num_queries) GROUP BY mac, and prefer counting PiholeQueries.
        dbo.DimType(type, type_text)
        dbo.DimStatus(status, status_text)
        dbo.GravityDomains(domain, adlist_id) -- one row per (domain, adlist) pair
        dbo.Adlists(id, address, enabled, comment)
        dbo.vDomainCategory(domain, canonical_category, source_category, subcategory,
          description, source, confidence)
          -- what the domain HOSTS. USE THIS, never dbo.DomainCategory: the four
          -- corpora that fill it disagree on names (ut1 says "ads", the rules say
          -- "advertising"), and this view reconciles them. Filtering the raw table
          -- returns a fraction of the matches and looks like a complete answer -
          -- "advertising" alone finds 2,310 of 10,497.
          -- ALWAYS filter and group on canonical_category. It is one of:
          --   advertising, tracking, analytics, infrastructure, cloud, software,
          --   streaming, media, sports, social, dating, shopping, news, gaming,
          --   iot, finance, adult, gambling, drugs, malware, threat, privacy,
          --   filesharing, shortener, communication, search, local, work,
          --   education, government, health, travel, ai, unknown.
          --   threat = fraud/scam/abuse/stalkerware; malware = malicious code.
          --   privacy = VPN/proxy/DoH, i.e. tools that bypass Pi-hole.
          -- source_category is what the corpus called it - use it only when asked
          -- which list said so. source in: manual, rule, ut1, blp, model - prefer
          -- rule/ut1/blp over model when accuracy matters. Join on
          -- dc.domain = q.domain (exact FQDN).
        dbo.DomainMetadata(domain, title, description, http_status, error)
          -- what the site itself served, fetched directly.

        RULES
        - One SELECT statement. Never INSERT, UPDATE, DELETE, DROP, EXEC, MERGE or INTO.
        - TOP goes IMMEDIATELY AFTER SELECT. This is the single most common mistake:
            RIGHT: SELECT TOP (25) domain, COUNT_BIG(*) AS n FROM ... GROUP BY domain ORDER BY n DESC
            WRONG: SELECT domain, COUNT_BIG(*) AS n FROM ... GROUP BY domain ORDER BY n DESC TOP (25)
            WRONG: SELECT domain, COUNT_BIG(*) AS n FROM ... GROUP BY domain LIMIT 25
          T-SQL has no LIMIT and no trailing TOP. If a row count was requested, put it
          in the TOP after SELECT - never append it.
        - A ranked list needs ORDER BY, and TOP without ORDER BY returns an arbitrary
          n rows. Always order a "top N" by the count, descending.
        - ts is UTC: use DATEADD(day, -n, SYSUTCDATETIME()) for relative ranges.
        - Statuses: blocked = 1,4,5,9,10,11; cache = 3,17; forwarded = 2.
        - Test blocklist membership with EXISTS against GravityDomains, never a join:
          it holds one row per (domain, adlist) and a join multiplies the counts.
        - reply_time is seconds; multiply by 1000 to report milliseconds.
        - Prefer COUNT_BIG(*) over COUNT(*) on this table.
        - Count activity from PiholeQueries. DimClient.num_queries is a lifetime
          per-device figure duplicated across IP rows and is not comparable to it.
        - Per-device totals: GROUP BY dc.mac, not dc.ip. To show a device by name,
          group on dc.display_name.
        - For "what kind of traffic is this", join dbo.vDomainCategory rather than
          guessing from the domain name, and group on canonical_category.

        RETURN ROWS, NOT A SINGLE NUMBER
        - "show me", "list", "what are", "which domains", "top N" all want a TABLE of
          rows. Return the rows. Asked for "all the youtube traffic", answer with the
          domains and their counts, NOT SELECT COUNT_BIG(*) - one number is not a list
          and the person asking cannot see anything in it.
        - Use COUNT_BIG(*) as a COLUMN alongside the thing being counted, not as the
          only column. Return the grouping key too.
        - Only return a bare scalar when the question is genuinely "how many".

        MATCHING NAMES AND DOMAINS
        - Device names are full hostnames like 'Pixel-9-Pro-XL' or 'WINSERVER01'. A
          person naming a device will not type it exactly, so match with
          dc.display_name LIKE '%pixel%', never dc.display_name = 'Pixel'.
        - To find a service by name, filter the QUERY domain:
          q.domain LIKE '%youtube%'. Do not put a domain pattern inside an EXISTS
          against the category view - that is for category lookups, and a LIKE on
          dc.domain there matches nothing useful.
        - A service spans several domains (youtube.com, ytimg.com, googlevideo.com),
          so prefer grouping the rows and letting the reader see them over guessing
          one domain.
        """;

    /// <summary>Turns a plain-English question into candidate SQL. Never executes it.</summary>
    public async Task<AiSqlSuggestion> SuggestSqlAsync(string question, CancellationToken ct = default)
    {
        EnsureEnabled();

        var reply = await WithFailoverAsync((target, token) => PostAsync(new OllamaGenerate
        {
            Model = target.Model,
            System = SchemaPrompt,
            Prompt = question,
            Stream = false,
            Format = "json",
            Options = new OllamaOptions
            {
                Temperature = _opt.Temperature,
                NumPredict = _opt.MaxOutputTokens,
            },
        }, target, token), ct);

        // format:"json" constrains the grammar, but a truncated or odd completion is
        // still possible, so parse defensively rather than trusting it.
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var sql = doc.RootElement.TryGetProperty("sql", out var s) ? s.GetString() : null;
            var notes = doc.RootElement.TryGetProperty("notes", out var n) ? n.GetString() : null;

            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidOperationException(
                    "The model replied without a query. Try rephrasing the question.");
            }
            return new AiSqlSuggestion(sql.Trim(), notes);
        }
        catch (JsonException)
        {
            _log.LogWarning("Model returned non-JSON for a SQL suggestion: {Reply}",
                reply.Length > 400 ? reply[..400] : reply);
            throw new InvalidOperationException(
                "The model's reply could not be read as JSON. Try rephrasing the question.");
        }
    }

    /// <summary>
    /// Narrates a pre-computed digest of a result set. The digest is built in
    /// <see cref="ResultDigest"/> rather than sending rows, because sending rows was
    /// measured at ~150 seconds for 100 rows and produced a recital rather than an
    /// insight.
    /// </summary>
    public Task<string> SummariseAsync(QueryResult result, string context, CancellationToken ct = default) =>
        SummariseFromDigestAsync(ResultDigest.Build(result, context), ct);

    /// <summary>
    /// Narrates an already-built digest. Separate from <see cref="SummariseAsync"/>
    /// because the digest is computed when the page renders and posted back by the
    /// browser — that keeps "explain" to one small request instead of shipping
    /// thousands of rows to the server and back.
    /// </summary>
    public async Task<string> SummariseFromDigestAsync(string digest, CancellationToken ct = default)
    {
        EnsureEnabled();

        var reply = await WithFailoverAsync((target, token) => PostAsync(new OllamaGenerate
        {
            Model = target.Model,
            System = """
                You are a network analyst summarising a DNS query report for its owner.
                You are given exact pre-computed statistics, not raw rows.
                Write at most four short bullet points about what stands out: concentration,
                notable devices or domains, and anything that looks anomalous.
                Use only the figures given. Never invent a number. Do not restate every value.
                Plain text, no markdown headings.
                """,
            Prompt = digest,
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = 0.2,
                NumPredict = Math.Min(_opt.MaxOutputTokens, 350),
            },
        }, target, token), ct);

        return reply.Trim();
    }

    /// <summary>
    /// One turn of a multi-turn exchange, returning the raw JSON reply. Used by the
    /// agent, which needs the model to see its own earlier queries and their results.
    /// </summary>
    public async Task<string> ChatJsonAsync(
        IReadOnlyList<AiChatMessage> messages, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsureEnabled();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }

        return await WithFailoverAsync(async (target, token) =>
        {
            var request = new OllamaChat
            {
                Model = target.Model,
                Messages = messages
                    .Select(m => new OllamaMessage { Role = m.Role, Content = m.Content }).ToList(),
                Stream = false,
                Format = "json",
                Options = new OllamaOptions
                {
                    Temperature = _opt.Temperature,
                    NumPredict = _opt.MaxOutputTokens,
                },
            };

            try
            {
                using var resp = await _http.PostAsJsonAsync(target.Uri("api/chat"), request, Json, token);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"The inference server returned HTTP {(int)resp.StatusCode} on chat.");
                }
                var payload = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>(Json, token);
                return payload?.Message?.Content ?? "";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"{target.Endpoint} ran out of time on this step. Try a narrower question.");
            }
        }, cts.Token);
    }

    /// <summary>Availability probe for the diagnostics page.</summary>
    public async Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return (false, "Disabled by configuration (Ai:Enabled).");
        }

        // Probe both hosts rather than only the active one. The whole value of a
        // standby is knowing whether it is actually there BEFORE it is needed; a
        // silently broken fallback is worse than none, because it reads as cover.
        var primary = await ProbeOneAsync(_hosts.Primary, ct);
        if (_hosts.Fallback is null)
        {
            return primary;
        }

        var fallback = await ProbeOneAsync(_hosts.Fallback, ct);
        var cooldown = _hosts.PrimaryCooldownRemaining;
        var note = cooldown is null
            ? ""
            : $" Primary is in cooldown for another {cooldown.Value.TotalSeconds:N0}s, " +
              "so requests are going to the standby.";

        return (primary.Ok || fallback.Ok,
                $"Primary: {primary.Detail} Standby: {fallback.Detail}{note}");
    }

    private async Task<(bool Ok, string Detail)> ProbeOneAsync(AiTarget target, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(target.Uri("api/tags"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"{target.Endpoint} returned HTTP {(int)resp.StatusCode}.");
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var models = doc.RootElement.TryGetProperty("models", out var m)
                ? m.EnumerateArray()
                   .Select(e => e.TryGetProperty("name", out var nm) ? nm.GetString() : null)
                   .Where(x => x is not null)
                   .ToList()
                : [];

            var present = models.Any(x =>
                string.Equals(x, target.Model, StringComparison.OrdinalIgnoreCase));

            return present
                ? (true, $"{target.Endpoint} — model '{target.Model}' available.")
                : (false, $"{target.Endpoint} is reachable but '{target.Model}' is not installed " +
                          $"(available: {(models.Count == 0 ? "none" : string.Join(", ", models))}).");
        }
        catch (Exception ex)
        {
            return (false, $"{target.Endpoint} unreachable: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs one logical request, moving to the standby host if the chosen one cannot
    /// be reached at all.
    /// <para>
    /// Only <see cref="HttpRequestException"/> triggers failover — a refused
    /// connection, a DNS failure, no route. A timeout does not, and nor does an HTTP
    /// error status: both mean a server answered, and the standby is the slower
    /// machine, so failing over on slowness would only produce a slower failure.
    /// </para>
    /// </summary>
    private async Task<T> WithFailoverAsync<T>(
        Func<AiTarget, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        var targets = _hosts.Attempts();
        HttpRequestException? last = null;

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            try
            {
                var result = await attempt(target, ct);
                _hosts.ReportReachable(target);
                return result;
            }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
            {
                _hosts.ReportUnreachable(target);
                last = ex;
                if (i + 1 < targets.Count)
                {
                    _log.LogWarning(
                        "AI: {Failed} could not be reached ({Reason}); trying {Next}.",
                        target.Endpoint, ex.Message, targets[i + 1].Endpoint);
                }
            }
        }

        var where = targets.Count > 1
            ? $"either inference host ({string.Join(" or ", targets.Select(t => t.Endpoint))})"
            : $"the inference server at {targets[0].Endpoint}";
        throw new InvalidOperationException($"Could not reach {where}: {last?.Message}", last);
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "The AI assistant is not configured. Set Ai:Enabled and Ai:Endpoint.");
        }
    }

    private async Task<string> PostAsync(
        OllamaGenerate request, AiTarget target, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(target.Uri("api/generate"), request, Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"The inference server returned HTTP {(int)resp.StatusCode}. {Clip(detail)}");
            }

            var payload = await resp.Content.ReadFromJsonAsync<OllamaResponse>(Json, ct)
                          ?? throw new InvalidOperationException("Empty reply from the inference server.");

            if (payload.EvalCount > 0 && payload.EvalDuration > 0)
            {
                _log.LogInformation(
                    "AI: {Out} tokens in {Sec:F1}s ({Rate:F1} tok/s), prompt {In} tokens, on {Host}.",
                    payload.EvalCount, payload.EvalDuration / 1e9,
                    payload.EvalCount / (payload.EvalDuration / 1e9), payload.PromptEvalCount,
                    target.Endpoint);
            }

            return payload.Response ?? "";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Distinguish "the host is slow" from "the user navigated away", because on
            // CPU-only hardware the former is common and the message should say so.
            throw new InvalidOperationException(
                $"{target.Endpoint} did not answer within {_opt.TimeoutSeconds}s. " +
                "Try a narrower question.");
        }
        // HttpRequestException is deliberately NOT caught here: WithFailoverAsync must
        // see it to decide whether to move to the standby host.
    }

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed class OllamaGenerate
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("system")] public string? System { get; set; }
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
    }

    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("num_predict")] public int NumPredict { get; set; }
    }

    private sealed class OllamaChat
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; set; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
    }

    private sealed class OllamaResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
        [JsonPropertyName("eval_count")] public int EvalCount { get; set; }
        [JsonPropertyName("eval_duration")] public long EvalDuration { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; set; }
    }
}
