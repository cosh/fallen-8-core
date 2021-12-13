#region Usings

using System;

#endregion

namespace NoSQL.GraphDB.Core.Helper
{
    /// <summary>
    ///   static helper class.
    /// </summary>
    public static class ParallelHelper
    {
        public static Int32 GetOptimalNumberOfTasks()
        {
			return (Environment.ProcessorCount / 2) * 3;
        }
    }
}