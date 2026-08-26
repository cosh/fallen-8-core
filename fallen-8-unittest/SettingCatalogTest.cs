// MIT License
//
// SettingCatalogTest.cs
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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The governance gate for the setting catalog (feature writable-instance-config phase 1).
    ///
    /// The catalog is the ONE home that decides which configuration keys an operator may write, so
    /// its completeness must be DERIVED rather than hand-maintained: these tests reflect over every
    /// options class the apiApp binds and fail unless every leaf key is catalogued, with a
    /// never-writable key carrying the rule that excludes it and a reason. That is the same shape as
    /// the enforced MCP bridge-or-defer rule (<see cref="McpRestCoverageTest"/>): a future option
    /// property forces a recorded decision instead of silently missing from the surface.
    ///
    /// Both directions matter. A leaf absent from the catalog is an unclassified key; a catalog key
    /// absent from the reflection is a stale entry left behind by a rename, which would publish a
    /// setting that no longer exists.
    /// </summary>
    [TestClass]
    public class SettingCatalogTest
    {
        private static readonly Assembly _apiApp = typeof(Fallen8SecurityOptions).Assembly;

        /// <summary>
        ///   The exclusion rules a catalogued key may cite: R1 to R6 of spec section 4.7, R8 (feature
        ///   nahil-backend), plus the collection rule of 4.3.5. R7 is deliberately NOT here.
        ///   Its resolution is to implement or delete the property, never to catalogue it, because a
        ///   never-writable entry still publishes the key and would go on advertising a control the
        ///   app does not have.
        ///   <para>
        ///     R8 - no credential this server PRESENTS - is the one rule added since. R1 covers the
        ///     credential the server DEMANDS and is blanket-scoped to <c>Fallen8:Security</c>, so a
        ///     key elsewhere cannot cite it (<see cref="AssertBlanketRule"/> fails it); and neither
        ///     the URL rule (R4) nor the capability rule (R5) describes a secret. A new hazard class
        ///     earns a rule rather than being filed under the nearest one, which is what keeps the
        ///     published reasons honest.
        ///   </para>
        /// </summary>
        private static readonly String[] _knownRules = { "R1", "R2", "R3", "R4", "R5", "R6", "R8", "4.3.5" };

        #region reflecting the real configuration surface

        /// <summary>
        ///   Every options class the apiApp binds: any class in the WHOLE assembly declaring a
        ///   <c>public const String SectionName</c> under the <c>Fallen8:</c> prefix. The marker is the
        ///   const, deliberately not the namespace: a namespace filter would let an options class added
        ///   beside its feature (rather than under Configuration/) bind real keys while silently
        ///   escaping the catalog, the section map and trial-binding, which is exactly the unclassified
        ///   drift this gate exists to make impossible.
        /// </summary>
        private static IEnumerable<Type> OptionsClasses()
        {
            return _apiApp.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract)
                .Where(type => SectionNameOf(type)?.StartsWith("Fallen8:", StringComparison.Ordinal) == true)
                .OrderBy(type => type.Name, StringComparer.Ordinal);
        }

        private static String SectionNameOf(Type type)
        {
            var field = type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static);
            if (field == null || !field.IsLiteral || field.FieldType != typeof(String))
            {
                return null;
            }

            return (String)field.GetRawConstantValue();
        }

        /// <summary>
        ///   Every bindable configuration leaf, keyed by its full colon-delimited configuration key.
        ///
        ///   <para>"Bindable" is taken from what <c>Microsoft.Extensions.Configuration</c> ACTUALLY
        ///   binds, not from the stricter guess that a leaf needs a setter, because the two differ in a
        ///   way that would silently hole this gate. The binder populates a nested block or a
        ///   collection through its GETTER, mutating the instance already there, so a
        ///   <c>public PrometheusOptions Prometheus { get; } = new();</c> block would be fully bindable
        ///   configuration that a setter-only sweep never demanded be catalogued. It ignores a get-only
        ///   SCALAR, which it has no way to write. Those three behaviours are pinned by
        ///   <see cref="TheLeafDefinition_MatchesWhatTheBinderActuallyBinds"/>.</para>
        /// </summary>
        private static Dictionary<String, PropertyInfo> ReflectLeaves()
        {
            return ReflectLeaves(out _);
        }

        /// <summary>
        ///   As <see cref="ReflectLeaves()"/>, and additionally reports every property the walk
        ///   classified as neither a block nor a leaf for a reason other than the one proven safe
        ///   (a get-only scalar). That list must stay empty: it is where a future option shape the
        ///   sweep does not understand shows up LOUDLY instead of vanishing.
        /// </summary>
        private static Dictionary<String, PropertyInfo> ReflectLeaves(out List<String> unclassified)
        {
            var leaves = new Dictionary<String, PropertyInfo>(StringComparer.Ordinal);
            unclassified = new List<String>();
            foreach (var type in OptionsClasses())
            {
                Walk(type, SectionNameOf(type), leaves, unclassified, new HashSet<Type>());
            }

            return leaves;
        }

        private static void Walk(Type type, String prefix, Dictionary<String, PropertyInfo> into,
            List<String> unclassified, HashSet<Type> path)
        {
            // A self-referencing options graph would otherwise recurse until the stack ran out.
            if (!path.Add(type))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue; // an indexer is not a configuration key
                }

                var key = prefix + ":" + property.Name;
                var readable = property.GetGetMethod() != null;
                var writable = property.GetSetMethod() != null;
                var kinds = ExpectedKinds(property.PropertyType);

                if (IsNestedBlock(property.PropertyType) && readable)
                {
                    Walk(property.PropertyType, key, into, unclassified, path);
                }
                else if (kinds != null && kinds.Contains(Fallen8SettingKind.Array) && readable)
                {
                    into.Add(key, property); // bound through the getter, setter or not
                }
                else if (kinds != null && writable)
                {
                    into.Add(key, property);
                }
                else if (kinds != null && readable)
                {
                    // A get-only scalar: the binder cannot write it, so it is not configuration.
                }
                else
                {
                    unclassified.Add(key + " (" + property.PropertyType.Name + ", "
                        + (readable ? "get" : "no get") + "/" + (writable ? "set" : "no set") + ")");
                }
            }

            path.Remove(type);
        }

        /// <summary>
        ///   Whether a property is a nested configuration BLOCK (recurse into it) rather than a leaf.
        ///   A collection is a leaf, not a block: its elements are values, and no collection is
        ///   writable anyway (spec 4.3.5).
        /// </summary>
        private static Boolean IsNestedBlock(Type type)
        {
            return type.IsClass
                && type != typeof(String)
                && !typeof(IEnumerable).IsAssignableFrom(type)
                && type.Assembly == _apiApp;
        }

        /// <summary>
        ///   The kinds a leaf of this CLR type may legitimately be catalogued as. A closed-set string
        ///   is catalogued <see cref="Fallen8SettingKind.Enum"/>, so a <c>String</c> leaf accepts
        ///   either. Returns null for a type this mapping does not know, which fails the test rather
        ///   than guessing: a new exotic option type must be a decision, not a default.
        /// </summary>
        private static Fallen8SettingKind[] ExpectedKinds(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(Boolean))
            {
                return new[] { Fallen8SettingKind.Bool };
            }

            if (underlying == typeof(Int32) || underlying == typeof(Int64))
            {
                return new[] { Fallen8SettingKind.Int };
            }

            if (underlying == typeof(Double) || underlying == typeof(Single))
            {
                return new[] { Fallen8SettingKind.Double };
            }

            if (underlying.IsEnum)
            {
                return new[] { Fallen8SettingKind.Enum };
            }

            if (underlying == typeof(String))
            {
                return new[] { Fallen8SettingKind.String, Fallen8SettingKind.Enum };
            }

            if (typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                return new[] { Fallen8SettingKind.Array };
            }

            return null;
        }

        private static void AssertNoViolations(List<String> violations, String rule)
        {
            Assert.AreEqual(0, violations.Count, rule + " - violations:\n" + String.Join("\n", violations));
        }

        #endregion

        #region completeness, both directions

        /// <summary>
        ///   The tripwire under every other test here: if the options-class discovery ever matched
        ///   nothing (a renamed namespace, a SectionName turned into a property), the sweeps below
        ///   would pass over an empty surface. Asserted as a SUPERSET so a new options class needs no
        ///   edit here - it only has to keep binding a section.
        /// </summary>
        [TestMethod]
        public void TheOptionsClassSweep_FindsEverySectionTheAppBinds()
        {
            var sections = OptionsClasses().Select(SectionNameOf).ToList();

            var expected = new[]
            {
                "Fallen8:Analytics", "Fallen8:BulkIO", "Fallen8:ChangeFeed", "Fallen8:Chat",
                "Fallen8:Durability", "Fallen8:Embedding", "Fallen8:Identity", "Fallen8:Ingestion",
                "Fallen8:Integrations", "Fallen8:Metadata", "Fallen8:Namespaces", "Fallen8:Nlp",
                "Fallen8:Observability", "Fallen8:Plugins", "Fallen8:Security", "Fallen8:StoredQueries"
            };

            CollectionAssert.IsSubsetOf(expected, sections,
                "the setting catalog's governance sweep must see every bound Fallen8 section");
            Assert.IsTrue(ReflectLeaves().Count > 50,
                "the leaf sweep collapsed: " + ReflectLeaves().Count + " leaves found");

            // The write path's section map is written out rather than reflected over (it cannot be
            // annotated for trimming), so its completeness is enforced here instead: a new options class
            // that never reaches Fallen8OptionsSections would silently skip trial-binding, and trial
            // binding is what catches a value the catalog's domain checks cannot see.
            var mapped = Fallen8OptionsSections.All;
            var missing = OptionsClasses()
                .Where(type => !mapped.ContainsKey(SectionNameOf(type)))
                .Select(type => "NOT MAPPED: " + SectionNameOf(type) + " (" + type.Name
                    + ") is missing from Fallen8OptionsSections")
                .Concat(mapped
                    .Where(pair => !OptionsClasses().Any(type => type == pair.Value))
                    .Select(pair => "STALE MAPPING: " + pair.Key + " maps to " + pair.Value.Name
                        + ", which no longer binds a section"))
                .ToList();

            AssertNoViolations(missing, "every bound section maps to the options class that binds it");
        }

        /// <summary>
        ///   Pins the three binder behaviours <see cref="ReflectLeaves()"/> depends on, measured rather
        ///   than assumed. If a future .NET release changes any of them, the sweep's leaf definition is
        ///   wrong and this test says so directly instead of the gate quietly missing keys.
        /// </summary>
        [TestMethod]
        public void TheLeafDefinition_MatchesWhatTheBinderActuallyBinds()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["BlockWithoutSetter:Value"] = "7",
                    ["BlockWithSetter:Value"] = "8",
                    ["CollectionWithoutSetter:0"] = "added",
                    ["ScalarWithoutSetter"] = "changed",
                    ["ScalarWithSetter"] = "9"
                })
                .Build();

            var probe = new BinderProbe();
            configuration.Bind(probe);

            Assert.AreEqual(7, probe.BlockWithoutSetter.Value,
                "a get-only nested block IS bound through its getter, so the sweep must recurse into one");
            Assert.AreEqual(1, probe.CollectionWithoutSetter.Count,
                "a get-only collection IS bound through its getter, so the sweep must treat one as a leaf");
            Assert.AreEqual("untouched", probe.ScalarWithoutSetter,
                "a get-only scalar is NOT bound, so the sweep may skip one");

            Assert.AreEqual(8, probe.BlockWithSetter.Value);
            Assert.AreEqual(9, probe.ScalarWithSetter);
        }

        /// <summary>
        ///   The sweep must understand the shape of every property it meets. A property it can neither
        ///   catalogue nor safely ignore is the one way a new option could still slip past the gate, so
        ///   it fails here by name rather than disappearing.
        /// </summary>
        [TestMethod]
        public void TheLeafSweep_UnderstandsEveryPropertyShapeItMeets()
        {
            ReflectLeaves(out var unclassified);

            AssertNoViolations(unclassified,
                "every options property is a nested block, a catalogued leaf, or a get-only scalar the binder ignores");
        }

        /// <summary>Shapes for <see cref="TheLeafDefinition_MatchesWhatTheBinderActuallyBinds"/>.</summary>
        private sealed class BinderProbe
        {
            public sealed class Block
            {
                public Int32 Value { get; set; }
            }

            public Block BlockWithoutSetter { get; } = new Block();

            public Block BlockWithSetter { get; set; } = new Block();

            public List<String> CollectionWithoutSetter { get; } = new List<String>();

            public String ScalarWithoutSetter { get; } = "untouched";

            public Int32 ScalarWithSetter { get; set; }
        }

        /// <summary>
        ///   Spec 4.1.1: every bindable leaf is catalogued, and every catalogued key still exists.
        /// </summary>
        [TestMethod]
        public void EveryConfigurationLeaf_IsCatalogued()
        {
            var reflected = ReflectLeaves();
            var catalogued = Fallen8SettingCatalog.Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

            var violations = reflected.Keys
                .Where(key => !catalogued.Contains(key))
                .Select(key => "NOT CATALOGUED: " + key + " (classify it, or exclude it with a rule and a reason)")
                .Concat(catalogued
                    .Where(key => !reflected.ContainsKey(key))
                    .Select(key => "STALE ENTRY: " + key + " is catalogued but no options property binds it"))
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            AssertNoViolations(violations,
                "the setting catalog and the bound options surface must match exactly (spec 4.1.1)");
        }

        /// <summary>
        ///   Spec 4.1.1: the totals are DERIVED, never arithmetic. There is deliberately no
        ///   duplicate-key hunt here: the catalog's own key index rejects a repeated key while the type
        ///   initialises, so a duplicate cannot reach a test at all.
        /// </summary>
        [TestMethod]
        public void TheCatalogTotals_AreDerivedFromReflection()
        {
            var entries = Fallen8SettingCatalog.Entries;
            var live = entries.Count(entry => entry.Tier == Fallen8SettingTier.Live);
            var restart = entries.Count(entry => entry.Tier == Fallen8SettingTier.Restart);
            var notWritable = entries.Count(entry => entry.Tier == Fallen8SettingTier.NotWritable);

            Assert.AreEqual(ReflectLeaves().Count, live + restart + notWritable,
                "live + restart + notWritable must equal the reflected leaf count");
        }

        #endregion

        #region the data cannot lie about itself

        /// <summary>
        ///   Spec 4.1.2: "declared live" is mechanically checkable, so it can never be aspirational.
        ///   The converse matters just as much: a delegate left on a key that was demoted out of the
        ///   live tier would never run again, and nothing else would notice.
        /// </summary>
        [TestMethod]
        public void EveryLiveEntry_HasAnApplyDelegate_AndNoOtherEntryCarriesOne()
        {
            var violations = new List<String>();
            foreach (var entry in Fallen8SettingCatalog.Entries)
            {
                if (entry.Tier == Fallen8SettingTier.Live && entry.ApplyNow == null)
                {
                    violations.Add("LIVE WITHOUT APPLY: " + entry.Key
                        + " claims it takes effect in this process but has no way to do it");
                }

                if (entry.Tier != Fallen8SettingTier.Live && entry.ApplyNow != null)
                {
                    violations.Add("APPLY WITHOUT LIVE: " + entry.Key
                        + " carries an apply delegate that can never run");
                }
            }

            AssertNoViolations(violations, "a live tier and an apply delegate imply each other (spec 4.1.2)");
        }

        /// <summary>
        ///   The catalogued kind must match the real CLR type of the property behind it. This is what
        ///   keeps the write path's parsing honest: a key catalogued Int over a String property would
        ///   accept a number and then bind it as text.
        /// </summary>
        [TestMethod]
        public void EveryCataloguedKind_MatchesTheReflectedPropertyType()
        {
            var reflected = ReflectLeaves();
            var violations = new List<String>();

            foreach (var entry in Fallen8SettingCatalog.Entries)
            {
                if (!reflected.TryGetValue(entry.Key, out var property))
                {
                    continue; // reported by EveryConfigurationLeaf_IsCatalogued
                }

                var expected = ExpectedKinds(property.PropertyType);
                if (expected == null)
                {
                    violations.Add("UNMAPPED TYPE: " + entry.Key + " binds " + property.PropertyType.Name
                        + ", which the catalog has no kind for");
                }
                else if (!expected.Contains(entry.Kind))
                {
                    violations.Add("WRONG KIND: " + entry.Key + " is catalogued " + entry.Kind
                        + " but binds " + property.PropertyType.Name);
                }
            }

            AssertNoViolations(violations, "every catalogued kind matches the property it describes");
        }

        /// <summary>Spec 4.3.5: a collection leaf can never be written, so it is never writable.</summary>
        [TestMethod]
        public void EveryCollectionLeaf_IsNotWritable()
        {
            var violations = new List<String>();
            foreach (var leaf in ReflectLeaves())
            {
                if (ExpectedKinds(leaf.Value.PropertyType)?.Contains(Fallen8SettingKind.Array) != true)
                {
                    continue;
                }

                if (!Fallen8SettingCatalog.TryGet(leaf.Key, out var entry))
                {
                    continue; // reported by EveryConfigurationLeaf_IsCatalogued
                }

                if (entry.IsWritable)
                {
                    violations.Add("WRITABLE COLLECTION: " + leaf.Key
                        + " - providers merge arrays index-wise, so an override could never shrink one");
                }
            }

            AssertNoViolations(violations, "no collection leaf is writable (spec 4.3.5)");
        }

        /// <summary>
        ///   Every exclusion names one of the rules the spec actually defines. A typo would silently
        ///   drop a key out of the docs page's rule grouping, and citing R7 would mean a dead knob had
        ///   been catalogued instead of implemented or deleted.
        /// </summary>
        [TestMethod]
        public void EveryExclusion_NamesAKnownRuleAndAReason()
        {
            var violations = new List<String>();
            foreach (var entry in Fallen8SettingCatalog.Entries.Where(e => !e.IsWritable))
            {
                if (String.Equals(entry.Rule, "R7", StringComparison.Ordinal))
                {
                    violations.Add("CATALOGUED UNDER R7: " + entry.Key
                        + " - a knob nothing reads must be implemented or deleted, never published");
                }
                else if (!_knownRules.Contains(entry.Rule, StringComparer.Ordinal))
                {
                    violations.Add("UNKNOWN RULE: " + entry.Key + " cites '" + entry.Rule + "'");
                }

                if (String.IsNullOrWhiteSpace(entry.Reason))
                {
                    violations.Add("NO REASON: " + entry.Key);
                }
            }

            AssertNoViolations(violations, "every never-writable key cites a known rule and gives a reason");
        }

        /// <summary>
        ///   A declared bound must be reachable through the property behind it. A maximum wider than the
        ///   property's own type is a typo that would let the write path accept a value binding then
        ///   rejects, turning a 400 into a confusing 500 one phase later.
        /// </summary>
        [TestMethod]
        public void EveryDeclaredBound_FitsThePropertyBehindIt()
        {
            var reflected = ReflectLeaves();
            var violations = new List<String>();

            foreach (var entry in Fallen8SettingCatalog.Entries)
            {
                if (!reflected.TryGetValue(entry.Key, out var property)
                    || (!entry.Minimum.HasValue && !entry.Maximum.HasValue))
                {
                    continue;
                }

                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type != typeof(Int32))
                {
                    continue; // Int64 and Double span every bound the catalog declares
                }

                if (entry.Maximum.HasValue && entry.Maximum.Value > Int32.MaxValue)
                {
                    violations.Add("MAXIMUM OUT OF RANGE: " + entry.Key + " declares " + entry.Maximum.Value
                        + " over an Int32 property");
                }

                if (entry.Minimum.HasValue && entry.Minimum.Value < Int32.MinValue)
                {
                    violations.Add("MINIMUM OUT OF RANGE: " + entry.Key + " declares " + entry.Minimum.Value
                        + " over an Int32 property");
                }
            }

            AssertNoViolations(violations, "every declared bound is representable by the property it bounds");
        }

        #endregion

        #region the blanket rules, checked mechanically

        /// <summary>
        ///   R1 is a blanket rule, which is exactly what makes it testable in both directions: nothing
        ///   under <c>Fallen8:Security</c> is writable, and nothing outside it claims R1. The section
        ///   holds the lockout generators, the code-execution switch, the CORS perimeter and the only
        ///   brake on the sensitive endpoints, so a single reviewable rule beats per-knob carve-outs.
        /// </summary>
        [TestMethod]
        public void EverySecurityKey_IsNotWritableUnderR1()
        {
            AssertBlanketRule("Fallen8:Security:", "R1");
        }

        /// <summary>
        ///   R6: the identity values are baked into the OpenTelemetry resource attributes at boot, so a
        ///   write could only falsify the reported identity of a process whose telemetry already went
        ///   out under the real one.
        /// </summary>
        [TestMethod]
        public void EveryIdentityKey_IsNotWritableUnderR6()
        {
            AssertBlanketRule("Fallen8:Identity:", "R6");
        }

        /// <summary>
        ///   Every key spec section 4.7 names explicitly, pinned against the rule it names it under.
        ///   This list is deliberately hand-authored and deliberately duplicates the catalog: it is the
        ///   SPEC's claim, so a future edit that quietly makes the API key or a storage path writable
        ///   fails here even though the catalog and the reflection would still agree with each other.
        ///   R1 and R6 are covered mechanically above, so only the enumerated rules appear.
        /// </summary>
        [TestMethod]
        public void EveryKeyTheSpecNames_IsNotWritableUnderThatRule()
        {
            var named = new Dictionary<String, String>(StringComparer.Ordinal)
            {
                // R2 - on-disk state.
                ["Fallen8:Durability:StorageDirectory"] = "R2",
                ["Fallen8:Durability:WalPath"] = "R2",
                ["Fallen8:Durability:CheckpointBaseName"] = "R2",
                ["Fallen8:Durability:Volatile"] = "R2",
                ["Fallen8:Metadata:Directory"] = "R2",

                // R3 - stored-data identity: the embedding stamp, the embedding function, index identity.
                ["Fallen8:Embedding:ModelName"] = "R3",
                ["Fallen8:Embedding:ModelVersion"] = "R3",
                ["Fallen8:Embedding:Dimension"] = "R3",
                ["Fallen8:Embedding:IntendedMetric"] = "R3",
                ["Fallen8:Embedding:Backend"] = "R3",
                ["Fallen8:Embedding:Ollama:Model"] = "R3",
                ["Fallen8:Embedding:Onnx:MaxTokens"] = "R3",
                ["Fallen8:Embedding:Onnx:Pooling"] = "R3",
                ["Fallen8:Embedding:Onnx:Normalize"] = "R3",
                ["Fallen8:Ingestion:EmbeddingName"] = "R3",
                ["Fallen8:Ingestion:VectorIndexId"] = "R3",
                ["Fallen8:Ingestion:FulltextIndexId"] = "R3",
                ["Fallen8:Ingestion:EntityIndexId"] = "R3",

                ["Fallen8:Embedding:Nahil:Model"] = "R3",

                // R4 - every URL the server dials.
                ["Fallen8:Embedding:Ollama:Endpoint"] = "R4",
                ["Fallen8:Chat:Ollama:Endpoint"] = "R4",
                ["Fallen8:Chat:Nahil:Endpoint"] = "R4",
                ["Fallen8:Embedding:Nahil:Endpoint"] = "R4",
                ["Fallen8:Ingestion:Docling:Endpoint"] = "R4",
                ["Fallen8:Nlp:Endpoint"] = "R4",
                ["Fallen8:Observability:Otlp:Endpoint"] = "R4",
                ["Fallen8:Integrations:Endpoint"] = "R4",

                // R5 - capability flags.
                ["Fallen8:Embedding:Enabled"] = "R5",
                ["Fallen8:Chat:Enabled"] = "R5",
                ["Fallen8:Ingestion:Enabled"] = "R5",
                ["Fallen8:Integrations:Enabled"] = "R5",
                ["Fallen8:Observability:Prometheus:Enabled"] = "R5",
                ["Fallen8:Observability:Prometheus:RequireApiKey"] = "R5",

                // R8 - no credential this server PRESENTS. Pinned here because it is the whole
                // security property of the gateway backend: writable it redirects someone's metered
                // spend, published it hands the key to an anonymous reader of GET /config.
                ["Fallen8:Chat:Nahil:ApiKey"] = "R8",
                ["Fallen8:Embedding:Nahil:ApiKey"] = "R8"
            };

            var violations = new List<String>();
            foreach (var expected in named)
            {
                if (!Fallen8SettingCatalog.TryGet(expected.Key, out var entry))
                {
                    violations.Add("MISSING: " + expected.Key + " is named by the spec but is not catalogued");
                    continue;
                }

                if (entry.IsWritable)
                {
                    violations.Add("WRITABLE: " + expected.Key + " is writable but the spec excludes it under "
                        + expected.Value);
                }
                else if (!String.Equals(entry.Rule, expected.Value, StringComparison.Ordinal))
                {
                    violations.Add("WRONG RULE: " + expected.Key + " cites " + entry.Rule
                        + " but the spec names it under " + expected.Value);
                }
            }

            AssertNoViolations(violations, "every key spec section 4.7 names stays never-writable under its rule");
        }

        private static void AssertBlanketRule(String prefix, String rule)
        {
            var violations = new List<String>();

            foreach (var entry in Fallen8SettingCatalog.Entries)
            {
                var inSection = entry.Key.StartsWith(prefix, StringComparison.Ordinal);

                if (inSection && entry.IsWritable)
                {
                    violations.Add("WRITABLE: " + entry.Key + " is under " + prefix
                        + ", which rule " + rule + " excludes without carve-outs");
                }

                if (inSection && !entry.IsWritable && !String.Equals(entry.Rule, rule, StringComparison.Ordinal))
                {
                    violations.Add("WRONG RULE: " + entry.Key + " cites " + entry.Rule + ", not " + rule);
                }

                if (!inSection && String.Equals(entry.Rule, rule, StringComparison.Ordinal))
                {
                    violations.Add("RULE ESCAPED ITS SECTION: " + entry.Key + " cites " + rule
                        + " but is not under " + prefix);
                }
            }

            AssertNoViolations(violations, "rule " + rule + " covers exactly the keys under " + prefix);
        }

        #endregion

        #region the entry contract - invalid entries are unconstructable

        /// <summary>
        ///   The entry type's claim is that a contradictory entry cannot be built, which is what lets
        ///   the catalog carry no defensive checks of its own. Every guard is exercised here, because a
        ///   guard nothing tests is a guard that can be removed by accident.
        /// </summary>
        [TestMethod]
        public void TheEntryFactories_RefuseEveryContradictoryEntry()
        {
            // A never-writable key must justify itself.
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.NotWritable("K", Fallen8SettingKind.Bool, rule: " ", reason: "r"));
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.NotWritable("K", Fallen8SettingKind.Bool, rule: "R1", reason: " "));

            // A live key must be able to apply itself (spec 4.1.2).
            Assert.ThrowsException<ArgumentNullException>(() =>
                Fallen8SettingEntry.Live("K", Fallen8SettingKind.Bool, applyNow: null));
            Assert.ThrowsException<ArgumentNullException>(() =>
                Fallen8SettingEntry.LiveForNewWork("K", Fallen8SettingKind.Int, applyNow: null));

            // A key needs a key.
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart(null, Fallen8SettingKind.Bool));
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("  ", Fallen8SettingKind.Bool));

            // No collection is ever writable (spec 4.3.5).
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Array));

            // Bounds belong to numbers, and must not be inverted.
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Bool, minimum: 1));
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.String, maximum: 5));
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Int, minimum: 10, maximum: 9));

            // An enum key states its accepted values; nothing else may.
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Enum));
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.String, allowedValues: new[] { "a" }));
            Assert.ThrowsException<ArgumentException>(() =>
                Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Enum, allowedValues: new[] { "a", "a" }));
        }

        /// <summary>
        ///   The tier is derived from the apply mode rather than stored, so this pins the mapping in
        ///   both directions, including for the two live factories that phase 1 deliberately never
        ///   calls. Phase 4 promotes keys through them, and it must find them already trustworthy.
        /// </summary>
        [TestMethod]
        public void TheEntryTiers_AreDerivedFromTheApplyMode()
        {
            var restart = Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Int, minimum: 1, maximum: 2);
            Assert.AreEqual(Fallen8SettingTier.Restart, restart.Tier);
            Assert.AreEqual(Fallen8SettingApplyMode.Restart, restart.ApplyMode);
            Assert.IsTrue(restart.IsWritable);
            Assert.IsNull(restart.ApplyNow, "a restart key has nothing to apply now");
            Assert.IsNull(restart.Rule);
            Assert.IsNull(restart.Reason);
            Assert.AreEqual(0, restart.AllowedValues.Count);

            var excluded = Fallen8SettingEntry.NotWritable("K", Fallen8SettingKind.String, "R1", "because");
            Assert.AreEqual(Fallen8SettingTier.NotWritable, excluded.Tier);
            Assert.AreEqual(Fallen8SettingApplyMode.Never, excluded.ApplyMode);
            Assert.IsFalse(excluded.IsWritable);
            Assert.IsNull(excluded.ApplyNow);
            Assert.AreEqual("R1", excluded.Rule);
            Assert.IsNull(excluded.Minimum);
            Assert.IsNull(excluded.Maximum);

            var applied = 0;
            var live = Fallen8SettingEntry.Live("K", Fallen8SettingKind.Bool, _ => applied++);
            Assert.AreEqual(Fallen8SettingTier.Live, live.Tier);
            Assert.AreEqual(Fallen8SettingApplyMode.Live, live.ApplyMode);
            Assert.IsTrue(live.IsWritable);
            Assert.IsNotNull(live.ApplyNow);
            live.ApplyNow(null);
            Assert.AreEqual(1, applied, "the delegate the catalog stored is the one that runs");

            var newWork = Fallen8SettingEntry.LiveForNewWork("K", Fallen8SettingKind.Int, _ => { }, minimum: 1);
            Assert.AreEqual(Fallen8SettingTier.Live, newWork.Tier,
                "new-work-only is still the live tier; the apply mode is what narrows the promise");
            Assert.AreEqual(Fallen8SettingApplyMode.LiveForNewWork, newWork.ApplyMode);
        }

        /// <summary>
        ///   A caller holding the published collections must not be able to rewrite them, which a plain
        ///   <c>IReadOnlyList</c> over a <c>List</c> would allow through a downcast.
        /// </summary>
        [TestMethod]
        public void ThePublishedCollections_CannotBeMutatedByACaller()
        {
            Assert.IsFalse(Fallen8SettingCatalog.Entries is ICollection<Fallen8SettingEntry> mutableEntries
                && !mutableEntries.IsReadOnly, "the catalog's entry list must not be castable to a mutable list");

            var entry = Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Enum,
                allowedValues: new[] { "a", "b" });
            Assert.IsFalse(entry.AllowedValues is ICollection<String> mutableValues && !mutableValues.IsReadOnly,
                "an entry's accepted values must not be castable to a mutable list");

            // The array handed in is copied, so mutating it afterwards cannot change the entry.
            var source = new[] { "a", "b" };
            var copied = Fallen8SettingEntry.Restart("K", Fallen8SettingKind.Enum, allowedValues: source);
            source[0] = "changed";
            Assert.AreEqual("a", copied.AllowedValues[0]);
        }

        /// <summary>
        ///   Configuration keys are case-insensitive, so the catalog's lookup must be too: a stricter
        ///   index would report "not catalogued" for a key the binder binds happily.
        /// </summary>
        [TestMethod]
        public void TheCatalogLookup_MatchesConfigurationsOwnCaseInsensitivity()
        {
            Assert.IsTrue(Fallen8SettingCatalog.TryGet("Fallen8:Plugins:MaxCount", out var exact));
            Assert.IsTrue(Fallen8SettingCatalog.TryGet("fallen8:plugins:maxcount", out var lowered));
            Assert.AreSame(exact, lowered);

            Assert.IsFalse(Fallen8SettingCatalog.TryGet("Fallen8:Plugins:Nope", out var missing));
            Assert.IsNull(missing);
            Assert.IsFalse(Fallen8SettingCatalog.TryGet(null, out var forNull));
            Assert.IsNull(forNull);
        }

        #endregion

        #region R7 - the dead knobs are gone and stay gone

        /// <summary>
        ///   R7, following the precedent of the removed <c>MaxSensitiveRequestBodyBytes</c> knob
        ///   (<see cref="SecurityOptions_ExposeNoRequestBodyKnob"/>): a property that is bound and
        ///   documented but read by no product code advertises a control the app does not implement.
        ///   Phase 1 deleted the two such properties it found rather than catalogue them, because a
        ///   never-writable entry still publishes the key and would keep advertising it.
        ///
        ///   <c>Fallen8:Security:AllowRemoteAccess</c> promised a loopback posture nothing enforced.
        ///   Note this is the apiApp's knob only: <c>Mcp:Security:AllowRemoteAccess</c> is a different
        ///   options class in the MCP deployable and IS enforced there.
        /// </summary>
        [TestMethod]
        public void SecurityOptions_ExposeNoBindAddressKnob()
        {
            var properties = typeof(Fallen8SecurityOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToList();

            CollectionAssert.DoesNotContain(properties, "AllowRemoteAccess",
                "the flag promised a loopback guarantee no product code enforced and was removed");

            // Guard against the same lie returning under a new name: the bind address is not a
            // Fallen-8 setting at all, it is ASPNETCORE_URLS / Kestrel.
            foreach (var name in properties)
            {
                Assert.IsFalse(name.IndexOf("RemoteAccess", StringComparison.OrdinalIgnoreCase) >= 0
                               || name.IndexOf("BindAddress", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Fallen8SecurityOptions must not advertise a bind-address control it does not enforce: " + name);
            }

            // The neighbouring knobs that ARE read stay untouched.
            Assert.AreEqual(30, new Fallen8SecurityOptions().SensitiveRateLimitPermitPerWindow);
            Assert.AreEqual(10000, new Fallen8SecurityOptions().BenchmarkMaxIterations);
        }

        /// <summary>
        ///   R7 again, for the knob whose removal set the precedent: <c>MaxSensitiveRequestBodyBytes</c>
        ///   was bound but read nowhere while its XML doc promised a 413, so an operator could believe
        ///   they had raised or tightened the code-endpoint body cap. The cap that is actually in force
        ///   is a compile-time attribute, pinned by <see cref="SensitiveRequestBodyLimitTest"/>.
        /// </summary>
        [TestMethod]
        public void SecurityOptions_ExposeNoRequestBodyKnob()
        {
            var properties = typeof(Fallen8SecurityOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToList();

            CollectionAssert.DoesNotContain(properties, "MaxSensitiveRequestBodyBytes",
                "the option promised a 413 it never enforced and was removed");

            // Guard against the same lie coming back under a new name: the body cap is not
            // configurable at all, it is the attribute asserted in SensitiveRequestBodyLimitTest.
            foreach (var name in properties)
            {
                Assert.IsFalse(name.IndexOf("RequestBody", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               name.IndexOf("BodyBytes", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Fallen8SecurityOptions must not advertise a request-body cap it cannot enforce: " + name);
            }

            // The knobs that ARE read stay untouched. (The permit-per-window default is asserted by
            // SecurityOptions_ExposeNoBindAddressKnob above, verbatim, so it is not repeated here.)
            Assert.AreEqual(10, new Fallen8SecurityOptions().RateLimitWindowSeconds);
        }

        /// <summary>
        ///   R7: <c>Fallen8:Nlp:MaxBatchSize</c> claimed to size the enrich request batch while
        ///   <c>NlpClient</c> posts every chunk of a document as one request, so nothing read it. The
        ///   limit it was meant to bound is real (the sidecar 413s above 512 items while this path can
        ///   post up to 2000), and honouring it means batching the call, which is a behaviour change
        ///   recorded as a follow-up rather than made here. See the comment in the options class.
        /// </summary>
        [TestMethod]
        public void NlpOptions_ExposeNoBatchSizeKnob()
        {
            var properties = typeof(Fallen8NlpOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToList();

            CollectionAssert.DoesNotContain(properties, "MaxBatchSize",
                "the knob claimed to size a batch the enrich path never splits");

            // The per-chunk ceilings that ARE read stay untouched.
            Assert.AreEqual(20000, new Fallen8NlpOptions().MaxCharsPerChunk);
            Assert.AreEqual(32, new Fallen8NlpOptions().MaxEntitiesPerChunk);
            Assert.AreEqual(32, new Fallen8NlpOptions().MaxKeyTermsPerChunk);
        }

        /// <summary>
        ///   An existing appsettings.json or environment that still sets a removed key keeps binding:
        ///   configuration binding ignores unknown keys, so the key simply has no effect, which is
        ///   what it always had.
        /// </summary>
        [TestMethod]
        public void ConfigurationStillCarryingARemovedKey_BindsWithoutError()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["Fallen8:Security:AllowRemoteAccess"] = "true",
                    ["Fallen8:Security:ApiKeyHeader"] = "X-Custom-Key",
                    ["Fallen8:Nlp:MaxBatchSize"] = "512",
                    ["Fallen8:Nlp:MaxEntitiesPerChunk"] = "7"
                })
                .Build();

            var security = new Fallen8SecurityOptions();
            configuration.GetSection(Fallen8SecurityOptions.SectionName).Bind(security);
            Assert.AreEqual("X-Custom-Key", security.ApiKeyHeader, "the neighbouring security key still binds");

            var nlp = new Fallen8NlpOptions();
            configuration.GetSection(Fallen8NlpOptions.SectionName).Bind(nlp);
            Assert.AreEqual(7, nlp.MaxEntitiesPerChunk, "the neighbouring NLP key still binds");
        }

        #endregion

        #region option values that correct themselves rather than trust configuration

        /// <summary>
        ///   <c>Fallen8:Security:BenchmarkMaxIterations</c> is the ceiling on
        ///   <c>GET /ns/{ns}/benchmark</c>, which accepted any positive iteration count although one
        ///   pass saturates every core. The property is the ONE home of that ceiling's value, so a 0
        ///   or a negative in configuration resets to the default instead of rejecting every request.
        ///   What the endpoint does with the ceiling is pinned by <see cref="BenchmarkEndpointTest"/>.
        /// </summary>
        [TestMethod]
        public void BenchmarkCeiling_DefaultsToTenThousand_AndRejectsNonPositiveConfiguration()
        {
            // Same guard as the analytics options: a 0 or negative in configuration would otherwise
            // reject every request, so it resets to the default. (The bare default is asserted
            // verbatim by SecurityOptions_ExposeNoBindAddressKnob, so it is not repeated here; the
            // reset cases below pin the same value from the other side.)
            Assert.AreEqual(10000, new Fallen8SecurityOptions { BenchmarkMaxIterations = 0 }.BenchmarkMaxIterations);
            Assert.AreEqual(10000, new Fallen8SecurityOptions { BenchmarkMaxIterations = -5 }.BenchmarkMaxIterations);
            Assert.AreEqual(7, new Fallen8SecurityOptions { BenchmarkMaxIterations = 7 }.BenchmarkMaxIterations);
        }

        #endregion
    }
}
