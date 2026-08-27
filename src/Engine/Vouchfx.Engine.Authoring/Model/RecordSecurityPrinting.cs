// Vouchfx.Engine.Authoring — RecordSecurityPrinting (#408, third round).
//
// ONE spelling of "a record that carries a `security` block never expands it into
// ToString()".
//
// It had grown to one-per-site, which is how it survived. #408 fixed `SecuredTarget`
// alone; measured against the built assembly with a canary in `ClientKeyPassword`, the
// two SIBLING holders still disclosed it in full:
//
//   ServiceSpec    { ..., Security = SecuritySpec { ..., ClientKeyPassword = <canary> }, ... }
//   DependencySpec { ..., Security = SecuritySpec { ..., ClientKeyPassword = <canary> }, ... }
//
// A per-site guard is a rule three call sites have to REMEMBER. This helper makes it a
// rule about a TYPE: any member whose runtime value is a `SecuritySpec` renders as the
// redaction marker, whatever the member is called and whichever record declares it. A
// fourth holder written tomorrow that routes its `PrintMembers` through here is guarded
// by construction rather than by whoever reviews it.
//
// The residual — a future member of some OTHER type that transitively reaches a
// `SecuritySpec` — is not closed by this file and cannot be, because it is a property of
// the whole record graph rather than of one record. It is closed by a gate instead:
// `SecuritySpecDisclosureTests.NoRecord_TransitivelyExpandsASecuritySpec_WithoutAGuard`
// walks the printed-member graph of this assembly and fails on any unguarded path.
//
// The ROOT — `SecuritySpec` itself, which is the type being withheld rather than a holder of
// one — is guarded from here too, by `Withhold` rather than by `Print`'s type test. That guard
// arrived on 2026-08-27, when the maintainer overturned the completeness objection that had
// kept the root unguarded through two rounds of per-holder fixes; `SecuritySpec`'s own remarks
// carry that decision and the argument for it.
//
// `Withhold` then took a second kind of caller (#353): the two untyped `Extra` buckets, on
// `SecuritySpec` and `DependencySpec`. Both were guarded in ONE change rather than one per site,
// because that is the whole lesson of the three rounds above — a per-site guard is a rule the
// next site has to remember, and this file exists so that it is not.
using System.Text;

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// Renders a record's members for its <c>ToString()</c>, withholding any
/// <see cref="SecuritySpec"/> among them and any member a record declares to be
/// secret-bearing.
/// </summary>
internal static class RecordSecurityPrinting
{
    /// <summary>
    /// The one spelling of a withheld <c>security</c> block. Deliberately a marker rather
    /// than nothing: a member printed as empty reads as "there is no security block", which
    /// is a different and misleading claim about a target that has one.
    /// </summary>
    internal const string RedactedMarker = "<redacted>";

