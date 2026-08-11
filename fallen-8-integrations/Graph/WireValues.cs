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
        ///   What a value turned out to be. THREE states rather than a boolean, because "the source did not
        ///   answer" and "the source answered with something a property cannot hold" are different facts and a
        ///   caller reports them differently: the first is silent, the second names the property. A boolean
        ///   forced the caller to tell them apart by null-checking the value, which cannot work for a boxed
        ///   <see cref="JsonElement"/> holding JSON null - so a pasted document got one diagnostic per
        ///   unanswered column while the same provider in process got none.
        /// </summary>
        public enum Outcome
        {
            /// <summary>The source did not answer. Absent is absent, never an empty string.</summary>
            Absent = 0,

            /// <summary>Rendered as the platform's (type name, text) pair.</summary>
            Rendered = 1,

            /// <summary>A shape the property surface does not carry.</summary>
            Unsupported = 2,
        }

        /// <summary>
        ///   Renders a provider-supplied value as the platform's (type name, text) pair.
        /// </summary>
        /// <param name="value">A CLR scalar, or a <see cref="JsonElement"/> when the snapshot arrived as JSON.</param>
        /// <param name="typeName">The platform literal type name, when this renders.</param>
        /// <param name="text">The invariant text form, when this renders.</param>
        /// <returns>
        ///   <see cref="Outcome.Absent"/> when the source did not answer (an absent value is absent, never
        ///   an empty string: writing empty makes the property exist and overwrites what another integration
        ///   knows), and <see cref="Outcome.Unsupported"/> for a shape this contract does not carry.
        /// </returns>
        public static Outcome TryRender(Object? value, out String? typeName, out String? text)
        {
            typeName = null;
            text = null;

            switch (value)
            {
                case null:
                    return Outcome.Absent;

                case String s:
                    typeName = StringTypeName;
                    text = s;
                    return Outcome.Rendered;

                case Boolean b:
                    typeName = "System.Boolean";
                    // Boolean is not IFormattable, so the platform's egress renders it with ToString():
                    // "True"/"False". Matching that exactly is what makes a read-back comparison equal.
                    text = b.ToString();
                    return Outcome.Rendered;

                case Byte or SByte or Int16 or UInt16 or Int32 or UInt32 or Int64 or UInt64
                    or Single or Double or Decimal:
                    typeName = "System." + value.GetType().Name;
                    text = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                    return Outcome.Rendered;

                case Char c:
                    typeName = "System.Char";
                    text = c.ToString();
                    return Outcome.Rendered;

                case DateTime dateTime:
                    typeName = "System.DateTime";
                    text = dateTime.ToString("O", CultureInfo.InvariantCulture);
                    return Outcome.Rendered;

                case DateTimeOffset dateTimeOffset:
                    typeName = "System.DateTimeOffset";
                    text = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
                    return Outcome.Rendered;

                case TimeSpan timeSpan:
                    typeName = "System.TimeSpan";
                    text = timeSpan.ToString(null, CultureInfo.InvariantCulture);
                    return Outcome.Rendered;

                case Guid guid:
                    typeName = "System.Guid";
                    text = guid.ToString();
                    return Outcome.Rendered;

                case JsonElement json:
                    return TryRenderJson(json, out typeName, out text);

                default:
                    return Outcome.Unsupported;
            }
        }

        /// <summary>
        ///   The JSON arm, for a snapshot that arrived over <c>POST /integration/snapshot/validate</c>
        ///   rather than from a provider in process. An integral number becomes an <c>Int64</c> and any
        ///   other number a <c>Double</c>, because JSON does not distinguish them and the alternative -
        ///   guessing the narrowest type - would make one source's value change type between runs and turn
        ///   every run into a write.
        /// </summary>
        private static Outcome TryRenderJson(JsonElement json, out String? typeName, out String? text)
        {
            typeName = null;
            text = null;

            switch (json.ValueKind)
            {
                case JsonValueKind.String:
                    typeName = StringTypeName;
                    text = json.GetString();
                    return text == null ? Outcome.Absent : Outcome.Rendered;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    typeName = "System.Boolean";
                    text = json.GetBoolean().ToString();
                    return Outcome.Rendered;

                case JsonValueKind.Number:
                    if (json.TryGetInt64(out var integral))
                    {
                        typeName = "System.Int64";
                        text = integral.ToString(CultureInfo.InvariantCulture);
                        return Outcome.Rendered;
                    }

                    if (json.TryGetDouble(out var real))
                    {
                        typeName = "System.Double";
                        text = real.ToString(null, CultureInfo.InvariantCulture);
                        return Outcome.Rendered;
                    }

                    return Outcome.Unsupported;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    // The source did not answer, which is the same fact as a CLR null and earns the same
                    // silence: a document pasted into the validate route must get the same verdict as the
                    // provider that would have produced it.
                    return Outcome.Absent;

                default:
                    // Object and Array: a shape the property surface does not carry.
                    return Outcome.Unsupported;
            }
        }
    }
}
