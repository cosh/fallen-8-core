// MIT License
//
// RootedNames.cs
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
using System.IO;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   THE ONE PRIMITIVE for every file this runtime opens: a caller supplies a bare NAME and a
    ///   configured root, and this resolves the one location that name may mean.
    ///
    ///   <para>A provider's file names arrive over the API from whoever can reach it, so a name able to
    ///   name a path is a name able to name anything this container can read. Both halves of the check are
    ///   load-bearing, and either alone is historically the bug: a shape check alone misses what the
    ///   platform normalises, and a prefix check on the resolved path alone misses a root that is itself a
    ///   prefix of a sibling directory (<c>/files</c> and <c>/files-old</c>), which is why the containment
    ///   test compares against the root WITH a trailing separator.</para>
    /// </summary>
    public static class RootedNames
    {
        /// <summary>
        ///   Resolves <paramref name="name"/> under <paramref name="root"/>.
        /// </summary>
        /// <param name="root">The configured directory. Never caller-supplied.</param>
        /// <param name="name">The bare file name, from a job or a setting.</param>
        /// <param name="what">What the name names, for the failure message (today always "file").</param>
        /// <param name="path">The resolved absolute path, when this returns true.</param>
        /// <param name="failure">Why the name was refused, when this returns false. Quotes the name, so
        /// a caller who mistyped one sees which value was rejected.</param>
        public static Boolean TryResolve(String? root, String? name, String what, out String? path, out String? failure)
        {
            path = null;

            if (String.IsNullOrWhiteSpace(root))
            {
                failure = String.Format("No {0} directory is configured.", what);
                return false;
            }

            if (String.IsNullOrWhiteSpace(name))
            {
                failure = String.Format("A {0} name is required.", what);
                return false;
            }

            var candidate = name!;

            // Shape first: a name is a NAME. Directory separators, the parent-directory segment, a
            // rooted path and any invalid file-name character are all refused before the platform gets
            // a chance to normalise them into something that passes a containment check.
            if (candidate.IndexOf('/') >= 0 || candidate.IndexOf('\\') >= 0)
            {
                failure = String.Format("The {0} name '{1}' may not contain a path separator.", what, candidate);
                return false;
            }

            if (candidate.Contains("..", StringComparison.Ordinal))
            {
                failure = String.Format("The {0} name '{1}' may not contain '..'.", what, candidate);
                return false;
            }

            if (Path.IsPathRooted(candidate))
            {
                failure = String.Format("The {0} name '{1}' may not be a rooted path.", what, candidate);
                return false;
            }

            if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                failure = String.Format("The {0} name '{1}' contains a character a file name may not have.",
                    what, candidate);
                return false;
            }

            // Then the resolved location, because the shape check speaks about the string and this one
            // speaks about the file the platform would actually open.
            String resolvedRoot;
            String resolved;
            try
            {
                resolvedRoot = Path.GetFullPath(root!);
                resolved = Path.GetFullPath(Path.Combine(resolvedRoot, candidate));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                failure = String.Format("The {0} name '{1}' does not resolve to a path.", what, candidate);
                return false;
            }

            var rootWithSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? resolvedRoot
                : resolvedRoot + Path.DirectorySeparatorChar;

            if (!resolved.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                failure = String.Format("The {0} name '{1}' resolves outside the configured directory.",
                    what, candidate);
                return false;
            }

            path = resolved;
            failure = null;
            return true;
        }
    }
}
