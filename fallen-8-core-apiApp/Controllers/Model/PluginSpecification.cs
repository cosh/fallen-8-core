using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The plugin creation specification
    /// </summary>
    public sealed class PluginSpecification
    {
        /// <summary>
        ///   The unique plugin identifier
        /// </summary>
        [Required]
        public String UniqueId { get; set; }

        /// <summary>
        ///   The name of the plugin type
        /// </summary>
        [Required]
        public String PluginType { get; set; }

        /// <summary>
        ///   The specification of the plugin options
        /// </summary>
        public Dictionary<String, PropertySpecification> PluginOptions { get; set; }
    }
}
