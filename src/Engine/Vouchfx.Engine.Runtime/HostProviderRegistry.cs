using System.Reflection;
using System.Text.RegularExpressions;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime;

/// <summary>Host admission before a discovered provider contributes its schema or executes.</summary>
internal static class HostProviderRegistry
{
    internal static string EngineVersion => typeof(HostProviderRegistry).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "VouchfxEngineVersion").Value
        ?? throw new InvalidOperationException("The engine capability version stamp is missing.");

    internal static StepKindRegistry Build(IEnumerable<Assembly> assemblies) => Build(assemblies, EngineVersion);

    // Kept internal so a host cannot accidentally replace the running engine's version.
    internal static StepKindRegistry Build(IEnumerable<Assembly> assemblies, string engineVersion)
    {
        var actual = EngineSemVersion.Parse(engineVersion);
        return StepKindRegistry.BuildAndFreeze(assemblies, (kind, metadata) =>
        {
            var minimumText = metadata.MinEngineVersion;
            EngineSemVersion minimum;
            try
            {
                minimum = EngineSemVersion.Parse(minimumText);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"Provider '{kind.Family}.{kind.Provider}' declares an invalid MinEngineVersion '{minimumText}'.", exception);
            }

            if (actual.CompareTo(minimum) < 0)
                throw new InvalidOperationException(
                    $"Provider '{kind.Family}.{kind.Provider}' requires engine {minimumText}; the running engine is {engineVersion}.");
        });
    }
}

// SemVer 2 precedence, including numeric prerelease identifiers. Build metadata has no precedence.
internal sealed class EngineSemVersion : IComparable<EngineSemVersion>
{
    private static readonly Regex s_pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly string[] _core;
    private readonly string[] _prerelease;

    private EngineSemVersion(string[] core, string[] prerelease) => (_core, _prerelease) = (core, prerelease);

    internal static EngineSemVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = s_pattern.Match(value);
        if (!match.Success)
            throw new ArgumentException("Engine versions must be SemVer 2 strings.", nameof(value));
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (prerelease.Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0'))
            throw new ArgumentException("Numeric prerelease identifiers cannot have leading zeroes.", nameof(value));
        return new EngineSemVersion(new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value }, prerelease);
    }

    public int CompareTo(EngineSemVersion? other)
    {
        if (other is null)
            return 1;
        for (var index = 0; index < _core.Length; index++)
        {
            var comparison = CompareNumeric(_core[index], other._core[index]);
            if (comparison != 0)
                return comparison;
        }
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return (_prerelease.Length == 0 ? 1 : 0).CompareTo(other._prerelease.Length == 0 ? 1 : 0);
        for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            var left = _prerelease[index];
            var right = other._prerelease[index];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            var comparison = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : string.Compare(left, right, StringComparison.Ordinal);
            if (comparison != 0)
                return comparison;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool IsNumeric(string value) => value.All(character => character is >= '0' and <= '9');
    private static int CompareNumeric(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : string.Compare(left, right, StringComparison.Ordinal);
}
