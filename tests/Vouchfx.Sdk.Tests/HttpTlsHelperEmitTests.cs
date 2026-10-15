using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Compilation;
using Xunit;

namespace Vouchfx.Sdk.Tests;

public sealed class HttpTlsHelperEmitTests
{
    private static async Task<IDictionary<string, object?>> Configure(ISecurityConfiguration? configuration)
    {
        var source = HttpTlsHelper.Source + """
            var handler = new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false };
            try
            {
                try
                {
                    HttpTls_Helpers.ConfigureHandler(
                        (Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor)Vars["accessor"], "sut", handler);
                    var policy = handler.SslOptions.CertificateChainPolicy;
                    Vars["configured"] = policy != null;
                    Vars["downloadsDisabled"] = policy?.DisableCertificateDownloads;
                    Vars["revocation"] = policy?.RevocationMode.ToString();
                    Vars["trustMode"] = policy?.TrustMode.ToString();
                    Vars["verificationFlags"] = policy?.VerificationFlags.ToString();
                    Vars["applicationOid"] = policy?.ApplicationPolicy[0]?.Value;
                    Vars["callbackAbsent"] = handler.SslOptions.RemoteCertificateValidationCallback == null;
                    Vars["clientAbsent"] = handler.SslOptions.ClientCertificateContext == null && handler.SslOptions.ClientCertificates == null;
                    Vars["redirectsDisabled"] = !handler.AllowAutoRedirect;
                }
                catch (Vouchfx.Engine.Abstractions.Security.SecurityMaterialException ex)
                {
                    Vars["refusal"] = ex.Message;
                }
            }
            finally { handler.Dispose(); }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compiled = RoslynScriptCompiler.CompileOnce(source, additionalReferencePaths: references);
        var vars = new Dictionary<string, object?> { ["accessor"] = new Accessor(configuration) };
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));
        return vars;
    }

    [Fact]
    public async Task AbsentSecurity_PreservesPlatformDefaultsAndNoClientIdentity()
    {
        var values = await Configure(null);
        Assert.Equal(false, values["configured"]);
        Assert.Equal(true, values["callbackAbsent"]);
        Assert.Equal(true, values["clientAbsent"]);
        Assert.Equal(true, values["redirectsDisabled"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredTls_SetsOfflinePolicyWithoutRelaxingTrust(bool withMaterial)
    {
        var values = await Configure(new Configuration("tls", withMaterial ? new EmptyMaterial() : null));
        Assert.Equal(true, values["configured"]);
        Assert.Equal(true, values["downloadsDisabled"]);
        Assert.Equal("NoCheck", values["revocation"]);
        Assert.Equal("System", values["trustMode"]);
        Assert.Equal("NoFlag", values["verificationFlags"]);
        Assert.Equal("1.3.6.1.5.5.7.3.1", values["applicationOid"]);
        Assert.Equal(true, values["callbackAbsent"]);
        Assert.Equal(true, values["clientAbsent"]);
    }

    [Theory]
    [InlineData("mtls")]
    [InlineData("future-profile")]
    [InlineData("TLS")]
    public async Task MissingIdentityOrUnknownProfile_Refuses(string profile)
    {
        var values = await Configure(new Configuration(profile, new EmptyMaterial()));
        Assert.Contains("profile", Assert.IsType<string>(values["refusal"]), StringComparison.Ordinal);
        Assert.False(values.ContainsKey("configured"));
    }

    [Theory]
    [InlineData("client.pem", null)]
    [InlineData(null, "key.pem")]
    public async Task DeclaredTlsWithClientPath_RefusesWithoutLoadingIdentity(string? cert, string? key)
    {
        var values = await Configure(new Configuration("tls", new EmptyMaterial(cert, key)));
        Assert.Contains("presents no client identity", Assert.IsType<string>(values["refusal"]), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingMaterialImplementation_DefaultsToNoIntermediates()
    {
        ISecurityCertificateMaterial material = new EmptyMaterial();
        Assert.Empty(material.ClientCertificateIntermediates);
    }

    [Fact]
    public void OldAndNewHelpers_CompileTogetherWithoutNameCollisions()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var source = SecurityHelper.Source + HttpTlsHelper.Source + """
            var oldHandler = new System.Net.Http.HttpClientHandler();
            var newHandler = new System.Net.Http.SocketsHttpHandler();
            try
            {
                Security_Helpers.ConfigureHandler(null, "old", oldHandler);
                HttpTls_Helpers.ConfigureHandler(null, "new", newHandler);
            }
            finally { oldHandler.Dispose(); newHandler.Dispose(); }
            """;
        Assert.NotNull(RoslynScriptCompiler.CompileOnce(source, additionalReferencePaths: references));
    }

    [Fact]
    public void NewHelper_OldMaterialContractWithoutChainCapability_RefusesCompilation()
    {
        // Model the old abstraction surface explicitly. No version-number bypass is
        // introduced: the new helper must not compile without its required member.
        const string oldContract = """
            namespace Vouchfx.Engine.Abstractions.Security
            {
                public interface ISecurityConfigurationAccessor { ISecurityConfiguration For(string name); }
                public interface ISecurityConfiguration { string Profile { get; } ISecurityCertificateMaterial Certificates { get; } }
                public interface ISecurityCertificateMaterial
                {
                    string ClientCertificatePath { get; }
                    string ClientKeyPath { get; }
                    System.Security.Cryptography.X509Certificates.X509Certificate2 ClientCertificate { get; }
                    System.Security.Cryptography.X509Certificates.X509Certificate2 CaCertificate { get; }
                    bool TrustsRemoteCertificate(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
                        System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors errors);
                }
                public class SecurityMaterialException : System.Exception { public SecurityMaterialException(string message) : base(message) { } }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !string.Equals(Path.GetFileName(path), "Vouchfx.Engine.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "OldMaterialContractControl", new[]
            {
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(oldContract),
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(HttpTlsHelper.Source),
            }, references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        var error = Assert.Single(errors);
        Assert.Equal("CS1061", error.Id);
        Assert.Contains("ClientCertificateIntermediates", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private sealed record Configuration(string Profile, ISecurityCertificateMaterial? Certificates) : ISecurityConfiguration;
    private sealed class Accessor : ISecurityConfigurationAccessor
    {
        private readonly ISecurityConfiguration? _configuration;
        public Accessor(ISecurityConfiguration? configuration) => _configuration = configuration;
        public ISecurityConfiguration? For(string targetName) => _configuration;
    }

    // Deliberately implements only the old material members: no certificates are minted,
    // imported, loaded, or built in this test suite.
    private sealed class EmptyMaterial : ISecurityCertificateMaterial
    {
        private readonly string? _cert;
        private readonly string? _key;
        public EmptyMaterial(string? cert = null, string? key = null) { _cert = cert; _key = key; }
        public string? CaCertificatePath => null;
        public string? ClientCertificatePath => _cert;
        public string? ClientKeyPath => _key;
        public X509Certificate2? CaCertificate => null;
        public X509Certificate2? ClientCertificate => null;
        public bool TrustsRemoteCertificate(X509Certificate2? remote, X509Chain? chain, SslPolicyErrors errors) => false;
    }
}
