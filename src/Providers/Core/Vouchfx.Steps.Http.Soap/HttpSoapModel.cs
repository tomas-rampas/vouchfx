// Vouchfx.Steps.Http.Soap — http.soap step model (DSL §5.1, second member of the http family).
// Strongly-typed record; Dictionary<string,object> is explicitly prohibited (§13).
//
// http.soap is the RAW-ENVELOPE provider (v1 decision, documented on Envelope below): the
// author supplies the FULL SOAP envelope XML themselves — there is no auto-wrapping of a bare
// payload into an <soap:Envelope><soap:Body>…</soap:Body></soap:Envelope> skeleton. This keeps
// the provider's contract small and unambiguous (no guessing at namespace prefixes, SOAP
// version, or header placement) at the cost of a little authoring verbosity; a future v1.x
// provider (or this one, additively) could add an auto-wrap convenience without breaking this
// one.
using Vouchfx.Sdk;

namespace Vouchfx.Steps.Http.Soap;

/// <summary>
/// A single expected value at an XPath location within the SOAP response envelope.
/// </summary>
/// <param name="Path">
/// The XPath expression evaluated against the response envelope via a hardened
/// <see cref="System.Xml.XmlReader"/> (identical hardening to <c>http.rest</c>'s XPath
/// capture: <c>DtdProcessing.Prohibit</c>, <c>XmlResolver=null</c>, character caps). No
/// <see cref="System.Xml.IXmlNamespaceResolver"/> is registered — write namespace-agnostic
/// expressions using <c>local-name()</c> (e.g.
/// <c>//*[local-name()='Body']/*[local-name()='GetUserResponse']/*[local-name()='Name']</c>),
/// exactly mirroring <c>http.rest</c>'s own XPath-capture behaviour (no namespace manager is
/// registered there either) — this is a deliberate consistency choice, not an oversight.
/// </param>
/// <param name="Value">
/// The expected string value at that location (the selected node's <c>.Value</c>), compared
/// by ordinal equality. May contain <c>{placeholder}</c> and <c>${secret:source/path}</c>
/// tokens resolved at step-execution time.
/// </param>
public sealed record SoapXPathAssertion(string Path, string Value);

/// <summary>
/// The response assertion block for an <c>http.soap</c> step. All members are optional; an
/// absent block is equivalent to <c>new SoapExpectation(null, null, null)</c> (status defaults
/// to 200, no fault expected, no XPath assertions).
/// </summary>
/// <param name="Status">
/// Expected HTTP status code. Defaults to <c>200</c> when omitted — the conventional SOAP 1.1
/// success status. A SOAP fault is commonly (but not universally) served with HTTP 500; declare
/// <c>status: 500</c> explicitly alongside <c>fault: true</c> when your SUT follows that
/// convention (see <see cref="Fault"/>'s remarks for the exact interplay).
/// </param>
/// <param name="Fault">
/// Whether the response is expected to contain a SOAP <c>Fault</c> element (matched by
/// <c>local-name()='Fault'</c> within the envelope, so either SOAP 1.1's
/// <c>http://schemas.xmlsoap.org/soap/envelope/</c> or SOAP 1.2's
/// <c>http://www.w3.org/2003/05/soap-envelope</c> namespace is recognised). Defaults to
/// <see langword="false"/> when omitted (no fault expected).
/// </param>
/// <param name="Xpath">
/// Optional list of XPath assertions evaluated against the response envelope after the status
/// and fault-expectation checks both pass. Ignored entirely when either of those checks fails
/// (the observation already carries the reason).
/// </param>
public sealed record SoapExpectation(
    int? Status,
    bool? Fault,
    IReadOnlyList<SoapXPathAssertion>? Xpath);

/// <summary>
/// Strongly-typed model for the <c>http.soap</c> step kind (DSL §5.1) — the second member of
/// the <c>http</c> family alongside <c>http.rest</c>.
/// </summary>
/// <param name="Target">
/// Logical name of the service to call, as declared under <c>environment.services</c>.
/// Resolved to a real address by Aspire service discovery at orchestration time — identical
/// addressing to <c>http.rest</c>.
/// </param>
/// <param name="Path">
/// The request path, which may contain <c>{placeholder}</c> and <c>${secret:…}</c> tokens
/// resolved at execution time. Subject to the same SSRF-hardening rules as
/// <c>http.rest</c>'s <c>path</c> (rooted relative path only).
/// </param>
/// <param name="Action">
/// Optional SOAPAction header value (the SOAP 1.1 convention). When declared, the emitted
/// helper sends it quoted per the SOAP 1.1 specification (<c>SOAPAction: "value"</c>). May
/// contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens. <see langword="null"/>
/// omits the header entirely (some SOAP 1.1 services tolerate its absence; a picky one will
/// reject the call, in which case declare it).
/// </param>
/// <param name="Envelope">
/// The FULL SOAP request envelope XML, given as a raw template string — this provider does
/// NOT auto-wrap a bare payload (see the file header for the rationale). Any
/// <c>{placeholder}</c> / <c>${secret:source/path}</c> token inside is resolved at
/// step-execution time inside the emitted helper's guarded region (§17), exactly like
/// <c>http.rest</c>'s <c>body</c> field. Sent with <c>Content-Type: text/xml; charset=utf-8</c>
/// (SOAP 1.1) — SOAP 1.2's <c>application/soap+xml</c> (with the action folded into the
/// Content-Type rather than a separate header) is a documented v1 limitation, not supported.
/// </param>
/// <param name="Expect">
/// The response assertion block. <see langword="null"/> is equivalent to an all-default
/// <see cref="SoapExpectation"/> (status 200, no fault expected, no XPath assertions).
/// </param>
public sealed record HttpSoapModel(
    string Target,
    string Path,
    string? Action,
    string Envelope,
    SoapExpectation? Expect) : IStepModel;
