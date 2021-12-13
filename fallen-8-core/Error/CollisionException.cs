using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Log;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Error
{
    /// <summary>
    /// Collision exception.
    /// </summary>
    [Serializable]
    public sealed class CollisionException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the CollisionException class.
        /// </summary>
#if DEBUG
        public CollisionException(AThreadSafeElement el) : base(el.lockerStack)
        {
            Logger.LogError(this.Message);
        }
#else
        public CollisionException (AThreadSafeElement el)
        {
        }
#endif
    }
}
