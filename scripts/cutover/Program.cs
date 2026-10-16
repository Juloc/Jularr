using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 3)
{
    throw new ArgumentException("Usage: SourceAudit <repository> <existing dependency DLL directory> <output JSON>");
}

var repository = Path.GetFullPath(args[0]);
var dependencyDirectory = Path.GetFullPath(args[1]);
var sourcePaths = Directory.GetFiles(Path.Combine(repository, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(path => !Regex.IsMatch(path, @"[/\\](bin|obj|Migrations)[/\\]"))
    .Order(StringComparer.Ordinal).ToArray();
var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithPreprocessorSymbols("DEBUG", "NET10_0");
var trees = sourcePaths.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path)).ToList();
var globalNamespaces = new[]
{
    "System", "System.Linq", "System.Collections.Generic", "System.IO", "System.Threading", "System.Threading.Tasks", "System.Net.Http", "System.Net.Http.Json",
    "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Http", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting",
    "Microsoft.Extensions.Logging", "Microsoft.Extensions.Configuration"
};
trees.Add(CSharpSyntaxTree.ParseText(string.Join("\n", globalNamespaces.Select(name => $"global using {name};")), parseOptions));
var runtimeReferences = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
var dependencyReferences = Directory.GetFiles(dependencyDirectory, "*.dll").Where(path => !Path.GetFileName(path).StartsWith("Jularr", StringComparison.Ordinal));
var aspNetRoot = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "Microsoft.AspNetCore.App");
var aspNetDirectory = Directory.GetDirectories(aspNetRoot).OrderBy(path => Version.Parse(Path.GetFileName(path))).Last();
var referencePaths = runtimeReferences.Concat(dependencyReferences).Concat(Directory.GetFiles(aspNetDirectory, "*.dll")).Distinct(StringComparer.OrdinalIgnoreCase);
var references = new List<MetadataReference>();
var nativeReferences = new List<string>();
foreach (var path in referencePaths)
{
    try
    {
        System.Reflection.AssemblyName.GetAssemblyName(path);
        references.Add(MetadataReference.CreateFromFile(path));
    }
    catch (BadImageFormatException)
    {
        // A runtime output directory can contain native DLLs; they are not Roslyn metadata references.
        nativeReferences.Add(Path.GetFileName(path));
    }
}
var compilation = CSharpCompilation.Create("PhaseASourceAudit", trees, references, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
var tableByEntity = new Dictionary<string, string>(StringComparer.Ordinal);
var snapshot = File.ReadAllText(Path.Combine(repository, "src/Jularr.Web/Data/Migrations/AppDbContextModelSnapshot.cs"));
foreach (Match match in Regex.Matches(snapshot, "modelBuilder.Entity\\(\"(?<entity>[^\"]+)\", b =>(?<body>.*?)(?=modelBuilder.Entity|\\z)", RegexOptions.Singleline))
{
    var table = Regex.Match(match.Groups["body"].Value, "ToTable\\(\"(?<table>[^\"]+)\"");
    if (table.Success)
    {
        tableByEntity[match.Groups["entity"].Value] = table.Groups["table"].Value;
    }
}

var fieldVariables = trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()).SelectMany(field => field.Declaration.Variables);
var literalFields = fieldVariables.Where(variable => variable.Initializer?.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression));
var sqlFields = literalFields.GroupBy(variable => variable.Identifier.ValueText)
    .ToDictionary(group => group.Key, group => string.Join("\n", group.Select(variable => ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText)));
