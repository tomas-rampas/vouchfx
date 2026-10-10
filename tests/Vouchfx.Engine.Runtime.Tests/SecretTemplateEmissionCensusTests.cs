using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Independent, bounded inspection of actual Bind/Emit outputs. The fixture modes are
/// reviewed expectations; the coverage oracle is the emitted resolver call graph, not
/// StepTemplateCensus's selectors or a list of helper Source inclusions.
/// </summary>
public sealed class SecretTemplateEmissionCensusTests
{
    private static readonly string[] s_unknownResolverHelpers =
    {
        "static class Fixture_Helpers { public static string Run(string template) { return Other_Helpers.ResolveTemplate(secrets, vars, template); } }",
    };

    internal enum Mode { Secret, Placeholder, Literal }
    internal sealed record Slot(string Path, Mode Mode);
    internal sealed record Fixture(StepNode Node, IReadOnlyDictionary<string, Slot> Markers);

    internal static StepKindRegistry Discover() => StepKindRegistry.BuildAndFreeze(
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Vouchfx.Steps*.dll")
            .Select(Assembly.LoadFrom).ToArray());

    public static IEnumerable<object[]> Providers() => Discover().All
        .Select(provider => new object[] { provider.Kind.Family + "." + provider.Kind.Provider });

    [Theory]
    [MemberData(nameof(Providers))]
    public void ActualResolverSlots_MatchReviewedModesAndCollectedReferences(string kind)
    {
        var registry = Discover();
        Assert.True(registry.TryGet(kind, out var provider));
        foreach (var avro in kind is "mq-publish.kafka" or "mq-expect.kafka" ? new[] { false, true } : new[] { false })
        {
            var fixture = CreateFixture(kind, avro);
            var (model, fragment) = BindAndEmit(provider!.Instance, fixture.Node);
            if (kind == "mq-publish.kafka" && avro)
            {
                var inactive = Assert.Single(fixture.Markers, pair => pair.Value.Path == "payload");
                Assert.Equal(Mode.Literal, inactive.Value.Mode);
                Assert.Equal("${secret:env/" + inactive.Key + "}", ((Vouchfx.Steps.MqPublish.Kafka.MqPublishKafkaModel)model).Payload);
                Assert.Contains(inactive.Key, fragment.StatementBlock, StringComparison.Ordinal);
            }
            var actual = ResolverMarkers(fragment);
            var expectedSecret = fixture.Markers.Where(pair => pair.Value.Mode == Mode.Secret)
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            var expectedPlaceholder = fixture.Markers.Where(pair => pair.Value.Mode == Mode.Placeholder)
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            Assert.True(expectedSecret.SetEquals(actual.Secret),
                $"{kind} secret emission drift: expected [{string.Join(',', expectedSecret)}], actual [{string.Join(',', actual.Secret)}]");
            Assert.True(expectedPlaceholder.SetEquals(actual.Placeholder),
                $"{kind} placeholder emission drift: expected [{string.Join(',', expectedPlaceholder)}], actual [{string.Join(',', actual.Placeholder)}]");
            var collection = StepTemplateCensus.Collect(fixture.Node);
            Assert.Null(collection.Failure);
            var collected = Markers(string.Join('\n', collection.Fields.Select(field => field.Text)));
            // PostgreSQL's original query/parameter/row scan is a compatibility rule.
            var expectedCollected = kind == "db-assert.postgres" ? expectedPlaceholder : expectedSecret;
            Assert.True(expectedCollected.SetEquals(collected),
                $"{kind} collector drift: expected [{string.Join(',', expectedCollected)}], actual [{string.Join(',', collected)}]");
            if (kind == "http.rest")
                Assert.Equal(((Vouchfx.Steps.HttpRest.HttpRestModel)model).Body,
                    Assert.Single(collection.Fields, field => field.Location == "body").Text);
        }
    }

