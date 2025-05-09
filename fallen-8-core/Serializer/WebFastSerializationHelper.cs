﻿// MIT License
//
// WebFastSerializationHelper.cs
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

#region Usings

using System;
using System.Collections;
using System.Collections.Specialized;

#endregion

namespace NoSQL.GraphDB.Core.Serializer
{
    public class WebFastSerializationHelper : IFastSerializationTypeSurrogate
    {
        #region Static
        public const double Epsilon = 2.2204460492503131e-016;

        internal static readonly int StateBagIsIgnoreCase = BitVector32.CreateMask();
        internal static readonly int StateBagHasDirtyEntries = BitVector32.CreateMask(StateBagIsIgnoreCase);
        internal static readonly int StateBagHasCleanEntries = BitVector32.CreateMask(StateBagHasDirtyEntries);

        internal static readonly BitVector32.Section UnitType = BitVector32.CreateSection(9); // 4 bits
        internal static readonly BitVector32.Section UnitIsZeroValue = BitVector32.CreateSection(1, UnitType);
        internal static readonly BitVector32.Section UnitIsNegativeValue = BitVector32.CreateSection(1, UnitIsZeroValue);
        internal static readonly BitVector32.Section UnitIsOptimizedValue = BitVector32.CreateSection(1, UnitIsNegativeValue);

        // Since Zero and Negative are mutually exclusive, we can use their combined masks and offsets
        // as a Pseudo-BitVector32.Section rather than use an explicit UnitIsDouble BitVector32.Section which would
        // require an extra bit and cause the BitVector32 to be written as 2 bytes depending on the value
        internal static readonly int UnitIsDoubleValue = UnitIsZeroValue.Mask << UnitIsZeroValue.Offset |
                                                         UnitIsNegativeValue.Mask << UnitIsNegativeValue.Offset;

        #endregion Static

        #region IFastSerializationTypeSurrogate
        public bool SupportsType(Type type)
        {
            if (type == typeof(Hashtable)) return true;

            return false;
        }

        public void Serialize(SerializationWriter writer, object value)
        {
            var type = value.GetType();

            if (type == typeof(Hashtable))
            {
                Serialize(writer, (Hashtable)value);
            }
            else
            {
                throw new InvalidOperationException(string.Format("{0} does not support Type: {1}", GetType(), type));
            }
        }

        public object Deserialize(SerializationReader reader, Type type)
        {
            if (type == typeof(Hashtable)) return DeserializeHashtable(reader);

            throw new InvalidOperationException(string.Format("{0} does not support Type: {1}", GetType(), type));
        }
        #endregion IFastSerializationTypeSurrogate

        #region Hashtable
        // Note this is a simplistic version as it assumes defaults for comparer, hashcodeprovider, loadfactor etc.
        public static void Serialize(SerializationWriter writer, Hashtable hashtable)
        {
            var keys = new object[hashtable.Count];
            var values = new object[hashtable.Count];

            hashtable.Keys.CopyTo(keys, 0);
            hashtable.Values.CopyTo(values, 0);

            writer.WriteOptimized(keys);
            writer.WriteOptimized(values);
        }

        // Note this is a simplistic version as it assumes defaults for comparer, hashcodeprovider, loadfactor etc.
        public static Hashtable DeserializeHashtable(SerializationReader reader)
        {
            var keys = reader.ReadOptimizedObjectArray();
            var values = reader.ReadOptimizedObjectArray();
            var result = new Hashtable(keys.Length);

            for (var i = 0; i < keys.Length; i++)
            {
                result[keys[i]] = values[i];
            }

            return result;
        }
        #endregion Hashtable

    }
}
