// MIT License
//
// VocabularyDto.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Identity
{
    /// <summary>
    ///   The vocabulary as <c>GET /integration/vocabulary</c> serves it: the same table the file carries,
    ///   so a provider author can read which types exist, which of them may resolve, and what each accepts,
    ///   without the file being mountable or editable.
    /// </summary>
    public sealed class VocabularyDto
    {
        /// <summary>The contract version of the vocabulary document.</summary>
        [JsonPropertyName("schemaVersion")]
        public Int32 SchemaVersion { get; set; } = IdentifierVocabulary.CurrentSchemaVersion;

        /// <summary>Every identifier type, in file order.</summary>
        [JsonPropertyName("identifiers")]
        public IList<VocabularyEntryDto> Identifiers { get; set; } = new List<VocabularyEntryDto>();

        /// <summary>Projects a loaded vocabulary.</summary>
        public static VocabularyDto From(IdentifierVocabulary vocabulary)
        {
            if (vocabulary == null)
            {
                throw new ArgumentNullException(nameof(vocabulary));
            }

            var dto = new VocabularyDto();
            foreach (var identifier in vocabulary.All)
            {
                dto.Identifiers.Add(new VocabularyEntryDto
                {
                    Type = identifier.Type,
                    Strength = IdentifierVocabulary.StrengthWords.ToWord(identifier.Strength),
                    Scope = identifier.Scope.ToString().ToLowerInvariant(),
                    Canonical = identifier.CanonicaliserName,
                    Accept = identifier.Accept.ToString(),
                    Description = identifier.Description,
                });
            }

            return dto;
        }
    }

    /// <summary>One identifier type, as data a provider author reads.</summary>
    public sealed class VocabularyEntryDto
    {
        /// <summary>The type name a provider declares on a claim.</summary>
        [JsonPropertyName("type")]
        public String Type { get; set; } = String.Empty;

        /// <summary><c>weak</c> or <c>strong</c>. Only strong may resolve.</summary>
        [JsonPropertyName("strength")]
        public String Strength { get; set; } = String.Empty;

        /// <summary><c>global</c>, <c>provider</c> or <c>instance</c>.</summary>
        [JsonPropertyName("scope")]
        public String Scope { get; set; } = String.Empty;

        /// <summary>The canonicaliser applied before the key is composed.</summary>
        [JsonPropertyName("canonical")]
        public String Canonical { get; set; } = String.Empty;

        /// <summary>The pattern the canonical form must match.</summary>
        [JsonPropertyName("accept")]
        public String Accept { get; set; } = String.Empty;

        /// <summary>What the identifier is, and why its strength and scope are what they are.</summary>
        [JsonPropertyName("description")]
        public String Description { get; set; } = String.Empty;
    }
}
