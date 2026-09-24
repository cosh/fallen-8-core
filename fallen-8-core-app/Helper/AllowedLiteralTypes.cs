// MIT License
//
// AllowedLiteralTypes.cs
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
using System.Globalization;
using System.Linq;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   A closed allow-list of the primitive literal types the scan/property REST endpoints accept in
    ///   a <c>fullQualifiedTypeName</c> (feature dynamic-code-resource-limits R3). It replaces the
    ///   former <c>Type.GetType(userString, throwOnError: true)</c> calls: resolving an
    ///   attacker-controlled type name ran that type's static constructor and could force-load an
    ///   assembly - a code/side-effect surface reachable even on READ endpoints. A lookup here NEVER
    ///   calls <c>Type.GetType</c>, loads an assembly, or runs a static ctor; it only maps a vetted
    ///   name to a well-known primitive <see cref="Type"/>, so <c>Convert.ChangeType</c> is only ever
    ///   handed a safe primitive. Case-insensitive; keyed by full name (<c>System.Int32</c>), short
    ///   name (<c>Int32</c>), and the C# language aliases (<c>int</c>).
    /// </summary>
    public static class AllowedLiteralTypes
    {
        private static readonly Type[] _types =
        {
            typeof(string), typeof(bool), typeof(byte), typeof(sbyte),
            typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double),
            typeof(decimal), typeof(char), typeof(DateTime), typeof(DateTimeOffset),
            typeof(TimeSpan), typeof(Guid)
        };

        private static readonly Dictionary<string, Type> _byName = BuildMap();

        private static Dictionary<string, Type> BuildMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _types)
            {
                map[t.FullName] = t;
                map[t.Name] = t;
            }

            // C# language aliases (not reflected by Type.Name).
            map["string"] = typeof(string);
            map["bool"] = typeof(bool);
            map["byte"] = typeof(byte);
            map["sbyte"] = typeof(sbyte);
            map["short"] = typeof(short);
            map["ushort"] = typeof(ushort);
            map["int"] = typeof(int);
            map["uint"] = typeof(uint);
            map["long"] = typeof(long);
            map["ulong"] = typeof(ulong);
            map["float"] = typeof(float);
            map["double"] = typeof(double);
            map["decimal"] = typeof(decimal);
            map["char"] = typeof(char);
            return map;
        }

        /// <summary>The full names of the accepted primitive types, for a rejection message.</summary>
        public static IEnumerable<string> AllowedNames => _types.Select(t => t.FullName);

        /// <summary>
        ///   Resolves an accepted primitive type name; <c>false</c> (with <paramref name="type"/> null)
        ///   for a null/empty or non-allow-listed name - WITHOUT ever calling <c>Type.GetType</c>.
        /// </summary>
        public static bool TryResolve(string name, out Type type)
        {
            if (string.IsNullOrEmpty(name))
            {
                type = null;
                return false;
            }

            return _byName.TryGetValue(name, out type);
        }

        /// <summary>
        ///   Resolves an accepted primitive type name, throwing <see cref="ArgumentException"/> for a
        ///   disallowed name (matching the former throw-on-unknown-type behaviour, but without the
        ///   arbitrary <c>Type.GetType</c> assembly load / static-ctor surface).
        /// </summary>
        public static Type Resolve(string name)
        {
            if (TryResolve(name, out var type))
            {
                return type;
            }

            throw new ArgumentException(String.Format(
                "The type name '{0}' is not an allowed literal type. Allowed: {1}.",
                name, String.Join(", ", AllowedNames)));
        }

        /// <summary>
        ///   THE conversion of a wire literal into its stored value. This type already owns "which
        ///   types are allowed", so it owns "how a string becomes one"; five call sites previously
        ///   repeated a bare <c>Convert.ChangeType(value, type, InvariantCulture)</c> while their
        ///   comments named a single ingest home that they did not actually route through.
        ///
        ///   <para><b>Culture</b> (feature property-ingestion-culture): always
        ///   <see cref="CultureInfo.InvariantCulture" />, never the host's, because the wire value is
        ///   data interchange - a comma-decimal host must not read "0.8" as 8.</para>
        ///
        ///   <para><b>Kind</b> (feature platform-integrity-audit W6): date-like types parse with
        ///   <see cref="DateTimeStyles.RoundtripKind" />, so ingress is the inverse of egress. Without
        ///   it, <c>Convert.ChangeType</c> parses with default styles and converts a UTC ("...Z") wire
        ///   value to the host's LOCAL time; egress then renders it with "O" and emits a different
        ///   string than the one that was sent. The instant was always preserved, so this is a
        ///   representation asymmetry rather than corruption - but it defeats any client that decides
        ///   "has anything changed?" by comparing the value it intends to write against the value it
        ///   just read: every date property would differ on every comparison, forever. It is invisible
        ///   in CI and in the container (both UTC), which is exactly why it survived. It also made the
        ///   stored tick value host-timezone-dependent, so a data directory moved between zones shifted
        ///   the wall-clock reading of every date property. This applies the culture feature's own
        ///   stated principle - "egress mirrors ingress" - to the dimension it did not cover.</para>
        /// </summary>
        /// <param name="value">The wire value.</param>
        /// <param name="target">The resolved target type; <c>null</c> leaves the value a string.</param>
        public static Object ConvertInvariant(String value, Type target)
        {
            if (target == null)
            {
                return value;
            }

            if (target == typeof(DateTime))
            {
                return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (target == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            // TimeSpan and Guid are on the allow-list ABOVE but are not IConvertible, so
            // Convert.ChangeType throws InvalidCastException for both - meaning this allow-list has
            // always advertised two types the conversion could not deliver. The failure was loud (the
            // call sites map it to a 400), so nothing was silently wrong, but the contract was. Parsing
            // them here is what makes the advertised set the accepted set. Found by a round-trip test
            // over every allowed type; no call site had ever exercised these two.
            if (target == typeof(TimeSpan))
            {
                return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
            }

            if (target == typeof(Guid))
            {
                return Guid.Parse(value);
            }

            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
    }
}
