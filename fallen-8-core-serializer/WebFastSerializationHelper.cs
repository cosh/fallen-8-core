#region Usings

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;

#endregion

namespace NoSQL.GraphDB.Serializer
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
