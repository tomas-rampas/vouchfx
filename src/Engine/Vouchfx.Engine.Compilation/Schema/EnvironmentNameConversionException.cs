using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>Safe provenance for recognized env names which cannot enter JSON emission.</summary>
internal sealed class EnvironmentNameConversionException : Exception
{
    internal EnvironmentNameConversionException(string location) : base(EnvironmentVariableNames.SyntaxMessage) => Location = location;
    internal string Location { get; }
}
