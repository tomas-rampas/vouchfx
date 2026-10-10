namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>A refused pure binding with known authoring paths that may repair it when edited.</summary>
public sealed class EnvironmentBindingException : ArgumentException
{
    /// <summary>The maximum number of distinct authoring paths retained by a binding.</summary>
    public const int MaximumInputFiles = 256;

    /// <summary>Initialises a refusal without input evidence.</summary>
    public EnvironmentBindingException() { }

    /// <summary>Initialises a refusal with an author-facing diagnostic.</summary>
    public EnvironmentBindingException(string? message) : base(message) { }

    /// <summary>Initialises a refusal with an author-facing diagnostic and underlying exception.</summary>
    public EnvironmentBindingException(string? message, Exception? innerException) : base(message, innerException) { }

    /// <summary>Copies bounded, absolute authoring paths without reading files or resolving secrets.</summary>
    public EnvironmentBindingException(string? message, IReadOnlyList<string> inputFiles) : base(message)
    {
        ArgumentNullException.ThrowIfNull(inputFiles);
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputFiles) TrackInput(inputs, input);
        InputFiles = Array.AsReadOnly(inputs.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Known authoring files, including missing inputs; never configuration or secret values.</summary>
    public IReadOnlyList<string> InputFiles { get; } = Array.Empty<string>();

    internal static void TrackInput(ISet<string> inputs, string path)
    {
        if (path is null || path.Length > 4096 || !Path.IsPathFullyQualified(path)
            || path.Any(char.IsControl) || path.Contains("${", StringComparison.Ordinal))
            throw new ArgumentException("environment.source requires literal absolute authoring paths of at most 4096 characters.");
        if (inputs.Count >= MaximumInputFiles && !inputs.Contains(path))
            throw new ArgumentException("environment.source exceeds the 256-file authoring input limit. Reduce the selected input files.");
        inputs.Add(path);
    }
}
