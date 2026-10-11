namespace Vouchfx.Engine.Runtime;

/// <summary>Independent findings stay typed until a human/report surface requests joined text.</summary>
internal sealed class PreTopologyAuthoringResult
{
    internal PreTopologyAuthoringResult(PipelineResult pipeline, IReadOnlyList<ValidationDiagnostic> diagnostics)
    {
        Pipeline = pipeline;
        Diagnostics = diagnostics;
    }

    internal PipelineResult Pipeline { get; }
    internal IReadOnlyList<ValidationDiagnostic> Diagnostics { get; }
    internal string? Fault => Diagnostics.Count == 0
        ? null
        : string.Join("\n", Diagnostics.Select(diagnostic => diagnostic.Message));

    internal void Deconstruct(out PipelineResult pipeline, out string? fault)
    {
        pipeline = Pipeline;
        fault = Fault;
    }
}

/// <summary>A safe field identity and message from the topology-free secret-reference walk.</summary>
internal sealed record SecretReferenceFault(string Message, string FieldPath, bool FromSecurityDeclaration);
