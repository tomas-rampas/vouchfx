using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Orchestration;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Shared diagnostic composition for Runtime and its existing CLI friend assembly (#405).
/// </summary>
internal static class DiagnosticText
{
    /// <summary>
    /// Redacts secrets, substitutes declared security paths, then neutralises terminal controls.
    /// Sanitising first changes a control-byte secret before the ledger can match it. Validation
    /// text containing only YAML and secret references remains sanitise-only: a run ledger can
    /// redact unrelated public text by ordinal coincidence.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    internal static string? ScrubForDisplay(
        string? text, ResolvedSecretLedger? ledger, SecurityPathDisclosureLedger? pathLedger)
        => DisplaySanitiser.SanitiseForDisplay(Scrub(text, ledger, pathLedger));

    /// <summary>
    /// Applies content scrubs to event text without terminal-specific rewriting. Secrets precede
    /// paths: substituting a path inside a secret first would prevent whole-secret redaction.
    /// Secrets inside a path may instead leave a partially redacted path, the lesser disclosure.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    internal static string? Scrub(
        string? text, ResolvedSecretLedger? ledger, SecurityPathDisclosureLedger? pathLedger)
    {
        var scrubbed = ledger is null ? text : ledger.Scrub(text);
        // The SECOND net (issue #375): a resolved security-material path this engine handed to a
        // client library whose diagnostics it does not write. The value ledger above cannot see
        // it — a path is not a resolved secret and is never recorded there — and the substitution
        // is the field's own DECLARED text rather than a redaction marker, which is why it is a
        // separate ledger rather than a second use of the first.
        //
        // THE ORDER IS SECRETS FIRST, AND IT IS LOAD-BEARING. THIS IS THE ONE PLACE THAT ARGUMENT
        // IS WRITTEN OUT; every other composition of the two nets in this engine points here.
        //
        // Both nets replace EXACT occurrences, so whichever runs first can destroy the other's
        // match. The two orders therefore fail differently, and the choice is which failure to
        // accept:
        //
        //   SECRETS FIRST (what this does). If a recorded secret VALUE happens to occur inside a
        //   recorded resolved PATH — a passphrase that is also a directory name is the realistic
        //   shape, and an author who names a folder after the credential in it has done nothing
        //   this engine forbids — the value scrub rewrites the middle of the path, so the path
        //   ledger no longer finds its recorded form and the remainder survives:
        //   `C:\work\[REDACTED]\ca.pem`. That is a partial HOST-LAYOUT disclosure. It is the
        //   failure this order accepts.
        //
        //   PATHS FIRST. If a recorded resolved PATH occurs inside a recorded secret VALUE — any
        //   credential that embeds a file location, a connection string with a certificate path
        //   in it, a JSON blob a Vault resolver returned whole — the path substitution rewrites
        //   the secret's interior, the value ledger's exact match then misses, and what reaches
        //   the archived event stream is a MANGLED SECRET rather than a redacted one. Most of the
        //   value is still there and is still readable.
        //
        // A partially-redacted path discloses where a file lives on a build agent. A mangled
        // secret discloses most of a credential. The second is strictly worse, so the order is
        // fixed at secrets-first and must not be reversed without a reason that outweighs that.
        //
        // WHAT WOULD ACTUALLY REMOVE THE TRADE-OFF, stated so nobody mistakes this for a defence
        // of the current shape: one pass that considers both nets' recorded forms together,
        // longest-first, and never re-scans text it has already replaced. That is a change to how
        // the two ledgers compose, not to their order, and it is out of scope here.
        return pathLedger?.Scrub(scrubbed) ?? scrubbed;
    }
}
