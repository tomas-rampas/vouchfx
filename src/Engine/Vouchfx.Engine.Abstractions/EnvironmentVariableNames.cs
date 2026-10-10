namespace Vouchfx.Engine.Abstractions;

/// <summary>The portable, case-sensitive grammar for authored environment variable names.</summary>
public static class EnvironmentVariableNames
{
    /// <summary>A bounded diagnostic which never embeds the rejected name or its value.</summary>
    public const string SyntaxMessage = "Environment variable names must start with an ASCII letter or underscore and contain only ASCII letters, digits, underscores or dots.";

    /// <summary>Checks the entire name without trimming, normalization or case folding.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || !IsLetter(name[0]) && name[0] != '_') return false;
        foreach (var character in name)
            if (!IsLetter(character) && character is not (>= '0' and <= '9') && character is not ('_' or '.')) return false;
        return true;
    }

    private static bool IsLetter(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
