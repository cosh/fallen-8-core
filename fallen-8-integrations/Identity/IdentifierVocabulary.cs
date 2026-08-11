// MIT License
//
// IdentifierVocabulary.cs
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
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoSQL.GraphDB.Integrations.Identity
{
    /// <summary>
    ///   The identifier types this runtime understands, loaded from the embedded
    ///   <c>identifier-vocabulary.v1.json</c>.
    ///
    ///   <para>It is DATA, not code, because both ways to get one entry wrong are unrepairable by running
    ///   again: an entry wrongly marked strong makes a run attach its data to the wrong element it claimed
    ///   before, and one wrongly marked weak, or a canonicalisation that does not converge, makes a run
    ///   fail to find its own element and duplicate its devices on every run. Data with a validator is
    ///   reviewable as a table and extendable without touching resolution logic.</para>
    ///
    ///   <para><see cref="Load"/> THROWS on a malformed file rather than starting with a half-understood
    ///   vocabulary, and the file is an embedded resource rather than a mount because a deployment that
    ///   could edit or lose it could silently change whether a claim resolves.</para>
    /// </summary>
    public sealed class IdentifierVocabulary
    {
        /// <summary>The one contract version this code implements.</summary>
        public const Int32 CurrentSchemaVersion = 1;

        private const String ResourceName = "NoSQL.GraphDB.Integrations.Identity.identifier-vocabulary.v1.json";

        private static readonly Lazy<IdentifierVocabulary> Embedded =
            new Lazy<IdentifierVocabulary>(LoadEmbedded, isThreadSafe: true);

        private readonly ImmutableDictionary<String, IdentifierType> _byType;

        private IdentifierVocabulary(ImmutableDictionary<String, IdentifierType> byType)
        {
            _byType = byType;
        }

        /// <summary>The shipped vocabulary. Loaded once; a malformed resource throws on first touch.</summary>
        public static IdentifierVocabulary Shipped => Embedded.Value;

        /// <summary>Every entry, in file order.</summary>
        public ImmutableArray<IdentifierType> All { get; private set; } = ImmutableArray<IdentifierType>.Empty;

        /// <summary>Looks up an entry by its type name (case-insensitive, since a type name is a word a
        /// provider author types).</summary>
        public Boolean TryGet(String? type, [NotNullWhen(true)] out IdentifierType? identifier)
        {
            if (type != null && _byType.TryGetValue(type, out var found))
            {
                identifier = found;
                return true;
            }

            identifier = null;
            return false;
        }

        /// <summary>Parses and validates a vocabulary document, throwing on anything it cannot fully
        /// understand.</summary>
        /// <exception cref="InvalidOperationException">The document is malformed, declares an unsupported
        /// schema version, names an unknown canonicaliser, carries an uncompilable accept pattern, or
        /// repeats a type.</exception>
        public static IdentifierVocabulary Load(String json)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("The identifier vocabulary is empty.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("The identifier vocabulary is not valid JSON: " + ex.Message, ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("The identifier vocabulary must be a JSON object.");
                }

                if (!root.TryGetProperty("schemaVersion", out var version) ||
                    version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var declaredVersion) ||
                    declaredVersion != CurrentSchemaVersion)
                {
                    throw new InvalidOperationException(String.Format(
                        "The identifier vocabulary must declare schemaVersion {0}.", CurrentSchemaVersion));
                }

                if (!root.TryGetProperty("identifiers", out var identifiers) ||
                    identifiers.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("The identifier vocabulary must carry an 'identifiers' array.");
                }

                var entries = ImmutableArray.CreateBuilder<IdentifierType>();
                var byType = ImmutableDictionary.CreateBuilder<String, IdentifierType>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in identifiers.EnumerateArray())
                {
                    var type = ReadRequiredString(entry, "type");
                    var strengthWord = ReadRequiredString(entry, "strength");
                    var scopeWord = ReadRequiredString(entry, "scope");
                    var canonicalName = ReadRequiredString(entry, "canonical");
                    var accept = ReadRequiredString(entry, "accept");
                    var description = ReadRequiredString(entry, "description");

                    if (!TryParseStrength(strengthWord, out var strength))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Identifier '{0}' declares strength '{1}', which is neither 'weak' nor 'strong'.",
                            type, strengthWord));
                    }

                    if (!TryParseScope(scopeWord, out var scope))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Identifier '{0}' declares scope '{1}', which is none of 'global', 'provider', 'instance'.",
                            type, scopeWord));
                    }

                    if (!Canonicalisers.TryGet(canonicalName, out var canonicaliser))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Identifier '{0}' names canonicaliser '{1}', which this runtime does not implement " +
                            "(known: {2}).", type, canonicalName, String.Join(", ", Canonicalisers.Names)));
                    }

                    Regex pattern;
                    try
                    {
                        pattern = new Regex(accept, RegexOptions.CultureInvariant);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidOperationException(String.Format(
                            "Identifier '{0}' carries an accept pattern that is not a valid regular expression: {1}",
                            type, ex.Message), ex);
                    }

                    // ANCHORED, not merely compilable: the pattern is applied with IsMatch, which is a
                    // substring search, so an unanchored one silently accepts a superstring. An unanchored
                    // serial pattern would match on the "ST" inside "ST 1234" and key the whole value as a
                    // strong identity, which is the wrong-element-attribution failure the strength field
                    // exists to prevent.
                    if (!accept.StartsWith("^", StringComparison.Ordinal) ||
                        !accept.EndsWith("$", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Identifier '{0}' carries the accept pattern '{1}', which is not anchored. The " +
                            "pattern is matched as a substring search, so an unanchored one accepts a value " +
                            "that merely CONTAINS an acceptable one.", type, accept));
                    }

                    var identifier = new IdentifierType(type, strength, scope, canonicalName, canonicaliser!,
                        pattern, description);

                    if (byType.ContainsKey(type))
                    {
                        throw new InvalidOperationException(String.Format(
                            "The identifier vocabulary declares '{0}' more than once.", type));
                    }

                    byType.Add(type, identifier);
                    entries.Add(identifier);
                }

                if (entries.Count == 0)
                {
                    throw new InvalidOperationException("The identifier vocabulary declares no identifiers.");
                }

                return new IdentifierVocabulary(byType.ToImmutable()) { All = entries.ToImmutable() };
            }
        }

        private static IdentifierVocabulary LoadEmbedded()
        {
            var assembly = typeof(IdentifierVocabulary).GetTypeInfo().Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(String.Format(
                    "The embedded identifier vocabulary '{0}' is missing from the assembly.", ResourceName));
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Load(reader.ReadToEnd());
        }

        private static String ReadRequiredString(JsonElement entry, String name)
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InvalidOperationException(String.Format(
                    "Every identifier entry needs a non-empty '{0}'.", name));
            }

            return value.GetString()!;
        }

        private static Boolean TryParseStrength(String word, out IdentifierStrength strength)
        {
            if (String.Equals(word, "weak", StringComparison.OrdinalIgnoreCase))
            {
                strength = IdentifierStrength.Weak;
                return true;
            }

            if (String.Equals(word, "strong", StringComparison.OrdinalIgnoreCase))
            {
                strength = IdentifierStrength.Strong;
                return true;
            }

            strength = IdentifierStrength.Weak;
            return false;
        }

        private static Boolean TryParseScope(String word, out IdentifierScope scope)
        {
            if (String.Equals(word, "global", StringComparison.OrdinalIgnoreCase))
            {
                scope = IdentifierScope.Global;
                return true;
            }

            if (String.Equals(word, "provider", StringComparison.OrdinalIgnoreCase))
            {
                scope = IdentifierScope.Provider;
                return true;
            }

            if (String.Equals(word, "instance", StringComparison.OrdinalIgnoreCase))
            {
                scope = IdentifierScope.Instance;
                return true;
            }

            scope = IdentifierScope.Global;
            return false;
        }

        /// <summary>The strength words, so a declared strength can be compared with the file's.</summary>
        public static class StrengthWords
        {
            /// <summary>The word for <see cref="IdentifierStrength.Weak"/>.</summary>
            public const String Weak = "weak";

            /// <summary>The word for <see cref="IdentifierStrength.Strong"/>.</summary>
            public const String Strong = "strong";

            /// <summary>Parses a declared strength word; false for anything that is neither.</summary>
            public static Boolean TryParse(String? word, out IdentifierStrength strength)
            {
                return TryParseStrength(word ?? String.Empty, out strength);
            }

            /// <summary>The word of a strength.</summary>
            public static String ToWord(IdentifierStrength strength)
            {
                return strength == IdentifierStrength.Strong ? Strong : Weak;
            }
        }
    }

    /// <summary>One vocabulary entry, with its canonicaliser and accept pattern already resolved.</summary>
    public sealed class IdentifierType
    {
        internal IdentifierType(String type, IdentifierStrength strength, IdentifierScope scope,
            String canonicaliserName, Func<String, String> canonicaliser, Regex accept, String description)
        {
            Type = type;
            Strength = strength;
            Scope = scope;
            CanonicaliserName = canonicaliserName;
            Accept = accept;
            Description = description;
            _canonicaliser = canonicaliser;
        }

        private readonly Func<String, String> _canonicaliser;

        /// <summary>The type name a provider declares.</summary>
        public String Type { get; }

        /// <summary>Whether a claim of this type may resolve. Only <see cref="IdentifierStrength.Strong"/> may.</summary>
        public IdentifierStrength Strength { get; }

        /// <summary>The uniqueness domain equal keys of this type must share.</summary>
        public IdentifierScope Scope { get; }

        /// <summary>Which canonicaliser the file named, kept for diagnostics and tests.</summary>
        public String CanonicaliserName { get; }

        /// <summary>The pattern the CANONICAL form must match.</summary>
        public Regex Accept { get; }

        /// <summary>What the identifier is, in the file's own words.</summary>
        public String Description { get; }

        /// <summary>The canonical form of a raw value. Canonicalisation happens before the key is
        /// composed, never after, because exact string equality on that key IS the resolution rule.</summary>
        public String Canonicalise(String? value)
        {
            return _canonicaliser(value ?? String.Empty);
        }

        /// <summary>Canonicalises, then checks the accept pattern. False means
        /// <c>invalidIdentifierValue</c>: a visible diagnostic rather than a silent drop.</summary>
        public Boolean TryCanonicalise(String? value, out String canonical)
        {
            canonical = Canonicalise(value);
            return canonical.Length > 0 && Accept.IsMatch(canonical);
        }
    }

    /// <summary>
    ///   Whether a claim of a type may RESOLVE. Only strong may: an address moves between devices, so
    ///   matching on one attaches this run's data to whichever element last held the value.
    /// </summary>
    public enum IdentifierStrength
    {
        /// <summary>Recorded, indexed and queryable, but never consulted during resolution.</summary>
        Weak = 0,

        /// <summary>May resolve an entity to an element this same integration already claimed.</summary>
        Strong = 1,
    }

    /// <summary>
    ///   The uniqueness domain in which equal keys of a type must mean the same value, because equal keys
    ///   assert an overlap and a wrongly widened scope advertises one that does not exist.
    /// </summary>
    public enum IdentifierScope
    {
        /// <summary>Unique everywhere (a MAC, a serial).</summary>
        Global = 0,

        /// <summary>Unique within one provider's own id space (a vendor UUID).</summary>
        Provider = 1,

        /// <summary>Unique only within one integration instance (a device-local short id).</summary>
        Instance = 2,
    }

    /// <summary>
    ///   The canonicalisers a vocabulary entry may name. A closed set, so a file naming one this runtime
    ///   does not implement fails to load rather than silently skipping normalisation - which would make a
    ///   run fail to find its own element and duplicate every device.
    /// </summary>
    public static class Canonicalisers
    {
        private static readonly Dictionary<String, Func<String, String>> Known =
            new Dictionary<String, Func<String, String>>(StringComparer.Ordinal)
            {
                ["lowerHexStripSeparators"] = LowerHexStripSeparators,
                ["trimUpper"] = value => value.Trim().ToUpperInvariant(),
                ["trimLower"] = value => value.Trim().ToLowerInvariant(),
                ["digitsOnly"] = DigitsOnly,
            };

        /// <summary>The names a vocabulary file may use, for the load failure's message.</summary>
        public static IEnumerable<String> Names => Known.Keys;

        /// <summary>Resolves a canonicaliser by the name the file used.</summary>
        public static Boolean TryGet(String name, out Func<String, String>? canonicaliser)
        {
            if (Known.TryGetValue(name, out var found))
            {
                canonicaliser = found;
                return true;
            }

            canonicaliser = null;
            return false;
        }

        /// <summary>
        ///   Lower-cases and drops every non-hex character, so <c>44:D2:44:AA:BB:CC</c>,
        ///   <c>44-d2-44-aa-bb-cc</c> and <c>44d244aabbcc</c> converge on one key.
        /// </summary>
        private static String LowerHexStripSeparators(String value)
        {
            var text = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if ((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f'))
                {
                    text.Append(character);
                }
                else if (character >= 'A' && character <= 'F')
                {
                    text.Append(Char.ToLowerInvariant(character));
                }
            }

            return text.ToString();
        }

        /// <summary>Keeps the digits, so a spaced or dashed IMEI converges.</summary>
        private static String DigitsOnly(String value)
        {
            var text = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (character >= '0' && character <= '9')
                {
                    text.Append(character);
                }
            }

            return text.ToString();
        }
    }
}
