// MIT License
//
// SingleValueIndex.cs
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

#region Usings

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

#endregion

namespace NoSQL.GraphDB.Core.Index
{
    /// <summary>
    /// Single value index.
    /// </summary>
    public sealed class SingleValueIndex : AThreadSafeElement, IIndex
    {
        #region Data

        /// <summary>
        /// The index dictionary.
        /// </summary>
        private Dictionary<IComparable, AGraphElementModel> _idx;

        /// <summary>
        /// The description of the plugin
        /// </summary>
        private String _description = "A very simple single value index.";

        /// <summary>
        /// The logger
        /// </summary>
        private ILogger<SingleValueIndex> _logger;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the SingleValueIndex class.
        /// </summary>
        public SingleValueIndex()
        {
        }

        #endregion

        #region IIndex implementation

        public Int32 CountOfKeys()
        {
            if (ReadResource())
            {
                var count = _idx.Count;

                FinishReadResource();

                return count;
            }

            throw new CollisionException();
        }

        public Int32 CountOfValues()
        {
            if (ReadResource())
            {
                var count = _idx.Count;

                FinishReadResource();

                return count;
            }

            throw new CollisionException();
        }

        public void AddOrUpdate(Object keyObject, AGraphElementModel graphElement)
        {
            IComparable key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                return;
            }

            if (WriteResource())
            {
                _idx[key] = graphElement;

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }

        public bool TryRemoveKey(Object keyObject)
        {
            IComparable key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                return false;
            }

            if (WriteResource())
            {
                var removed = _idx.Remove(key);

                FinishWriteResource();

                return removed;
            }

            throw new CollisionException();
        }

        public void RemoveValue(AGraphElementModel graphElement)
        {
            if (WriteResource())
            {
                var toBeRemovedKeys = (from aKv in _idx where ReferenceEquals(aKv.Value, graphElement) select aKv.Key).ToList();

                toBeRemovedKeys.ForEach(_ => _idx.Remove(_));

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }

        public void Wipe()
        {
            if (WriteResource())
            {
                _idx.Clear();

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }

        public IEnumerable<Object> GetKeys()
        {
            if (ReadResource())
            {
                var keys = new List<IComparable>(_idx.Keys);

                FinishReadResource();

                return keys;
            }

            throw new CollisionException();
        }


        public IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues()
        {
            if (ReadResource())
            {
                try
                {
                    foreach (var aKv in _idx)
                        yield return new KeyValuePair<object, ImmutableList<AGraphElementModel>>(aKv.Key, ImmutableList.Create<AGraphElementModel>(aKv.Value));

                }
                finally
                {
                    FinishReadResource();
                }

                yield break;
            }

            throw new CollisionException();
        }

        public bool TryGetValue(out ImmutableList<AGraphElementModel> result, Object keyObject)
        {
            IComparable key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                result = null;

                return false;
            }

            if (ReadResource())
            {
                AGraphElementModel element;
                var foundSth = _idx.TryGetValue(key, out element);

                result = foundSth ? ImmutableList.Create<AGraphElementModel>(element) : null;

                FinishReadResource();

                return foundSth;
            }

            throw new CollisionException();
        }
        #endregion

        #region IFallen8Serializable

        public void Save(SerializationWriter writer)
        {
            if (ReadResource())
            {
                writer.Write(0);//parameter
                writer.Write(_idx.Count);
                foreach (var aKV in _idx)
                {
                    writer.WriteObject(aKV.Key);
                    writer.Write(aKV.Value.Id);
                }

                FinishReadResource();

                return;
            }

            throw new CollisionException();
        }

        public void Load(SerializationReader reader, Fallen8 fallen8)
        {
            if (WriteResource())
            {
                reader.ReadInt32();//parameter

                var keyCount = reader.ReadInt32();

                _idx = new Dictionary<IComparable, AGraphElementModel>(keyCount);

                for (var i = 0; i < keyCount; i++)
                {
                    var key = reader.ReadObject();
                    var graphElementId = reader.ReadInt32();
                    AGraphElementModel graphElement;
                    if (fallen8.TryGetGraphElement(out graphElement, graphElementId))
                    {
                        _idx.Add((IComparable)key, graphElement);
                    }
                    else
                    {
                        _logger.LogError(String.Format("[SingleValueIndex] Error while deserializing the index. Could not find the graph element \"{0}\"", graphElementId));
                    }
                }

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }

        #endregion

        #region IPlugin implementation

        public void Initialize(Fallen8 fallen8, IDictionary<string, object> parameter)
        {
            _idx = new Dictionary<IComparable, AGraphElementModel>();
            _logger = fallen8._loggerFactory.CreateLogger<SingleValueIndex>();
        }

        public string PluginName
        {
            get { return "SingleValueIndex"; }
        }

        public Type PluginType
        {
            get { return typeof(SingleValueIndex); }
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

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _idx.Clear();
            _idx = null;
        }

        #endregion

        #region public methods

        /// <summary>
        /// Gets a value from the index
        /// </summary>
        /// <param name="result">Result</param>
        /// <param name="key">Key</param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        public bool TryGetValue(out AGraphElementModel result, IComparable key)
        {
            if (ReadResource())
            {
                var value = _idx.TryGetValue(key, out result);

                FinishReadResource();

                return value;
            }

            throw new CollisionException();
        }

        /// <summary>
        /// The values.
        /// </summary>
        public List<AGraphElementModel> Values()
        {
            if (ReadResource())
            {
                var values = new List<AGraphElementModel>(_idx.Values);

                FinishReadResource();

                return values;
            }

            throw new CollisionException();
        }

        #endregion
    }
}

