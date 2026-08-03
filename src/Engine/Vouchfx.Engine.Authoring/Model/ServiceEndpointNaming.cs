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

using System.Globalization;

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// Computes the Aspire endpoint names a <see cref="ServiceSpec"/>'s declared shape
/// produces (services-generalisation spec, REQ-008/REQ-010; the secured endpoint,
/// authenticated-infrastructure-mtls REQ-023).
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
    /// The fixed endpoint name a service's SECURED endpoint uses (REQ-023) — the one
    /// declared with an <c>https</c> URI scheme, and the one staged at
    /// <c>svc::&lt;name&gt;</c> for a service that declares <c>security</c>.
    /// </summary>
    /// <remarks>
    /// One fixed name whichever shape <c>security.endpoint</c> takes: whether the author
    /// wrote a port number or the name of an endpoint their own <c>ports:</c>/<c>httpPort:</c>
    /// declaration produced, the resulting endpoint is named <c>"https"</c> and REPLACES the
    /// plaintext endpoint that port would otherwise have carried. Keeping the original name
    /// (a <c>"tcp-8443"</c> endpoint silently serving <c>https://</c>) was rejected: the name
    /// is what <see cref="DeclaredEndpointNames"/> surfaces through
    /// <c>IProjectContext.DeclaredServices</c> and what a <c>healthCheck</c> resolves against,
    /// and a name that contradicts its own scheme is a defect waiting to be read literally.
    /// </remarks>
    public const string HttpsEndpointName = "https";

    /// <summary>
    /// The endpoint name a <see cref="ServiceSpec.Ports"/> entry produces, e.g. port
    /// <c>9093</c> names its endpoint <c>"tcp-9093"</c>.
    /// </summary>
    public static string TcpEndpointName(int port) => $"tcp-{port}";

    /// <summary>
    /// Resolves the container port a service's <c>security.endpoint</c> selector names
    /// (REQ-002/REQ-023), or <see langword="null"/> when the service declares no
    /// <c>security</c> block or the selector cannot be resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two accepted shapes, matching the schema's own <c>endpoint</c> field:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <strong>A port number</strong> — a bare decimal integer in 1..65535 with no
    ///     leading zero. The strictness is deliberate: <see cref="SecuritySpec.Endpoint"/>
    ///     retains the RAW scalar text, so a leading-zero value such as <c>0123</c> (which
    ///     YAML itself reads as octal 83, while this text reads 123) has three plausible
    ///     meanings and is treated as none of them — it falls through to the name branch and
    ///     is reported as unresolvable rather than silently securing an arbitrary port.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>The name of a declared endpoint</strong> — one of the names this
    ///     service's own <c>ports:</c>/<c>httpPort:</c> declaration produces
    ///     (<c>"http"</c>, <c>"tcp-&lt;port&gt;"</c>), resolved to that endpoint's container
    ///     port.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The schema documents a quoted numeric value as a NAME rather than a port, but
    /// <see cref="SecuritySpec.Endpoint"/> carries raw scalar text with quoting already
    /// discarded, so the two shapes are indistinguishable here and the port reading wins. No
    /// endpoint a service can declare is ever NAMED with bare digits, so nothing is lost:
    /// the name reading of <c>"8443"</c> would resolve to nothing in every case.
    /// </para>
    /// </remarks>
    public static int? ResolveSecuredPort(ServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Security?.Endpoint is not { Length: > 0 } selector)
        {
            return null;
        }

        if (IsPortLiteral(selector, out var port))
        {
            return port;
        }

        foreach (var (name, endpointPort) in PlaintextEndpoints(spec))
        {
            if (string.Equals(name, selector, StringComparison.Ordinal))
            {
                return endpointPort;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the ordered <c>(name, container port)</c> pairs a service's
    /// <c>ports:</c>/<c>httpPort:</c> declaration produces, IGNORING any <c>security</c>
    /// block — the pre-REQ-023 endpoint set, which
    /// <see cref="ResolveSecuredPort(ServiceSpec)"/> resolves a named selector against and
    /// <see cref="DeclaredEndpointNames(ServiceSpec)"/> then rewrites.
    /// </summary>
    public static IReadOnlyList<(string Name, int Port)> PlaintextEndpoints(ServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Project is not null)
        {
            return Array.Empty<(string, int)>();
        }

        if (spec.Ports is not { Count: > 0 })
        {
            if (spec.HttpPort is { } explicitHttpPort)
            {
                return new[] { (HttpEndpointName, explicitHttpPort) };
            }

            // REQ-023, applying REQ-008's own precedent: declaring `security` — like declaring
            // `ports` — switches the service off the HTTP-only-by-default shape, so the
            // IMPLICIT default HTTP endpoint on port 80 is suppressed unless `httpPort` is
            // ALSO explicitly declared (the same opt-in hybrid rule). A service whose only
            // declared endpoint is its secured one would otherwise be given a plaintext
            // endpoint on a port nothing serves, and DCP would allocate and health-gate it.
            return spec.Security is null
                ? new[] { (HttpEndpointName, 80) }
                : Array.Empty<(string, int)>();
        }

        var pairs = new List<(string Name, int Port)>(spec.Ports.Count + 1);
        foreach (var port in spec.Ports)
        {
            pairs.Add((TcpEndpointName(port), port));
        }

        if (spec.HttpPort is { } httpPort)
        {
            pairs.Add((HttpEndpointName, httpPort));
        }

        return pairs;
    }

    /// <summary>
    /// Returns the ordered <c>(name, container port, scheme-carrying)</c> endpoint set a
    /// service's declared shape produces once its <c>security</c> block (REQ-023) is applied:
    /// the endpoint on the secured port is named <see cref="HttpsEndpointName"/> and REPLACES
    /// the plaintext one, and a secured port that matches no declared port is appended.
    /// </summary>
    /// <remarks>
    /// This is the single computation <c>EnvironmentMapper</c> builds its actual Aspire
    /// <c>WithEndpoint</c>/<c>WithHttpEndpoint</c>/<c>WithHttpsEndpoint</c> calls from and
    /// <see cref="DeclaredEndpointNames(ServiceSpec)"/> projects the names of — so the
    /// topology and <c>IProjectContext.DeclaredServices</c> cannot disagree about either the
    /// names or which one is secured.
    /// </remarks>
    public static IReadOnlyList<ServiceEndpointDeclaration> EndpointDeclarations(ServiceSpec spec)
    {
        // Stated here rather than inherited from PlaintextEndpoints below. The behaviour is
        // identical either way — that call throws the same exception for the same argument — but
        // all four public entry points on this class guarding their own argument is the property
        // a reader can check locally, and it survives a later refactor that reorders these calls.
        ArgumentNullException.ThrowIfNull(spec);

        var plaintext = PlaintextEndpoints(spec);
        var securedPort = ResolveSecuredPort(spec);

        if (securedPort is not { } secured)
        {
            var asDeclared = new List<ServiceEndpointDeclaration>(plaintext.Count);
            foreach (var (name, port) in plaintext)
            {
                asDeclared.Add(new ServiceEndpointDeclaration(name, port, IsSecured: false));
            }

            return asDeclared;
        }

        var result = new List<ServiceEndpointDeclaration>(plaintext.Count + 1);
        var replaced = false;
        foreach (var (name, port) in plaintext)
        {
            // EVERY plaintext endpoint on the secured port is replaced, not merely the first.
            // `replaced` decides only whether a secured endpoint still has to be APPENDED
            // below; it must not gate the match, or a second plaintext endpoint on the same
            // port survives alongside the secured one. Two shapes reach that: `ports: [P]`
            // with a sibling `httpPort: P` (which the schema PERMITS — $defs/service's three
            // allOf clauses check `project`/`image` exclusivity, the ports-without-httpPort
            // health-check rule and the project-form restrictions, and none cross-checks
            // httpPort against ports items; `uniqueItems` rejects `[P, P]` but says nothing
            // about httpPort), and `ports: [P, P]` via a hand-built EnvironmentSpec. Measured
            // before this fix, `ports:[8443] + httpPort:8443 + security.endpoint:8443` emitted
            // `https@8443 SECURED | http@8443 PLAINTEXT`: the mapper then made BOTH
            // WithHttpsEndpoint and WithHttpEndpoint calls on one port, the REQ-023
            // http-health-check guard did not fire because an "http" endpoint still existed,
            // and WithHttpHealthCheck probed the mTLS listener — which answers 400 to an
            // unauthenticated request, holding a working topology unhealthy forever. It also
            // advertised a plaintext endpoint through DeclaredServices on a port the author
            // declared secured. Not a step-path bypass (`svc::<name>` still stages `https`),
            // but a hung topology. A service with no `security` block cannot reach this loop
            // at all — securedPort is null and the branch above returns.
            // `replaced` still gates EMISSION, so the secured port yields exactly ONE endpoint
            // however many plaintext declarations collided on it. Matching unconditionally but
            // emitting once is the whole fix: matching only the first (the defect) let the
            // second survive as plaintext, while emitting on every match would produce two
            // endpoints both named "https" on one port — a duplicate name for Aspire to build
            // and for DeclaredEndpointNames to advertise, which trades one defect for another.
            if (port == secured)
            {
                if (replaced)
                {
                    continue;
                }

                replaced = true;
                result.Add(new ServiceEndpointDeclaration(HttpsEndpointName, port, IsSecured: true));
                continue;
            }

            result.Add(new ServiceEndpointDeclaration(name, port, IsSecured: false));
        }

        if (!replaced)
        {
            result.Add(new ServiceEndpointDeclaration(HttpsEndpointName, secured, IsSecured: true));
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="value"/> is a bare decimal port literal in 1..65535 with no
    /// leading zero.
    /// </summary>
    private static bool IsPortLiteral(string value, out int port)
    {
        port = 0;

        if (value.Length is 0 or > 5 || value[0] == '0')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
               port is >= 1 and <= 65535;
    }

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
    ///   <item><description>
    ///     A service declaring <c>security</c> (REQ-023) additionally has the endpoint on
    ///     its secured port named <see cref="HttpsEndpointName"/> instead of the plaintext
    ///     name it would otherwise carry; a secured port matching no declared port appends
    ///     <see cref="HttpsEndpointName"/>. A service declaring no <c>security</c> block is
    ///     unaffected in every shape above.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> DeclaredEndpointNames(ServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Project is not null)
        {
            return Array.Empty<string>();
        }

        var declarations = EndpointDeclarations(spec);
        var names = new List<string>(declarations.Count);
        foreach (var declaration in declarations)
        {
            names.Add(declaration.Name);
        }

        return names;
    }
}

/// <summary>
/// One endpoint a service's declared shape produces: its Aspire endpoint name, the container
/// port it targets, and whether it is the service's SECURED endpoint (REQ-023).
/// </summary>
/// <param name="Name">The Aspire endpoint name.</param>
/// <param name="Port">The container port the endpoint targets.</param>
/// <param name="IsSecured">
/// <see langword="true"/> for the one endpoint a declared <c>security</c> block selects — the
/// endpoint declared with an <c>https</c> URI scheme and staged at <c>svc::&lt;name&gt;</c>.
/// </param>
public sealed record ServiceEndpointDeclaration(string Name, int Port, bool IsSecured);
