namespace NoSQL.GraphDB.Core.Helper
{
    /// <summary>
    ///   Constants.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        ///   The size of the file buffer when reading or writing Fallen-8 from a file stream.
        /// </summary>
        public const int BufferSize = 104857600;

        /// <summary>
        /// The version separator in save files
        /// </summary>
        public const char VersionSeparator = '#';

        /// <summary>
        /// Graph element files contain this string
        /// </summary>
        public const string GraphElementsSaveString = "_graphElements_";

        /// <summary>
        /// Index files contain this string
        /// </summary>
        public const string IndexSaveString = "_index_";

        /// <summary>
        /// Service files contain this string
        /// </summary>
        public const string ServiceSaveString = "_service_";
    }
}
