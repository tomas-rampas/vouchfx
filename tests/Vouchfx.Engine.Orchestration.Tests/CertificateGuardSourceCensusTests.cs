using System.Xml.Linq;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class CertificateGuardSourceCensusTests
{
    private const string TestProject = "<Project><ItemGroup><PackageReference Include='Microsoft.NET.Test.Sdk'/></ItemGroup></Project>";
    private const string LibraryProject = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>";
    private static readonly string[] s_missingExtra = { "Extra.Tests" };
    private const string MintingSource = "class Fixture { void Create() { TestCertificateAuthority.CreateSuiteDirectory(); } }";
    private const string GuardedSource = """
        using Xunit;
        [assembly: TestCollectionOrderer("Extra.Tests.Orderer", "Extra.Tests")]
        namespace Extra.Tests;
        [CollectionDefinition("guard", DisableParallelization = true)]
        class Definition { }
        class Orderer : ITestCollectionOrderer
        {
            IEnumerable<object> OrderTestCollections(IEnumerable<object> items) => GuardLast(items);
            IEnumerable<object> GuardLast(IEnumerable<object> items) => items.OrderBy(item => item.Name == "guard" ? 1 : 0);
        }
        [Collection("guard")]
        class Guard
        {
            [Fact]
            void Check()
            {
                var residue = TestCertificateStoreGuard.SweepProcessResidue();
                Assert.True(residue is null, residue);
            }
        }
        """;

    [Fact]
    public void RepositoryCertificateMaterialTestHostsAllHaveFinalGuards()
    {
        var projects = ReadProjects(RepoRoot.Resolve());
        var materialHosts = projects.Where(CertificateGuardSourceCensus.IsTestHost)
            .Where(project => CertificateGuardSourceCensus.CreatesOrImportsCertificate(project.Sources))
            .Select(project => project.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = { "Vouchfx.Cli.Tests", "Vouchfx.Engine.Orchestration.Tests", "Vouchfx.Engine.Runtime.Tests", "Vouchfx.Engine.Runtime.TransportAcceptance.Tests" };
        Assert.All(expected, name => Assert.Contains(name, materialHosts));
        Assert.Empty(CertificateGuardSourceCensus.MissingGuards(projects));
    }

    [Fact]
    public void AdditionalMintingTestHostWithoutGuardIsRefused()
    {
        var projects = ReadProjects(RepoRoot.Resolve()).Append(new("Extra.Tests", TestProject, new[] { MintingSource }));
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(projects));
    }

    [Theory]
    [InlineData("// TestCertificateAuthority.CreateSuiteDirectory();\nclass Fixture { }")]
    [InlineData("class Fixture { const string Example = \"TestCertificateAuthority.CreateSuiteDirectory();\"; }")]
    [InlineData("using Vouchfx.TestSupport; class Fixture { string Name => nameof(TestCertificateAuthority); }")]
    public void CommentsStringsAndTypeMentionsDoNotCountAsCertificateCreation(string source)
    {
        Assert.False(CertificateGuardSourceCensus.CreatesOrImportsCertificate(new[] { source }));
    }

    [Fact]
    public void CanonicalAuthorityLibraryDoesNotBecomeAnIndependentTestHost()
    {
        Assert.Empty(CertificateGuardSourceCensus.MissingGuards(new[]
        {
            new CertificateGuardSourceCensus.Project("Vouchfx.TestSupport", LibraryProject, new[] { MintingSource }),
        }));
    }

    [Theory]
    [InlineData("using CA = Vouchfx.TestSupport.TestCertificateAuthority; class Fixture { void Run() { CA.CreateSuiteDirectory(); } }")]
    [InlineData("namespace Extra { using CA = Vouchfx.TestSupport.TestCertificateAuthority; class Fixture { void Run() { CA.CreateSuiteDirectory(); } } }")]
    [InlineData("class Fixture { X509Certificate2 Certificate { get; } = new(bytes); }")]
    [InlineData("using static Vouchfx.TestSupport.TestCertificateAuthority; class Fixture { void Run() { CreateSuiteDirectory(); } }")]
    [InlineData("class Fixture { void Run() { new System.Security.Cryptography.X509Certificates.CertificateRequest(\"neutral\", key, hash); } }")]
    [InlineData("class Fixture { System.Security.Cryptography.X509Certificates.X509Certificate2 Load() => new(bytes); }")]
    [InlineData("using Cert = System.Security.Cryptography.X509Certificates.X509Certificate2; class Fixture { void Run() { Cert.CreateFromPemFile(path); } }")]
    [InlineData("class Fixture { void Run() { var certificates = new X509Certificate2Collection(); certificates.Import(bytes); } }")]
    [InlineData("class Fixture { void Run() { request.CreateSelfSigned(before, after); } }")]
    public void ConstructorFactoryAliasAndImportPathsRequireGuard(string source)
    {
        Assert.True(CertificateGuardSourceCensus.CreatesOrImportsCertificate(new[] { source }));
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(new[]
        {
            new CertificateGuardSourceCensus.Project("Extra.Tests", TestProject, new[] { source }),
        }));
    }

    [Fact]
    public void ExclusiveGuardRegistrationAndResidueAssertionSatisfySourceContract()
    {
        Assert.Empty(CertificateGuardSourceCensus.MissingGuards(new[] { GuardedProject(GuardedSource) }));
    }

    [Theory]
    [InlineData("DisableParallelization = true", "DisableParallelization = false")]
    [InlineData("Extra.Tests.Orderer", "Extra.Tests.MissingOrderer")]
    [InlineData("=> GuardLast(items)", "=> items")]
    [InlineData("? 1 : 0", "? 0 : 1")]
    [InlineData("Assert.True(residue is null, residue);", "Assert.True(true);")]
    [InlineData("[Fact]", "[Fact(Skip = \"disabled\")]")]
    [InlineData("[Collection(\"guard\")]", "[Collection(\"wrong\")]")]
    public void MissingExclusionOrdererAssertionOrNativeFactIsRefused(string original, string replacement)
    {
        Assert.Contains(original, GuardedSource, StringComparison.Ordinal);
        var source = GuardedSource.Replace(original, replacement, StringComparison.Ordinal);
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(new[] { GuardedProject(source) }));
    }

    [Theory]
    [InlineData("global using CA = Vouchfx.TestSupport.TestCertificateAuthority;", "class Fixture { void Create() { CA.CreateSuiteDirectory(); } }")]
    [InlineData("global using static Vouchfx.TestSupport.TestCertificateAuthority;", "class Fixture { void Create() { CreateSuiteDirectory(); } }")]
    [InlineData("global using Cert = System.Security.Cryptography.X509Certificates.X509Certificate2;", "class Fixture { void Create(byte[] data) { using var certificate = new Cert(data); } }")]
    public void ProjectGlobalImportsAcrossFilesRequireGuard(string imports, string consumer)
    {
        string[] sources = { imports, consumer };
        Assert.True(CertificateGuardSourceCensus.CreatesOrImportsCertificate(sources));
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(new[]
        {
            new CertificateGuardSourceCensus.Project("Extra.Tests", TestProject, sources),
        }));
    }

    [Theory]
    [InlineData("global using X509Certificate2 = Neutral.Other; namespace Neutral { class Other { } }", "class Fixture { void Create(byte[] data) { using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(data); } }")]
    [InlineData("global using TestCertificateAuthority = Neutral.Other; namespace Neutral { class Other { } }", "class Fixture { void Create() { Vouchfx.TestSupport.TestCertificateAuthority.CreateSuiteDirectory(); } }")]
    public void FullyQualifiedCertificatePathsAreNotHiddenByLeafAliases(string imports, string consumer)
    {
        string[] sources = { imports, consumer };
        Assert.True(CertificateGuardSourceCensus.CreatesOrImportsCertificate(sources));
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(new[]
        {
            new CertificateGuardSourceCensus.Project("Extra.Tests", TestProject, sources),
        }));
    }

    [Theory]
    [InlineData("global using Certs = System.Security.Cryptography.X509Certificates;", "class Fixture { void Create(byte[] data) { using var certificate = new Certs.X509Certificate2(data); } }")]
    [InlineData("global using Certs = System.Security.Cryptography.X509Certificates;", "class Fixture { void Create(byte[] data) { using var certificate = new Certs::X509Certificate2(data); } }")]
    [InlineData("global using Support = Vouchfx.TestSupport;", "class Fixture { void Create() { Support::TestCertificateAuthority.CreateSuiteDirectory(); } }")]
    public void NamespaceAliasHeadsPreserveCertificateLeaf(string imports, string consumer)
    {
        string[] sources = { imports, consumer };
        Assert.True(CertificateGuardSourceCensus.CreatesOrImportsCertificate(sources));
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(new[]
        {
            new CertificateGuardSourceCensus.Project("Extra.Tests", TestProject, sources),
        }));
    }

    [Theory]
    [InlineData("using CA = Vouchfx.TestSupport.TestCertificateAuthority; class Other { }", "class Fixture { void Create() { CA.CreateSuiteDirectory(); } }")]
    [InlineData("using static Vouchfx.TestSupport.TestCertificateAuthority; class Other { }", "class Fixture { void Create() { CreateSuiteDirectory(); } }")]
    public void NonGlobalImportsDoNotLeakAcrossFiles(string imports, string consumer)
    {
        Assert.False(CertificateGuardSourceCensus.CreatesOrImportsCertificate(new[] { imports, consumer }));
    }

    [Fact]
    public void NamespaceAliasDoesNotLeakIntoSiblingNamespace()
    {
        const string source = "namespace Owner { using CA = Vouchfx.TestSupport.TestCertificateAuthority; class Other { } } namespace Sibling { class Fixture { void Create() { CA.CreateSuiteDirectory(); } } }";
        Assert.False(CertificateGuardSourceCensus.CreatesOrImportsCertificate(new[] { source }));
    }

    [Fact]
    public void ActualOrdererDiscardingGuardLastAndReturningInputIsRefused()
    {
        var project = ReadProjects(RepoRoot.Resolve()).Single(project => project.Name == "Vouchfx.Cli.Tests");
        const string expression = "GuardLast(new DefaultTestCollectionOrderer().OrderTestCollections(testCollections), collection => collection.DisplayName);";
        var sources = project.Sources.Select(source => source.Replace("=>\n        " + expression,
            "{ _ = " + expression + " return testCollections; }", StringComparison.Ordinal)).ToArray();
        Assert.Contains(sources, source => source.Contains("return testCollections;", StringComparison.Ordinal));
        Assert.False(CertificateGuardSourceCensus.HasFinalGuard(project with { Sources = sources }));
    }

    [Fact]
    public void ActualGuardOverwritingSweepResultWithNullIsRefused()
    {
        var project = ReadProjects(RepoRoot.Resolve()).Single(project => project.Name == "Vouchfx.Cli.Tests");
        var sources = project.Sources.Select(source => source.Replace("Assert.True(residue is null, residue);",
            "residue = null; Assert.True(residue is null, residue);", StringComparison.Ordinal)).ToArray();
        Assert.Contains(sources, source => source.Contains("residue = null;", StringComparison.Ordinal));
        Assert.False(CertificateGuardSourceCensus.HasFinalGuard(project with { Sources = sources }));
    }

    [Theory]
    [InlineData("class Fixture { void Run() { HostedHttpTlsCertificateBed.CreateAiaServer(url); } }")]
    [InlineData("using Bed = Vouchfx.TestSupport.HostedHttpTlsCertificateBed; class Fixture { void Run() { Bed.CreateClientChain(arm); } }")]
    public void HostedTransportFactoryCallsRequireGuard(string source)
    {
        Assert.True(CertificateGuardSourceCensus.CreatesOrImportsCertificate(new[] { source }));
        Assert.Equal(s_missingExtra, CertificateGuardSourceCensus.MissingGuards(new[] { new CertificateGuardSourceCensus.Project("Extra.Tests", TestProject, new[] { source }) }));
    }

    [Fact]
    public void ActualHostedTransportHostIsDetectedAndItsOptInGuardIsWired()
    {
        var project = ReadProjects(RepoRoot.Resolve()).Single(project => project.Name == "Vouchfx.Engine.Runtime.TransportAcceptance.Tests");
        Assert.True(CertificateGuardSourceCensus.CreatesOrImportsCertificate(project.Sources));
        Assert.True(CertificateGuardSourceCensus.HasFinalGuard(project));
        var noGuard = project with { Sources = project.Sources.Select(source => source.Replace("=> Assert.Null(TestCertificateStoreGuard.SweepProcessResidue());", "=> Assert.True(true);", StringComparison.Ordinal)).ToArray() };
        Assert.False(CertificateGuardSourceCensus.HasFinalGuard(noGuard));
        Assert.False(CertificateGuardSourceCensus.HasFinalGuard(project with { Name = "Extra.Tests" }));
    }

    private static CertificateGuardSourceCensus.Project GuardedProject(string source) =>
        new("Extra.Tests", TestProject, new[] { MintingSource, source });

    private static CertificateGuardSourceCensus.Project[] ReadProjects(string root) => Directory.GetDirectories(Path.Combine(root, "tests"))
        .SelectMany(directory => Directory.GetFiles(directory, "*.csproj").Select(project =>
        {
            var xml = File.ReadAllText(project);
            var name = XDocument.Parse(xml).Descendants().SingleOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value
                ?? Path.GetFileNameWithoutExtension(project);
            var sources = Directory.EnumerateFiles(directory, "*.cs", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj")).Select(File.ReadAllText).ToArray();
            return new CertificateGuardSourceCensus.Project(name, xml, sources);
        })).ToArray();
}
