// MIT License
//
// TrimSafetyTest.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Convention tests for TRIM SAFETY (feature trim-safety).
    ///
    ///   <para>The first line of defence is the BUILD, not these tests: <c>IsTrimmable</c> in
    ///   <c>fallen-8-core.csproj</c> keeps the trim analyzer on, and warnings are errors, so removing
    ///   the annotation from one declaration fails as IL2095 (mismatch with the override) and removing
    ///   it from all of them fails as IL2087 at the <c>Activator</c> call - both verified by mutating
    ///   the source. These tests pin what that gate CANNOT see:</para>
    ///
    ///   <list type="bullet">
    ///     <item><description>An IL2087 "fixed" with a suppression instead of an annotation. That
    ///     silences the build and moves the failure into the consumer, where a trimmed app throws
    ///     <c>MissingMethodException</c> (<c>Arg_NoDefCTor</c>) at runtime - the exact regression this
    ///     feature fixed.</description></item>
    ///     <item><description>The gate itself being switched off: <c>IsTrimmable</c> removed from the
    ///     project, or an annotation dropped in a project whose analyzer is not enabled.</description></item>
    ///     <item><description>The reverse mistake: marking the ORDINARY WRITE PATH trim-unsafe, which
    ///     would bury a browser consumer in warnings about writes that trim perfectly well.</description></item>
    ///     <item><description>A declaration chain drifting apart where nothing analyzes it, and the
    ///     honest-declaration surfaces silently losing their <see cref="RequiresUnreferencedCodeAttribute" />.
    ///     </description></item>
    ///   </list>
    /// </summary>
    [TestClass]
    public class TrimSafetyTest
    {
        #region helpers

        private static MethodInfo GenericMethod(Type declaringType, string name, int genericArity = 1)
        {
            var candidates = declaringType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == name && m.IsGenericMethodDefinition
                            && m.GetGenericArguments().Length == genericArity)
                .ToList();

            Assert.AreEqual(1, candidates.Count,
                $"Expected exactly one generic {declaringType.Name}.{name}<T> to pin; found {candidates.Count}.");
            return candidates[0];
        }

        private static void AssertParameterlessCtorIsKept(MethodInfo method, string site)
        {
            var typeParameter = method.GetGenericArguments()[0];
            var annotation = typeParameter
                .GetCustomAttributes(typeof(DynamicallyAccessedMembersAttribute), inherit: false)
                .Cast<DynamicallyAccessedMembersAttribute>()
                .FirstOrDefault();

            Assert.IsNotNull(annotation,
                $"{site} reflectively constructs its type argument, so T MUST carry " +
                "[DynamicallyAccessedMembers(PublicParameterlessConstructor)]. Without it a trimmed consumer " +
                "removes the constructor and gets MissingMethodException (Arg_NoDefCTor) at runtime - and the " +
                "engine build stays green, which is why this is a test.");

            Assert.IsTrue(annotation.MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor),
                $"{site}: the annotation must include PublicParameterlessConstructor (found {annotation.MemberTypes}).");
        }

        private static RequiresUnreferencedCodeAttribute TrimRequirementOf(MemberInfo member)
        {
            return member
                .GetCustomAttributes(typeof(RequiresUnreferencedCodeAttribute), inherit: false)
                .Cast<RequiresUnreferencedCodeAttribute>()
                .FirstOrDefault();
        }

        private static void AssertDeclaresTrimRequirement(MemberInfo member, string site)
        {
            var attribute = TrimRequirementOf(member);

            Assert.IsNotNull(attribute,
                $"{site} resolves types from strings or scanned assemblies, so it must declare " +
                "[RequiresUnreferencedCode] - that is what turns a silent runtime failure in a trimmed " +
                "consumer into a warning at its own call site.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(attribute.Message),
                $"{site}: the trim requirement must carry a message telling the caller what to do instead.");
        }

        private static void AssertNoTrimRequirement(MemberInfo member, string site, string because)
        {
            Assert.IsNotNull(member, $"{site}: the member to pin was not found (renamed or removed?).");
            Assert.IsNull(TrimRequirementOf(member),
                $"{site} must NOT declare [RequiresUnreferencedCode]: {because} Re-adding one here hands every " +
                "caller a warning for a capability that has a trim-safe path, which is the decision this test pins.");
        }

        /// <summary>
        ///   Asserts that a private one-line pass-through carries the trim suppression it exists for.
        ///   The seam is what keeps the suppression scoped to a single call, so losing it (inlining the
        ///   discovery call back into the caller) is exactly the silent widening this pins.
        /// </summary>
        private static void AssertSuppressionSeam(Type declaringType, string methodName)
        {
            var seam = declaringType.GetMethod(methodName,
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

            Assert.IsNotNull(seam,
                $"{declaringType.Name}.{methodName} is the suppression seam for plugin discovery; without it the " +
                "suppression would have to sit on the whole resolving member and would cover more than the one call.");

            var suppression = seam
                .GetCustomAttributes(typeof(UnconditionalSuppressMessageAttribute), inherit: false)
                .Cast<UnconditionalSuppressMessageAttribute>()
                .FirstOrDefault(a => string.Equals(a.CheckId, "IL2026", StringComparison.Ordinal));

            Assert.IsNotNull(suppression,
                $"{declaringType.Name}.{methodName} must carry [UnconditionalSuppressMessage(\"Trimming\", \"IL2026\")].");
            Assert.IsFalse(string.IsNullOrWhiteSpace(suppression.Justification),
                $"{declaringType.Name}.{methodName}: a suppression without a justification is an unexplained silence.");
        }

        private static void AssertSameTrimMessage(MemberInfo engineMember, MemberInfo forwarderMember, string site)
        {
            Assert.IsNotNull(engineMember, $"{site}: could not resolve the engine interface member to compare against.");
            Assert.IsNotNull(forwarderMember, $"{site}: could not resolve the forwarding implementation.");

            var engine = TrimRequirementOf(engineMember);
            Assert.IsNotNull(engine, $"IFallen8Read.{site} must declare [RequiresUnreferencedCode].");

            var forwarder = TrimRequirementOf(forwarderMember);
            Assert.IsNotNull(forwarder,
                $"{forwarderMember.DeclaringType.Name}.{site} implements an annotated interface member and must " +
                "repeat the annotation.");

            Assert.AreEqual(engine.Message, forwarder.Message,
                $"{site}: the forwarder's trim message must be the SAME STRING as the engine's, not a copy of its " +
                "wording. A copied literal drifts silently the moment the engine's message changes - the analyzer " +
                "only checks that an annotation is PRESENT, never what it says - and a consumer then reads two " +
                "different stories about one limitation. Reference the engine's shared message constant instead.");
        }

        #endregion

        #region the two reflectively-constructed type parameters

        [TestMethod]
        public void TypedPathOverload_KeepsItsAlgorithmsConstructor_OnEveryDeclaration()
        {
            // The interface, the abstract base and the implementation each need the annotation: it does
            // not flow along an override chain. The apiApp forwarder is covered below.
            AssertParameterlessCtorIsKept(
                GenericMethod(typeof(IFallen8Read), nameof(IFallen8Read.TryCalculateShortestPath)),
                "IFallen8Read.TryCalculateShortestPath<T>");
            AssertParameterlessCtorIsKept(
                GenericMethod(typeof(AFallen8), nameof(AFallen8.TryCalculateShortestPath)),
                "AFallen8.TryCalculateShortestPath<T>");
            AssertParameterlessCtorIsKept(
                GenericMethod(typeof(Fallen8), nameof(Fallen8.TryCalculateShortestPath)),
                "Fallen8.TryCalculateShortestPath<T>");
        }

        [TestMethod]
        public void TypedSubGraphOverloads_KeepTheirAlgorithmsConstructor()
        {
            AssertParameterlessCtorIsKept(
                GenericMethod(typeof(SubGraphFactory), nameof(SubGraphFactory.TryCreateSubGraph)),
                "SubGraphFactory.TryCreateSubGraph<T>");
            AssertParameterlessCtorIsKept(
                GenericMethod(typeof(SubGraphFactory), nameof(SubGraphFactory.TryCreateSubGraphFromSource)),
                "SubGraphFactory.TryCreateSubGraphFromSource<T>");
        }

        [TestMethod]
        public void EveryImplementationOfTheTypedPathOverload_CarriesTheSameAnnotation()
        {
            // Any type in the loaded product assemblies that implements IFallen8Read must repeat the
            // annotation on its own generic parameter, or a trimming consumer of THAT assembly loses the
            // guarantee (and the analyzer only complains where it happens to be enabled). The apiApp's
            // AddressedFallen8 is the real case; a test double is excluded because the test assembly is
            // never trimmed.
            var implementations = new[] { typeof(Fallen8).Assembly, typeof(App.Namespaces.AddressedFallen8).Assembly }
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IFallen8Read).IsAssignableFrom(t))
                .ToList();

            Assert.IsTrue(implementations.Count >= 2,
                "Expected at least the engine and the addressed forwarder to implement IFallen8Read.");

            foreach (var implementation in implementations)
            {
                AssertParameterlessCtorIsKept(
                    GenericMethod(implementation, nameof(IFallen8Read.TryCalculateShortestPath)),
                    implementation.Name + ".TryCalculateShortestPath<T>");
            }
        }

        #endregion

        #region the surfaces that cannot be made trim-safe declare it

        [TestMethod]
        public void PluginDiscovery_DeclaresItsTrimRequirement()
        {
            AssertDeclaresTrimRequirement(
                typeof(PluginFactory).GetMethod(nameof(PluginFactory.TryFindPlugin)),
                "PluginFactory.TryFindPlugin<T>");
            AssertDeclaresTrimRequirement(
                typeof(PluginFactory).GetMethod(nameof(PluginFactory.TryGetAvailablePlugins)),
                "PluginFactory.TryGetAvailablePlugins<T>");
            AssertDeclaresTrimRequirement(
                typeof(PluginFactory).GetMethod(nameof(PluginFactory.AvailableBuiltInNames)),
                "PluginFactory.AvailableBuiltInNames");
        }

        [TestMethod]
        public void StringNamedEngineOverloads_DeclareTheirTrimRequirement()
        {
            // The counterpart of the typed overloads: resolved through discovery, so they cannot be made
            // trim-safe and must say so instead. Pinned on the INTERFACE, which is what a consumer codes
            // against.
            var path = typeof(IFallen8Read).GetMethod(nameof(IFallen8Read.TryCalculateShortestPath),
                new[] { typeof(List<Core.Algorithms.Path.Path>).MakeByRefType(), typeof(string), typeof(Core.Algorithms.Path.ShortestPathDefinition) });
            AssertDeclaresTrimRequirement(path, "IFallen8Read.TryCalculateShortestPath(out, string, definition)");

            var analytics = typeof(IFallen8Read).GetMethod(nameof(IFallen8Read.TryRunAnalytics));
            AssertDeclaresTrimRequirement(analytics, "IFallen8Read.TryRunAnalytics");
        }

        [TestMethod]
        public void TheAddressedForwarder_RepeatsTheEngineTrimMessage_WordForWord()
        {
            // AddressedFallen8 implements the two string-named surfaces by forwarding to the engine, so
            // it must repeat their annotation - and the analyzer stops there: it never compares the
            // MESSAGES. That is the gap this test closes; the drift it catches reaches a consumer, whose
            // build quotes whichever of the two texts its call site happened to hit.
            var stringNamedPath = new[]
            {
                typeof(List<Core.Algorithms.Path.Path>).MakeByRefType(),
                typeof(string),
                typeof(Core.Algorithms.Path.ShortestPathDefinition)
            };

            AssertSameTrimMessage(
                typeof(IFallen8Read).GetMethod(nameof(IFallen8Read.TryCalculateShortestPath), stringNamedPath),
                typeof(App.Namespaces.AddressedFallen8).GetMethod(nameof(IFallen8Read.TryCalculateShortestPath), stringNamedPath),
                "TryCalculateShortestPath(out, string, definition)");

            AssertSameTrimMessage(
                typeof(IFallen8Read).GetMethod(nameof(IFallen8Read.TryRunAnalytics)),
                typeof(App.Namespaces.AddressedFallen8).GetMethod(nameof(IFallen8Read.TryRunAnalytics)),
                "TryRunAnalytics");
        }

        [TestMethod]
        public void ReflectiveSerializerEntryPoints_DeclareTheirTrimRequirement()
        {
            AssertDeclaresTrimRequirement(
                typeof(SerializationReader).GetMethod(nameof(SerializationReader.ReadObject)),
                "SerializationReader.ReadObject");
            AssertDeclaresTrimRequirement(
                typeof(SerializationReader).GetMethod(nameof(SerializationReader.ReadType), Type.EmptyTypes),
                "SerializationReader.ReadType");
            AssertDeclaresTrimRequirement(
                typeof(SerializationWriter).GetMethod(nameof(SerializationWriter.WriteObject)),
                "SerializationWriter.WriteObject");
            AssertDeclaresTrimRequirement(
                typeof(DelegateJson).GetMethod(nameof(DelegateJson.Serialize)),
                "DelegateJson.Serialize");
        }

        [TestMethod]
        public void TransactionsThatResolveTypesByName_DeclareItOnTheType()
        {
            // Annotated at the TYPE so the warning reaches the consumer where it constructs the
            // transaction, and so the abstract ATransaction.TryExecute - shared with every trim-safe
            // write - stays unannotated.
            AssertDeclaresTrimRequirement(typeof(SaveTransaction), nameof(SaveTransaction));
            AssertDeclaresTrimRequirement(typeof(LoadTransaction), nameof(LoadTransaction));
            AssertDeclaresTrimRequirement(typeof(CreateSubGraphTransaction), nameof(CreateSubGraphTransaction));
        }

        [TestMethod]
        public void OrdinaryWrites_StayFreeOfTrimRequirements()
        {
            // The other half of the contract: creating and changing graph elements must NEVER be marked
            // trim-unsafe, otherwise a browser consumer drowns in warnings for writes that are perfectly
            // trimmable. This is what the boundary suppressions exist to protect.
            foreach (var trimSafe in new[]
            {
                typeof(CreateVerticesTransaction), typeof(CreateEdgesTransaction),
                typeof(AddPropertiesTransaction), typeof(SetPropertiesTransaction),
                typeof(RemoveGraphElementsTransaction), typeof(TrimTransaction)
            })
            {
                Assert.AreEqual(0,
                    trimSafe.GetCustomAttributes(typeof(RequiresUnreferencedCodeAttribute), inherit: false).Length,
                    $"{trimSafe.Name} needs no reflection and must stay trim-safe.");
            }

            var enqueue = typeof(IFallen8Write).GetMethod(nameof(IFallen8Write.EnqueueTransaction));
            Assert.AreEqual(0, enqueue.GetCustomAttributes(typeof(RequiresUnreferencedCodeAttribute), inherit: false).Length,
                "EnqueueTransaction is the write path for every transaction and must stay trim-safe.");
        }

        [TestMethod]
        public void HostTypeRegistration_IsTrimSafe_AndKeepsTheRegisteredTypesConstructor()
        {
            // The point of registration (feature host-plugin-registration): a statically-known type
            // travels from the host's typeof(T) to the Activator, so this member must stay free of a
            // trim requirement AND carry the annotation that keeps the constructor.
            var register = GenericMethod(typeof(Fallen8), nameof(Fallen8.RegisterPluginType));

            AssertNoTrimRequirement(register, "Fallen8.RegisterPluginType<T>",
                "nothing is scanned and no type is resolved from a string - the type argument IS the type.");
            AssertParameterlessCtorIsKept(register, "Fallen8.RegisterPluginType<T>");

            var typeParameter = register.GetGenericArguments()[0];
            Assert.IsTrue(
                typeParameter.GenericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint),
                "T must keep its new() constraint: activation NEEDS a parameterless constructor, and the constraint " +
                "is what turns a runtime activation failure at the host into a compile error there.");
        }

        [TestMethod]
        public void IndexAndServiceResolution_StayFreeOfTrimRequirements_BehindASuppressionSeam()
        {
            // The deliberate trim-surface change of feature host-plugin-registration: these four
            // members resolve a plugin name through the per-namespace registry FIRST - statically-known
            // types, nothing scanned - and only fall back to discovery behind a one-line suppressed
            // seam. Pinned so the decision cannot silently flip either way: re-adding the requirement
            // would warn a browser host about the very capability the registry gives it trim-safely,
            // and dropping the seam would widen an unexplained suppression over the whole member.
            var indexFactory = typeof(NoSQL.GraphDB.Core.Index.IndexFactory);
            var serviceFactory = typeof(NoSQL.GraphDB.Core.Service.ServiceFactory);

            AssertNoTrimRequirement(indexFactory.GetMethod("TryCreateIndex"), "IndexFactory.TryCreateIndex",
                "an index type reached through host registration needs no scanning, and the discovery fallback " +
                "degrades to a clean not-found.");
            AssertNoTrimRequirement(
                indexFactory.GetMethod("OpenIndex", BindingFlags.NonPublic | BindingFlags.Instance),
                "IndexFactory.OpenIndex", "checkpoint rehydration resolves through the same registry-first seam.");
            AssertNoTrimRequirement(serviceFactory.GetMethod("TryAddService"), "ServiceFactory.TryAddService",
                "services resolve registry-first as well.");
            AssertNoTrimRequirement(
                serviceFactory.GetMethod("OpenService", BindingFlags.NonPublic | BindingFlags.Instance),
                "ServiceFactory.OpenService", "services resolve registry-first as well.");

            AssertSuppressionSeam(indexFactory, "TryFindDiscoveredIndexSuppressed");
            AssertSuppressionSeam(serviceFactory, "TryFindDiscoveredServiceSuppressed");
        }

        [TestMethod]
        public void TheEngineAssembly_DeclaresItselfTrimmable()
        {
            // Pins the IsTrimmable project property: it is what keeps the trim ANALYZER on for every
            // build, which is the gate that stopped this class of bug from coming back.
            var trimmable = typeof(Fallen8).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Any(a => string.Equals(a.Key, "IsTrimmable", StringComparison.Ordinal)
                          && string.Equals(a.Value, "True", StringComparison.OrdinalIgnoreCase));

            Assert.IsTrue(trimmable,
                "fallen-8-core must keep <IsTrimmable>true</IsTrimmable>: it declares the assembly trim-ready " +
                "for consumers AND keeps the trim analyzer enabled, which is the build gate for this feature.");
        }

        #endregion
    }
}
