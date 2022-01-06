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
        public static PropertyContainer[] GenerateProperties(
            Dictionary<UInt16, PropertySpecification> propertySpecification)
        {
            PropertyContainer[] properties = null;

            if (propertySpecification != null)
            {
                var propCounter = 0;
                properties = new PropertyContainer[propertySpecification.Count];

                foreach (var aPropertyDefinition in propertySpecification)
                {
                    properties[propCounter] = new PropertyContainer
                    {
                        PropertyId = aPropertyDefinition.Key,
                        Value = aPropertyDefinition.Value.FullQualifiedTypeName != null
                             ? Convert.ChangeType(aPropertyDefinition.Value.PropertyValue,
                                                Type.GetType(
                                                    aPropertyDefinition.Value.FullQualifiedTypeName,
                                                    true, true))
                            : aPropertyDefinition.Value.PropertyValue
                    };
                    propCounter++;
                }
            }

            return properties;
        }

        /// <summary>
        ///   Generates the properties.
        /// </summary>
        /// <returns> The properties. </returns>
        /// <param name='propertySpecification'> Property specification. </param>
        public static PropertyContainer[] GenerateProperties(List<PropertySpecification> propertySpecification)
        {
            PropertyContainer[] properties = null;

            if (propertySpecification != null)
            {
                var propCounter = 0;
                properties = new PropertyContainer[propertySpecification.Count];

                foreach (var aPropertyDefinition in propertySpecification)
                {
                    properties[propCounter] = new PropertyContainer
                    {
                        PropertyId = aPropertyDefinition.PropertyId,
                        Value = aPropertyDefinition.FullQualifiedTypeName != null
                             ? Convert.ChangeType(aPropertyDefinition.PropertyValue,
                                                Type.GetType(
                                                    aPropertyDefinition.FullQualifiedTypeName,
                                                    true, true))
                            : aPropertyDefinition.PropertyValue
                    };
                    propCounter++;
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
