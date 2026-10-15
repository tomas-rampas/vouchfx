namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>Projects collected schema errors into messages for suite authors.</summary>
internal static class SchemaDiagnosticPresentation
{
    internal static SchemaValidationResult ForAuthor(SchemaValidationResult result)
    {
        if (result.Errors.Count == 0)
            return result;

        var errors = new SchemaValidationError[result.Errors.Count];
        for (var i = 0; i < errors.Length; i++)
        {
            var error = result.Errors[i];
            errors[i] = new SchemaValidationError(error.InstanceLocation, ForAuthor(error.Message));
        }

        return new SchemaValidationResult(result.IsValid, errors);
    }

    internal static string ForAuthor(string message)
    {
        // The collector also uses [] for a boolean-false schema with no keyword.
        // Only that leading marker is presentation metadata.
        // Never replace bracketed text inside the explanation: property names,
        // values and required-property lists can contain arbitrary author data.
        if (message.Length < 4 || message[0] != '[')
            return message;

        var end = message.IndexOf(']');
        if (end < 1 || end + 1 >= message.Length || message[end + 1] != ' ')
            return message;

        for (var i = 1; i < end; i++)
        {
            var character = message[i];
            if (character is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not '$')
                return message;
        }

        return message[(end + 2)..];
    }
}
