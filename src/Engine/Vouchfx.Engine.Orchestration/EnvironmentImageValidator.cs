using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Topology-free image checks shared by inline binding and runtime mapping.
/// External drivers validate their own configuration; this pass handles only the
/// built-in services and dependency declarations.
/// </summary>
internal static class EnvironmentImageValidator
{
    internal static void Validate(EnvironmentSpec? env)
    {
        foreach (var (name, spec) in env?.Services ?? new Dictionary<string, ServiceSpec>())
            ValidateService(name, spec, nameof(env));
        foreach (var (name, spec) in env?.Dependencies ?? new Dictionary<string, DependencySpec>())
            ValidateDependency(name, spec, nameof(env));
    }

    internal static void ValidateService(string name, ServiceSpec spec, string parameterName)
    {
        if (spec.Image is not { } image)
        {
            if (spec.Project is null)
            {
                throw new ArgumentException(
                    $"Service '{name}' has neither 'image' nor 'project' set. " +
                    "Exactly one must be supplied.",
                    parameterName);
            }
            return;
        }

        var parsed = ImageReferenceParser.Parse(image);
        ValidateDigest("Service", name, parsed, parameterName);
        if (parsed.Tag is null && parsed.Digest is null)
        {
            throw new ArgumentException(
                $"Service '{name}' sets 'image: {image}' with no tag or digest. This would " +
                "silently float on ':latest', defeating the section 4 determinism invariant. " +
                "Embed a tag or a 'sha256:' digest in 'image:'.",
                parameterName);
        }
    }

    internal static void ValidateDependency(string name, DependencySpec spec, string parameterName)
    {
        // Empty means absent for dependency image/version overrides, including quoted
        // empty YAML scalars. Whitespace is authored text: reject it through Parse,
        // rather than silently discarding it in favour of the provider's default.
        if (string.IsNullOrEmpty(spec.Image))
            return;

        var parsedImage = ImageReferenceParser.Parse(spec.Image);
        ValidateDigest("Dependency", name, parsedImage, parameterName);
        var hasVersion = !string.IsNullOrEmpty(spec.Version);

        if ((parsedImage.Tag is not null || parsedImage.Digest is not null) && hasVersion)
        {
            var carries = parsedImage.Digest is not null
                ? $"digest '{parsedImage.Digest}'"
                : $"tag '{parsedImage.Tag}'";
            throw new ArgumentException(
                $"Dependency '{name}' sets both 'image: {spec.Image}' (which already " +
                $"carries a {carries}) and 'version: {spec.Version}'. This is ambiguous - " +
                "specify the tag/digest in exactly one place: either embed it in 'image:', " +
                "or use 'version:' alone.",
                parameterName);
        }

        // WithImage(repository) replaces a provider's pinned default with latest.
        // A repository-only override therefore needs the sibling version as its tag.
        if (parsedImage.Tag is null && parsedImage.Digest is null && !hasVersion)
        {
            throw new ArgumentException(
                $"Dependency '{name}' sets 'image: {spec.Image}' with no tag or digest, " +
                "and no sibling 'version:' either. This would silently float on the " +
                "':latest' tag, defeating the section 4 determinism invariant. Either embed a " +
                $"tag in 'image:' (e.g. 'image: {spec.Image}:<tag>'), or add a " +
                "'version:' field.",
                parameterName);
        }
    }

    private static void ValidateDigest(string owner, string name, ImageReference image, string parameterName)
    {
        // ContainerImageAnnotation.SHA256 supports this algorithm only. Check before
        // Configure mutates any resource; Parse already checks the hexadecimal body.
        if (image.Digest is not null && !image.Digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{owner} '{name}' image digest '{image.Digest}' does not use " +
                "the 'sha256:' algorithm prefix - the only digest algorithm Aspire's " +
                "container image annotation supports.",
                parameterName);
        }
    }
}
