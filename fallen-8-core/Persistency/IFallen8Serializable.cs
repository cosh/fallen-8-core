using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// The interface for serializable things in Fallen-8
    /// </summary>
    public interface IFallen8Serializable
    {
        /// <summary>
        /// Save the plugin.
        /// </summary>
        /// <param name='writer'>
        /// Writer.
        /// </param>
        void Save(SerializationWriter writer);

        /// <summary>
        ///   Load the plugin.
        /// </summary>
        /// <param name="reader">The reader</param>
        /// <param name="fallen8">Fallen-8</param>
        void Load(SerializationReader reader, Fallen8 fallen8);
    }
}
