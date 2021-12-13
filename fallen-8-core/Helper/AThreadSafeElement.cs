using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace NoSQL.GraphDB.Core.Helper
{
    /// <summary>
    /// A thread safe element.
    /// </summary>
    public abstract class AThreadSafeElement
    {
        /// <summary>
        /// The using resource.
        /// 0 for false, 1 for true.
        /// </summary>
        private volatile Int32 _usingResource;

#if DEBUG
        public string lockerStack;
#endif

        /// <summary>
        /// Reads the resource.
        /// Blocks if reading is currently not allowed
        /// </summary>
        /// <returns>
        /// <c>true</c> if reading is allowed; otherwise, <c>false</c>.
        /// </returns>
        protected bool ReadResource()
        {
            for (var i = 0; i < int.MaxValue; i++)
            {
                while ((_usingResource & 0xfff00000) != 0)
                    Thread.Yield();

                if ((Interlocked.Increment(ref _usingResource) & 0xfff00000) == 0)
                {
                    return true;
                }

                Interlocked.Decrement(ref _usingResource);
            }

            return false;
        }

        /// <summary>
        /// Reading this resource is finished.
        /// </summary>
        protected void FinishReadResource()
        {
            //Release the lock
            Interlocked.Decrement(ref _usingResource);
        }

        /// <summary>
        /// Writes the resource.
        /// Blocks if another thread reads or writes this resource
        /// </summary>
        /// <returns>
        /// <c>true</c> if writing is allowed; otherwise, <c>false</c>.
        /// </returns>
        protected bool WriteResource()
        {
            for (var i = 0; i < int.MaxValue; i++)
            {
                while ((_usingResource & 0xfff00000) != 0)
                    Thread.Yield();

                if ((Interlocked.Add(ref _usingResource, 0x100000) & 0xfff00000) == 0x100000)
                {
#if DEBUG
                    lockerStack = Environment.StackTrace;
#endif
                    while ((_usingResource & 0x000fffff) != 0)
                        Thread.Yield();

                    return true;
                }

                Interlocked.Add(ref _usingResource, -0x100000);
            }

            return false;
        }

        /// <summary>
        /// Writing this resource is finished
        /// </summary>
        protected void FinishWriteResource()
        {
            //Release the lock
#if DEBUG
            lockerStack = String.Empty;
#endif
            Interlocked.Add(ref _usingResource, -0x100000);
        }
    }
}
