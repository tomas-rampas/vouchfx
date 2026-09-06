// Tests for the client-key-password spec, REQ-003: YamlDocumentParser.ParseSecurity binds
// 'security.clientKeyPassword' into SecuritySpec as an init-only property, and that property
// is added WITHOUT changing the record's primary-constructor arity.
//
// Written against the public Parse contract only. Two properties are pinned here:
//   • the declared text is retained VERBATIM and UNRESOLVED — the '${secret:}' sigil is still
//     in the bound string, because §17 puts secret resolution at RUN time — never at compile
//     time — and this layer is authoring-surface only; and
//   • the primary constructor still takes exactly six parameters (see the arity guard at the
//     foot of this file for why that number is load-bearing).
//
// It has since gained the parser half of #353's `Extra` bucket, in two rows: one pinning that no
// BOUND key reaches the bucket (the whole typed surface leaves it null), and one pinning that a
// key differing from a bound one only in CASE does reach it, because the comparison is ordinal.

using System.Reflection;
using Vouchfx.Engine.Authoring.Model;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// Verifies that <see cref="YamlDocumentParser.Parse"/> binds
/// <c>security.clientKeyPassword</c> into <see cref="SecuritySpec.ClientKeyPassword"/>
/// (client-key-password spec, REQ-003).
/// </summary>
public sealed class SecuritySpecBindingTests
{
    /// <summary>
    /// <see cref="SecuritySpec"/>'s primary-constructor parameters, in declaration order and
    /// as <c>"&lt;type&gt; &lt;name&gt;"</c> — the positional surface a compiled caller binds
    /// against. Type AND name, because a name-only projection would catch a REORDER while
    /// missing a RETYPE, and a retype is the more thoroughly binary-breaking of the two.
    /// Nullability is deliberately outside this guard: <c>string?</c> and <c>string</c> are the
    /// same CLR type, so a nullability flip is source-affecting but not binary-breaking, and it
    /// is invisible to <see cref="System.Reflection.MemberInfo.Name"/> by construction.
    /// </summary>
    private static readonly string[] s_primaryConstructorSignature =
    {
        "String Profile",
        "String Endpoint",
        "String CaCert",
        "String ClientCert",
        "String ClientKey",
        "IReadOnlyList`1 ServerArtifacts",
    };

    [Fact]
    public void Parse_ServiceSecurityClientKeyPassword_BindsTheReferenceVerbatim()
    {
        // Arrange — a service declaring mutual TLS with an encrypted client key.
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    caCert: certs/ca.pem
                    clientCert: certs/client.pem
                    clientKey: certs/client.key
                    clientKeyPassword: "${secret:env/CLIENT_KEY_PASS}"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        // Act
        var doc = YamlDocumentParser.Parse(yaml);

        // Assert — bound as DECLARED TEXT: the reference is retained character for character,
        // sigil included. Nothing at this layer resolves it (§17: resolution happens at run
        // time, never at compile time — and for THIS field the run-time moment is first use of
        // the certificate material, before any step runs, not step execution. An earlier version
        // of this comment asserted both moments at once, which cannot be true of one field).
        var security = doc.Environment!.Services!["app"].Security;
        Assert.NotNull(security);
        Assert.Equal("${secret:env/CLIENT_KEY_PASS}", security!.ClientKeyPassword);

        // The six sibling fields are unaffected — this is a purely additive seventh key.
        Assert.Equal("mtls", security.Profile);
        Assert.Equal("8443", security.Endpoint);
        Assert.Equal("certs/ca.pem", security.CaCert);
        Assert.Equal("certs/client.pem", security.ClientCert);
        Assert.Equal("certs/client.key", security.ClientKey);
        Assert.Null(security.ServerArtifacts);
    }

