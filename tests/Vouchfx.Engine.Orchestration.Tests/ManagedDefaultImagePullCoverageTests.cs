using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>Docker-free parity between actual managed resources and CI's pull inventory.</summary>
public sealed class ManagedDefaultImagePullCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkflowPullList_CoversEveryManagedDefaultAndSidecar(bool securedKafka)
    {
        var pulls = WorkflowPullImages();

        // Discover the actual registration table: a new kind must participate without
        // someone remembering to extend a second list of kinds or Aspire defaults.
        var field = typeof(EnvironmentMapper).GetField(
            "s_dependencyRegistry", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var registry = Assert.IsAssignableFrom<IDictionary>(field.GetValue(null));
        Assert.NotEmpty(registry.Keys.Cast<string>());

        var missing = new List<string>();
        foreach (var kind in registry.Keys.Cast<string>().Order(StringComparer.Ordinal))
        {
            // The optional Kafka schema registry also has an engine-owned default.
            var extra = kind == "kafka"
                ? new YamlMappingNode { { "schemaRegistry", "true" } }
                : null;
            var env = new EnvironmentSpec(
                Services: null,
                Dependencies: new Dictionary<string, DependencySpec>
                {
                    ["dependency"] = new(kind, Version: null, Extra: extra)
                    {
                        // TLS also exercises the secured mapper recipe without reading files
                        // or connecting. Future image substitutions must stay in the inventory.
                        Security = kind == "kafka" && securedKafka
                            ? new SecuritySpec("tls", "tcp", null, null, null, null)
                            : null,
                    },
                },
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null);
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                DisableDashboard = true,
                Args = Array.Empty<string>(),
                AssemblyName = typeof(ManagedDefaultImagePullCoverageTests).Assembly.GetName().Name,
            });

            // Configure only builds an in-memory resource graph. Never Build/Start it.
            EnvironmentMapper.Map(env).Configure(builder);
            var containers = builder.Resources.OfType<ContainerResource>().ToArray();
            Assert.NotEmpty(containers);
            foreach (var container in containers)
            {
                var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
                Assert.False(string.IsNullOrEmpty(image.Image));
                var reference = string.IsNullOrEmpty(image.Registry)
                    ? image.Image
                    : image.Registry + "/" + image.Image;
                // Docker selects the digest when Aspire carries both a tag and SHA256.
                reference += !string.IsNullOrEmpty(image.SHA256)
                    ? "@sha256:" + image.SHA256
                    : ":" + image.Tag;
                Assert.True(!string.IsNullOrEmpty(image.SHA256) || !string.IsNullOrEmpty(image.Tag),
                    $"Managed default {kind}/{container.Name} has no explicit tag or digest.");
                if (!pulls.Contains(Normalize(reference)))
                {
                    missing.Add($"{kind}/{container.Name}: {reference}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "CI pre-pull is missing actual managed defaults/sidecars:" + Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    private static HashSet<string> WorkflowPullImages()
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(RepoRoot.Resolve(), ".github", "workflows", "build.yml"));
        yaml.Load(reader);
        var root = (YamlMappingNode)Assert.Single(yaml.Documents).RootNode;
        var integration = ((YamlMappingNode)root["jobs"])["integration"];
        var steps = (YamlSequenceNode)((YamlMappingNode)integration)["steps"];
        var pull = Assert.Single(steps.Children.Cast<YamlMappingNode>(), step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
            ((YamlScalarNode)name).Value ==
            "Pre-pull dependency images (warm Docker cache; fail fast on an unpullable pin)");
        var script = ((YamlScalarNode)pull["run"]).Value!;
        var loops = Regex.Matches(script, @"(?m)^for image in \\\n(?<images>[\s\S]*?) ; do$");
        var list = Assert.Single(loops.Cast<Match>()).Groups["images"].Value;
        // Accept literal refs only; a dynamic shell list must supply a new extraction contract.
        var tokens = list.Replace("\\\n", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.Matches(@"\A[a-zA-Z0-9][a-zA-Z0-9./:@_-]*\z", token));
        var normalized = tokens.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(tokens.Length, normalized.Count);
        return normalized;
    }

    private static string Normalize(string reference)
    {
        // Docker Hub's official-image shorthand is equivalent to docker.io/library/.
        if (reference.StartsWith("docker.io/", StringComparison.Ordinal))
        {
            reference = reference["docker.io/".Length..];
        }
        else
        {
            var slash = reference.IndexOf('/');
            if (slash >= 0 && (reference[..slash].Contains('.') || reference[..slash].Contains(':') ||
                reference[..slash] == "localhost"))
            {
                return reference;
            }
        }

        return reference.StartsWith("library/", StringComparison.Ordinal)
            ? reference["library/".Length..]
            : reference;
    }
}
