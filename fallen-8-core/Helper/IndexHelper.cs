using System;

namespace NoSQL.GraphDB.Core.Helper
{
    /// <summary>
    ///   Index helper.
    /// </summary>
    public static class IndexHelper
    {
        /// <summary>
        /// Checks if an object is of Type T
        /// </summary>
        /// <typeparam name="T">The desired type</typeparam>
        /// <param name="result">The result</param>
        /// <param name="obj">The object</param>
        /// <returns>True for success</returns>
        public static Boolean CheckObject<T>(out T result, Object obj) where T : class
        {
            result = obj as T;
            return result != null;
        }
    }
}
