using System;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    /// Property container.
    /// </summary>
    public struct PropertyContainer
    {
        #region Data

        /// <summary>
        /// Gets or sets the property identifier.
        /// </summary>
        /// <value>
        /// The property identifier.
        /// </value>
        public UInt16 PropertyId;

        /// <summary>
        /// Gets or sets the value.
        /// </summary>
        /// <value>
        /// The value.
        /// </value>
        public Object Value;

        #endregion

        #region overrides

        public override string ToString()
        {
            return PropertyId + ": " + Value;
        }

        #endregion
    }
}
