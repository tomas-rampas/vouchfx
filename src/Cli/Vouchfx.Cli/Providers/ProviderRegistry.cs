using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Cli.Providers;

internal sealed record ProviderRegistryEntry(
    [property: JsonPropertyName("stepKindId")] string StepKindId,
    [property: JsonPropertyName("nuget")] string? Nuget,
    [property: JsonPropertyName("description")] string? Description);

internal static class ProviderRegistry
{
    internal const string Url = "https://raw.githubusercontent.com/tomas-rampas/vouchfx-providers/main/registry/community-providers.json";
    internal static async Task<IReadOnlyList<ProviderRegistryEntry>> ReadAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(new ProviderHttpResource.HttpsHandler(new HttpClientHandler { AllowAutoRedirect = false }));
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = new BoundedStream(await response.Content.ReadAsStreamAsync(timeout.Token), 1024 * 1024);
        var entries = await JsonSerializer.DeserializeAsync<ProviderRegistryEntry[]>(stream, cancellationToken: timeout.Token)
            ?? throw new ProviderLoadException("Community provider registry is invalid.");
        if (entries.Length > 5000 || entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.StepKindId)))
            throw new ProviderLoadException("Community provider registry is invalid or exceeds its entry limit.");
        return entries;
    }

    internal static string Resolve(string argument, IReadOnlyList<ProviderRegistryEntry> entries)
    {
        var matches = entries.Where(entry => string.Equals(entry.StepKindId, argument, StringComparison.Ordinal)
            || string.Equals(entry.Nuget, argument, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Nuget))
        {
            var candidates = entries.Where(entry => entry.StepKindId.Contains(argument.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.StepKindId).Distinct().Take(10).ToArray();
            throw new ProviderLoadException("Provider ID is absent, ambiguous or source-only in the community registry. "
                + "Use an exact published NuGet ID. Nearby step kinds: " + string.Join(", ", candidates));
        }
        ProviderStore.ValidateId(matches[0].Nuget!);
        return matches[0].Nuget!;
    }
}