var methods = new List<object>();
var sqlSites = new List<object>();
var edges = new HashSet<(string Caller, string Callee)>();
var accesses = new List<object>();
var files = new List<object>();
var unresolved = 0;
var invocationCount = 0;
foreach (var tree in trees.Where(tree => tree.FilePath.Length > 0))
{
    var model = compilation.GetSemanticModel(tree);
    var root = tree.GetRoot();
    var relativePath = Path.GetRelativePath(repository, tree.FilePath).Replace('\\', '/');
    var nodes = root.DescendantNodes().Where(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax).ToArray();
    string Owner(SyntaxNode node)
    {
        var declaration = node.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax);
        var symbol = declaration is null ? null : model.GetDeclaredSymbol(declaration);
        return symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? relativePath + "::<top-level>";
    }
    foreach (var node in nodes)
    {
        var symbol = model.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            continue;
        }
        var text = node.ToString();
        var sqlText = text + "\n" + string.Join("\n", node.DescendantNodes().OfType<IdentifierNameSyntax>().Select(identifier => sqlFields.GetValueOrDefault(identifier.Identifier.ValueText, "")));
        var scopeMatches = Regex.Matches(text, @"\b(ProfileId|profileId|AccountId|accountId|actor|Actor|RequireAuthorization|Authorize|RequireCapability|HasCapability|OwnerOnly|ModuleRegistry|ModuleId|moduleId|IsEnabled)\b");
        var writeMatches = Regex.Matches(text, @"\b(SaveChangesAsync|SaveChanges|ExecuteUpdateAsync|ExecuteDeleteAsync|ExecuteSqlRawAsync|ExecuteSqlInterpolatedAsync|ExecuteNonQueryAsync|Add|AddRange|Remove|RemoveRange|Update)\b");
        var tableMatches = Regex.Matches(sqlText, "(?i)\\b(?:FROM|JOIN|UPDATE|INTO|TABLE)\\s+\\\\?\"(?<table>[A-Za-z][A-Za-z0-9_]*)");
        var routeLiterals = node.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.StartsWith('/'));
        methods.Add(new
        {
            path = relativePath,
            line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            symbol = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            containingType = symbol.ContainingType?.ToDisplayString(),
            scopeMarkers = scopeMatches.Select(match => match.Value).Distinct().Order().ToArray(),
            writeMarkers = writeMatches.Select(match => match.Value).Distinct().Order().ToArray(),
            sqlTables = tableMatches.Select(match => match.Groups["table"].Value).Distinct().Order().ToArray(),
            routes = routeLiterals.Select(literal => literal.Token.ValueText).Distinct().Order().ToArray()
        });
    }
    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        invocationCount++;
        var callName = invocation.Expression is MemberAccessExpressionSyntax callMember ? callMember.Name.Identifier.ValueText : invocation.Expression.ToString();
        if (Regex.IsMatch(callName, @"^(SaveChanges|ExecuteSql|SqlQuery|FromSql|ExecuteReader|ExecuteNonQuery|ExecuteScalar|BeginTransaction|ExecuteUpdate|ExecuteDelete)|^Migrate(?:Async)?$"))
        {
            sqlSites.Add(new
            {
                path = relativePath,
                line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                symbol = Owner(invocation),
                operation = callName,
                resolved = model.GetSymbolInfo(invocation).Symbol is not null
            });
        }
        var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is null)
        {
            unresolved++;
            continue;
        }
        var target = (symbol.ReducedFrom ?? symbol).OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        if (symbol.Locations.Any(location => location.IsInSource))
        {
            edges.Add((Owner(invocation), target));
        }
    }
    foreach (var member in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
    {
        var symbol = model.GetSymbolInfo(member).Symbol;
        if (symbol is IPropertySymbol property && property.Type is INamedTypeSymbol type && type.Name == "DbSet" && type.TypeArguments.Length == 1)
        {
            var entity = type.TypeArguments[0].ToDisplayString();
            var statement = member.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            var memberSymbols = statement?.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Select(expression => model.GetSymbolInfo(expression).Symbol).OfType<IPropertySymbol>() ?? [];
            var columns = memberSymbols.Where(candidate => tableByEntity.ContainsKey(candidate.ContainingType.ToDisplayString()))
                .Select(candidate => tableByEntity[candidate.ContainingType.ToDisplayString()] + "." + candidate.Name).Distinct().Order().ToArray();
            accesses.Add(new
            {
                path = relativePath,
                line = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                symbol = Owner(member),
                property = property.Name,
                entity,
                table = tableByEntity.GetValueOrDefault(entity, "UNRESOLVED_ENTITY_TABLE"),
                evidence = "ROSLYN_DBSET_PROPERTY_REFERENCE",
                columns,
                chain = member.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault()?.ToString()
            });
        }
    }
    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method?.Name == "Set" && method.TypeArguments.Length == 1 && method.ContainingType.Name == "DbContext")
        {
            var entity = method.TypeArguments[0].ToDisplayString();
            accesses.Add(new
            {
                path = relativePath,
                line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                symbol = Owner(invocation),
                property = "Set<T>",
                entity,
                table = tableByEntity.GetValueOrDefault(entity, "UNRESOLVED_ENTITY_TABLE"),
                evidence = "ROSLYN_DBSET_GENERIC_REFERENCE",
                chain = "Set<T>"
            });
        }
    }
    files.Add(new { path = relativePath, declarations = nodes.Length, syntaxErrors = tree.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) });
    if (files.Count % 100 == 0)
    {
        Console.WriteLine($"Indexed {files.Count}/{sourcePaths.Length} source files");
    }
}
var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
var diagnostics = errors.GroupBy(diagnostic => diagnostic.Id).Select(group => new { id = group.Key, count = group.Count() }).ToArray();
var result = new
{
    source = "SourceAudit Roslyn compilation from existing SDK and dependency DLLs; not a successful product build or proof of runtime dispatch",
    sourceFiles = sourcePaths.Length,
    aspNetReferenceVersion = Path.GetFileName(aspNetDirectory),
    skippedNativeReferences = nativeReferences,
    diagnosticExamples = errors.GroupBy(diagnostic => diagnostic.Id).Select(group => group.First().ToString()).ToArray(),
    invocationCount,
    unresolvedInvocations = unresolved,
    diagnostics,
    files,
    methods,
    edges = edges.OrderBy(edge => edge.Caller).ThenBy(edge => edge.Callee).Select(edge => new { caller = edge.Caller, callee = edge.Callee }),
    accesses,
    sqlSites
};
File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { sourceFiles = sourcePaths.Length, methods = methods.Count, edges = edges.Count, accesses = accesses.Count, invocationCount, unresolvedInvocations = unresolved, diagnostics }));
