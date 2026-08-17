// MIT License
//
// NamespaceExceptionUnwrap.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   How the two namespace exception filters find their exception when it did not arrive bare.
    ///   <para>An engine dereference can happen on a worker thread rather than the request flow -
    ///   benchmark generation resolves the addressed engine inside a <c>Parallel.ForEach</c> body -
    ///   and <c>Parallel.ForEach</c> reports a body's exception wrapped in an
    ///   <see cref="AggregateException"/>. A filter testing <c>context.Exception is
    ///   UnknownNamespaceException</c> therefore misses a namespace dropped or excluded MID-request
    ///   and lets it fall through to the generic 500, breaking the contract that such a race is
    ///   "indistinguishable from arriving a moment later" (404/503 problem+json).</para>
    ///   <para>Only the namespace refusals are unwrapped this way. Any other exception from a
    ///   parallel body stays an <see cref="AggregateException"/> and keeps its 500 - this is a
    ///   mapping fix, not a blanket unwrap.</para>
    /// </summary>
    internal static class NamespaceExceptionUnwrap
    {
        /// <summary>
        ///   <paramref name="exception"/> itself when it is a <typeparamref name="T"/>, otherwise the
        ///   first <typeparamref name="T"/> nested anywhere inside it (aggregates are flattened, so
        ///   nesting depth does not matter), otherwise <c>null</c>.
        /// </summary>
        internal static T FirstOrDefault<T>(Exception exception)
            where T : Exception
        {
            if (exception is T match)
            {
                return match;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (inner is T innerMatch)
                    {
                        return innerMatch;
                    }
                }
            }

            return null;
        }
    }
}