    [Fact]
    public void RegistryAndModes_CoverAllDiscoveredProviders()
    {
        var providers = Discover().All;
        Assert.Equal(25, providers.Count);
        Assert.Equal(18, providers.Count(provider => Slots(provider.Kind.Family + "." + provider.Kind.Provider).Any(slot => slot.Mode == Mode.Secret)));
        Assert.Equal(6, providers.Count(provider => Slots(provider.Kind.Family + "." + provider.Kind.Provider).Any(slot => slot.Mode == Mode.Placeholder)
            && !Slots(provider.Kind.Family + "." + provider.Kind.Provider).Any(slot => slot.Mode == Mode.Secret)));
        Assert.All(providers, provider => Assert.NotEmpty(Slots(provider.Kind.Family + "." + provider.Kind.Provider)));
    }

    [Theory]
    [InlineData("template.Trim()", "\"${secret:env/CENSUS_1}\"", "Unrecognised resolver input")]
    [InlineData("template", "Compute(\"${secret:env/CENSUS_1}\")", "Unrecognised emitted resolver argument")]
    [InlineData("template", "null", "Unmarked resolver input")]
    public void UnknownResolverShapesAndUnmarkedNewSlots_FailClosed(string input, string argument, string diagnostic)
    {
        var fragment = new CsxFragment(Array.Empty<string>(), new[]
        {
            "static class Fixture_Helpers { public static string Run(string template) { return Secret_Helpers.ResolveTemplate(secrets, vars, " + input + "); } }",
        }, "{ Fixture_Helpers.Run(" + argument + "); }");
        var exception = Record.Exception(() => ResolverMarkers(fragment));
        Assert.NotNull(exception);
        Assert.Contains(diagnostic, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownResolverReceiver_FailsClosed()
    {
        var fragment = new CsxFragment(Array.Empty<string>(), s_unknownResolverHelpers,
            "{ Fixture_Helpers.Run(\"${secret:env/CENSUS_1}\"); }");
        var exception = Assert.Throws<InvalidOperationException>(() => ResolverMarkers(fragment));
        Assert.Contains("Unrecognised secret resolver call", exception.Message, StringComparison.Ordinal);
    }

    internal static (object Model, CsxFragment Fragment) BindAndEmit(IStepProvider provider, StepNode node)
    {
        var binder = provider.GetType().GetInterfaces().Single(type => type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IStepBinder<>));
        var model = binder.GetMethod("Bind")!.Invoke(provider, new object[] { node.RawNode, new RunBindingContext() })!;
        var compiler = provider.GetType().GetInterfaces().Single(type => type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IStepCompiler<>));
        var fragment = (CsxFragment)compiler.GetMethod("Emit")!.Invoke(provider,
            new[] { model, new RunCompileContext(node.Id, "Census", Path.GetTempPath()) })!;
        return (model, fragment);
    }

