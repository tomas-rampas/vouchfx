namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>Explicit recovery for an ownership record left after interruption or hard termination.</summary>
public static class EnvironmentRecovery
{
    /// <summary>Removes only the recorded owned Compose project after verifying resource ownership.</summary>
    public static Task CleanupAsync(string journalPath, CancellationToken cancellationToken)
        => ComposeLease.RecoverAsync(journalPath, cancellationToken);
}
