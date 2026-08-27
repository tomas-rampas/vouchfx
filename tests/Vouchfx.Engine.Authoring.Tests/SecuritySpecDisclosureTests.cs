// Vouchfx.Engine.Authoring.Tests — the SecuritySpec disclosure guard (#408, fourth round).
//
// Supersedes SecuredTargetRedactionTests, which pinned ONE of the three holders. That
// narrowness is the defect's own history: #408 guarded `SecuredTarget` and shipped a test
// for `SecuredTarget`, while `ServiceSpec` and `DependencySpec` — holding the same type,
// in the same file's neighbour — kept printing a canary `clientKeyPassword` in full. A
// per-holder test finds a per-holder fix and nothing else, so the tests below are written
// over the CLASS: two of them enumerate the assembly rather than a list of type names, and
// therefore cover a fourth holder written after this file.
//
// The ROOT was guarded on 2026-08-27, when the maintainer overturned the completeness
// objection in `SecuritySpec`'s own remarks. `SecuritySpec.ToString()` no longer expands
// `ClientKeyPassword`, so the three per-holder guards are now the SECOND line rather than
// the only one, and this file pins both lines: each guard renders the marker on its own
// merits, and `SecuritySpec` itself is pinned directly. The censuses are what discharge the
// completeness objection — including the root's own, added in that round.

using System.Reflection;
using System.Text;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// No <c>ToString()</c> in this assembly may render a declared
/// <see cref="SecuritySpec.ClientKeyPassword"/> — neither
/// <see cref="SecuritySpec"/>'s own, nor that of any record holding one.
/// </summary>
public sealed class SecuritySpecDisclosureTests
{
    private const string Canary = "P@ssw0rd-LEAK-CANARY";

    /// <summary>CA1861 is an error in this project, so each expected set is a field.</summary>
    private static readonly string[] s_securedTargetMembers = { "Kind", "Name", "Security" };

    private static readonly string[] s_securitySpecMembers =
    {
        "CaCert", "ClientCert", "ClientKey", "ClientKeyPassword",
        "Endpoint", "Profile", "ServerArtifacts",
    };

    private static readonly string[] s_serviceSpecMembers =
    {
        "Env", "HealthCheck", "HttpPort", "Image", "ImagePullPolicy",
        "PinnedHostPorts", "Ports", "Project", "Security",
    };

    private static readonly string[] s_dependencySpecMembers =
    {
        "Env", "Extra", "Image", "Security", "Type", "Version",
    };

    private static SecuritySpec CanarySecurity() => new(
        Profile: "mtls",
        Endpoint: "kafka://localhost:9092",
        CaCert: "./certs/ca.pem",
        ClientCert: "./certs/client.pem",
        ClientKey: "./certs/client.key",
        ServerArtifacts: null)
    {
        ClientKeyPassword = Canary,
    };

    private static SecuredTarget Target() => new("api", "service", CanarySecurity());

    private static ServiceSpec Service() =>
        new("api:1", null, null, 8080, null) { Security = CanarySecurity() };

    private static DependencySpec Dependency() =>
        new("kafka", "3.7", null) { Security = CanarySecurity() };

    // ── The root: the type being withheld, not a holder of one ──────────────────────