    internal static Fixture CreateFixture(string kind, bool avro = false)
    {
        // Inputs use distinct legal reference labels, including map names/literal slots.
        // Bind occurs once; inspection never calls a resolver or reads a secret value.
        var raw = new YamlMappingNode
        {
            { "id", "census" }, { "type", kind }, { "target", "fixture" },
            { "listener", "fixture" }, { "receiver", "fixture" },
            { "method", "POST" }, { "operation", "get" },
        };
        var markers = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var index = 0;
        foreach (var declared in Slots(kind))
        {
            var slot = kind == "mq-publish.kafka" && declared.Path == "payload" && avro
                ? declared with { Mode = Mode.Literal } : declared;
            if (slot.Path.StartsWith("avro.", StringComparison.Ordinal) && !avro) continue;
            var marker = "CENSUS_" + (++index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            markers.Add(marker, slot);
            Set(raw, slot.Path, new YamlScalarNode("${secret:env/" + marker + "}"));
        }
        if (kind == "script.csharp")
            raw.Children[new YamlScalarNode("code")] = new YamlScalarNode("var literal = \"${secret:env/CENSUS_1}\";");
        var pieces = kind.Split('.');
        return new Fixture(new StepNode("census", new StepKindId(pieces[0], pieces[1]), kind, raw,
            new Dictionary<string, CaptureExpr>(), VerifyMode.Immediate, null, false), markers);
    }

    internal static IReadOnlyList<Slot> Slots(string kind)
    {
        static IEnumerable<Slot> Parse(string paths, Mode mode) => paths.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => new Slot(path, mode));
        var (secret, placeholder, literal) = kind switch
        {
            "http.rest" => ("path headers.* body.bodyKey body.* body.# expect.json.* expect.bodyContains", "", "headers.# expect.json.#"),
            "http.soap" => ("path action envelope expect.xpath[].value", "", "expect.xpath[].path"),
            "mq-publish.kafka" => ("topic key headers.* payload avro.record.*", "", "headers.# avro.record.# avro.schemaRegistry avro.subject avro.schema"),
            "mq-expect.kafka" => ("match.key match.payloadContains match.headers.* match.json.*", "", "topic match.headers.# match.json.# avro.schemaRegistry avro.subject avro.schema"),
            "mq-publish.rabbitmq" => ("exchange routingKey payload headers.*", "", "headers.#"),
            "mq-expect.rabbitmq" => ("match.payloadContains match.headers.* match.json.*", "", "queue match.headers.# match.json.#"),
            "mq-publish.nats" => ("subject payload", "", ""),
            "mq-expect.nats" => ("subject match.payloadContains match.json.*", "", "stream match.json.#"),
            "mq-publish.redis" => ("stream payload", "", ""),
            "mq-expect.redis" => ("stream match.payloadContains match.json.*", "", "match.json.#"),
            "mq-publish.azureservicebus" => ("payload properties.*", "queue topic", "properties.#"),
            "mq-expect.azureservicebus" => ("expectPayloadContains expectProperties.*", "queue topic subscription", "expectProperties.#"),
            "webhook-listen.http" => ("match.method match.path match.bodyContains match.headers.*", "", "match.headers.#"),
            "mail-expect.smtp" => ("expect.match.to expect.match.subject-contains expect.match.body-contains", "", ""),
            "metrics-assert.prometheus" => ("path metric labels.* expect.value expect.min expect.max", "", "labels.#"),
            "storage-assert.s3" => ("bucket key expect.size expect.minSize expect.contentType expect.sha256 expect.contentContains expect.metadata.*", "", "expect.metadata.#"),
            "trace-expect.otlp" => ("match.traceId match.service match.spanName match.attributes.*", "", "match.attributes.#"),
            "cache-assert.elasticsearch" => ("expect.fields[].value", "query", "index expect.fields[].field"),
            "db-assert.postgres" or "db-assert.mysql" or "db-assert.sqlserver" => ("", "query parameters.* expect.row.*", "parameters.# expect.row.#"),
            "db-assert.mongodb" => ("", "filter expect.document.*", "collection expect.document.#"),
            "db-assert.dynamodb" => ("", "table key expect.item.*", "expect.item.#"),
            "cache-assert.redis" => ("", "key field expect.value", ""),
            "script.csharp" => ("", "", "code"),
            _ => throw new InvalidOperationException($"Unmapped discovered provider: {kind}. Review actual Bind/Emit and declare its modes."),
        };
        return Parse(secret, Mode.Secret).Concat(Parse(placeholder, Mode.Placeholder)).Concat(Parse(literal, Mode.Literal)).ToArray();
    }

