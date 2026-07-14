// MIT License
//
// AllowedLiteralTypes.cs
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
    }
}