    /// <summary>
    /// The hole the three per-holder guards could not close. Measured red against the built
    /// assembly before the root guard:
    /// <c>SecuritySpec { Profile = mtls, ..., ClientKeyPassword = P@ssw0rd-LEAK-CANARY }</c>.
    /// </summary>
    [Fact]
    public void SecuritySpec_ToString_DoesNotDiscloseTheClientKeyPassword()
    {
        var rendered = CanarySecurity().ToString();

        Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        Assert.Contains("ClientKeyPassword = <redacted>", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Interpolating a bare <see cref="SecuritySpec"/> is the shape the root hazard arrives in,
    /// and the one the per-holder guards never covered.
    /// </summary>
    [Fact]
    public void InterpolatingABareSecuritySpec_DoesNotDiscloseTheClientKeyPassword() =>
        Assert.DoesNotContain(
            Canary, $"loading {CanarySecurity()}", StringComparison.Ordinal);

    /// <summary>
    /// The passphrase alone is withheld, not the block: which profile, which endpoint and which
    /// certificate paths are exactly what a reader of one of these needs, and none of them is
    /// secret. A guard that answered the disclosure by rendering one marker would have cost the
    /// engine the diagnostic it was protecting.
    /// </summary>
    [Fact]
    public void SecuritySpec_ToString_StillPrintsEveryMemberButThePassphrase()
    {
        var rendered = CanarySecurity().ToString();

        Assert.Contains("Profile = mtls", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "Endpoint = kafka://localhost:9092", rendered, StringComparison.Ordinal);
        Assert.Contains("CaCert = ./certs/ca.pem", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "ClientCert = ./certs/client.pem", rendered, StringComparison.Ordinal);
        Assert.Contains("ClientKey = ./certs/client.key", rendered, StringComparison.Ordinal);
        Assert.Contains("ServerArtifacts = ", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNDECLARED passphrase prints empty rather than the marker — the same absent-versus-
    /// withheld distinction <c>RecordSecurityPrinting</c> draws one level up, applied to
    /// the member instead of the block. <c>ClientKeyPassword = &lt;redacted&gt;</c> on an
    /// unencrypted key would assert a passphrase that does not exist.
    /// </summary>
    [Fact]
    public void AnUndeclaredClientKeyPassword_RendersAsAbsentRatherThanRedacted()
    {
        var rendered = new SecuritySpec("tls", "8443", null, null, null, null).ToString();

        Assert.DoesNotContain("<redacted>", rendered, StringComparison.Ordinal);
        Assert.Contains("ClientKeyPassword = ", rendered, StringComparison.Ordinal);
    }

    // ── The three holders, each pinned directly ──────────────────────────────────────

    /// <summary>
    /// The defect exactly as #408 reported it: red before the guard, where the
    /// compiler-generated <c>ToString()</c> emitted
    /// <c>... ClientKeyPassword = P@ssw0rd-LEAK-CANARY }</c>.
    /// </summary>
    [Fact]
    public void SecuredTarget_ToString_DoesNotDiscloseTheClientKeyPassword()
    {
        var rendered = Target().ToString();

        Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientKeyPassword", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first of the two siblings #408's spot fix left open. Measured red against the
    /// built assembly before this guard:
    /// <c>ServiceSpec { ..., Security = SecuritySpec { ..., ClientKeyPassword =
    /// P@ssw0rd-LEAK-CANARY }, ... }</c>.
    /// </summary>
    [Fact]
    public void ServiceSpec_ToString_DoesNotDiscloseTheClientKeyPassword()
    {
        var rendered = Service().ToString();

        Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientKeyPassword", rendered, StringComparison.Ordinal);
    }

    /// <summary>The second sibling, red in exactly the same shape before this guard.</summary>
    [Fact]
    public void DependencySpec_ToString_DoesNotDiscloseTheClientKeyPassword()
    {
        var rendered = Dependency().ToString();

        Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientKeyPassword", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Interpolation is the shape the hazard actually arrives in — a diagnostic, event or log
    /// line that names the target — so it is pinned separately from the direct call, for all
    /// three holders.
    /// </summary>
    [Fact]
    public void Interpolation_DoesNotDiscloseTheClientKeyPassword()
    {
        var target = Target();
        var service = Service();
        var dependency = Dependency();

        Assert.DoesNotContain(Canary, $"probing {target}", StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, $"starting {service}", StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, $"starting {dependency}", StringComparison.Ordinal);
    }

    // ── Redaction must not read as absence, and must not eat the other members ───────

    /// <summary>
    /// Redaction must not read as absence: a target that HAS a security block and one that
    /// merely defaulted must not render identically, or a reader concludes the wrong thing.
    /// </summary>
    [Fact]
    public void SecuredTarget_ToString_StillIdentifiesTheTargetAndMarksTheRedaction()
    {
        var rendered = Target().ToString();

        Assert.Contains("Name = api", rendered, StringComparison.Ordinal);
        Assert.Contains("Kind = service", rendered, StringComparison.Ordinal);
        Assert.Contains("<redacted>", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hand-written <c>PrintMembers</c> that dropped a member would disclose less, not more —
    /// so the census below cannot catch it. This does: every non-secret member must still reach
    /// <c>ToString()</c>, or the guard has quietly cost the engine its diagnostics.
    /// </summary>
    [Fact]
    public void ServiceSpec_ToString_StillPrintsEveryMemberButTheSecurityBlock()
    {
        var rendered = new ServiceSpec("api:1", "./api.csproj", "Always", 8080, null)
        {
            Security = CanarySecurity(),
            Ports = new[] { 9093 },
            PinnedHostPorts = null,
            HealthCheck = null,
        }.ToString();

        Assert.Contains("Image = api:1", rendered, StringComparison.Ordinal);
        Assert.Contains("Project = ./api.csproj", rendered, StringComparison.Ordinal);
        Assert.Contains("ImagePullPolicy = Always", rendered, StringComparison.Ordinal);
        Assert.Contains("HttpPort = 8080", rendered, StringComparison.Ordinal);
        Assert.Contains("Env = ", rendered, StringComparison.Ordinal);
        Assert.Contains("Security = <redacted>", rendered, StringComparison.Ordinal);
        Assert.Contains("Ports = ", rendered, StringComparison.Ordinal);
        Assert.Contains("PinnedHostPorts = ", rendered, StringComparison.Ordinal);
        Assert.Contains("HealthCheck = ", rendered, StringComparison.Ordinal);
    }

    /// <summary>The dependency counterpart of the member-preservation check above.</summary>
    [Fact]
    public void DependencySpec_ToString_StillPrintsEveryMemberButTheSecurityBlock()
    {
        var rendered = new DependencySpec("kafka", "3.7", null)
        {
            Image = "nexus.example.com/mirror/kafka:3.7",
            Security = CanarySecurity(),
            Env = null,
        }.ToString();

        Assert.Contains("Type = kafka", rendered, StringComparison.Ordinal);
        Assert.Contains("Version = 3.7", rendered, StringComparison.Ordinal);
        Assert.Contains("Extra = ", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "Image = nexus.example.com/mirror/kafka:3.7", rendered, StringComparison.Ordinal);
        Assert.Contains("Security = <redacted>", rendered, StringComparison.Ordinal);
        Assert.Contains("Env = ", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNDECLARED security block prints empty rather than the marker, because
    /// <c>Security = &lt;redacted&gt;</c> on a service that declares no <c>security:</c> block
    /// would assert a block that does not exist — the mirror image of the misleading-absence
    /// argument, and the reason the guard keys on the VALUE'S TYPE rather than the member name.
    /// </summary>
    [Fact]
    public void AnUndeclaredSecurityBlock_RendersAsAbsentRatherThanRedacted()
    {
        Assert.DoesNotContain(
            "<redacted>",
            new ServiceSpec("api:1", null, null, null, null).ToString(),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "<redacted>",
            new DependencySpec("kafka", null, null).ToString(),
            StringComparison.Ordinal);
    }

    // ── The censuses ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Closes the drift objection against a hand-written <c>PrintMembers</c>: an explicit
    /// override cannot enumerate a member that does not exist yet, so a new member would be
    /// silently unprinted. Pinning the set turns that silence into a failing test, forcing a
    /// conscious decision about whether the new member belongs in the redacted rendering.
    /// </summary>
    /// <remarks>
    /// This is the mechanism, and the ROOT's census below is the one that mattered:
    /// <see cref="SecuritySpec"/> refused a guard on exactly this objection for two rounds,
    /// while the answer to it was already shipping here.
    /// </remarks>
    [Fact]
    public void SecuredTarget_HasExactlyTheMembersPrintMembersEnumerates() =>
        Assert.Equal(s_securedTargetMembers, PrintableMemberNames(typeof(SecuredTarget)));

    /// <summary>
    /// The census #408 never gave <see cref="ServiceSpec"/>. Its absence is why nobody noticed
    /// that this record grew <c>Ports</c>, <c>PinnedHostPorts</c> and <c>HealthCheck</c> around
    /// an unguarded <c>Security</c>.
    /// </summary>
    [Fact]
    public void ServiceSpec_HasExactlyTheMembersPrintMembersEnumerates() =>
        Assert.Equal(s_serviceSpecMembers, PrintableMemberNames(typeof(ServiceSpec)));

    /// <summary>The dependency counterpart of the census above.</summary>
    [Fact]
    public void DependencySpec_HasExactlyTheMembersPrintMembersEnumerates() =>
        Assert.Equal(s_dependencySpecMembers, PrintableMemberNames(typeof(DependencySpec)));

    /// <summary>
    /// The census that discharges the completeness objection ON THE RECORD THAT RAISED IT.
    /// Adding a member to <see cref="SecuritySpec"/> fails this row, so the hand-written
    /// <c>PrintMembers</c> beside it cannot fall behind the record's shape unnoticed — which is
    /// the whole of what that objection ever turned on. Measured red before it was written: an
    /// eighth property added to the record produced
    /// <c>Expected: [..., "ClientKeyPassword", "Endpoint", ...] / Actual: ["CaCert",
    /// "CensusProbeMember", ...]</c>, then green once reverted.
    /// </summary>
    [Fact]
    public void SecuritySpec_HasExactlyTheMembersPrintMembersEnumerates() =>
        Assert.Equal(s_securitySpecMembers, PrintableMemberNames(typeof(SecuritySpec)));

    // ── The class-level gates: these two cover a holder written after this file ──────

    /// <summary>
    /// Every record in this assembly that holds a <see cref="SecuritySpec"/> DIRECTLY is
    /// constructed with a canary in <see cref="SecuritySpec.ClientKeyPassword"/> and rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumerated from the assembly rather than from a list of type names, deliberately: the
    /// three hand-written cases above prove the three holders that exist today, and this one
    /// covers the fourth. #408's fix was correct and its test was correct, and the defect
    /// survived anyway because both named a type.
    /// </para>
    /// <para>
    /// <strong>The root guard made this row trivially satisfiable, on 2026-08-27, and it is kept
    /// anyway.</strong> Measured against an unguarded control holder built on the guarded
    /// assembly: it renders
    /// <c>UnguardedHolder { Name = api, Security = SecuritySpec { …, ClientKeyPassword =
    /// &lt;redacted&gt; } }</c> — canary absent — so a fourth holder with NO guard of its own now
    /// passes this row. It is kept because it is the only row asserting the end-to-end property
    /// (a canary planted anywhere in this graph does not surface), and because losing that
    /// assertion to keep a tidier file is how the previous two rounds of this defect were
    /// possible. What it no longer does is FIND an unguarded holder; the structural gate below is
    /// what does that, and the per-holder <c>Security = &lt;redacted&gt;</c> assertions above are
    /// what prove each holder's own guard still fires — the same control measures
    /// <c>Security = &lt;redacted&gt;</c> ABSENT, so the root guard alone does not satisfy them.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDirectHolderOfASecuritySpec_RedactsItFromToString()
    {
        var holders = typeof(SecuritySpec).Assembly.GetTypes()
            .Where(IsRecord)
            .SelectMany(
                t => PrintableMembers(t).Select(m => (Type: t, Member: m)))
            .Where(x => TypeOf(x.Member) == typeof(SecuritySpec))
            .ToArray();

        Assert.NotEmpty(holders);

        foreach (var (type, member) in holders)
        {
            var rendered = RenderWithCanary(type, member);

            Assert.False(
                rendered.Contains(Canary, StringComparison.Ordinal),
                $"{type.FullName}.{member.Name} expands its SecuritySpec into ToString(), " +
                $"disclosing the declared clientKeyPassword. Route this record's PrintMembers " +
                $"through RecordSecurityPrinting.Print. Rendered: {rendered}");
        }
    }

    /// <summary>
    /// No record in this assembly may reach a <see cref="SecuritySpec"/> through the
    /// printed-member graph without an explicit <c>PrintMembers</c>/<c>ToString</c> somewhere on
    /// the path to cut it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate the sibling above cannot be: an INDIRECT holder — a record whose printed member
    /// is a <see cref="ServiceSpec"/>, say — expands the whole chain, and no member of it is
    /// typed <see cref="SecuritySpec"/>. Measured today, no such record exists; the chain from
    /// <see cref="EnvironmentSpec"/> down is cut by the <c>IReadOnlyDictionary</c> holding the
    /// two spec types, whose <c>ToString()</c> is its own type name. That cut is a property of a
    /// collection type nobody chose for this reason and could be lost by retyping one field.
    /// </para>
    /// <para>
    /// <strong><see cref="SecuritySpec"/> is still the terminus of the walk, and since the root
    /// guard landed that is a choice rather than a necessity.</strong> The walk treats reaching
    /// one as a finding WITHOUT consulting its own guard, so this gate keeps asserting that every
    /// holder cuts the path itself. It could instead let the walk terminate on the root's own
    /// explicit <c>PrintMembers</c> — and that would make the gate vacuous, passing for every
    /// record in the assembly on the strength of one guard. Two independent lines is the point:
    /// the root stops the disclosure, and this gate stops a holder from relying on it.
    /// </para>
    /// <para>
    /// What this gate proves is structural — that a path exists to a guard — not that the guard
    /// redacts. The rendering itself is proved by the canary tests above.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoRecord_TransitivelyExpandsASecuritySpec_WithoutAGuard()
    {
        var unguarded = typeof(SecuritySpec).Assembly.GetTypes()
            .Where(t => IsRecord(t) && t != typeof(SecuritySpec))
            .SelectMany(t => PathsToSecuritySpec(t, new HashSet<Type>())
                .Select(p => $"{t.FullName}{p}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unguarded);
    }

    // ── Reflection helpers: the printed-member model the compiler actually uses ──────

    /// <summary>
    /// A record's generated <c>PrintMembers</c> enumerates its public instance fields and
    /// readable public instance properties, declared on the type itself. Every record — class
    /// or struct — has a <c>PrintMembers(StringBuilder)</c>, which is what identifies one here.
    /// </summary>
    private static bool IsRecord(Type type) => PrintMembersMethod(type) is not null;

    private static MethodInfo? PrintMembersMethod(Type type) =>
        type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m =>
                m.Name == "PrintMembers"
                && m.GetParameters() is { Length: 1 } p
                && p[0].ParameterType == typeof(StringBuilder));

    /// <summary>
    /// True when this record's <c>PrintMembers</c> was written by hand rather than generated —
    /// the compiler stamps its own with <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/>.
    /// A hand-written one cuts the expansion at this node.
    /// </summary>
    private static bool HasExplicitPrintGuard(Type type)
    {
        var printMembers = PrintMembersMethod(type);

        return printMembers is not null
            && !printMembers.GetCustomAttributes(inherit: false)
                .Any(a => a is System.Runtime.CompilerServices.CompilerGeneratedAttribute);
    }

    private static IEnumerable<MemberInfo> PrintableMembers(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(Declared))
        {
            yield return field;
        }

        foreach (var property in type.GetProperties(Declared))
        {
            if (property.CanRead
                && property.GetIndexParameters().Length == 0
                && property.Name != "EqualityContract")
            {
                yield return property;
            }
        }
    }

    private static Type TypeOf(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => typeof(void),
    };

    private static string[] PrintableMemberNames(Type type) =>
        PrintableMembers(type)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Every printed-member path from <paramref name="type"/> down to a
    /// <see cref="SecuritySpec"/> that no explicit guard cuts. A non-record member type
    /// terminates the walk: its <c>ToString()</c> is its own type name, not an expansion.
    /// </summary>
    private static List<string> PathsToSecuritySpec(Type type, HashSet<Type> visiting)
    {
        if (type == typeof(SecuritySpec))
        {
            return new List<string> { ".ClientKeyPassword" };
        }

        var paths = new List<string>();

        if (!IsRecord(type) || HasExplicitPrintGuard(type) || !visiting.Add(type))
        {
            return paths;
        }

        foreach (var member in PrintableMembers(type))
        {
            var memberType = TypeOf(member);
            var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;

            paths.AddRange(
                PathsToSecuritySpec(underlying, visiting).Select(p => $".{member.Name}{p}"));
        }

        visiting.Remove(type);

        return paths;
    }

    /// <summary>
    /// Constructs <paramref name="type"/> with defaulted constructor arguments, assigns a
    /// canary-bearing <see cref="SecuritySpec"/> to <paramref name="member"/>, and renders it.
    /// </summary>
    private static string RenderWithCanary(Type type, MemberInfo member)
    {
        var constructor = type.GetConstructors()
            .Where(c => c.GetParameters().Length != 1
                || c.GetParameters()[0].ParameterType != type)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        Assert.True(
            constructor is not null,
            $"{type.FullName} holds a SecuritySpec but exposes no non-copy constructor, so this " +
            $"gate cannot render it. Pin it with a hand-written test instead.");

        var arguments = constructor!.GetParameters()
            .Select(p => p.ParameterType == typeof(SecuritySpec)
                ? CanarySecurity()
                : DefaultOf(p.ParameterType))
            .ToArray();

        object instance;

        try
        {
            instance = constructor.Invoke(arguments);
        }
        catch (TargetInvocationException e)
        {
            throw new InvalidOperationException(
                $"{type.FullName} holds a SecuritySpec but its constructor rejects defaulted " +
                $"arguments, so this gate cannot render it. Pin it with a hand-written test " +
                $"instead.", e.InnerException);
        }

        if (member is PropertyInfo { CanWrite: true } writable)
        {
            writable.SetValue(instance, CanarySecurity());
        }
        else if (member is FieldInfo field)
        {
            field.SetValue(instance, CanarySecurity());
        }

        return instance.ToString() ?? string.Empty;
    }

    private static object? DefaultOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;
}
