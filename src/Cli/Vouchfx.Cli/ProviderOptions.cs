using System.CommandLine;
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Cli;

internal sealed class ProviderOptions
{
    private readonly Option<string[]> _directories = new("--providers")
    {
        Description = "Load trusted external providers from this directory. Repeat for additional directories.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = false,
    };
    private readonly Option<bool> _noDefault = new("--no-default-providers")
    {
        Description = "Do not probe ~/.vouchfx/providers (explicit directories and VOUCHFX_PROVIDERS still apply).",
    };

    internal static ProviderOptions Add(Command command)
    {
        var options = new ProviderOptions();
        command.Add(options._directories);
        command.Add(options._noDefault);
        return options;
    }

    internal async Task<int> InvokeAsync(ParseResult result, Func<Task<int>> action, bool discoverProviders = true)
    {
        if (!discoverProviders)
            return await action();
        ProviderRegistryScope scope;
        try
        {
            var directories = ResolveDirectories(result.GetValue(_directories) ?? Array.Empty<string>(),
                Environment.GetEnvironmentVariable("VOUCHFX_PROVIDERS"), result.GetValue(_noDefault),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            scope = new ProviderRegistryScope(ProviderDirectoryScanner.Load(directories));
        }
        catch (Exception exception) when (exception is ProviderLoadException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(DisplaySanitiser.SanitiseForDisplay(exception.Message));
            return ExitCodes.EnvironmentError;
        }
        using (scope)
            return await action();
    }

    internal static string[] ResolveDirectories(IEnumerable<string> explicitDirectories, string? environment,
        bool noDefault, string userProfile)
    {
        try
        {
            var paths = explicitDirectories.Concat((environment ?? "").Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries));
            var defaultPath = Path.Combine(userProfile, ".vouchfx", "providers");
            if (!noDefault && Directory.Exists(defaultPath))
                paths = paths.Append(defaultPath);
            return paths.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new ProviderLoadException($"Invalid provider search path: {exception.Message}", exception);
        }
    }
}
