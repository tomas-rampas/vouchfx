using System.CommandLine;
using System.Text.Json;
using Vouchfx.Cli.Providers;
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Cli;

internal static class ProvidersCommand
{
    internal static Command Build()
    {
        var command = new Command("providers", "Install, list or uninstall trusted Community step providers. Provider code is not sandboxed.");
        var install = new Command("install", "Install or replace a provider from a NuGet ID or community registry step kind.");
        var installId = new Argument<string>("id"); install.Add(installId);
        var packageId = new Option<bool>("--package-id") { Description = "Treat the argument as an exact NuGet package ID and do not consult the Community registry." }; install.Add(packageId);
        var version = new Option<string?>("--version") { Description = "Exact NuGet version; defaults to the latest stable root version." }; install.Add(version);
        var source = new Option<string>("--source") { Description = "HTTPS NuGet v3 endpoint or an explicit local feed directory.", DefaultValueFactory = _ => ProviderPackages.DefaultSource }; install.Add(source);
        var installDirectory = DirectoryOption(); install.Add(installDirectory);
        install.SetAction((result, token) => GuardAsync(async () =>
        {
            var store = new ProviderStore(result.GetValue(installDirectory));
            var installed = await store.InstallAsync(result.GetValue(installId)!, result.GetValue(version), result.GetValue(source)!, token,
                registry: result.GetValue(packageId) ? null : ProviderRegistry.ReadAsync);
            WriteLine($"Installed {installed.Id} {installed.Version} at {installed.Directory}. Community code is trusted and unendorsed; package signatures are not verified.");
            return ExitCodes.Success;
        }, token));
        command.Add(install);

        var uninstall = new Command("uninstall", "Remove a provider installed under its NuGet package ID.");
        var uninstallId = new Argument<string>("id"); uninstall.Add(uninstallId);
        var uninstallDirectory = DirectoryOption(); uninstall.Add(uninstallDirectory);
        uninstall.SetAction((result, token) => GuardAsync(() =>
        {
            var id = result.GetValue(uninstallId)!;
            var removed = new ProviderStore(result.GetValue(uninstallDirectory)).Uninstall(id);
            WriteLine(removed ? $"Uninstalled {id}." : $"Provider {id} is not installed in this directory.");
            return Task.FromResult(ExitCodes.Success);
        }, token));
        command.Add(uninstall);

        var list = new Command("list", "List installed providers and the public community registry; offline failures retain installed results.");
        var listDirectory = DirectoryOption(); list.Add(listDirectory);
        var offline = new Option<bool>("--offline") { Description = "List installed providers without contacting the community registry." }; list.Add(offline);
        var json = new Option<bool>("--json") { Description = "Write installed and available provider records as JSON." }; list.Add(json);
        list.SetAction((result, token) => GuardAsync(async () =>
        {
            var listing = await ReadListAsync(result.GetValue(listDirectory), result.GetValue(offline), ProviderRegistry.ReadAsync, token);
            var installed = listing.Installed; var available = listing.Available; var note = listing.Note;
            if (result.GetValue(json))
                Console.Out.WriteLine(JsonSerializer.Serialize(new { schemaVersion = CliJsonContract.SchemaVersion, engineVersion = CliJsonContract.EngineVersion, installed, available, note }, CliJsonContract.Options));
            else
            {
                WriteLine("Installed providers:");
                foreach (var item in installed) WriteLine($"  {item.Id} {item.Version}  {item.Directory}");
                WriteLine("Available Community providers (unendorsed):");
                foreach (var item in available) WriteLine($"  {item.StepKindId}  {item.Nuget ?? "source-only"}");
                if (note is not null) WriteLine(note);
            }
            return ExitCodes.Success;
        }, token));
        command.Add(list);
        return command;
    }

    internal sealed record ProviderListing(IReadOnlyList<InstalledProvider> Installed, IReadOnlyList<ProviderRegistryEntry> Available, string? Note);

    internal static async Task<ProviderListing> ReadListAsync(string? explicitRoot, bool offline,
        Func<CancellationToken, Task<IReadOnlyList<ProviderRegistryEntry>>> readRegistry, CancellationToken token)
    {
        var roots = explicitRoot is not null ? new[] { Path.GetFullPath(explicitRoot) }
            : ProviderOptions.ResolveDirectories(Array.Empty<string>(), Environment.GetEnvironmentVariable("VOUCHFX_PROVIDERS"),
                false, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var installed = roots.SelectMany(root => new ProviderStore(root).List()).ToArray();
        IReadOnlyList<ProviderRegistryEntry> available = Array.Empty<ProviderRegistryEntry>();
        string? note = offline ? "Offline: available registry was not requested." : null;
        if (note is null)
        {
            try { available = await readRegistry(token); }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException or JsonException or ProviderLoadException)
            { note = "Community registry is unavailable; showing installed providers only."; }
        }
        return new ProviderListing(installed, available, note);
    }

    private static Option<string?> DirectoryOption() => new("--directory")
    { Description = "Override ~/.vouchfx/providers with this installation root. Listing this directory ignores environment and home paths." };

    private static void WriteLine(string value) => Console.Out.WriteLine(DisplaySanitiser.SanitiseForDisplay(value));

    private static async Task<int> GuardAsync(Func<Task<int>> action, CancellationToken token)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Console.Error.WriteLine("Provider operation cancelled."); return ExitCodes.EnvironmentError; }
        catch (ProviderLoadException exception)
        { Console.Error.WriteLine(DisplaySanitiser.SanitiseForDisplay(exception.Message)); return ExitCodes.EnvironmentError; }
        catch (Exception)
        {
            // Protocol/configuration diagnostics may contain private feed material. Do not echo them.
            Console.Error.WriteLine("Provider operation failed. Check the source, package compatibility and filesystem access.");
            return ExitCodes.EnvironmentError;
        }
    }
}