    private static void Set(YamlMappingNode root, string path, YamlNode value)
    {
        var parts = path.Split('.');
        var map = root;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var name = parts[index];
            if (name.EndsWith("[]", StringComparison.Ordinal))
            {
                name = name[..^2];
                if (!map.Children.TryGetValue(new YamlScalarNode(name), out var sequence))
                    map.Add(name, sequence = new YamlSequenceNode(new YamlMappingNode()));
                map = (YamlMappingNode)((YamlSequenceNode)sequence).Children[0];
            }
            else
            {
                if (!map.Children.TryGetValue(new YamlScalarNode(name), out var child))
                    map.Add(name, child = new YamlMappingNode());
                map = (YamlMappingNode)child;
            }
        }
        if (parts[^1] == "*") map.Children[new YamlScalarNode("fixtureName")] = value;
        else if (parts[^1] == "#")
        {
            var oldKey = new YamlScalarNode("fixtureName");
            var oldValue = map.Children[oldKey];
            map.Children.Remove(oldKey);
            map.Add(value, oldValue);
        }
        else map.Children[new YamlScalarNode(parts[^1])] = value;
    }

    private sealed record Method(string Class, MethodDeclarationSyntax Syntax, HashSet<int> Secret, HashSet<int> Placeholder);
    internal sealed record ResolvedMarkers(HashSet<string> Secret, HashSet<string> Placeholder);

    internal static ResolvedMarkers ResolverMarkers(CsxFragment fragment)
    {
        var methods = fragment.RequiredHelpers.SelectMany(source => CSharpSyntaxTree.ParseText(source).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => method.Parent is ClassDeclarationSyntax declaration
                && declaration.Identifier.Text is not "Secret_Helpers" and not "Substitute_Helpers")
            .Select(method => new Method(((ClassDeclarationSyntax)method.Parent!).Identifier.Text, method, new(), new())).ToArray();
        Assert.True(methods.Length < 200, "Helper census exceeded bounded method budget.");
        foreach (var method in methods)
            foreach (var call in method.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression.ToString();
                RequireKnownResolverSpelling(call);
                if (name == "Secret_Helpers.ResolveTemplate")
                {
                    Assert.Equal(3, call.ArgumentList.Arguments.Count);
                    method.Secret.UnionWith(Formals(method.Syntax, call.ArgumentList.Arguments[2].Expression));
                }
                else if (name is "Substitute_Helpers.Resolve" or "Substitute_Helpers.ResolveIdentifier")
                {
                    Assert.Equal(2, call.ArgumentList.Arguments.Count);
                    method.Placeholder.UnionWith(Formals(method.Syntax, call.ArgumentList.Arguments[1].Expression));
                }
                else if (name == "_placeholderRegex.Replace"
                    && method.Syntax.Identifier.Text is "ResolveQuery" or "ResolveFilter" or "ResolveKeyTemplate")
                {
                    // Three existing JSON-escaping placeholder helpers have their own
                    // regex Replace instead of calling Substitute_Helpers. Follow only
                    // their explicit input formal, not the lambda's runtime values.
                    Assert.Equal(2, call.ArgumentList.Arguments.Count);
                    method.Placeholder.UnionWith(Formals(method.Syntax, call.ArgumentList.Arguments[0].Expression));
                }
            }
        // Only formal forwarding to a known helper is followed (Kafka Avro paths).
        for (var pass = 0; pass < methods.Length; pass++)
        {
            var changed = false;
            foreach (var method in methods)
                foreach (var call in method.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var callee = Find(methods, call, method.Class);
                    if (callee is null) continue;
                    foreach (var (slots, destination) in new[] { (callee.Secret, method.Secret), (callee.Placeholder, method.Placeholder) })
                        foreach (var slot in slots.ToArray())
                        {
                            Assert.True(slot < call.ArgumentList.Arguments.Count, "Unsupported helper forwarding arity.");
                            foreach (var formal in Formals(method.Syntax, call.ArgumentList.Arguments[slot].Expression))
                                changed |= destination.Add(formal);
                        }
                }
            if (!changed) break;
            Assert.True(pass < methods.Length - 1, "Helper forwarding did not converge.");
        }
        var result = new ResolvedMarkers(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var block = SyntaxFactory.ParseStatement(fragment.StatementBlock);
        Assert.False(block.ContainsDiagnostics, "Emitted statement block must parse.");
        foreach (var call in block.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            RequireKnownResolverSpelling(call);
            if (call.Expression.ToString() is "Secret_Helpers.ResolveTemplate" or "Substitute_Helpers.Resolve" or "Substitute_Helpers.ResolveIdentifier")
            {
                var secret = call.Expression.ToString() == "Secret_Helpers.ResolveTemplate";
                var markers = LiteralMarkers(call.ArgumentList.Arguments[secret ? 2 : 1].Expression);
                Assert.NotEmpty(markers);
                (secret ? result.Secret : result.Placeholder).UnionWith(markers);
            }
            var method = Find(methods, call, null);
            if (method is null) continue;
            var selected = InputsForInvocation(methods, method, call);
            foreach (var (slots, destination) in new[] { (selected.Secret, result.Secret), (selected.Placeholder, result.Placeholder) })
                foreach (var slot in slots)
                {
                    var argument = call.ArgumentList.Arguments[slot].Expression;
                    var markers = LiteralMarkers(argument);
                    Assert.True(markers.Count > 0,
                        $"Unmarked resolver input: {method.Class}.{method.Syntax.Identifier} parameter {method.Syntax.ParameterList.Parameters[slot].Identifier}. Add a distinct bound fixture for this actual input.");
                    destination.UnionWith(markers);
                }
        }
        return result;
    }

    private static void RequireKnownResolverSpelling(InvocationExpressionSyntax call)
    {
        // Inspect the invoked member itself. An outer result transformation such as
        // ResolveTemplate(...).ToLowerInvariant() is not another secret-helper call;
        // its nested resolver is inspected separately by DescendantNodes.
        var unknown = call.Expression switch
        {
            MemberAccessExpressionSyntax member when member.Name.Identifier.ValueText == "ResolveTemplate"
                || member.Expression.ToString() == "Secret_Helpers"
                => member.Expression.ToString() != "Secret_Helpers" || member.Name.Identifier.ValueText != "ResolveTemplate",
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "ResolveTemplate",
            _ => false,
        };
        if (unknown)
            throw new InvalidOperationException($"Unrecognised secret resolver call: {call.Expression}. Update the bounded census explicitly.");
    }

    private static (HashSet<int> Secret, HashSet<int> Placeholder) InputsForInvocation(
        Method[] methods, Method method, InvocationExpressionSyntax invocation)
    {
        if (method.Class != "MqPublishKafka_Helpers" || method.Syntax.Identifier.Text != "PublishAsync")
            return (method.Secret, method.Placeholder);
        // One supported native dispatch shape, checked against actual helper syntax.
        // No generic condition analysis: changes require an explicit gate update.
        var guard = Assert.IsType<IfStatementSyntax>(method.Syntax.Body!.Statements[0]);
        Assert.Equal("avroSubject is not null", guard.Condition.ToString());
        Assert.Null(guard.Else);
        var avroBody = Assert.IsType<BlockSyntax>(guard.Statement);
        Assert.Equal(2, avroBody.Statements.Count);
        Assert.IsType<ReturnStatementSyntax>(avroBody.Statements[1]);
        var forwarded = Assert.Single(avroBody.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.Expression.ToString() == "PublishAvroAsync");
        Assert.NotNull(Find(methods, forwarded, method.Class));
        Assert.Equal("avroSubject", method.Syntax.ParameterList.Parameters[12].Identifier.ValueText);
        var subject = invocation.ArgumentList.Arguments[12].Expression;
        Assert.True(subject.IsKind(SyntaxKind.NullLiteralExpression) || subject.IsKind(SyntaxKind.StringLiteralExpression),
            "Unsupported emitted Avro mode discriminator.");
        IEnumerable<StatementSyntax> selected = subject.IsKind(SyntaxKind.NullLiteralExpression)
            ? method.Syntax.Body.Statements.Skip(1) : avroBody.Statements;
        var secret = new HashSet<int>();
        var placeholder = new HashSet<int>();
        foreach (var call in selected.SelectMany(statement => statement.DescendantNodes().OfType<InvocationExpressionSyntax>()))
        {
            var name = call.Expression.ToString();
            if (name == "Secret_Helpers.ResolveTemplate")
                secret.UnionWith(Formals(method.Syntax, call.ArgumentList.Arguments[2].Expression));
            else if (name is "Substitute_Helpers.Resolve" or "Substitute_Helpers.ResolveIdentifier")
                placeholder.UnionWith(Formals(method.Syntax, call.ArgumentList.Arguments[1].Expression));
            var callee = Find(methods, call, method.Class);
            if (callee is null) continue;
            foreach (var (slots, destination) in new[] { (callee.Secret, secret), (callee.Placeholder, placeholder) })
                foreach (var slot in slots)
                    destination.UnionWith(Formals(method.Syntax, call.ArgumentList.Arguments[slot].Expression));
        }
        return (secret, placeholder);
    }

    private static Method? Find(IEnumerable<Method> methods, InvocationExpressionSyntax call, string? ownClass)
    {
        var name = call.Expression.ToString();
        return methods.SingleOrDefault(method => name == method.Class + "." + method.Syntax.Identifier.Text
            || (method.Class == ownClass && name == method.Syntax.Identifier.Text));
    }

    private static IEnumerable<int> Formals(MethodDeclarationSyntax method, ExpressionSyntax expression)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesized) return Formals(method, parenthesized.Expression);
        if (expression is ElementAccessExpressionSyntax element) return Formals(method, element.Expression);
        if (expression is ConditionalExpressionSyntax conditional)
            return Formals(method, conditional.WhenTrue).Concat(Formals(method, conditional.WhenFalse));
        if (expression is LiteralExpressionSyntax literal && (literal.IsKind(SyntaxKind.NullLiteralExpression)
            || literal.IsKind(SyntaxKind.StringLiteralExpression))) return Array.Empty<int>();
        if (expression.ToString() == "string.Empty") return Array.Empty<int>();
        if (expression is IdentifierNameSyntax identifier)
        {
            var parameters = method.ParameterList.Parameters;
            for (var index = 0; index < parameters.Count; index++)
                if (parameters[index].Identifier.Text == identifier.Identifier.Text) return new[] { index };
            if (identifier.Identifier.Text == "expectedRaw")
            {
                var declaration = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Single(variable => variable.Identifier.Text == "expectedRaw");
                Assert.IsType<ConditionalExpressionSyntax>(declaration.Initializer!.Value);
                return Formals(method, declaration.Initializer.Value);
            }
        }
        throw new InvalidOperationException($"Unrecognised resolver input in {method.Identifier}: {expression.Kind()} ({expression}). Update the bounded census explicitly.");
    }

    private static HashSet<string> LiteralMarkers(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal
            && (literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.IsKind(SyntaxKind.NullLiteralExpression)))
            return Markers(literal.Token.ValueText);
        var initializer = expression switch
        {
            ArrayCreationExpressionSyntax array => array.Initializer,
            ImplicitArrayCreationExpressionSyntax array => array.Initializer,
            _ => null,
        };
        if (initializer is not null)
            return initializer.Expressions.SelectMany(LiteralMarkers).ToHashSet(StringComparer.Ordinal);
        if (expression is InvocationExpressionSyntax invocation
            && invocation.Expression.ToString() is "Substitute_Helpers.Resolve" or "Substitute_Helpers.ResolveIdentifier"
            && invocation.ArgumentList.Arguments.Count == 2
            && invocation.ArgumentList.Arguments[0].Expression.ToString() == "Vars")
            return LiteralMarkers(invocation.ArgumentList.Arguments[1].Expression);
        throw new InvalidOperationException($"Unrecognised emitted resolver argument: {expression.Kind()} ({expression}).");
    }

    private static HashSet<string> Markers(string text) => Regex.Matches(text, @"CENSUS_\d+")
        .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
}
