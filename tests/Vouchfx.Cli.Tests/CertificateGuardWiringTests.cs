using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Vouchfx.Cli.Tests;

// Inert: metadata and neutral names only. Never invokes the certificate-store guard.
public sealed class CertificateGuardWiringTests
{
    [Fact]
    public void GuardLast_PreservesOtherOrderAndMovesOwnCollectionLast()
    {
        string[] names = { "zulu", CertificateStoreGuardCollectionDefinition.Name, "alpha", "mike" };
        string[] expected = { "zulu", "alpha", "mike", CertificateStoreGuardCollectionDefinition.Name };
        Assert.Equal(expected, CertificateStoreGuardLastOrderer.GuardLast(names, name => name));
    }

    [Fact]
    public void GuardLast_WithoutOwnCollectionPreservesInputOrder()
    {
        string[] names = { "zulu", "alpha", "mike" };
        Assert.Equal(names, CertificateStoreGuardLastOrderer.GuardLast(names, name => name));
    }

    [Fact]
    public void ActualOrdererPreservesDefaultRelativeOrderAndPlacesGuardLast()
    {
        var assembly = typeof(CertificateStoreGuardLastOrderer).Assembly;
        var testAssembly = new TestAssembly(Reflector.Wrap(assembly), assembly.Location, assembly.GetName().Version!);
        ITestCollection[] collections =
        {
            new TestCollection(testAssembly, null, "zulu", new Guid("00000000-0000-0000-0000-000000000003")),
            new TestCollection(testAssembly, Reflector.Wrap(typeof(CertificateStoreGuardCollectionDefinition)),
                CertificateStoreGuardCollectionDefinition.Name, new Guid("00000000-0000-0000-0000-000000000002")),
            new TestCollection(testAssembly, null, "alpha", new Guid("00000000-0000-0000-0000-000000000001")),
        };
        var defaultOrder = new DefaultTestCollectionOrderer().OrderTestCollections(collections).ToArray();
        Assert.NotEqual(CertificateStoreGuardCollectionDefinition.Name, defaultOrder[^1].DisplayName);
        var actual = new CertificateStoreGuardLastOrderer().OrderTestCollections(collections).ToArray();
        Assert.Equal(CertificateStoreGuardCollectionDefinition.Name, actual[^1].DisplayName);
        Assert.Equal(defaultOrder.Where(collection => collection.DisplayName != CertificateStoreGuardCollectionDefinition.Name), actual[..^1]);
    }

    [Fact]
    public void ExclusiveCollectionAndNativeGuardHaveMatchingMetadata()
    {
        var definition = Assert.Single(typeof(CertificateStoreGuardCollectionDefinition)
            .GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute));
        Assert.Equal(CertificateStoreGuardCollectionDefinition.Name, definition.ConstructorArguments[0].Value);
        Assert.Equal(true, Assert.Single(definition.NamedArguments, argument => argument.MemberName == "DisableParallelization").TypedValue.Value);
        var guard = Assert.Single(typeof(CertificateStoreHygieneGuardTests)
            .GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(CollectionAttribute));
        Assert.Equal(definition.ConstructorArguments[0].Value, guard.ConstructorArguments[0].Value);
    }

    [Fact]
    public void AssemblyRegistrationResolvesToConstructableOrderer()
    {
        var registration = Assert.Single(typeof(CertificateStoreGuardLastOrderer).Assembly
            .GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(TestCollectionOrdererAttribute));
        var resolved = Type.GetType($"{registration.ConstructorArguments[0].Value}, {registration.ConstructorArguments[1].Value}");
        Assert.Equal(typeof(CertificateStoreGuardLastOrderer), resolved);
        Assert.IsAssignableFrom<ITestCollectionOrderer>(Activator.CreateInstance(resolved!)!);
    }
}
