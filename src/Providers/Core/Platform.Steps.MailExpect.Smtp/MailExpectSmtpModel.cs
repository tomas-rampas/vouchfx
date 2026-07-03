// Platform.Steps.MailExpect.Smtp — mail-expect.smtp step model (DSL §5).
//
// Three strongly-typed records (no Dictionary<string,object> — §13 invariant):
//   MailMatch         — the filtering criteria (To address, subject substring, body substring).
//   MailExpectation   — wraps MailMatch with an optional count.
//   MailExpectSmtpModel — the top-level model: target dependency + expectation block.
//
// YAML shape (DSL §5):
//   type: mail-expect.smtp
//   target: mailpit          # logical name of the mailpit dependency
//   expect:
//     count: 1               # optional; defaults to 1 when omitted
//     match:
//       to: alice@example.com
//       subject-contains: "Welcome"
//       body-contains: "Hello, Alice"
//   verifyMode: RETRY        # typical: RETRY so the engine polls until mail arrives
//   timeout: PT30S
using Platform.Sdk;

namespace Platform.Steps.MailExpect.Smtp;

/// <summary>
/// The filtering criteria for a <c>mail-expect.smtp</c> step.
/// All fields are optional; at least one must be declared (validated by
/// <see cref="MailExpectSmtpProvider.Validate"/>).
/// </summary>
/// <param name="To">
/// Expected recipient address (case-insensitive equality against each address in the
/// message's <c>To</c> list).  Optional.
/// </param>
/// <param name="SubjectContains">
/// Substring the message subject must contain (ordinal).  Optional.
/// </param>
/// <param name="BodyContains">
/// Substring the message plain-text body must contain (ordinal).  Optional.
/// Fetching the full body requires a second Mailpit API call per candidate message.
/// </param>
public sealed record MailMatch(
    string? To = null,
    string? SubjectContains = null,
    string? BodyContains = null);

/// <summary>
/// The expectation block for a <c>mail-expect.smtp</c> step: match criteria
/// plus an optional expected count.
/// </summary>
/// <param name="Match">
/// The filtering criteria a message must satisfy to be counted.
/// </param>
/// <param name="Count">
/// Expected number of matching messages.  When absent the assertion passes
/// when at least one matching message is found (count >= 1).
/// </param>
public sealed record MailExpectation(
    MailMatch Match,
    int? Count = null);

/// <summary>
/// Strongly-typed model for the <c>mail-expect.smtp</c> step kind.
/// </summary>
/// <param name="Target">
/// Logical name of the <c>mailpit</c> dependency declared under
/// <c>environment.dependencies</c>.  The engine stages its HTTP API URL at
/// <c>conn::&lt;Target&gt;</c> via <see cref="VarKeys.Connection"/>.
/// </param>
/// <param name="Expect">
/// The expectation block (match criteria + optional count).
/// </param>
public sealed record MailExpectSmtpModel(
    string Target,
    MailExpectation Expect)
    : IStepModel;
