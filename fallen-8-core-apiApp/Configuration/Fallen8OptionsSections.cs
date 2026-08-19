// MIT License
//
// Fallen8OptionsSections.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Which options class owns which <c>Fallen8:*</c> configuration section.
    ///
    ///   <para>The configuration-write path uses this to trial-bind a batch before persisting it: the
    ///   catalog's own domain checks cannot see a value that BINDING would reject, for instance a whole
    ///   number too large for the <see cref="Int32"/> property behind it.</para>
    ///
    ///   <para>Written out rather than reflected over, because reflecting on each type's
    ///   <c>SectionName</c> field cannot be annotated for trimming and would need a suppression. The
    ///   section names still come from the constants themselves, so they cannot drift from what the app
    ///   binds, and <c>SettingCatalogTest</c> fails if a new options class is missing here.</para>
    /// </summary>
    public static class Fallen8OptionsSections
    {
        private static readonly IReadOnlyDictionary<String, Type> _bySection =
            new Dictionary<String, Type>(StringComparer.OrdinalIgnoreCase)
            {
                [Fallen8AnalyticsOptions.SectionName] = typeof(Fallen8AnalyticsOptions),
                [Fallen8BulkIOOptions.SectionName] = typeof(Fallen8BulkIOOptions),
                [Fallen8ChangeFeedOptions.SectionName] = typeof(Fallen8ChangeFeedOptions),
                [Fallen8ChatOptions.SectionName] = typeof(Fallen8ChatOptions),
                [Fallen8DurabilityOptions.SectionName] = typeof(Fallen8DurabilityOptions),
                [Fallen8EmbeddingOptions.SectionName] = typeof(Fallen8EmbeddingOptions),
                [Fallen8IdentityOptions.SectionName] = typeof(Fallen8IdentityOptions),
                [Fallen8IngestionOptions.SectionName] = typeof(Fallen8IngestionOptions),
                [Fallen8IntegrationsOptions.SectionName] = typeof(Fallen8IntegrationsOptions),
                [Fallen8MetadataOptions.SectionName] = typeof(Fallen8MetadataOptions),
                [Fallen8NamespacesOptions.SectionName] = typeof(Fallen8NamespacesOptions),
                [Fallen8NlpOptions.SectionName] = typeof(Fallen8NlpOptions),
                [Fallen8ObservabilityOptions.SectionName] = typeof(Fallen8ObservabilityOptions),
                [Fallen8PluginOptions.SectionName] = typeof(Fallen8PluginOptions),
                [Fallen8SecurityOptions.SectionName] = typeof(Fallen8SecurityOptions),
                [Fallen8StoredQueryOptions.SectionName] = typeof(Fallen8StoredQueryOptions)
            };

        /// <summary>Every bound section, keyed by its <c>Fallen8:Section</c> name.</summary>
        public static IReadOnlyDictionary<String, Type> All => _bySection;

        /// <summary>The options class bound from a section, or <c>null</c> when nothing binds it.</summary>
        public static Type TypeOf(String section)
        {
            return section != null && _bySection.TryGetValue(section, out var type) ? type : null;
        }

        /// <summary>
        ///   The <c>Fallen8:Section</c> prefix a configuration key belongs to, or <c>null</c> when the
        ///   key has none. The one home for the "a section is the first two segments" rule, which both
        ///   the trial-bind and the effective-value read depend on.
        /// </summary>
        public static String SectionOf(String key)
        {
            if (key == null)
            {
                return null;
            }

            var first = key.IndexOf(':');
            if (first < 0)
            {
                return null;
            }

            var second = key.IndexOf(':', first + 1);
            return second < 0 ? null : key.Substring(0, second);
        }

        /// <summary>The options class a configuration key binds through, or <c>null</c>.</summary>
        public static Type TypeOfKey(String key)
        {
            return TypeOf(SectionOf(key));
        }
    }
}
