// MIT License
//
// WireValues.cs
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

namespace NoSQL.GraphDB.Integrations.Graph
{
    /// <summary>
    ///   THE ONE HOME of "what a provider's property value looks like on the wire": the pair
    ///   (<c>fullQualifiedTypeName</c>, <c>propertyValue</c>) the platform's property routes take.
    ///
    ///   <para>Two rules make the zero-mutation invariant observable rather than aspirational. First,
    ///   values are rendered with <see cref="CultureInfo.InvariantCulture"/> and dates round-trip
    ///   ("O"), which is exactly what the platform's egress does, so a value read back compares equal to
    ///   the value that would be written and "write only if it differs" can tell same from different.
    ///   Second, <c>propertyValue</c> is always a STRING on the wire: the platform's DTO declares it a
    ///   string, so an unquoted JSON number is a deserialization failure, not a number.</para>
    ///
    ///   <para>The type names are restricted to the platform's own closed literal allow-list. A value of
    ///   any other shape - a nested object, an array, a provider's own class - is REFUSED here rather
    ///   than sent and rejected downstream, because a refusal here names the property.</para>
    /// </summary>
    public static class WireValues
    {
        /// <summary>The type name of a text value, which is also every claim key's type.</summary>
        public const String StringTypeName = "System.String";

        /// <summary>
        ///   Renders a provider-supplied value as the platform's (type name, text) pair.
        /// </summary>
        /// <param name="value">A CLR scalar, or a <see cref="JsonElement"/> when the snapshot arrived as JSON.</param>
        /// <param name="typeName">The platform literal type name, when this returns true.</param>
        /// <param name="text">The invariant text form, when this returns true.</param>
        /// <returns>
        ///   False for null (an absent value is absent, never an empty string: writing empty makes the
        ///   property exist and overwrites what another integration knows) and for a value this contract
        ///   does not carry.
        /// </returns>
        public static Boolean TryRender(Object? value, out String? typeName, out String? text)
        {
            typeName = null;
            text = null;

            switch (value)
            {
                case null:
                    return false;

                case String s:
                    typeName = StringTypeName;
                    text = s;
                    return true;

                case Boolean b:
                    typeName = "System.Boolean";
                    // Boolean is not IFormattable, so the platform's egress renders it with ToString():
                    // "True"/"False". Matching that exactly is what makes a read-back comparison equal.
                    text = b.ToString();
                    return true;

                case Byte or SByte or Int16 or UInt16 or Int32 or UInt32 or Int64 or UInt64
                    or Single or Double or Decimal:
                    typeName = "System." + value.GetType().Name;
                    text = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                    return true;

                case Char c:
                    typeName = "System.Char";
                    text = c.ToString();
                    return true;

                case DateTime dateTime:
                    typeName = "System.DateTime";
                    text = dateTime.ToString("O", CultureInfo.InvariantCulture);
                    return true;

                case DateTimeOffset dateTimeOffset:
                    typeName = "System.DateTimeOffset";
                    text = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
                    return true;

                case TimeSpan timeSpan:
                    typeName = "System.TimeSpan";
                    text = timeSpan.ToString(null, CultureInfo.InvariantCulture);
                    return true;

                case Guid guid:
                    typeName = "System.Guid";
                    text = guid.ToString();
                    return true;

                case JsonElement json:
                    return TryRenderJson(json, out typeName, out text);

                default:
                    return false;
            }
        }

        /// <summary>
        ///   The JSON arm, for a snapshot that arrived over <c>POST /integration/snapshot/validate</c>
        ///   rather than from a provider in process. An integral number becomes an <c>Int64</c> and any
        ///   other number a <c>Double</c>, because JSON does not distinguish them and the alternative -
        ///   guessing the narrowest type - would make one source's value change type between runs and turn
        ///   every run into a write.
        /// </summary>
        private static Boolean TryRenderJson(JsonElement json, out String? typeName, out String? text)
        {
            typeName = null;
            text = null;

            switch (json.ValueKind)
            {
                case JsonValueKind.String:
                    typeName = StringTypeName;
                    text = json.GetString();
                    return text != null;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    typeName = "System.Boolean";
                    text = json.GetBoolean().ToString();
                    return true;

                case JsonValueKind.Number:
                    if (json.TryGetInt64(out var integral))
                    {
                        typeName = "System.Int64";
                        text = integral.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }

                    if (json.TryGetDouble(out var real))
                    {
                        typeName = "System.Double";
                        text = real.ToString(null, CultureInfo.InvariantCulture);
                        return true;
                    }

                    return false;

                default:
                    // Null, Object, Array, Undefined: an absent value, or a shape the property surface
                    // does not carry.
                    return false;
            }
        }
    }
}
