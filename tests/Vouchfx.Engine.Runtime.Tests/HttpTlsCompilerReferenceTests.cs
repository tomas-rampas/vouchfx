using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class HttpTlsCompilerReferenceTests
{
    [Fact]
    public void CompileOnce_DefaultReferences_ResolveSharedHttpTlsFrameworkTypes()
    {
        // Type-check only. The emitted image is never loaded or executed, and
        // no certificate, chain, socket or TLS context is constructed.
        const string source = """
            System.Security.Cryptography.X509Certificates.X509Certificate2Collection certificates = null;
            System.Security.Cryptography.X509Certificates.X509ChainPolicy policy = null;
            System.Security.Cryptography.Oid oid = null;
            System.Net.Security.SslStreamCertificateContext context = null;
            """;

        var compiled = RoslynScriptCompiler.CompileOnce(source);

        Assert.NotEmpty(compiled.Image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompileOnce_DeclaredHttpReference_CompilesNewAndFrozenHelpers(bool includeFrozenHelper)
    {
        var source = (includeFrozenHelper ? SecurityHelper.Source : string.Empty) + HttpTlsHelper.Source;
        // No TPA expansion or caller-supplied cryptography/security reference.
        // Every method body is type-checked, including the client chain path.
        var compiled = RoslynScriptCompiler.CompileOnce(source, additionalReferencePaths: new[]
        {
            typeof(System.Net.Http.HttpClient).Assembly.Location,
        });

        Assert.NotEmpty(compiled.Image);
    }
}
