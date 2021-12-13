﻿using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Serializer
{
	/// <summary>
	/// Interface which allows a class to specify that it can be recreated during deserialization using a default constructor
	/// and then calling DeserializeOwnedData()
	/// </summary>
	public interface IOwnedDataSerializableAndRecreatable : IOwnedDataSerializable
	{
	}
}
