using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   Edge model.
    /// </summary>
    public sealed class EdgeModel : AGraphElement
    {
        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the <see cref="EdgeModel" /> class.
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='targetVertex'> Target vertex. </param>
        /// <param name='sourceVertex'> Source vertex. </param>
        /// <param name='properties'> Properties. </param>
        public EdgeModel(Int32 id, UInt32 creationDate, VertexModel targetVertex, VertexModel sourceVertex,
                         PropertyContainer[] properties)
            : base(id, creationDate, properties)
        {
            TargetVertex = targetVertex;
            SourceVertex = sourceVertex;
        }

        /// <summary>
        ///   Initializes a new instance of the EdgeModel class.
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='modificationDate'> Modification date. </param>
        /// <param name='targetVertex'> Target vertex. </param>
        /// <param name='sourceVertex'> Source vertex. </param>
        /// <param name='properties'> Properties. </param>
        internal EdgeModel(Int32 id, UInt32 creationDate, UInt32 modificationDate, VertexModel targetVertex,
                           VertexModel sourceVertex, PropertyContainer[] properties)
            : base(id, creationDate, properties)
        {
            TargetVertex = targetVertex;
            SourceVertex = sourceVertex;
            ModificationDate = modificationDate;
        }

        #endregion

        #region data

        /// <summary>
        ///   The target vertex.
        /// </summary>
        public readonly VertexModel TargetVertex;

        /// <summary>
        ///   The source vertex.
        /// </summary>
        public readonly VertexModel SourceVertex;

        #endregion

        #region overrides

        public override string ToString()
        {
            return Id.ToString(CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
