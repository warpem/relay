using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RelaySourceGenerators;

[Generator]
public class ReadOnlyWrapperGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
        
    }

    public void Execute(GeneratorExecutionContext context)
    {
        // Find base ReadOnlyJob members to exclude
        var readOnlyJobMembers = context.Compilation
            .GetTypeByMetadataName("Refund.DataModel.Job")
            ?.GetMembers()
            .Select(m => m.Name)
            .ToHashSet() ?? new HashSet<string>();

        foreach (var jobClass in GetJobClassesToWrap(context))
        {
            // Get the semantic model for this syntax tree
            var semanticModel = context.Compilation.GetSemanticModel(jobClass.SyntaxTree);
            
            // Get the type symbol for the class
            var classSymbol = semanticModel.GetDeclaredSymbol(jobClass);
            
            // Get all interface methods that this class implements
            var interfaceMethods = classSymbol?.AllInterfaces
                                              .SelectMany(i => i.GetMembers())
                                              .OfType<IMethodSymbol>()
                                              .Select(m => m.Name)
                                              .ToHashSet() ?? new HashSet<string>();
            
            var properties = jobClass.Members
                                     .OfType<PropertyDeclarationSyntax>()
                                     .Where(p => !readOnlyJobMembers.Contains(p.Identifier.Text) && 
                                                 p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

            var methods = jobClass.Members
                                  .OfType<MethodDeclarationSyntax>()
                                  .Where(m => !readOnlyJobMembers.Contains(m.Identifier.Text) && 
                                              m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) &&
                                              !interfaceMethods.Contains(m.Identifier.Text));

            var namespaceName = GetNamespace(jobClass);
            var className = jobClass.Identifier.Text;
            
            var usings = CollectUsingDirectives(jobClass, context.Compilation);

            // Get the base class and its namespace
            var (baseClassName, baseNamespace) = GetBaseClass(jobClass, semanticModel);
            
            // Determine the appropriate base class for the ReadOnly wrapper
            string baseReadOnlyClass = "ReadOnlyJob";
            if (baseClassName != null && baseClassName != "Job")
            {
                // If we have a base class other than Job, use its ReadOnly wrapper
                baseReadOnlyClass = $"ReadOnly{baseClassName}";
                
                // Add the base namespace to usings if needed
                if (baseNamespace != null && baseNamespace != namespaceName && 
                    !baseNamespace.StartsWith("System") && !baseNamespace.StartsWith("Microsoft"))
                {
                    usings.Add($"using {baseNamespace};");
                }
            }

            var source = $@"
{string.Join('\n', usings)}

namespace {namespaceName};

[ReadOnlyFor(typeof({className}))]
public class ReadOnly{className} : {baseReadOnlyClass}
{{
    {className} _typedJob;

    public ReadOnly{className}({className} job) : base(job)
    {{
        _typedJob = job;
    }}

    public static ReadOnlyJob Wrap(Job job) => new ReadOnly{className}(({className})job);

    {GeneratePropertyAccessors(properties)}
    {GenerateMethodAccessors(methods)}
}}";

            context.AddSource($"ReadOnly{className}.g.cs", source);
        }
    }
    
    private IEnumerable<ClassDeclarationSyntax> GetJobClassesToWrap(GeneratorExecutionContext context)
    {
        return context.Compilation.SyntaxTrees
                      .SelectMany(st => st.GetRoot().DescendantNodes())
                      .OfType<ClassDeclarationSyntax>()
                      .Where(c => c.AttributeLists
                                   .SelectMany(al => al.Attributes)
                                   .Any(a => a.Name.ToString() == "GenerateReadOnly"));
    }
    
    private HashSet<string> CollectUsingDirectives(ClassDeclarationSyntax classDecl, Compilation compilation)
    {
        var usings = new HashSet<string>();

        // Get usings from the source file
        var sourceUsings = classDecl.SyntaxTree.GetRoot()
                                    .DescendantNodes()
                                    .OfType<UsingDirectiveSyntax>();

        foreach (var usingDir in sourceUsings)
        {
            usings.Add(usingDir.ToString());
        }

        // Get usings from parent files (for partial classes)
        var parentFiles = compilation.SyntaxTrees
                                     .Where(st => st.GetRoot()
                                                    .DescendantNodes()
                                                    .OfType<ClassDeclarationSyntax>()
                                                    .Any(c => c.Identifier.Text == classDecl.Identifier.Text));

        foreach (var tree in parentFiles)
        {
            var parentUsings = tree.GetRoot()
                                   .DescendantNodes()
                                   .OfType<UsingDirectiveSyntax>();

            foreach (var usingDir in parentUsings)
            {
                usings.Add(usingDir.ToString());
            }
        }

        // Ensure we have System for basic types
        usings.Add("using System;");
    
        // Ensure we have the namespace containing ReadOnlyJob
        var readOnlyJobSymbol = compilation.GetTypeByMetadataName("Refund.DataModel.ReadOnly.ReadOnlyJob");
        if (readOnlyJobSymbol != null)
        {
            usings.Add($"using {readOnlyJobSymbol.ContainingNamespace};");
        }

        return usings;
    }

    private string GeneratePropertyAccessors(IEnumerable<PropertyDeclarationSyntax> properties)
    {
        return string.Join("\n", properties.Select(p => $@"
    public {p.Type} {p.Identifier} => _typedJob.{p.Identifier};"));
    }

    private string GenerateMethodAccessors(IEnumerable<MethodDeclarationSyntax> methods)
    {
        return string.Join("\n", methods.Select(m => $@"
    public {m.ReturnType} {m.Identifier}({GenerateParameters(m.ParameterList)})
    {{
        return _typedJob.{m.Identifier}({GenerateParameterNames(m.ParameterList)});
    }}"));
    }

    private string GenerateParameters(ParameterListSyntax parameterList)
    {
        return string.Join(", ", parameterList.Parameters.Select(p => 
            $"{p.Type} {p.Identifier}"));
    }

    private string GenerateParameterNames(ParameterListSyntax parameterList)
    {
        return string.Join(", ", parameterList.Parameters.Select(p => 
            p.Identifier.Text));
    }

    private string GetNamespace(ClassDeclarationSyntax classDeclaration)
    {
        var namespaceName = classDeclaration.Parent switch
        {
            NamespaceDeclarationSyntax ns => ns.Name.ToString(),
            FileScopedNamespaceDeclarationSyntax ns => ns.Name.ToString(),
            _ => string.Empty
        };
        return namespaceName;
    }
    
    private (string ClassName, string Namespace) GetBaseClass(ClassDeclarationSyntax classDeclaration, SemanticModel semanticModel)
    {
        if (classDeclaration.BaseList == null)
            return (null, null);
            
        // Get the first base type that's a class (not an interface)
        foreach (var baseType in classDeclaration.BaseList.Types)
        {
            var typeInfo = semanticModel.GetTypeInfo(baseType.Type);
            if (typeInfo.Type != null && typeInfo.Type.TypeKind == TypeKind.Class)
            {
                return (typeInfo.Type.Name, typeInfo.Type.ContainingNamespace?.ToDisplayString());
            }
        }
        
        return (null, null);
    }
}