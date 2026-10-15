using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vouchfx.Engine.Orchestration.Tests;

// A source contract, not a store scan or whole-program execution analysis. Inspect syntax
// rather than text so comments and generated-code strings cannot create fictitious callers.
internal static class CertificateGuardSourceCensus
{
    internal sealed record Project(string Name, string ProjectXml, IReadOnlyList<string> Sources);

    internal static bool IsTestHost(Project project)
    {
        var xml = XDocument.Parse(project.ProjectXml);
        return xml.Descendants().Any(element =>
            (element.Name.LocalName == "IsTestProject" && element.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) ||
            (element.Name.LocalName == "PackageReference" && (string?)element.Attribute("Include") == "Microsoft.NET.Test.Sdk"));
    }

    internal static string[] MissingGuards(IEnumerable<Project> projects) => projects
        .Where(IsTestHost)
        .Where(project => CreatesOrImportsCertificate(project.Sources) && !HasFinalGuard(project))
        .Select(project => project.Name).Order(StringComparer.Ordinal).ToArray();

    internal static bool CreatesOrImportsCertificate(IReadOnlyList<string> sources)
    {
        var roots = sources.Select(source => SyntaxFactory.ParseCompilationUnit(source)).ToArray();
        var globalUsings = roots.SelectMany(root => root.Usings)
            .Where(usingDirective => usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).ToArray();
        foreach (var root in roots)
        {
            var fileUsings = root.Usings.Where(usingDirective => !usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)).ToArray();
            IEnumerable<UsingDirectiveSyntax> ApplicableUsings(SyntaxNode scope) => globalUsings.Concat(fileUsings)
                .Concat(scope.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().SelectMany(ns => ns.Usings));
            string Resolve(string name, SyntaxNode scope)
            {
                // Aliases bind an unqualified name or the head of a qualified name, never its leaf.
                NameSyntax head = SyntaxFactory.ParseName(name);
                while (head is QualifiedNameSyntax qualified)
                {
                    head = qualified.Left;
                }

                var identifier = head switch
                {
                    IdentifierNameSyntax unqualified => unqualified,
                    AliasQualifiedNameSyntax qualified when qualified.Alias.Identifier.ValueText != "global" => qualified.Alias,
                    _ => null,
                };
                var alias = identifier is null ? null : ApplicableUsings(scope).LastOrDefault(usingDirective =>
                    usingDirective.Alias?.Name.Identifier.ValueText == identifier.Identifier.ValueText);
                if (alias is null)
                {
                    return LastName(name);
                }

                var suffix = name[identifier!.Span.End..];
                if (suffix.StartsWith("::", StringComparison.Ordinal))
                {
                    suffix = "." + suffix[2..];
                }

                return LastName(alias.Name!.ToString() + suffix);
            }

            bool CertificateType(string name, SyntaxNode scope) => Resolve(name, scope) is "CertificateRequest" or "X509Certificate2";
            var collectionNames = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Parent is VariableDeclarationSyntax declaration &&
                    (Resolve(declaration.Type.ToString(), variable) == "X509Certificate2Collection" ||
                     variable.Initializer?.Value is ObjectCreationExpressionSyntax creation &&
                     Resolve(creation.Type.ToString(), variable) == "X509Certificate2Collection"))
                .Select(variable => variable.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

            foreach (var node in root.DescendantNodes())
            {
                if (node is ObjectCreationExpressionSyntax creation && CertificateType(creation.Type.ToString(), creation))
                {
                    return true;
                }

                if (node is ImplicitObjectCreationExpressionSyntax implicitCreation)
                {
                    var declaration = implicitCreation.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault();
                    var method = implicitCreation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    if (CertificateType(declaration?.Type.ToString() ?? method?.ReturnType.ToString() ?? implicitCreation.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Type.ToString() ?? "", implicitCreation))
                    {
                        return true;
                    }
                }

                if (node is not InvocationExpressionSyntax invocation)
                {
                    continue;
                }

                var member = invocation.Expression as MemberAccessExpressionSyntax;
                var name = LastName(member?.Name.ToString() ?? invocation.Expression.ToString());
                var owner = member is null ? "" : Resolve(member.Expression.ToString(), invocation);
                var staticAuthority = ApplicableUsings(invocation).Any(usingDirective =>
                    usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) &&
                    LastName(usingDirective.Name!.ToString()) is "TestCertificateAuthority" or "HostedHttpTlsCertificateBed");
                if (name == "CreateSelfSigned" ||
                    ((owner is "TestCertificateAuthority" or "HostedHttpTlsCertificateBed" || staticAuthority && member is null) &&
                     (name.StartsWith("Create", StringComparison.Ordinal) || name.StartsWith("Write", StringComparison.Ordinal))) ||
                    (owner == "X509Certificate2" && name.StartsWith("CreateFrom", StringComparison.Ordinal)) ||
                    (member is not null && collectionNames.Contains(LastName(member.Expression.ToString())) && name is "Import" or "ImportFromPem"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool HasFinalGuard(Project project)
    {
        var roots = project.Sources.Select(source => SyntaxFactory.ParseCompilationUnit(source)).ToArray();
        var classes = roots.SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()).ToArray();
        var assemblyAttributes = roots.SelectMany(root => root.AttributeLists)
            .Where(list => list.Target?.Identifier.ValueText == "assembly")
            .SelectMany(list => list.Attributes).ToArray();
        var registrations = assemblyAttributes.Where(attribute => AttributeName(attribute) == "TestCollectionOrderer").ToArray();
        if (registrations.Length != 1 || registrations[0].ArgumentList?.Arguments.Count != 2)
        {
            return false;
        }

        var registeredType = StringValue(registrations[0].ArgumentList!.Arguments[0].Expression, classes);
        var registeredAssembly = StringValue(registrations[0].ArgumentList!.Arguments[1].Expression, classes);
        var orderer = classes.SingleOrDefault(type => FullName(type) == registeredType);
        if (registeredAssembly != project.Name || orderer is null ||
            orderer.BaseList?.Types.Any(type => LastName(type.Type.ToString()) == "ITestCollectionOrderer") != true)
        {
            return false;
        }

        var serialAssembly = assemblyAttributes.Any(attribute => AttributeName(attribute) == "CollectionBehavior" &&
            NamedTrue(attribute, "DisableTestParallelization"));
        foreach (var guard in classes)
        {
            var collection = guard.AttributeLists.SelectMany(list => list.Attributes)
                .SingleOrDefault(attribute => AttributeName(attribute) == "Collection");
            if (collection?.ArgumentList?.Arguments.Count != 1)
            {
                continue;
            }

            var name = StringValue(collection.ArgumentList.Arguments[0].Expression, classes);
            var definitions = classes.SelectMany(type => type.AttributeLists.SelectMany(list => list.Attributes))
                .Where(attribute => AttributeName(attribute) == "CollectionDefinition" &&
                    attribute.ArgumentList?.Arguments.Count > 0 &&
                    StringValue(attribute.ArgumentList.Arguments[0].Expression, classes) == name).ToArray();
            if (name is null || definitions.Length != 1 || !serialAssembly && !NamedTrue(definitions[0], "DisableParallelization") ||
                !OrdersGuardLast(orderer, name, classes))
            {
                continue;
            }

            if (guard.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                    (AttributeName(attribute) == "Fact" ||
                     project.Name == "Vouchfx.Engine.Runtime.TransportAcceptance.Tests" && AttributeName(attribute) == "HostedTlsFact" &&
                     classes.Any(type => type.Identifier.ValueText == "HostedTlsFactAttribute" &&
                         type.BaseList?.Types.Any(baseType => baseType.Type.ToString() == "Xunit.FactAttribute") == true)) &&
                    attribute.ArgumentList?.Arguments.Any(argument =>
                        argument.NameEquals?.Name.Identifier.ValueText == "Skip") != true) &&
                method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                    invocation.Expression is MemberAccessExpressionSyntax member &&
                    LastName(member.Expression.ToString()) == "TestCertificateStoreGuard" &&
                    member.Name.Identifier.ValueText == "SweepProcessResidue") &&
                HasResidueAssertion(method)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool OrdersGuardLast(ClassDeclarationSyntax orderer, string name, IReadOnlyList<ClassDeclarationSyntax> classes)
    {
        var methods = orderer.Members.OfType<MethodDeclarationSyntax>().ToArray();
        var registeredMethod = methods.SingleOrDefault(method => method.Identifier.ValueText == "OrderTestCollections");
        var helper = methods.SingleOrDefault(method => method.Identifier.ValueText == "GuardLast");
        return registeredMethod is not null && ReturnsGuardLast(registeredMethod) &&
            helper?.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "OrderBy" &&
                invocation.ArgumentList.Arguments.SelectMany(argument => argument.DescendantNodes()).OfType<ConditionalExpressionSyntax>()
                    .Any(condition => condition.WhenTrue is LiteralExpressionSyntax yes && yes.Token.ValueText == "1" &&
                        condition.WhenFalse is LiteralExpressionSyntax no && no.Token.ValueText == "0" &&
                        condition.Condition.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression => StringValue(expression, classes) == name))) == true;
    }

    // Deliberately supports direct expression returns and one-return block bodies only.
    // Calling GuardLast somewhere is not evidence that its result is returned.
    private static bool ReturnsGuardLast(MethodDeclarationSyntax method)
    {
        var result = method.ExpressionBody?.Expression ??
            (method.Body?.Statements.Count == 1 && method.Body.Statements[0] is ReturnStatementSyntax returned ? returned.Expression : null);
        return result is InvocationExpressionSyntax invocation && invocation.Expression is SimpleNameSyntax name &&
            name.Identifier.ValueText == "GuardLast";
    }

    // A straight-line native guard either asserts a direct sweep, or declares its result
    // then immediately asserts it. Unsupported statements cannot overwrite or discard it.
    private static bool HasResidueAssertion(MethodDeclarationSyntax method)
    {
        bool IsSweep(ExpressionSyntax expression) => expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax member &&
            LastName(member.Expression.ToString()) == "TestCertificateStoreGuard" && member.Name.Identifier.ValueText == "SweepProcessResidue";
        string? resultName = null;
        ExpressionSyntax? assertion = method.ExpressionBody?.Expression;
        if (method.Body is { Statements.Count: 1 } direct && direct.Statements[0] is ExpressionStatementSyntax directStatement)
        {
            assertion = directStatement.Expression;
        }
        else if (method.Body is { Statements.Count: 2 } body &&
            body.Statements[0] is LocalDeclarationStatementSyntax declaration && declaration.Declaration.Variables.Count == 1 &&
            declaration.Declaration.Variables[0] is var variable && variable.Initializer is not null && IsSweep(variable.Initializer.Value) &&
            body.Statements[1] is ExpressionStatementSyntax asserted)
        {
            resultName = variable.Identifier.ValueText;
            assertion = asserted.Expression;
        }

        bool IsResidue(ExpressionSyntax expression) => IsSweep(expression) ||
            expression is IdentifierNameSyntax identifier && resultName is not null && identifier.Identifier.ValueText == resultName;
        if (assertion is not InvocationExpressionSyntax check || check.Expression is not MemberAccessExpressionSyntax member ||
            LastName(member.Expression.ToString()) != "Assert" || check.ArgumentList.Arguments.Count == 0)
        {
            return false;
        }

        var argument = check.ArgumentList.Arguments[0].Expression;
        return member.Name.Identifier.ValueText == "Null" && IsResidue(argument) ||
            member.Name.Identifier.ValueText == "True" && argument is IsPatternExpressionSyntax pattern &&
            IsResidue(pattern.Expression) && pattern.Pattern is ConstantPatternSyntax constant &&
            constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
    }

    private static bool NamedTrue(AttributeSyntax attribute, string name) => attribute.ArgumentList?.Arguments.Any(argument =>
        argument.NameEquals?.Name.Identifier.ValueText == name && argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression)) == true;

    private static string AttributeName(AttributeSyntax attribute) => LastName(attribute.Name.ToString()).Replace("Attribute", "", StringComparison.Ordinal);

    private static string LastName(string name) => name.Split('.').Last().Replace("global::", "", StringComparison.Ordinal);

    private static string FullName(ClassDeclarationSyntax type) => string.Join(".", type.Ancestors()
        .OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()).Append(type.Identifier.ValueText));

    private static string? StringValue(ExpressionSyntax expression, IReadOnlyList<ClassDeclarationSyntax> classes)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        if (expression is MemberAccessExpressionSyntax member)
        {
            var owner = classes.SingleOrDefault(type => type.Identifier.ValueText == LastName(member.Expression.ToString()));
            return owner?.Members.OfType<FieldDeclarationSyntax>().SelectMany(field => field.Declaration.Variables)
                .Where(variable => variable.Identifier.ValueText == member.Name.Identifier.ValueText)
                .Select(variable => variable.Initializer?.Value).OfType<LiteralExpressionSyntax>()
                .SingleOrDefault(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText;
        }

        return null;
    }
}
