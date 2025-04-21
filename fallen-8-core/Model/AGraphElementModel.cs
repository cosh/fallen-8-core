// MIT License
//
// AGraphElement.cs
//
// Copyright (c) 2025 Henning Rauch
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
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   A graph element.
    /// </summary>
    public abstract class AGraphElementModel
    {
        #region Data

        /// <summary>
        ///   The identifier of this graph element.
        /// </summary>
        public Int32 Id;

        /// <summary>
        /// The label of the graph element
        /// </summary>
        public String Label;

        /// <summary>
        ///   The creation date.
        /// </summary>
        public readonly UInt32 CreationDate;

        /// <summary>
        ///   The modification date.
        /// </summary>
        public UInt32 ModificationDate;

        /// <summary>
        ///   The properties.
        /// </summary>
        private ImmutableDictionary<String, Object> _properties;

        /// <summary>
        ///  Defines if the object has been removed. If it is set to true then it will not be returned in searches
        /// </summary>
        internal bool _removed = false;

        #endregion

        #region constructor

        /// <summary>
        ///   Initializes a new instance of the <see cref="AGraphElementModel" /> class.
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='label'> Label. </param>
        /// <param name='properties'> Properties. </param>
        protected AGraphElementModel(Int32 id, UInt32 creationDate, String label = null, Dictionary<String, Object> properties = null)
        {
            Id = id;
            CreationDate = creationDate;
            ModificationDate = 0;
            _properties = properties != null ? properties.ToImmutableDictionary() : null;
            Label = label;
        }

        #endregion

        #region public methods

        /// <summary>
        ///  Gets the creation date
        /// </summary>
        /// <returns> Creation date </returns>
        public DateTime GetCreationDate()
        {
            return DateHelper.GetDateTimeFromUnixTimeStamp(CreationDate);
        }

        /// <summary>
        ///  Gets the modification date
        /// </summary>
        /// <returns> Modification date </returns>
        public DateTime GetModificationDate()
        {
            return DateHelper.GetDateTimeFromUnixTimeStamp(CreationDate + ModificationDate);
        }

        /// <summary>
        ///   Returns the count of properties
        /// </summary>
        /// <returns> Count of Properties </returns>
        public Int32 GetPropertyCount()
        {

            return _properties.Count;

        }

        /// <summary>
        ///   Gets all properties.
        /// </summary>
        /// <returns> All properties. </returns>
        public ImmutableDictionary<String, Object> GetAllProperties()
        {
            return _properties != null
                        ? _properties
                        : ImmutableDictionary.Create<String, Object>();
        }

        /// <summary>
        ///   Tries the get property.
        /// </summary>
        /// <typeparam name="TProperty"> Type of the property </typeparam>
        /// <param name="result"> Result. </param>
        /// <param name="propertyId"> Property identifier. </param>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        public Boolean TryGetProperty<TProperty>(out TProperty result, String propertyId)
        {
            if (_properties != null)
            {
                object pValue;
                if (_properties.TryGetValue(propertyId, out pValue))
                {
                    result = (TProperty)pValue;

                    return true;
                }
            }

            result = default(TProperty);

            return false;
        }

        #endregion

        #region internal methods

        /// <summary>
        ///   Trims the graph element
        /// </summary>
        internal virtual void Trim()
        {
            //do nothing
        }

        /// <summary>
        ///   Sets a property
        /// </summary>
        /// <returns> <c>true</c> if it was an update; otherwise, <c>false</c> . </returns>
        /// <param name='propertyId'> If set to <c>true</c> property identifier. </param>
        /// <param name='property'> If set to <c>true</c> property. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void SetProperty(String propertyId, object property)
        {
            _properties = _properties.Add(propertyId, property);

            ModificationDate = DateHelper.GetModificationDate(CreationDate);
        }

        /// <summary>
        ///   Tries to remove a property.
        /// </summary>
        /// <returns> <c>true</c> if the property was removed; otherwise, <c>false</c> if there was no such property. </returns>
        /// <param name='propertyId'> If set to <c>true</c> property identifier. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void RemoveProperty(String propertyId)
        {
            if (_properties != null)
            {
                if (_properties.ContainsKey(propertyId))
                {
                    _properties = _properties.Remove(propertyId);

                    ModificationDate = DateHelper.GetModificationDate(CreationDate);
                }
            }
        }

        /// <summary>
        /// Set the label for the graph element
        /// </summary>
        /// <param name="newLabel">The new Label</param>
        internal void SetLabel(String newLabel)
        {
            Label = newLabel;
        }

        /// <summary>
        /// Sets the id of the element
        /// </summary>
        /// <param name="newId">The new id</param>
        internal void SetId(Int32 newId)
        {
            Id = newId;
        }

        /// <summary>
        /// Marks the graph element as removed
        /// </summary>
        internal void MarkAsRemoved()
        {
            _removed = true;
        }

        // <summary>
        /// Marks the graph element as not removed
        /// </summary>
        internal void MarkAsNotRemoved()
        {
            _removed = false;
        }

        #endregion
    }
}
