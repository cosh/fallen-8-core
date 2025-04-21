// MIT License
//
// CodeGenerationHelper.cs
//
// Copyright (c) 2025 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms.Path;

namespace NoSQL.GraphDB.Core.App.Helper
{

    public static class CodeGenerationHelper
    {
        #region Code generation stuff

        private const String PathDelegateNamespace = "NoSQL.GraphDB.Core.Algorithms.Path";
        private const String PathDelegateClassName = "PathDelegateProvider";

        private const String VertexFilterMethodName = "VertexFilter";
        private const String EdgeFilterMethodName = "EdgeFilter";
        private const String EdgePropertyFilterMethodName = "EdgePropertyFilter";
        private const String VertexCostMethodName = "VertexCost";
        private const String EdgeCostMethodName = "EdgeCost";

        #endregion

        public static String GeneratePathTraverser(out IPathTraverser traverser, PathSpecification definition)
        {
            var sourceCode = CreateSource(definition);

            var tree = SyntaxFactory.ParseSyntaxTree(sourceCode, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            string fileName = System.IO.Path.GetRandomFileName();

            // A single, immutable invocation to the compiler
            // to produce a library
            var compilation = CSharpCompilation.Create(fileName)
              .WithOptions(
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
              .AddReferences(GetGlobalReferences())
              .AddSyntaxTrees(tree);

            IPathTraverser pathTraverserInstance = null;
            String resultMessage = null;

            using (var ms = new MemoryStream())
            {
                EmitResult compilationResult = compilation.Emit(ms);
                if (compilationResult.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);

                    // Load the assembly
                    Assembly assembly = Assembly.Load(ms.ToArray());

                    var assemblyType = assembly.GetType(PathDelegateNamespace + "." + PathDelegateClassName);

                    pathTraverserInstance = (IPathTraverser)Activator.CreateInstance(assemblyType);
                }
                else
                {
                    StringBuilder sb = new StringBuilder();

                    foreach (Diagnostic codeIssue in compilationResult.Diagnostics)
                    {
                        string issue = $"ID: {codeIssue.Id}, Message: {codeIssue.GetMessage()}, Location: {codeIssue.Location.GetLineSpan()}, Severity: {codeIssue.Severity}";

                        sb.AppendLine(issue);
                    }

                    resultMessage = sb.ToString();
                }
            }

            traverser = pathTraverserInstance; ;

            return resultMessage;
        }

        /// <summary>
        /// Create the source for the code generation
        /// </summary>
        /// <param name="definition">The path specification</param>
        /// <returns>The source code</returns>
        private static string CreateSource(PathSpecification definition)
        {
            var @namespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(PathDelegateNamespace)).NormalizeWhitespace();

            @namespace = @namespace.AddUsings(
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Linq")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("NoSQL.GraphDB.Core.Model"))
                );

            var classDeclaration = SyntaxFactory.ClassDeclaration(PathDelegateClassName);

            // Add the public modifier: (public class Order)
            classDeclaration = classDeclaration.AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)
                );

            // Inherit BaseEntity<T> and implement IHaveIdentity: (public class Order : BaseEntity<T>, IHaveIdentity)
            classDeclaration = classDeclaration.AddBaseListTypes(
                SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("IPathTraverser")));

            var methods = new List<MethodDeclarationSyntax>();

            if (definition.Filter != null)
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexFilter", VertexFilterMethodName, definition.Filter.Vertex));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeFilter", EdgeFilterMethodName, definition.Filter.Edge));
                methods.Add(GenerateMethodSyntax("Delegates.EdgePropertyFilter", EdgePropertyFilterMethodName, definition.Filter.EdgeProperty));
            }
            else
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexFilter", VertexFilterMethodName, null));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeFilter", EdgeFilterMethodName, null));
                methods.Add(GenerateMethodSyntax("Delegates.EdgePropertyFilter", EdgePropertyFilterMethodName, null));
            }

            if (definition.Cost != null)
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexCost", VertexCostMethodName, definition.Cost.Vertex));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeCost", EdgeCostMethodName, definition.Cost.Edge));
            }
            else
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexCost", VertexCostMethodName, null));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeCost", EdgeCostMethodName, null));
            }

            // Add the field, the property and method to the class.
            classDeclaration = classDeclaration.AddMembers(methods.ToArray());

            // Add the class to the namespace.
            @namespace = @namespace.AddMembers(classDeclaration);

            // Normalize and get code as string.
            var code = @namespace
                .NormalizeWhitespace()
                .ToFullString();

            return code;
        }

        private static MethodDeclarationSyntax GenerateMethodSyntax(string returnType, string methodname, string code)
        {
            String codeToCompile = "return null;";

            if (!String.IsNullOrWhiteSpace(code))
            {
                codeToCompile = code;
            }

            // Create a stament with the body of a method.
            var syntax = SyntaxFactory.ParseStatement(codeToCompile);

            // Create a method
            return SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(returnType), methodname)
               .AddModifiers(
                   SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                   )
               .WithBody(SyntaxFactory.Block(syntax));
        }

        private static IEnumerable<MetadataReference> GetGlobalReferences()
        {
            var assemblies = new[]
                {
                typeof(System.Linq.Enumerable).Assembly,
                typeof(IRead).Assembly,
                typeof(object).Assembly
            };

            var refs = from a in assemblies
                       select MetadataReference.CreateFromFile(a.Location);
            var returnList = refs.ToList();

            //The location of the .NET assemblies
            var assemblyPath = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);

            /*
            * Adding some necessary .NET assemblies
            * These assemblies couldn't be loaded correctly via the same construction as above,
            * in specific the System.Runtime.
            */
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "mscorlib.dll")));
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "System.dll")));
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "System.Core.dll")));
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "System.Runtime.dll")));

            return returnList;
        }
    }
}