    /// <summary>
    /// The value to hand <see cref="Print"/> for a member whose own VALUE is the secret-bearing
    /// thing: <see cref="RedactedMarker"/> when it is declared, <see langword="null"/> — which
    /// prints empty — when it is not.
    /// </summary>
    /// <param name="declared">The member's declared value, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>
    /// <strong>Why the root needs this and cannot use <see cref="Print"/>'s own test.</strong>
    /// That test keys on the VALUE'S TYPE, which is exactly right for a HOLDER — a member whose
    /// value is a <see cref="SecuritySpec"/> is recognisable whatever it is called. It cannot
    /// reach <see cref="SecuritySpec.ClientKeyPassword"/>, whose type is
    /// <see langword="string"/>, indistinguishable from the <c>profile</c>, <c>caCert</c>,
    /// <c>clientCert</c> and <c>clientKey</c> printed beside it. Which member carries the secret
    /// is knowledge only the declaring record has, so the declaring record states it — here,
    /// at the call site, rather than by a name test this file would have to keep in step with a
    /// property name. The parameter is GENERIC for the same reason this is not a
    /// type test: what the withheld member happens to be typed as is not the criterion — the two
    /// <c>Extra</c> buckets, both <c>YamlMappingNode</c>, route through here beside the
    /// <see langword="string"/> passphrase.
    /// </para>
    /// <para>
    /// The <see langword="null"/>/declared split is the same one <see cref="Print"/> draws for a
    /// holder, one level down, and for the same reason: an undeclared <c>clientKeyPassword</c>
    /// means an unencrypted key, which is a true and useful thing for a diagnostic to say, while
    /// <see cref="RedactedMarker"/> asserts a passphrase that is being withheld.
    /// </para>
    /// <para>
    /// <strong>Generic over reference types rather than over <see cref="object"/>.</strong> The
    /// parameter was <see langword="string"/> while <see cref="SecuritySpec.ClientKeyPassword"/>
    /// was the only caller; #353 added the two <c>Extra</c> buckets
    /// (<see cref="SecuritySpec.Extra"/>, <see cref="DependencySpec.Extra"/>), both YAML mapping
    /// nodes, bringing the call sites to three. Nothing here
    /// inspects the value — the marker is chosen on PRESENCE alone — so one spelling serves them
    /// all.
    /// </para>
    /// <para>
    /// <strong>The <c>class</c> constraint is load-bearing, and <see cref="object"/> would not
    /// have been free.</strong> A non-nullable value type boxes to a non-null reference, so
    /// <c>Withhold(false)</c> or <c>Withhold(0)</c> would print
    /// <see cref="RedactedMarker"/> for a member that is merely DEFAULT — destroying the
    /// absent-versus-withheld distinction the paragraphs above defend, and doing it silently.
    /// The constraint makes the compiler refuse that call instead. A future secret-bearing member
    /// of nullable value type needs a sibling overload taking <c>T?</c> where <c>T : struct</c>,
    /// never a widening back to <see cref="object"/>.
    /// </para>
    /// <para>
    /// <strong>The withholding is UNCONDITIONAL for every caller, and the argument is not one
    /// argument.</strong> It is the same conclusion reached twice, from opposite starting points,
    /// and stating only the first would leave the buckets' guard unjustified.
    /// </para>
    /// <para>
    /// For <see cref="SecuritySpec.ClientKeyPassword"/> the schema DOES constrain the text: on a
    /// schema-validated path it is a <c>${secret:}</c> REFERENCE, which §17 permits quoting — but
    /// only once <c>SecretReference.ValidateSecretBearingField</c> has RETURNED TRUE, and that
    /// method needs the run's secret-source list. A <c>ToString()</c> has no such list, the parser
    /// is deliberately lenient enough to bind a literal passphrase anyway, and
    /// <c>SecretReference.TryParse</c> alone is not the proof — see that property's own remarks
    /// for why the shortcut reproduces a disclosure defect. So: constrained, but not PROVABLY so
    /// from here.
    /// </para>
    /// <para>
    /// For <see cref="SecuritySpec.Extra"/> and <see cref="DependencySpec.Extra"/> there is no
    /// such constraint to appeal to and the conclusion is stronger, not weaker. A bucket holds
    /// precisely the keys no typed member claims, so no <c>pattern</c> governs its contents and
    /// nothing in the record's own type says what a value is. Both buckets are
    /// <see langword="null"/> on every schema-valid document today, which makes the guard cost
    /// nothing in the ordinary case and says nothing whatever about the case it exists for.
    /// </para>
    /// </remarks>
    internal static object? Withhold<T>(T? declared)
        where T : class =>
        declared is null ? null : RedactedMarker;

    /// <summary>
    /// Writes <paramref name="members"/> in the compiler's own <c>PrintMembers</c> shape
    /// (<c>Name = value, Name = value</c>), except that a member holding a
    /// <see cref="SecuritySpec"/> is written as <see cref="RedactedMarker"/> rather than
    /// expanded.
    /// </summary>
    /// <param name="builder">The builder the record's <c>PrintMembers</c> was handed.</param>
    /// <param name="members">
    /// The record's printable members, in declaration order — every one of them. A record
    /// routing its <c>PrintMembers</c> through here takes on the obligation to keep this
    /// list complete, which is what its own member-count census test pins.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when anything was written, matching the contract the compiler's
    /// generated <c>PrintMembers</c> has with the generated <c>ToString()</c> (the return value
    /// decides whether the closing brace is preceded by a space).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The redaction is driven by the VALUE'S TYPE, not by the member's name.</strong>
    /// That is the whole point: the three holders in this assembly all happen to call the member
    /// <c>Security</c>, but a guard keyed on that name would be a fourth restatement of the rule
    /// and would miss a member called anything else. A <see langword="null"/>
    /// <see cref="SecuritySpec"/> is not a <see cref="SecuritySpec"/> and so falls through to the
    /// ordinary path, printing empty — which is correct and NOT the misleading absence described
    /// on <see cref="RedactedMarker"/>: for a holder whose member is genuinely undeclared
    /// (<see cref="ServiceSpec.Security"/> is <see langword="null"/> in every suite that declares
    /// no <c>security:</c> block), "there is no security block" is exactly the true claim.
    /// </para>
    /// <para>
    /// <strong>Why a hand-written <c>PrintMembers</c> is acceptable at all.</strong> The
    /// objection is completeness: an override must enumerate every member, so a future member
    /// would go unprinted. For a REDACTION guard the drift is fail-SAFE — a dropped member
    /// discloses less, never more — and the remaining cost, a genuinely diagnostic future member
    /// going unprinted, is turned from silence into a failing test by the per-record census that
    /// pins each routed record's member count.
    /// </para>
    /// <para>
    /// <see cref="StringBuilder.Append(object?)"/> is what the compiler's own generated
    /// <c>PrintMembers</c> emits for a member of reference or nullable-value type, so an
    /// unredacted member renders byte-for-byte as it did before this helper existed.
    /// </para>
    /// </remarks>
    internal static bool Print(StringBuilder builder, params (string Name, object? Value)[] members)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(members);

        for (var i = 0; i < members.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var (name, value) = members[i];

            builder.Append(name).Append(" = ");
            builder.Append(value is SecuritySpec ? RedactedMarker : value);
        }

        return members.Length > 0;
    }
}
