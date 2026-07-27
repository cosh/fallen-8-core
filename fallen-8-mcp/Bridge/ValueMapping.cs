// MIT License
//
// ValueMapping.cs
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
using System.Globalization;
using System.Text.Json;

namespace NoSQL.GraphDB.Mcp.Bridge
{
    /// <summary>
    ///   Absorbs Fallen-8's typed-property wire format so agents never emit .NET type names
    ///   (spec §3.5). Fallen-8 carries a property/literal as a <c>{value:string,
    ///   fullQualifiedTypeName}</c> pair; agents pass a JSON-native value and this infers the
    ///   type from the JSON kind (string → System.String, integral → System.Int32/Int64,
    ///   real → System.Double, bool → System.Boolean), formatting the value with
    ///   <see cref="CultureInfo.InvariantCulture"/> so it round-trips through the invariant
    ///   ingest the engine uses.
    /// </summary>
    public static class ValueMapping
    {
        /// <summary>
        ///   Maps a JSON-native scalar to the (invariant string value, .NET FQTN) pair Fallen-8
        ///   expects. Returns false (with <paramref name="error"/>) for a non-scalar (array/object)
        ///   or null, which are not valid comparison literals.
        /// </summary>
        public static Boolean TryFromJson(JsonElement value, out String literal, out String fullQualifiedTypeName, out String error)
        {
            literal = String.Empty;
            fullQualifiedTypeName = "System.String";
            error = String.Empty;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    literal = value.GetString() ?? String.Empty;
                    fullQualifiedTypeName = "System.String";
                    return true;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    literal = value.GetBoolean() ? "true" : "false";
                    fullQualifiedTypeName = "System.Boolean";
                    return true;

                case JsonValueKind.Number:
                    if (value.TryGetInt32(out var i))
                    {
                        literal = i.ToString(CultureInfo.InvariantCulture);
                        fullQualifiedTypeName = "System.Int32";
                        return true;
                    }
                    if (value.TryGetInt64(out var l))
                    {
                        literal = l.ToString(CultureInfo.InvariantCulture);
                        fullQualifiedTypeName = "System.Int64";
                        return true;
                    }
                    literal = value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                    fullQualifiedTypeName = "System.Double";
                    return true;

                default:
                    error = "The value must be a string, number, or boolean.";
                    return false;
            }
        }
    }
}
