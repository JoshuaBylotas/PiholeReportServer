using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// The diagnostics probe. Its whole job is to answer "are the AI hosts up" without
/// lying in either direction, so these pin the two ways it could: calling a host
/// healthy when it cannot serve the model asked of it, and calling a host broken
/// over a tag spelling that generation would have resolved anyway.
/// </summary>
public class AiHealthProbeTests
{
    private const string Fast = "http://10.20.0.139:11434";
    private const string Slow = "http://10.20.0.14:11434";

    /// <summary>Answers each host from a script rather than the network.</summary>
    private sealed class Handler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request.RequestUri!));
    }

    private static HttpResponseMessage Tags(params string[] models)
    {
        var json = "{\"models\":[" +
                   string.Join(",", models.Select(m => "{\"name\":\"" + m + "\"}")) +
                   "]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static AiClient Build(
        Func<Uri, HttpResponseMessage> reply,
        string model = "qwen2.5:14b-instruct",
        string fallbackEndpoint = Slow,
        string fallbackModel = "qwen2.5:7b-instruct",
        bool enabled = true,
        string endpoint = Fast)
    {
        var opt = Options.Create(new AiOptions
        {
            Enabled = enabled,
            Endpoint = endpoint,
            Model = model,
            FallbackEndpoint = fallbackEndpoint,
            FallbackModel = fallbackModel,
        });

        var hosts = new AiEndpointSelector(opt, NullLogger<AiEndpointSelector>.Instance);
        var http = new HttpClient(new Handler(reply));
        return new AiClient(http, opt, hosts, NullLogger<AiClient>.Instance);
    }

    [Fact]
    public async Task Reports_both_hosts_even_when_only_one_is_in_use()
    {
        // The point of a standby is knowing it is there BEFORE it is needed. A probe
        // that only checked the active host would report a healthy system right up to
        // the moment the laptop left the building.
        var ai = Build(_ => Tags("qwen2.5:14b-instruct", "qwen2.5:7b-instruct"));

        var health = await ai.ProbeAsync();

        Assert.Equal(2, health.Hosts.Count);
        Assert.Equal(["Preferred", "Standby"], health.Hosts.Select(h => h.Role));
        Assert.All(health.Hosts, h => Assert.True(h.Ok));
    }

    [Fact]
    public async Task Each_host_is_checked_against_the_model_it_is_asked_for()
    {
        // The standby runs a smaller model. Probing both against the primary's model
        // would report the standby broken for doing exactly what it is configured to.
        var ai = Build(uri => uri.ToString().Contains("10.20.0.139")
            ? Tags("qwen2.5:14b-instruct")
            : Tags("qwen2.5:7b-instruct"));

        var health = await ai.ProbeAsync();

        Assert.All(health.Hosts, h => Assert.True(h.Ok, h.Role + ": " + h.Problem));
    }

    [Fact]
    public async Task A_reachable_host_missing_its_model_is_not_up()
    {
        // The failure that looks healthiest: the host answers, so a connectivity
        // check passes, but every generation against it fails.
        var ai = Build(_ => Tags("llama3.2:3b"));

        var health = await ai.ProbeAsync();
        var primary = health.Hosts[0];

        Assert.True(primary.Reachable);
        Assert.False(primary.ModelInstalled);
        Assert.False(primary.Ok);
        Assert.False(health.AnyUp);
        Assert.Contains("ollama pull", primary.Problem);
        // The tags it does have are carried through, because the usual cause is a
        // tag typo and the fix is unguessable without the list.
        Assert.Contains("llama3.2:3b", primary.InstalledModels);
    }

    [Fact]
    public async Task An_untagged_model_matches_the_latest_tag()
    {
        // Ollama reports "qwen2.5-coder:3b" but resolves a bare "qwen2.5-coder" to
        // :latest at generation time. Reporting that configuration as broken would
        // send someone chasing a host that works.
        var ai = Build(_ => Tags("qwen2.5-coder:latest"),
                       model: "qwen2.5-coder", fallbackEndpoint: "");

        var health = await ai.ProbeAsync();

        Assert.True(health.Hosts[0].Ok);
    }

    [Fact]
    public async Task A_tagged_model_does_not_match_a_different_tag()
    {
        // The inverse of the above: :7b and :14b are different models, and swapping
        // one for the other silently changes every answer the Analyst gives.
        var ai = Build(_ => Tags("qwen2.5:7b-instruct"),
                       model: "qwen2.5:14b-instruct", fallbackEndpoint: "");

        var health = await ai.ProbeAsync();

        Assert.False(health.Hosts[0].Ok);
    }

    [Fact]
    public async Task An_unreachable_host_is_reported_rather_than_thrown()
    {
        // A diagnostics page whose job is to report failures must not fail on one.
        var ai = Build(_ => throw new HttpRequestException("No route to host"),
                       fallbackEndpoint: "");

        var health = await ai.ProbeAsync();

        Assert.False(health.Hosts[0].Reachable);
        Assert.False(health.AnyUp);
        Assert.Contains("No route to host", health.Hosts[0].Problem);
    }

    [Fact]
    public async Task Something_that_is_not_ollama_is_distinguished_from_nothing_at_all()
    {
        // A reverse proxy or a wrong port answers, and "unreachable" would send
        // someone to check the network instead of the address.
        var ai = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound),
                       fallbackEndpoint: "");

        var health = await ai.ProbeAsync();

        Assert.False(health.Hosts[0].Ok);
        Assert.Contains("404", health.Hosts[0].Problem);
    }

    [Fact]
    public async Task A_probe_never_puts_the_preferred_host_into_cooldown()
    {
        // The probe runs on a much shorter leash than a real request, so a host that
        // fails it may still generate. Letting the page's own probe trigger failover
        // would mean opening diagnostics degrades what it is reporting on.
        var opt = Options.Create(new AiOptions
        {
            Enabled = true,
            Endpoint = Fast,
            Model = "qwen2.5:14b-instruct",
            FallbackEndpoint = Slow,
            FallbackModel = "qwen2.5:7b-instruct",
        });
        var hosts = new AiEndpointSelector(opt, NullLogger<AiEndpointSelector>.Instance);
        var http = new HttpClient(new Handler(_ => throw new HttpRequestException("down")));
        var ai = new AiClient(http, opt, hosts, NullLogger<AiClient>.Instance);

        await ai.ProbeAsync();

        Assert.Null(hosts.PrimaryCooldownRemaining);
        Assert.False(hosts.Current.IsFallback);
    }

    [Fact]
    public async Task Says_why_when_inference_is_switched_off()
    {
        // "Disabled" and "down" need different responses, and an empty panel reads
        // as the second.
        var off = await Build(_ => Tags("anything"), enabled: false).ProbeAsync();
        Assert.False(off.Enabled);
        Assert.Contains("Ai:Enabled", off.Disabled);

        var unset = await Build(_ => Tags("anything"), endpoint: "").ProbeAsync();
        Assert.False(unset.Enabled);
        Assert.Contains("Ai:Endpoint", unset.Disabled);
    }
}
