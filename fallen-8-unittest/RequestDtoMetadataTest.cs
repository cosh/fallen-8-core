// MIT License
//
// RequestDtoMetadataTest.cs
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
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Service;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins two pieces of request-DTO metadata that a client generated from the published schema
    /// believes: the <c>[DefaultValue]</c> a DTO publishes must be the default the server really
    /// applies when the field is omitted, and the <c>pluginType</c> a DTO advertises must be a name
    /// the product can resolve. Both lied to such a client once (audit defects B29 and B42).
    /// </summary>
    /// <remarks>
    /// <para>
    /// B29: <c>PathSpecification.MaxPathWeight</c> carried <c>[DefaultValue(100.0)]</c> while its
    /// runtime initialiser is <c>Double.MaxValue</c> (unbounded). <c>[DefaultValue]</c> is schema
    /// metadata only - System.Text.Json never applies it - so the document advertised
    /// <c>"default": 100</c> and a generated client that materialises schema defaults silently pruned
    /// every DIJKSTRA path heavier than 100. The sweep below generalises the rule that produced the
    /// bug: a PUBLISHED default must equal what the server actually applies when the field is omitted.
    /// </para>
    /// <para>
    /// B42: <c>PluginSpecification</c> is bound by both <c>POST /index</c> (name resolved as an
    /// <see cref="IIndex"/>) and <c>POST /service</c> (name resolved as an <see cref="IService"/>),
    /// and its advertised <c>pluginType</c> is an index name. The test pins that the advertised name
    /// really resolves to a shipped index plugin (so the documented body works where it is claimed to)
    /// and that it does NOT resolve as a service plugin - the asymmetry the DTO's own documentation
    /// now spells out, because no service plugin ships at all.
    /// </para>
    /// </remarks>
    [TestClass]
    public class RequestDtoMetadataTest
    {
        /// <summary>
        /// B29 proper: the weight ceiling must publish no schema default, and omitting it must leave
        /// the traversal unbounded.
        /// </summary>
        [TestMethod]
        public void PathSpecification_UnboundedKnobs_PublishNoSchemaDefault()
        {
            // Arrange
            var weightProperty = typeof(PathSpecification).GetProperty(nameof(PathSpecification.MaxPathWeight));
            var budgetProperty = typeof(PathSpecification).GetProperty(nameof(PathSpecification.TimeBudgetSeconds));
            Assert.IsNotNull(weightProperty, "PathSpecification must keep a MaxPathWeight property.");
            Assert.IsNotNull(budgetProperty, "PathSpecification must keep a TimeBudgetSeconds property.");

            // Act
            var specification = new PathSpecification();

            // Assert - the schema must stay silent about a default it cannot honour.
            Assert.IsNull(weightProperty.GetCustomAttribute<DefaultValueAttribute>(),
                "B29: maxPathWeight must publish NO default. The runtime default is Double.MaxValue " +
                "(unbounded); a published number would make a generated client prune heavier paths " +
                "the server would have returned.");
            Assert.IsNull(budgetProperty.GetCustomAttribute<DefaultValueAttribute>(),
                "An omitted timeBudgetSeconds means UNBOUNDED, so it must not publish a default either.");

            // Assert - and the runtime really is unbounded, well past the 100 that used to be published.
            Assert.AreEqual(Double.MaxValue, specification.MaxPathWeight,
                "An omitted maxPathWeight must stay the unbounded sentinel the engine's " +
                "ShortestPathDefinition uses.");
            Assert.IsTrue(specification.MaxPathWeight > 100.0d,
                "The applied default must be far above the 100 the schema used to publish, so a " +
                "DIJKSTRA path of weight 1000 passes the bound the server really uses.");
            Assert.IsNull(specification.TimeBudgetSeconds,
                "An omitted timeBudgetSeconds must stay null (unbounded).");

            // Assert - the schema keys the knob under the name clients send.
            Assert.AreEqual("maxPathWeight", weightProperty.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name,
                "The wire name of the weight ceiling must not drift.");
        }

        /// <summary>
        /// The generalised rule behind B29 for the whole path DTO: every property that DOES publish a
        /// default must publish the value the server applies when the field is omitted. The sibling
        /// knobs are spot-checked so "fixing" B29 by deleting all defaults would fail here.
        /// </summary>
        [TestMethod]
        public void PathSpecification_PublishedDefaults_MatchTheRuntimeInitialisers()
        {
            // Arrange & Act
            var specification = new PathSpecification();

            // Assert
            AssertPublishedDefaultsMatchInitialisers(specification);

            AssertPublishedDefault(specification, nameof(PathSpecification.PathAlgorithmName), "BLS");
            AssertPublishedDefault(specification, nameof(PathSpecification.MaxDepth), (UInt16)7);
            AssertPublishedDefault(specification, nameof(PathSpecification.MaxResults), UInt16.MaxValue);
        }

        /// <summary>
        /// The same rule for the plugin DTO: its two published defaults must equal what the server
        /// uses when the caller omits the field.
        /// </summary>
        [TestMethod]
        public void PluginSpecification_PublishedDefaults_MatchTheRuntimeInitialisers()
        {
            // Arrange & Act
            var specification = new PluginSpecification();

            // Assert
            AssertPublishedDefaultsMatchInitialisers(specification);

            AssertPublishedDefault(specification, nameof(PluginSpecification.UniqueId), "indexService1");
            AssertPublishedDefault(specification, nameof(PluginSpecification.PluginType), "DictionaryIndex");
        }

        /// <summary>
        /// B42 proper: the advertised <c>pluginType</c> must be a name the product can actually
        /// resolve. It resolves as an index plugin (the <c>POST /index</c> flavour the DTO documents)
        /// and NOT as a service plugin, which is exactly why the DTO must not present it as a valid
        /// <c>POST /service</c> body.
        /// </summary>
        [TestMethod]
        public void PluginSpecification_AdvertisedPluginType_ResolvesAsAShippedIndexPluginOnly()
        {
            // Arrange - take the name from the DTO itself, so a future edit of example/default is checked.
            var advertised = new PluginSpecification().PluginType;
            var published = typeof(PluginSpecification)
                .GetProperty(nameof(PluginSpecification.PluginType))
                .GetCustomAttribute<DefaultValueAttribute>();
            Assert.IsNotNull(published, "pluginType is required and defaults at runtime, so it publishes that default.");
            Assert.AreEqual(advertised, published.Value,
                "The published pluginType default must be the value the DTO actually uses.");

            // Act
            IIndex indexPlugin;
            var resolvesAsIndex = PluginFactory.TryFindPlugin(out indexPlugin, advertised);

            IService servicePlugin;
            var resolvesAsService = PluginFactory.TryFindPlugin(out servicePlugin, advertised);

            // Assert
            using (indexPlugin)
            {
                Assert.IsTrue(resolvesAsIndex,
                    "B42: the advertised pluginType \"" + advertised + "\" must name a plugin the product " +
                    "ships, otherwise the documented POST /index body can never succeed.");
                Assert.AreEqual(advertised, indexPlugin.PluginName,
                    "The resolved index plugin must be the one the DTO names.");
            }

            using (servicePlugin)
            {
                Assert.IsFalse(resolvesAsService,
                    "B42: the advertised pluginType is an INDEX name and must not silently pass as a " +
                    "service plugin. If a plugin ever answers to this name as an IService, revisit the " +
                    "pluginType documentation - it currently states the example is invalid for POST /service.");
            }

            // Assert - and an unknown name resolves to nothing under either contract (the edge a caller hits
            // when they send one of the phantom names the docs used to suggest for POST /service).
            IService phantomService;
            Assert.IsFalse(PluginFactory.TryFindPlugin(out phantomService, "ImportService"),
                "No service plugin ships with Fallen-8, so a phantom name must not resolve.");
            Assert.IsNull(phantomService, "A failed resolution must hand back nothing to dispose.");

            IIndex nullNamed;
            Assert.IsFalse(PluginFactory.TryFindPlugin(out nullNamed, null),
                "A null plugin type must not resolve (the endpoints treat it as unknown, answering false).");
            Assert.IsNull(nullNamed, "A failed resolution must hand back nothing to dispose.");
        }

        /// <summary>
        /// Asserts the rule B29 broke, over every property of <paramref name="instance"/>: a
        /// <see cref="DefaultValueAttribute"/> is schema metadata only, so whatever it publishes must be
        /// what the property already holds on a freshly constructed DTO.
        /// </summary>
        private static void AssertPublishedDefaultsMatchInitialisers(Object instance)
        {
            var checkedProperties = new List<String>();

            foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var published = property.GetCustomAttribute<DefaultValueAttribute>();
                if (published == null)
                {
                    continue;
                }

                var runtimeValue = property.GetValue(instance);
                Assert.IsTrue(Equals(published.Value, runtimeValue),
                    instance.GetType().Name + "." + property.Name + " publishes the default " +
                    Describe(published.Value) + " but the server applies " + Describe(runtimeValue) +
                    " when the field is omitted. A published default that the DTO does not apply " +
                    "misleads every generated client (audit defect B29).");

                checkedProperties.Add(property.Name);
            }

            Assert.IsTrue(checkedProperties.Count > 0,
                instance.GetType().Name + " is expected to publish at least one default; if that changed " +
                "deliberately, update this test.");
        }

        /// <summary>
        /// Asserts one property publishes <paramref name="expected"/> AND initialises to it, so the
        /// sibling knobs cannot be "fixed" by dropping their (correct) defaults.
        /// </summary>
        private static void AssertPublishedDefault(Object instance, String propertyName, Object expected)
        {
            var property = instance.GetType().GetProperty(propertyName);
            Assert.IsNotNull(property, instance.GetType().Name + " must keep a " + propertyName + " property.");

            var published = property.GetCustomAttribute<DefaultValueAttribute>();
            Assert.IsNotNull(published, propertyName + " must keep publishing its default.");
            Assert.IsTrue(Equals(expected, published.Value),
                propertyName + " must publish " + Describe(expected) + ", not " + Describe(published.Value) + ".");
            Assert.IsTrue(Equals(expected, property.GetValue(instance)),
                propertyName + " must initialise to " + Describe(expected) + ".");
        }

        /// <summary>
        /// Renders a boxed metadata value (including <c>null</c>) for an assertion message.
        /// </summary>
        private static String Describe(Object value)
        {
            return value == null ? "<null>" : "\"" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) + "\"";
        }
    }
}