    [Fact]
    public void Parse_DependencySecurityClientKeyPassword_BindsTheSameWayAsAService()
    {
        // ParseSecurity is shared by the service and dependency paths; pinned on both so a
        // future divergence (one owner learning about a key the other does not) is caught.
        const string yaml = """
            environment:
              dependencies:
                events:
                  type: kafka
                  security:
                    profile: mtls
                    endpoint: 9093
                    clientCert: certs/client.pem
                    clientKey: certs/client.key
                    clientKeyPassword: "${secret:vault/kv/data/suite#clientKeyPass}"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        var security = doc.Environment!.Dependencies!["events"].Security;
        Assert.NotNull(security);
        Assert.Equal("${secret:vault/kv/data/suite#clientKeyPass}", security!.ClientKeyPassword);
    }

    [Fact]
    public void Parse_SecurityBlockWithoutClientKeyPassword_LeavesItNull()
    {
        // The ordinary case — an unencrypted client key. Null means UNDECLARED, never
        // "defaulted": nothing downstream may synthesise a passphrase.
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: certs/client.pem
                    clientKey: certs/client.key
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        var security = doc.Environment!.Services!["app"].Security;
        Assert.NotNull(security);
        Assert.Null(security!.ClientKeyPassword);
    }

    [Fact]
    public void Parse_ClientKeyPasswordLiteral_IsStillBound_ParserStaysLenient()
    {
        // The parser deliberately enforces NO requiredness and NO shape: a literal
        // passphrase is refused by the JSON Schema layer's own 'pattern'
        // (root-language-schema.json's $defs/security, REQ-001), never here — mirroring
        // every sibling field's handling. Pinned so a future "helpful" shape check added to
        // the parser (which would split one rule across two layers, exactly the drift
        // SecurityArtifactPath's own header warns about) turns this red.
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: certs/client.pem
                    clientKey: certs/client.key
                    clientKeyPassword: "hunter2"
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        Assert.Equal("hunter2", doc.Environment!.Services!["app"].Security!.ClientKeyPassword);
    }

    /// <summary>
    /// #353: a <c>security</c> block declaring the WHOLE typed surface and nothing else leaves
    /// <see cref="SecuritySpec.Extra"/> <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The other half of the bucket's correctness, and the half a round-trip test cannot see: if
    /// <c>ParseSecurity</c>'s exclusion list ever falls behind the keys it binds, the missed key
    /// lands in the untyped bucket AS WELL AS in its typed member, and every assertion about the
    /// typed member still passes. Declaring all seven keys here means one omission from that list
    /// reddens this row. <c>BuildExtraNode</c> returns <see langword="null"/> rather than an empty
    /// node for an empty result, so "no unbound key" and "no bucket" are the same state — matching
    /// <see cref="DependencySpec.Extra"/> exactly.
    /// </remarks>
    [Fact]
    public void Parse_SecurityBlockDeclaringOnlyBoundKeys_LeavesExtraNull()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: mtls
                    endpoint: 8443
                    caCert: certs/ca.pem
                    clientCert: certs/client.pem
                    clientKey: certs/client.key
                    clientKeyPassword: "${secret:env/CLIENT_KEY_PASS}"
                    serverArtifacts:
                      - source: certs/broker.jks
                        target: /etc/kafka/secrets/broker.jks
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        var security = doc.Environment!.Services!["app"].Security;
        Assert.NotNull(security);
        Assert.Null(security!.Extra);

        // Non-vacuity: the fixture must actually declare the whole surface, or a null bucket
        // proves only that the keys it omitted were omitted.
        Assert.NotNull(security.Profile);
        Assert.NotNull(security.Endpoint);
        Assert.NotNull(security.CaCert);
        Assert.NotNull(security.ClientCert);
        Assert.NotNull(security.ClientKey);
        Assert.NotNull(security.ClientKeyPassword);
        Assert.NotNull(security.ServerArtifacts);
    }

    /// <summary>
    /// #353: the key comparison is ORDINAL, matching <see cref="DependencySpec.Extra"/>'s. A
    /// differently-cased spelling of a bound key is NOT that key, so it lands in the bucket
    /// rather than being silently accepted as the typed field.
    /// </summary>
    [Fact]
    public void Parse_SecurityKeyDifferingOnlyInCase_LandsInExtra_NotInTheTypedMember()
    {
        const string yaml = """
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  security:
                    profile: tls
                    endpoint: 8443
                    CaCert: certs/ca.pem
            steps:
              - id: s1
                type: script.csharp
                code: "// no-op"
            """;

        var doc = YamlDocumentParser.Parse(yaml);

        var security = doc.Environment!.Services!["app"].Security;
        Assert.NotNull(security);
        Assert.Null(security!.CaCert);
        Assert.NotNull(security.Extra);
        Assert.Equal(
            "certs/ca.pem",
            ((YamlScalarNode)security.Extra!.Children[new YamlScalarNode("CaCert")]).Value);
    }

    /// <summary>
    /// <see cref="SecuritySpec"/>'s primary constructor must still take exactly SIX
    /// parameters: <c>ClientKeyPassword</c> was added as an init-only property precisely
    /// because <c>Vouchfx.Engine.Authoring</c> is a PACKABLE assembly, so a seventh positional
    /// parameter would change the primary constructor's arity and the compiler-generated
    /// <c>Deconstruct</c> — binary-breaking for any already-compiled caller, which a source
    /// rebuild silently hides. This guard makes that mistake a red suite rather than a
    /// runtime <c>MissingMethodException</c> in a consumer nobody rebuilt.
    /// </summary>
    [Fact]
    public void SecuritySpec_PrimaryConstructor_StillTakesExactlySixParameters()
    {
        // The copy constructor a record synthesises takes the record type itself; every other
        // public constructor is author-declared, and SecuritySpec declares only its primary.
        var constructors = typeof(SecuritySpec)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => !IsCopyConstructor(c))
            .ToArray();

        Assert.True(
            constructors.Length == 1,
            "SecuritySpec is expected to declare exactly ONE non-copy constructor — its " +
            "primary. Finding more (or none) means the record's shape changed in a way this " +
            $"guard cannot reason about, so the signature assertion below would be checking the " +
            $"wrong constructor. Found {constructors.Length}.");
        var primary = constructors[0];

        Assert.Equal(6, primary.GetParameters().Length);

        // Typed AND named, in order — an arity check alone would notice neither a reorder nor a
        // retype, and a name-only projection would notice only the reorder; both break a
        // positional caller, and a retype breaks it binary-wise most thoroughly of all.
        // Nullability is out of scope by construction: string? and string are one CLR type.
        Assert.Equal(
            s_primaryConstructorSignature,
            primary.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}").ToArray());

        // And the new member is a property, not a parameter — the positive half of the guard.
        var clientKeyPassword = typeof(SecuritySpec).GetProperty(nameof(SecuritySpec.ClientKeyPassword));
        Assert.NotNull(clientKeyPassword);
        Assert.DoesNotContain(
            primary.GetParameters(),
            p => string.Equals(p.Name, nameof(SecuritySpec.ClientKeyPassword), StringComparison.Ordinal));

        static bool IsCopyConstructor(ConstructorInfo constructor)
        {
            var parameters = constructor.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(SecuritySpec);
        }
    }
}
