// MIT License
//
// DelegateJson.cs
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
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Core.Serializer
{
    /// <summary>
    /// Factory interface for reconstructing delegate targets from JSON arguments.
    /// </summary>
    public interface ITargetFactory
    {
        /// <summary>
        /// Creates a target instance from JSON arguments.
        /// </summary>
        /// <param name="jsonArgs">Optional JSON arguments for target construction.</param>
        /// <returns>The constructed target object.</returns>
        object Create(string jsonArgs);
    }

    /// <summary>
    /// Specification for reconstructing delegate targets via a factory.
    /// </summary>
    public sealed class DelegateTargetSpec
    {
        /// <summary>
        /// Gets or initializes the assembly-qualified name of the factory type.
        /// </summary>
        [JsonPropertyName("factoryTypeAssemblyQualifiedName")]
        public string FactoryTypeAssemblyQualifiedName { get; init; } = string.Empty;

        /// <summary>
        /// Gets or initializes optional JSON arguments to pass to the factory.
        /// </summary>
        [JsonPropertyName("jsonArgs")]
        public string JsonArgs
        {
            get; init;
        }
    }

    /// <summary>
    /// Internal descriptor for delegate serialization.
    /// </summary>
    internal sealed class DelegateDescriptor
    {
        /// <summary>
        /// Gets or sets the version of the descriptor format.
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>
        /// Gets or sets the assembly-qualified name of the delegate type.
        /// </summary>
        [JsonPropertyName("delegateTypeAssemblyQualifiedName")]
        public string DelegateTypeAssemblyQualifiedName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the assembly-qualified name of the declaring type.
        /// </summary>
        [JsonPropertyName("declaringTypeAssemblyQualifiedName")]
        public string DeclaringTypeAssemblyQualifiedName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the method name.
        /// </summary>
        [JsonPropertyName("methodName")]
        public string MethodName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the assembly-qualified names of parameter types.
        /// </summary>
        [JsonPropertyName("parameterTypeAssemblyQualifiedNames")]
        public string[] ParameterTypeAssemblyQualifiedNames { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets whether the method is static.
        /// </summary>
        [JsonPropertyName("isStatic")]
        public bool IsStatic
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets optional target specification for instance methods.
        /// </summary>
        [JsonPropertyName("target")]
        public DelegateTargetSpec Target
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets optional notes about the delegate.
        /// </summary>
        [JsonPropertyName("notes")]
        public string Notes
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets optional SHA256 signature hash for integrity verification.
        /// </summary>
        [JsonPropertyName("signatureHash")]
        public string SignatureHash
        {
            get; set;
        }
    }

    /// <summary>
    /// Configuration for delegate deserialization security.
    /// </summary>
    public sealed class DelegateSecurityConfig
    {
        /// <summary>
        /// Gets or sets the list of trusted assembly names (simple names, not full qualified).
        /// If null or empty, all assemblies from currently loaded assemblies are trusted.
        /// </summary>
        public HashSet<string> TrustedAssemblies
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the list of trusted namespace prefixes.
        /// If null or empty, all namespaces are trusted.
        /// </summary>
        public HashSet<string> TrustedNamespaces
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets whether to verify signature hash on deserialization.
        /// </summary>
        public bool VerifySignatureHash { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to allow non-public methods.
        /// Default is false for security.
        /// </summary>
        public bool AllowNonPublic { get; set; } = false;

        /// <summary>
        /// Creates a default security configuration that trusts NoSQL.GraphDB assemblies.
        /// </summary>
        public static DelegateSecurityConfig CreateDefault()
        {
            return new DelegateSecurityConfig
            {
                TrustedAssemblies = new HashSet<string>
                {
                    "fallen-8-core",
                    "fallen-8-unittest",  // Allow test assembly
                    "mscorlib",
                    "System.Private.CoreLib",
                    "System.Runtime"
                },
                TrustedNamespaces = new HashSet<string>
                {
                    "NoSQL.GraphDB",  // Trust all NoSQL.GraphDB namespaces
                    "System"
                },
                VerifySignatureHash = false,
                AllowNonPublic = false
            };
        }

        /// <summary>
        /// Creates a strict security configuration that only trusts NoSQL.GraphDB assemblies.
        /// </summary>
        public static DelegateSecurityConfig CreateStrict()
        {
            return new DelegateSecurityConfig
            {
                TrustedAssemblies = new HashSet<string>
                {
                    "fallen-8-core"
                },
                TrustedNamespaces = new HashSet<string>
                {
                    "NoSQL.GraphDB.Core"
                },
                VerifySignatureHash = true,
                AllowNonPublic = false
            };
        }
    }

    /// <summary>
    /// Static utility class for serializing and deserializing delegates to/from JSON.
    /// </summary>
    public static class DelegateJson
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Serializes a delegate to a JSON string descriptor.
        /// </summary>
        /// <param name="del">The delegate to serialize.</param>
        /// <param name="targetSpec">Optional target specification for instance methods.</param>
        /// <param name="includeSignatureHash">Whether to include a signature hash for integrity verification.</param>
        /// <returns>A JSON string descriptor of the delegate.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="del"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the delegate cannot be serialized (e.g., closure without targetSpec).</exception>
        public static string Serialize(Delegate del, DelegateTargetSpec targetSpec = null, bool includeSignatureHash = false)
        {
            if (del == null)
                throw new ArgumentNullException(nameof(del));

            var method = del.Method;
            var declaringType = method.DeclaringType;

            if (declaringType == null)
                throw new InvalidOperationException("Delegate method has no declaring type.");

            // Check for closures - compiler-generated types
            if (declaringType.Name.Contains("<") || declaringType.Name.Contains(">"))
            {
                if (targetSpec == null)
                    throw new InvalidOperationException(
                        "Captured closures cannot be serialized without a targetSpec. Use method groups or static lambdas.");
            }

            // For instance methods, we require a targetSpec to reconstruct the target
            if (!method.IsStatic && targetSpec == null)
            {
                throw new InvalidOperationException(
                    "Instance methods require a targetSpec to reconstruct the target object during deserialization.");
            }

            var parameters = method.GetParameters();
            var parameterTypes = parameters.Select(p => p.ParameterType.AssemblyQualifiedName ?? string.Empty).ToArray();

            var descriptor = new DelegateDescriptor
            {
                Version = 1,
                DelegateTypeAssemblyQualifiedName = del.GetType().AssemblyQualifiedName ?? string.Empty,
                DeclaringTypeAssemblyQualifiedName = declaringType.AssemblyQualifiedName ?? string.Empty,
                MethodName = method.Name,
                ParameterTypeAssemblyQualifiedNames = parameterTypes,
                IsStatic = method.IsStatic,
                Target = targetSpec
            };

            // Add signature hash if requested
            if (includeSignatureHash)
            {
                descriptor.SignatureHash = ComputeSignatureHash(descriptor);
            }

            return JsonSerializer.Serialize(descriptor, JsonOptions);
        }

        /// <summary>
        /// Deserializes a JSON string descriptor to a strongly-typed delegate.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type to create.</typeparam>
        /// <param name="json">The JSON string descriptor.</param>
        /// <param name="services">Service provider for resolving target factories. Can be null if not needed.</param>
        /// <param name="securityConfig">Security configuration for validation. If null, uses default secure configuration.</param>
        /// <returns>The reconstructed delegate.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when deserialization fails due to type mismatches or missing methods.</exception>
        /// <exception cref="SecurityException">Thrown when security validation fails.</exception>
        public static TDelegate Deserialize<TDelegate>(string json, IServiceProvider services = null, DelegateSecurityConfig securityConfig = null)
            where TDelegate : Delegate
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentNullException(nameof(json));

            // Use default security config if none provided
            if (securityConfig == null)
            {
                securityConfig = DelegateSecurityConfig.CreateDefault();
            }

            DelegateDescriptor descriptor;
            try
            {
                descriptor = JsonSerializer.Deserialize<DelegateDescriptor>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Deserialization failed.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Invalid JSON format.", ex);
            }

            // Validate version
            if (descriptor.Version != 1)
                throw new InvalidOperationException($"Unsupported version: {descriptor.Version}");

            // Verify signature hash if enabled
            if (securityConfig.VerifySignatureHash)
            {
                if (string.IsNullOrEmpty(descriptor.SignatureHash))
                    throw new InvalidOperationException("Signature hash required but not present.");

                var computedHash = ComputeSignatureHash(descriptor);
                if (descriptor.SignatureHash != computedHash)
                    throw new InvalidOperationException("Signature hash verification failed.");
            }

            // Validate delegate type compatibility
            var expectedDelegateType = typeof(TDelegate);
            var descriptorDelegateType = SafeResolveType(descriptor.DelegateTypeAssemblyQualifiedName, securityConfig, "delegate");

            if (!expectedDelegateType.IsAssignableFrom(descriptorDelegateType) &&
                !AreDelegateSignaturesCompatible(expectedDelegateType, descriptorDelegateType))
            {
                throw new InvalidOperationException("Delegate type mismatch.");
            }

            // Resolve declaring type with security validation
            var declaringType = SafeResolveType(descriptor.DeclaringTypeAssemblyQualifiedName, securityConfig, "declaring");

            // Resolve parameter types with security validation
            var parameterTypes = new Type[descriptor.ParameterTypeAssemblyQualifiedNames.Length];
            for (int i = 0; i < descriptor.ParameterTypeAssemblyQualifiedNames.Length; i++)
            {
                parameterTypes[i] = SafeResolveType(descriptor.ParameterTypeAssemblyQualifiedNames[i], securityConfig, "parameter");
            }

            // Find the method
            var bindingFlags = BindingFlags.Public | (descriptor.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
            if (securityConfig.AllowNonPublic)
                bindingFlags |= BindingFlags.NonPublic;

            var method = declaringType.GetMethod(descriptor.MethodName, bindingFlags, null, parameterTypes, null);
            if (method == null)
            {
                throw new InvalidOperationException($"Method not found: {descriptor.MethodName}");
            }

            // Reconstruct target if needed
            object target = null;
            if (!descriptor.IsStatic)
            {
                if (descriptor.Target == null)
                    throw new InvalidOperationException("Instance method requires target specification.");

                target = ReconstructTarget(descriptor.Target, services, securityConfig);
            }

            // Create delegate
            try
            {
                var del = descriptor.IsStatic
                    ? Delegate.CreateDelegate(typeof(TDelegate), method)
                    : Delegate.CreateDelegate(typeof(TDelegate), target, method);

                return (TDelegate)del;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to create delegate.", ex);
            }
        }

        /// <summary>
        /// Safely resolves a type with security validation.
        /// </summary>
        private static Type SafeResolveType(string assemblyQualifiedName, DelegateSecurityConfig config, string typeCategory)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                throw new InvalidOperationException($"Type name is required for {typeCategory} type.");

            Type type;
            try
            {
                // Type.GetType with throwOnError=false to avoid loading new assemblies
                type = Type.GetType(assemblyQualifiedName, throwOnError: false, ignoreCase: false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to resolve {typeCategory} type.", ex);
            }

            if (type == null)
                throw new InvalidOperationException($"Type not found: {typeCategory}");

            // Validate assembly is trusted
            var assemblyName = type.Assembly.GetName().Name;
            if (config.TrustedAssemblies != null && config.TrustedAssemblies.Count > 0)
            {
                if (!config.TrustedAssemblies.Contains(assemblyName))
                    throw new InvalidOperationException($"Assembly not trusted: {assemblyName}");
            }

            // Validate namespace is trusted
            if (config.TrustedNamespaces != null && config.TrustedNamespaces.Count > 0)
            {
                var typeNamespace = type.Namespace ?? string.Empty;
                var isTrusted = config.TrustedNamespaces.Any(ns => typeNamespace.StartsWith(ns));
                if (!isTrusted)
                    throw new InvalidOperationException($"Namespace not trusted: {typeNamespace}");
            }

            return type;
        }

        /// <summary>
        /// Computes SHA256 hash of the delegate signature for integrity verification.
        /// </summary>
        private static string ComputeSignatureHash(DelegateDescriptor descriptor)
        {
            var signatureBuilder = new StringBuilder();
            signatureBuilder.Append(descriptor.Version);
            signatureBuilder.Append(descriptor.DelegateTypeAssemblyQualifiedName);
            signatureBuilder.Append(descriptor.DeclaringTypeAssemblyQualifiedName);
            signatureBuilder.Append(descriptor.MethodName);
            signatureBuilder.Append(descriptor.IsStatic);

            foreach (var paramType in descriptor.ParameterTypeAssemblyQualifiedNames)
            {
                signatureBuilder.Append(paramType);
            }

            var signatureBytes = Encoding.UTF8.GetBytes(signatureBuilder.ToString());
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(signatureBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        /// <summary>
        /// Checks if two delegate types have compatible signatures.
        /// </summary>
        private static bool AreDelegateSignaturesCompatible(Type type1, Type type2)
        {
            if (!typeof(Delegate).IsAssignableFrom(type1) || !typeof(Delegate).IsAssignableFrom(type2))
                return false;

            var invoke1 = type1.GetMethod("Invoke");
            var invoke2 = type2.GetMethod("Invoke");

            if (invoke1 == null || invoke2 == null)
                return false;

            // Check return types
            if (invoke1.ReturnType != invoke2.ReturnType)
                return false;

            // Check parameters
            var params1 = invoke1.GetParameters();
            var params2 = invoke2.GetParameters();

            if (params1.Length != params2.Length)
                return false;

            for (int i = 0; i < params1.Length; i++)
            {
                if (params1[i].ParameterType != params2[i].ParameterType)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Reconstructs a target object using the specified target specification.
        /// </summary>
        private static object ReconstructTarget(DelegateTargetSpec targetSpec, IServiceProvider services, DelegateSecurityConfig config)
        {
            var factoryType = SafeResolveType(targetSpec.FactoryTypeAssemblyQualifiedName, config, "factory");

            if (!typeof(ITargetFactory).IsAssignableFrom(factoryType))
                throw new InvalidOperationException("Factory type does not implement ITargetFactory.");

            ITargetFactory factory;

            // Try to resolve via service provider first
            if (services != null)
            {
                var serviceFactory = services.GetService(factoryType);
                if (serviceFactory is ITargetFactory targetFactory)
                {
                    factory = targetFactory;
                }
                else
                {
                    // Fall back to Activator
                    factory = (ITargetFactory)(Activator.CreateInstance(factoryType)
                        ?? throw new InvalidOperationException("Failed to create factory instance."));
                }
            }
            else
            {
                // No service provider, use Activator
                factory = (ITargetFactory)(Activator.CreateInstance(factoryType)
                    ?? throw new InvalidOperationException("Failed to create factory instance."));
            }

            return factory.Create(targetSpec.JsonArgs);
        }
    }
}
