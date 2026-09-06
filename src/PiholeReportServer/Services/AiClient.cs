using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

public sealed record AiSqlSuggestion(string Sql, string? Notes);

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
    private readonly ILogger<AiClient> _log;

    public AiClient(HttpClient http, IOptions<AiOptions> opt, ILogger<AiClient> log)
    {
        _opt = opt.Value;
        _log = log;
        _http = http;

        if (!string.IsNullOrWhiteSpace(_opt.Endpoint))
        {
            _http.BaseAddress = new Uri(_opt.Endpoint.TrimEnd('/') + "/");
        }
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(30, _opt.TimeoutSeconds));
    }

    public bool Enabled => _opt.Enabled && !string.IsNullOrWhiteSpace(_opt.Endpoint);

    public string Model => _opt.Model;

    public int MaxSummaryRows => _opt.MaxSummaryRows;

    /// <summary>
    /// The schema the model is given. Kept deliberately terse — prompt tokens are
    /// evaluated at roughly 26/sec on the inference host, so every line of schema is
    /// real latency on every question.
    /// </summary>
    private const string SchemaPrompt = """
        You write Microsoft SQL Server (T-SQL) SELECT queries over a Pi-hole DNS warehouse.
        Reply ONLY with JSON: {"sql": "...", "notes": "one short sentence"}

        SCHEMA
        dbo.PiholeQueries(id bigint, ts datetime2 UTC, type int, status int, status_text varchar,
          domain varchar(255), client varchar(255) = IP, forward varchar(255), reply_type int,
          reply_time float SECONDS, dnssec int, ede int)
        dbo.DimClient(ip, name, mac, mac_vendor, interface, num_queries, last_query)
          -- device name column is "name"; vendor is "mac_vendor"; join dc.ip = q.client
        dbo.DimType(type, type_text)
        dbo.DimStatus(status, status_text)
        dbo.GravityDomains(domain, adlist_id) -- one row per (domain, adlist) pair
        dbo.Adlists(id, address, enabled, comment)

        RULES
        - One SELECT statement. Never INSERT, UPDATE, DELETE, DROP, EXEC, MERGE or INTO.
        - Always bound the result with TOP (n).
        - ts is UTC: use DATEADD(day, -n, SYSUTCDATETIME()) for relative ranges.
        - Statuses: blocked = 1,4,5,9,10,11; cache = 3,17; forwarded = 2.
        - Test blocklist membership with EXISTS against GravityDomains, never a join:
          it holds one row per (domain, adlist) and a join multiplies the counts.
        - reply_time is seconds; multiply by 1000 to report milliseconds.
        - Prefer COUNT_BIG(*) over COUNT(*) on this table.
        """;

    /// <summary>Turns a plain-English question into candidate SQL. Never executes it.</summary>
    public async Task<AiSqlSuggestion> SuggestSqlAsync(string question, CancellationToken ct = default)
    {
        EnsureEnabled();

        var request = new OllamaGenerate
        {
            Model = _opt.Model,
            System = SchemaPrompt,
            Prompt = question,
            Stream = false,
            Format = "json",
            Options = new OllamaOptions
            {
                Temperature = _opt.Temperature,
                NumPredict = _opt.MaxOutputTokens,
            },
        };

        var reply = await PostAsync(request, ct);

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
    public async Task<string> SummariseAsync(QueryResult result, string context, CancellationToken ct = default)
    {
        EnsureEnabled();

        var digest = ResultDigest.Build(result, context);

        var request = new OllamaGenerate
        {
            Model = _opt.Model,
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
        };

        return (await PostAsync(request, ct)).Trim();
    }

    /// <summary>Availability probe for the diagnostics page.</summary>
    public async Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return (false, "Disabled by configuration (Ai:Enabled).");
        }

        try
        {
            using var resp = await _http.GetAsync("api/tags", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"{_opt.Endpoint} returned HTTP {(int)resp.StatusCode}.");
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
                string.Equals(x, _opt.Model, StringComparison.OrdinalIgnoreCase));

            return present
                ? (true, $"{_opt.Endpoint} — model '{_opt.Model}' loaded.")
                : (false, $"{_opt.Endpoint} is reachable but '{_opt.Model}' is not installed " +
                          $"(available: {(models.Count == 0 ? "none" : string.Join(", ", models))}).");
        }
        catch (Exception ex)
        {
            return (false, $"{_opt.Endpoint} unreachable: {ex.Message}");
        }
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "The AI assistant is not configured. Set Ai:Enabled and Ai:Endpoint.");
        }
    }

    private async Task<string> PostAsync(OllamaGenerate request, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/generate", request, Json, ct);
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
                    "AI: {Out} tokens in {Sec:F1}s ({Rate:F1} tok/s), prompt {In} tokens.",
                    payload.EvalCount, payload.EvalDuration / 1e9,
                    payload.EvalCount / (payload.EvalDuration / 1e9), payload.PromptEvalCount);
            }

            return payload.Response ?? "";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Distinguish "the host is slow" from "the user navigated away", because on
            // CPU-only hardware the former is common and the message should say so.
            throw new InvalidOperationException(
                $"The inference server did not answer within {_opt.TimeoutSeconds}s. " +
                "It runs on CPU, so long questions can exceed the timeout — try a narrower one.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach the inference server at {_opt.Endpoint}: {ex.Message}");
        }
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

    private sealed class OllamaResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
        [JsonPropertyName("eval_count")] public int EvalCount { get; set; }
        [JsonPropertyName("eval_duration")] public long EvalDuration { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; set; }
    }
}
