// MIT License
//
// ProviderCatalog.cs
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
using System.Collections.Immutable;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   The providers this deployable ships, and the STARTUP check on their declared claim types.
    ///
    ///   <para>The per-run validator checks each emitted claim too, and this is that same check moved as
    ///   early as it can go, because the late version costs duplicates: an unknown claim type fails
    ///   validation per entity, the entity arrives with no strong claim, it never resolves against what the
    ///   same instance wrote last time, and every run creates another copy. By the time a diagnostic on a
    ///   job report is read the duplicates exist and re-running does not remove them. A typo caught at
    ///   startup costs a restart.</para>
    /// </summary>
    public sealed class ProviderCatalog
    {
        private readonly ImmutableDictionary<String, IIntegrationProvider> _byId;

        /// <summary>
        ///   Builds the catalog, THROWING when a provider declares a claim type the vocabulary does not
        ///   have, or declares an id or a setting that could not work.
        /// </summary>
        /// <exception cref="InvalidOperationException">A declared claim type is unknown, two providers share
        /// an id, a provider declares no id, or a credential setting carries a default value.</exception>
        public ProviderCatalog(IEnumerable<IIntegrationProvider> providers, IdentifierVocabulary vocabulary)
        {
            if (providers == null)
            {
                throw new ArgumentNullException(nameof(providers));
            }

            if (vocabulary == null)
            {
                throw new ArgumentNullException(nameof(vocabulary));
            }

            var byId = ImmutableDictionary.CreateBuilder<String, IIntegrationProvider>(StringComparer.OrdinalIgnoreCase);
            var descriptors = ImmutableArray.CreateBuilder<ProviderDescriptor>();

            foreach (var provider in providers)
            {
                var descriptor = provider?.Descriptor
                    ?? throw new InvalidOperationException("A provider must carry a descriptor.");

                if (String.IsNullOrWhiteSpace(descriptor.Id))
                {
                    throw new InvalidOperationException(
                        "A provider declares no id. The id appears inside every provider-scoped claim key, " +
                        "so it is assigned once and never reused.");
                }

                if (byId.ContainsKey(descriptor.Id))
                {
                    throw new InvalidOperationException(String.Format(
                        "Two providers declare the id '{0}'. An id renames every identity a provider ever " +
                        "asserted, so it cannot be shared.", descriptor.Id));
                }

                foreach (var claimType in descriptor.ClaimTypes ?? Array.Empty<String>())
                {
                    if (!vocabulary.TryGet(claimType, out _))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Provider '{0}' declares claim type '{1}', which the identifier vocabulary does " +
                            "not have. Left to the per-run validator this costs a duplicate element on every " +
                            "run; caught here it costs a restart.", descriptor.Id, claimType));
                    }
                }

                foreach (var setting in descriptor.Settings ?? Array.Empty<ProviderSetting>())
                {
                    if (String.IsNullOrWhiteSpace(setting.Key))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Provider '{0}' declares a setting with no key.", descriptor.Id));
                    }

                    if (setting.Kind == SettingKind.Credential && !String.IsNullOrEmpty(setting.DefaultValue))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Provider '{0}' gives credential setting '{1}' a default value. A default never " +
                            "carries a credential.", descriptor.Id, setting.Key));
                    }
                }

                byId.Add(descriptor.Id, provider!);
                descriptors.Add(descriptor);
            }

            _byId = byId.ToImmutable();
            Descriptors = descriptors.ToImmutable();
        }

        /// <summary>Every provider's descriptor, which is what <c>GET /integration/providers</c> answers.</summary>
        public ImmutableArray<ProviderDescriptor> Descriptors { get; }

        /// <summary>Resolves a provider by the id a job named.</summary>
        public Boolean TryGet(String? providerId, [NotNullWhen(true)] out IIntegrationProvider? provider)
        {
            if (providerId != null && _byId.TryGetValue(providerId, out var found))
            {
                provider = found;
                return true;
            }

            provider = null;
            return false;
        }
    }
}
