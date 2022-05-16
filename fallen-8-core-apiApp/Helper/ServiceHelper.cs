// MIT License
//
// ServiceHelper.cs
//
// Copyright (c) 2022 Henning Rauch
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
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Static service helper class
    /// </summary>
    public static class ServiceHelper
    {
        /// <summary>
        /// Creates the plugin options.
        /// </summary>
        /// <returns>
        /// The plugin options.
        /// </returns>
        /// <param name='options'>
        /// Options.
        /// </param>
        internal static IDictionary<string, object> CreatePluginOptions(Dictionary<string, PropertySpecification> options)
        {
            return options.ToDictionary(key => key.Key, value => CreateObject(value.Value));
        }

        /// <summary>
        /// Creates the object.
        /// </summary>
        /// <returns>
        /// The object.
        /// </returns>
        /// <param name='key'>
        /// Key.
        /// </param>
        internal static object CreateObject(PropertySpecification key)
        {
            return Convert.ChangeType(
                key.PropertyValue,
                Type.GetType(key.FullQualifiedTypeName, true, true));
        }

        /// <summary>
        ///   Generates the properties.
        /// </summary>
        /// <returns> The properties. </returns>
        /// <param name='propertySpecification'> Property specification. </param>
        public static Dictionary<String, Object> GenerateProperties(
            Dictionary<String, PropertySpecification> propertySpecification)
        {
            Dictionary<String, Object> properties = null;

            if (propertySpecification != null)
            {
                properties = new Dictionary<String, Object>(propertySpecification.Count);

                foreach (var aPropertyDefinition in propertySpecification)
                {
                    properties.Add(aPropertyDefinition.Key, aPropertyDefinition.Value.FullQualifiedTypeName != null
                             ? Convert.ChangeType(aPropertyDefinition.Value.PropertyValue,
                                                Type.GetType(
                                                    aPropertyDefinition.Value.FullQualifiedTypeName,
                                                    true, true))
                            : aPropertyDefinition.Value.PropertyValue);
                }
            }

            return properties;
        }

        /// <summary>
        ///   Generates the properties.
        /// </summary>
        /// <returns> The properties. </returns>
        /// <param name='propertySpecification'> Property specification. </param>
        public static Dictionary<String, Object> GenerateProperties(List<PropertySpecification> propertySpecification)
        {
            Dictionary<String, Object> properties = null;

            if (propertySpecification != null)
            {
                properties = new Dictionary<String, Object>(propertySpecification.Count);

                foreach (var aPropertyDefinition in propertySpecification)
                {
                    properties.Add(aPropertyDefinition.PropertyId, aPropertyDefinition.FullQualifiedTypeName != null
                             ? Convert.ChangeType(aPropertyDefinition.PropertyValue,
                                                Type.GetType(
                                                    aPropertyDefinition.FullQualifiedTypeName,
                                                    true, true))
                            : aPropertyDefinition.PropertyValue);
                }
            }

            return properties;
        }

        public static Object Transform(PropertySpecification definition)
        {
            return definition.FullQualifiedTypeName == null
                ? definition.PropertyValue
                : Convert.ChangeType(definition.PropertyValue, Type.GetType(definition.FullQualifiedTypeName, true, true));
        }
    }
}
