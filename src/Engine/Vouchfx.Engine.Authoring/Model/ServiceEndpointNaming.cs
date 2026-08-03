// Vouchfx.Engine.Authoring — ServiceEndpointNaming (services-generalisation, PR B).
//
// Single source of truth for the Aspire endpoint NAMES a service's declared shape
// (ports/httpPort) produces. Shared by two independent consumers that must agree on
// these names without either one deriving from the other:
//   • Vouchfx.Engine.Orchestration.EnvironmentMapper — builds the ACTUAL Aspire
//     WithEndpoint/WithHttpEndpoint calls under these names (and a healthCheck's 'tcp'
//     branch resolves its probed endpoint by looking one of these names up via
//     IResourceBuilder.GetEndpoint(name)).
//   • Vouchfx.Engine.Runtime.RunProjectContext (via ProviderPipeline.BuildProjectContext)
//     — surfaces these SAME names through IProjectContext.DeclaredServices at provider
//     validate/compile time, before any Aspire topology exists at all.
// Centralising the convention here means the two can never silently drift apart.

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// Computes the Aspire endpoint names a <see cref="ServiceSpec"/>'s declared shape
/// produces (services-generalisation spec, REQ-008/REQ-010).
/// </summary>
public static class ServiceEndpointNaming
{
    /// <summary>
    /// The fixed endpoint name every implicit or explicit HTTP endpoint uses — unchanged
    /// from the engine's pre-REQ-008 behaviour, where every image-form service's single
    /// endpoint was always named <c>"http"</c>.
    /// </summary>
    public const string HttpEndpointName = "http";

    /// <summary>
    /// The endpoint name a <see cref="ServiceSpec.Ports"/> entry produces, e.g. port
    /// <c>9093</c> names its endpoint <c>"tcp-9093"</c>.
    /// </summary>
    public static string TcpEndpointName(int port) => $"tcp-{port}";

    /// <summary>
    /// Returns the ordered list of Aspire endpoint names <paramref name="spec"/>'s declared
    /// shape produces.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///     A project-form service (<see cref="ServiceSpec.Project"/> set) yields an EMPTY
    ///     list: Aspire's <c>AddProject(name, csprojPath)</c> auto-discovers the project's
    ///     own launch-profile endpoints, which this engine does not model or name.
    ///   </description></item>
    ///   <item><description>
    ///     An image-form service with NO explicit <see cref="ServiceSpec.Ports"/> yields
    ///     exactly <c>["http"]</c> — the implicit default HTTP endpoint (REQ-008's
    ///     backward-compatible default shape).
    ///   </description></item>
    ///   <item><description>
    ///     An image-form service WITH explicit <see cref="ServiceSpec.Ports"/> yields one
    ///     <see cref="TcpEndpointName(int)"/> entry per declared port, PLUS <c>"http"</c>
    ///     only when <see cref="ServiceSpec.HttpPort"/> is ALSO explicitly declared (the
    ///     opt-in hybrid shape — declaring <c>ports</c> alone no longer implies an HTTP
    ///     endpoint at all, REQ-008's whole point).
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> DeclaredEndpointNames(ServiceSpec spec)
    {
        if (spec.Project is not null)
        {
            return Array.Empty<string>();
        }

        var hasExplicitPorts = spec.Ports is { Count: > 0 };

        if (!hasExplicitPorts)
        {
            return new[] { HttpEndpointName };
        }

        var names = new List<string>(spec.Ports!.Count + 1);
        foreach (var port in spec.Ports!)
        {
            names.Add(TcpEndpointName(port));
        }

        if (spec.HttpPort is not null)
        {
            names.Add(HttpEndpointName);
        }

        return names;
    }
}
