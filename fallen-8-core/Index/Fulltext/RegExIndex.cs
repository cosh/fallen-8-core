// MIT License
//
// RegExIndex.cs
//
// Copyright (c) 2022 Henning Rauch
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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Index.Fulltext
{
    /// <summary>
	/// Regular expression index
	/// </summary>
    public sealed class RegExIndex : AThreadSafeElement, IFulltextIndex
    {
        #region Data

        /// <summary>
        /// The index dictionary.
        /// </summary>
        private Dictionary<String, ImmutableList<AGraphElementModel>> _idx;
        
        /// <summary>
        /// The description of the plugin
        /// </summary>
        private String _description = "A very very simple fulltext index using regular expressions";

        /// <summary>
        /// The logger
        /// </summary>
        private ILogger<RegExIndex> _logger;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the RegExIndex class.
        /// </summary>
        public RegExIndex()
        {
        }

        #endregion

        #region IFulltextIndex Members

        public bool TryQuery(out FulltextSearchResult result, string query)
        {
            var regexpression = new Regex(query, RegexOptions.IgnoreCase);
            var foundSth = false;
            result = null;

            if (ReadResource())
            {
                try
                {
                    var matchingGraphElements = new Dictionary<AGraphElementModel, NestedHighLightAndCounter>();
                    var currentScore = 0;
                    var maximumScore = 0;
                    const char whitespace = ' ';

                    foreach (var aKV in _idx)
                    {
                        var matches = regexpression.Matches(aKV.Key);

                        if (matches.Count > 0)
                        {
                            if (!foundSth)
                            {
                                result = new FulltextSearchResult();
                                foundSth = true;
                            }

                            var localHighlights = new HashSet<String>();
                            var countOfLocalHighlights = 0;
                            foreach (Match match in matches)
                            {
                                var currentPosition = -1;
                                var lastPosition = -1;

                                for (var i = 0; i < match.Index; i++)
                                {
                                    if (aKV.Key[i] == whitespace)
                                    {
                                        currentPosition = i;
                                    }

                                    if (currentPosition > lastPosition)
                                    {
                                        lastPosition = currentPosition;
                                    }
                                }

                                var firstWhitespacePrev = lastPosition;

                                lastPosition = -1;

                                for (var i = match.Index + match.Length; i < aKV.Key.Length; i++)
                                {
                                    if (aKV.Key[i] == whitespace)
                                    {
                                        lastPosition = i;
                                        break;
                                    }
                                }

                                var firstWhitespaceAfter = lastPosition;

                                if (firstWhitespacePrev == -1 && firstWhitespaceAfter == -1)
                                {
                                    localHighlights.Add(aKV.Key);
                                    countOfLocalHighlights++;
                                    continue;
                                }

                                if (firstWhitespacePrev == -1)
                                {
                                    localHighlights.Add(aKV.Key.Substring(0, firstWhitespaceAfter));
                                    countOfLocalHighlights++;
                                    continue;
                                }

                                if (firstWhitespaceAfter == -1)
                                {
                                    localHighlights.Add(aKV.Key.Substring(firstWhitespacePrev + 1));
                                    countOfLocalHighlights++;
                                    continue;
                                }

                                localHighlights.Add(aKV.Key.Substring(firstWhitespacePrev + 1, firstWhitespaceAfter - firstWhitespacePrev - 1));
                                countOfLocalHighlights++;
                            }

                            for (var i = 0; i < aKV.Value.Count; i++)
                            {
                                NestedHighLightAndCounter globalHighlights;
                                if (matchingGraphElements.TryGetValue(aKV.Value[i], out globalHighlights))
                                {
                                    globalHighlights.Highlights.UnionWith(localHighlights);
                                    currentScore = globalHighlights.NumberOfHighlights + countOfLocalHighlights;
                                }
                                else
                                {
                                    matchingGraphElements.Add(aKV.Value[i],
                                                              new NestedHighLightAndCounter
                                                              {
                                                                  Highlights = new HashSet<string>(localHighlights),
                                                                  NumberOfHighlights = countOfLocalHighlights
                                                              });
                                    currentScore = countOfLocalHighlights;
                                }

                                maximumScore = currentScore > maximumScore
                                        ? currentScore
                                        : maximumScore;
                            }
                        }
                    }

                    if (foundSth)
                    {
                        //create the result
                        result = new FulltextSearchResult
                        {
                            MaximumScore = maximumScore,
                            Elements = matchingGraphElements
                                .Select(aKV => new FulltextSearchResultElement(aKV.Key, aKV.Value.NumberOfHighlights, aKV.Value.Highlights))
                                .ToList()
                        };
                    }

                    return foundSth;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region public methods

        /// <summary>
        /// A method to query the regex index
        /// </summary>
        /// <param name="result">The number of matching graph elements (distinct)</param>
        /// <param name="query">The query</param>
        /// <param name="filter">The filter that should be applied</param>
        /// <returns>True if something has been found, otherwise false</returns>
        public bool TryQuery(out ImmutableList<AGraphElementModel> result, string query, Func<Regex, String, Boolean> filter)
        {
            var regexpression = new Regex(query, RegexOptions.IgnoreCase);
            result = null;

            if (ReadResource())
            {
                try
                {
                    var matchingGraphElements = new HashSet<AGraphElementModel>();

                    foreach (var aKV in _idx)
                    {
                        if (filter(regexpression, aKV.Key))
                        {
                            matchingGraphElements.UnionWith(aKV.Value);
                        }
                    }

                    result = ImmutableList.CreateRange<AGraphElementModel>(matchingGraphElements);

                    return matchingGraphElements.Count > 0;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region IIndex Members

        public int CountOfKeys()
        {
            if (ReadResource())
            {
                try
                {
                    var keyCount = _idx.Keys.Count;
                    return keyCount;
                }
                finally
                {
                    FinishReadResource();
                }

            }

            throw new CollisionException();
        }

        public int CountOfValues()
        {
            if (ReadResource())
            {
                try
                {
                    var valueCount = _idx.Values.SelectMany(_ => _).Count();
                    return valueCount;
                }
                finally
                {
                    FinishReadResource();
                }

            }

            throw new CollisionException();
        }

        public void AddOrUpdate(object keyObject, AGraphElementModel graphElement)
        {
            String key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                return;
            }

            if (WriteResource())
            {
                try
                {
                    ImmutableList<AGraphElementModel> values;
                    if (_idx.TryGetValue(key, out values))
                    {
                        values.Add(graphElement);
                    }
                    else
                    {
                        values =  ImmutableList.Create<AGraphElementModel>( graphElement );
                        _idx.Add(key, values);
                    }
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public bool TryRemoveKey(object keyObject)
        {
            String key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                return false;
            }

            if (WriteResource())
            {
                try
                {
                    var foundSth = _idx.Remove(key);
                    return foundSth;
                }
                finally
                {
                    FinishWriteResource();
                }

            }

            throw new CollisionException();
        }

        public void RemoveValue(AGraphElementModel graphElement)
        {
            if (WriteResource())
            {
                try
                {
                    var toBeRemovedKeys = new List<String>();

                    foreach (var aKv in _idx)
                    {
                        aKv.Value.Remove(graphElement);
                        if (aKv.Value.Count == 0)
                        {
                            toBeRemovedKeys.Add(aKv.Key);
                        }
                    }

                    toBeRemovedKeys.ForEach(_ => _idx.Remove(_));
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public void Wipe()
        {
            if (WriteResource())
            {
                try
                {
                    _idx.Clear();
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public IEnumerable<object> GetKeys()
        {
            if (ReadResource())
            {
                try
                {
                    var keys = new List<IComparable>(_idx.Keys);
                    return keys;
                }
                finally
                {
                    FinishReadResource();
                }

            }

            throw new CollisionException();
        }

        public IEnumerable<KeyValuePair<Object, ImmutableList<AGraphElementModel>>> GetKeyValues()
        {
            if (ReadResource())
            {
                try
                {
                    foreach (var aKv in _idx)
                        yield return new KeyValuePair<object, ImmutableList<AGraphElementModel>>(aKv.Key, aKv.Value);
                }
                finally
                {
                    FinishReadResource();
                }

                yield break;
            }

            throw new CollisionException();
        }

        public bool TryGetValue(out ImmutableList<AGraphElementModel> result, object keyObject)
        {
            String key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                result = null;
                return false;
            }

            if (ReadResource())
            {
                try
                {
                    ImmutableList<AGraphElementModel> graphElements;
                    var foundSth = _idx.TryGetValue(key, out graphElements);

                    result = foundSth ? graphElements : null;
                    return foundSth;
                }
                finally
                {
                    FinishReadResource();
                }

            }

            throw new CollisionException();
        }

        #endregion

        #region IPlugin Members

        public string PluginName
        {
            get { return "RegExIndex"; }
        }

        public Type PluginCategory
        {
            get { return typeof(IIndex); }
        }

        public string Description
        {
            get
            {
                return _description;
            }
        }

        public string Manufacturer
        {
            get { return "Henning Rauch"; }
        }

        public void Initialize(Fallen8 fallen8, IDictionary<string, object> parameter)
        {
            _idx = new Dictionary<String, ImmutableList<AGraphElementModel>>();
            _logger = fallen8._loggerFactory.CreateLogger<RegExIndex>();
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _idx.Clear();
            _idx = null;
        }

        #endregion

        #region IFallen8Serializable Members

        public void Save(SerializationWriter writer)
        {
            if (ReadResource())
            {
                try
                {
                    writer.Write(0);//parameter
                    writer.Write(_idx.Count);
                    foreach (var aKV in _idx)
                    {
                        writer.Write(aKV.Key);
                        writer.Write(aKV.Value.Count);
                        foreach (var aItem in aKV.Value)
                        {
                            writer.Write(aItem.Id);
                        }
                    }
                }
                finally
                {
                    FinishReadResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public void Load(SerializationReader reader, Fallen8 fallen8)
        {
            if (WriteResource())
            {
                try
                {
                    reader.ReadInt32();//parameter

                    var keyCount = reader.ReadInt32();

                    _idx = new Dictionary<String, ImmutableList<AGraphElementModel>>(keyCount);

                    for (var i = 0; i < keyCount; i++)
                    {
                        var key = reader.ReadString();
                        var value = new List<AGraphElementModel>();
                        var valueCount = reader.ReadInt32();
                        for (var j = 0; j < valueCount; j++)
                        {
                            var graphElementId = reader.ReadInt32();
                            AGraphElementModel graphElement;
                            if (fallen8.TryGetGraphElement(out graphElement, graphElementId))
                            {
                                value.Add(graphElement);
                            }
                            else
                            {
                                _logger.LogError(String.Format("Error while deserializing the index. Could not find the graph element \"{0}\"", graphElementId));
                            }
                        }
                        _idx.Add(key, ImmutableList.CreateRange<AGraphElementModel>(value));
                    }
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        #endregion

        #region private helper

        /// <summary>
        /// Generates the highlight.
        /// </summary>
        /// <returns>
        /// The highlight.
        /// </returns>
        /// <param name='value'>
        /// Value.
        /// </param>
        /// <param name="baseString">The base string </param>
        private static String GenerateHighlight(Match value, String baseString)
        {
            //die linken und rechten Nachbarn auch noch mit ausgeben (über whitespaces)
            const char whitespace = ' ';
            var firstWhitespacePrev = baseString.LastIndexOf(whitespace, 0, value.Index);
            var firstWhitespaceAfter = baseString.IndexOf(whitespace, value.Index + value.Length + 1);

            if (firstWhitespacePrev == -1 && firstWhitespaceAfter == -1)
            {
                return baseString;
            }

            if (firstWhitespacePrev == -1)
            {
                return baseString.Substring(0, firstWhitespaceAfter);
            }

            if (firstWhitespaceAfter == -1)
            {
                return baseString.Substring(firstWhitespacePrev);
            }

            return baseString.Substring(firstWhitespacePrev, firstWhitespaceAfter);
        }

        #endregion

        #region helper class

        /// <summary>
        /// Private nested class used to carry some highlightning information
        /// </summary>
        class NestedHighLightAndCounter
        {
            /// <summary>
            /// The highlights
            /// </summary>
            public HashSet<String> Highlights { get; set; }

            /// <summary>
            /// The number of highlights
            /// </summary>
            public Int32 NumberOfHighlights { get; set; }
        }

        #endregion
    }
}
