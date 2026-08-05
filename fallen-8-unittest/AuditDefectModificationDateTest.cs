// MIT License
//
// AuditDefectModificationDateTest.cs
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
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;
using EdgeDto = NoSQL.GraphDB.App.Controllers.Model.Edge;
using VertexDto = NoSQL.GraphDB.App.Controllers.Model.Vertex;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Audit defect B18: the REST projection of an element's modification stamp. The engine keeps
    ///   AGraphElementModel.ModificationDate as a DELTA in seconds since the element's creation
    ///   stamp (zero when never modified), so the DTO base must render creation + delta - exactly
    ///   what AGraphElementModel.GetModificationDate returns. It used to render the delta as if it
    ///   were absolute, reporting 1970-01-01 for every untouched element.
    /// </summary>
    [TestClass]
    public class AuditDefectModificationDateTest
    {
        /// <summary>The 1970 epoch every stamp in DateHelper is relative to.</summary>
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0);

        /// <summary>A fixed, safely-in-the-past creation stamp (2025-04-22 10:00 on the local clock the engine stamps with).</summary>
        private static readonly UInt32 PastStamp = DateHelper.ConvertDateTime(new DateTime(2025, 4, 22, 10, 0, 0));

        /// <summary>A second, distinctly different creation stamp, so a wrong creation term is visible.</summary>
        private static readonly UInt32 OtherPastStamp = DateHelper.ConvertDateTime(new DateTime(2024, 1, 2, 3, 4, 5));

        private Fallen8 _fallen8;

        [TestInitialize]
        public void Setup()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void Teardown()
        {
            _fallen8?.Dispose();
        }

        #region helpers

        private int AddVertex(UInt32 creationDate, String label = "person")
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, label);
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices()[0].Id;
        }

        private int AddEdge(int source, int target, UInt32 creationDate)
        {
            var tx = new CreateEdgesTransaction();
            tx.AddEdge(source, "knows", target, creationDate, "knows");
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedEdges()[0].Id;
        }

        private void SetProperty(int elementId, String key, Object value)
        {
            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = elementId, PropertyId = key, Property = value }
            }).WaitUntilFinished();
        }

        private AGraphElementModel Element(int id)
        {
            Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, id), "the element must be readable");
            return element;
        }

        private VertexDto ProjectVertex(int id)
        {
            return new VertexDto((VertexModel)Element(id));
        }

        private EdgeDto ProjectEdge(int id)
        {
            return new EdgeDto((EdgeModel)Element(id));
        }

        #endregion

        #region never modified: the case that was silently wrong

        [TestMethod]
        public void NeverModifiedVertex_ProjectsModificationDateEqualToCreationDate()
        {
            var id = AddVertex(PastStamp);
            var model = (VertexModel)Element(id);
            Assert.AreEqual(0u, model.ModificationDate, "an untouched element carries a ZERO delta, not an absolute stamp");

            var dto = ProjectVertex(id);

            Assert.AreEqual(new DateTime(2025, 4, 22, 10, 0, 0), dto.CreationDate);
            Assert.AreEqual(dto.CreationDate, dto.ModificationDate,
                "never modified means last-modified == created");
            Assert.AreEqual(model.GetModificationDate(), dto.ModificationDate,
                "the DTO must agree with the engine's own accessor");
            Assert.AreNotEqual(Epoch, dto.ModificationDate,
                "the delta must not be rendered as an absolute stamp (defect B18: reported 1970)");
        }

        [TestMethod]
        public void NeverModifiedEdge_ProjectsModificationDateEqualToCreationDate()
        {
            var source = AddVertex(PastStamp);
            var target = AddVertex(PastStamp);
            var id = AddEdge(source, target, OtherPastStamp);
            var model = (EdgeModel)Element(id);
            Assert.AreEqual(0u, model.ModificationDate);

            var dto = ProjectEdge(id);

            Assert.AreEqual(new DateTime(2024, 1, 2, 3, 4, 5), dto.CreationDate);
            Assert.AreEqual(dto.CreationDate, dto.ModificationDate);
            Assert.AreEqual(model.GetModificationDate(), dto.ModificationDate);
            Assert.AreNotEqual(Epoch, dto.ModificationDate);
        }

        #endregion

        #region modified through the engine's real stamping path

        [TestMethod]
        public void ModifiedVertex_ProjectsCreationPlusDelta_LandingAtNow()
        {
            var id = AddVertex(PastStamp);

            var before = DateHelper.GetNowStamp();
            SetProperty(id, "city", "Berlin");
            var after = DateHelper.GetNowStamp();

            var model = (VertexModel)Element(id);
            Assert.IsTrue(model.ModificationDate > 0u, "a property set stamps a non-zero delta");

            var dto = ProjectVertex(id);

            Assert.AreEqual(model.GetModificationDate(), dto.ModificationDate,
                "the DTO must agree with the engine's own accessor");
            Assert.AreEqual(dto.CreationDate.AddSeconds(model.ModificationDate), dto.ModificationDate,
                "the projection is creation + delta");
            Assert.IsTrue(dto.ModificationDate > dto.CreationDate,
                "a modified element was modified AFTER it was created");

            // The stamp really is "now" on the engine's clock, not 1970 + delta (which the defect
            // produced: a 2025-created element reported a 1971 modification date).
            Assert.IsTrue(dto.ModificationDate >= DateHelper.GetDateTimeFromUnixTimeStamp(before),
                "the modification date is not before the write started");
            Assert.IsTrue(dto.ModificationDate <= DateHelper.GetDateTimeFromUnixTimeStamp(after),
                "the modification date is not after the write finished");
        }

        [TestMethod]
        public void ModifiedEdge_ProjectsCreationPlusDelta_AndLeavesItsVerticesUntouched()
        {
            var source = AddVertex(PastStamp);
            var target = AddVertex(PastStamp);
            var edge = AddEdge(source, target, OtherPastStamp);

            var before = DateHelper.GetNowStamp();
            SetProperty(edge, "since", 2024);
            var after = DateHelper.GetNowStamp();

            var edgeModel = (EdgeModel)Element(edge);
            Assert.IsTrue(edgeModel.ModificationDate > 0u);

            var edgeDto = ProjectEdge(edge);
            Assert.AreEqual(edgeModel.GetModificationDate(), edgeDto.ModificationDate);
            Assert.AreEqual(edgeDto.CreationDate.AddSeconds(edgeModel.ModificationDate), edgeDto.ModificationDate);
            Assert.IsTrue(edgeDto.ModificationDate >= DateHelper.GetDateTimeFromUnixTimeStamp(before));
            Assert.IsTrue(edgeDto.ModificationDate <= DateHelper.GetDateTimeFromUnixTimeStamp(after));

            // Each element carries its OWN creation term: the untouched source vertex, created at a
            // different stamp than the edge, still projects its own creation date.
            var sourceDto = ProjectVertex(source);
            Assert.AreEqual(new DateTime(2025, 4, 22, 10, 0, 0), sourceDto.ModificationDate);
            Assert.AreEqual(sourceDto.CreationDate, sourceDto.ModificationDate,
                "writing to the edge must not move the vertex's modification date");
            Assert.AreNotEqual(edgeDto.CreationDate, sourceDto.CreationDate,
                "the two elements deliberately have different creation stamps");
        }

        [TestMethod]
        public void RemovingAProperty_AlsoStampsTheDelta_AndProjectsIt()
        {
            var id = AddVertex(PastStamp, "company");
            SetProperty(id, "city", "Berlin");
            var afterSet = ((VertexModel)Element(id)).ModificationDate;

            _fallen8.EnqueueTransaction(new RemovePropertyTransaction { GraphElementId = id, PropertyId = "city" })
                .WaitUntilFinished();

            var model = (VertexModel)Element(id);
            Assert.IsTrue(model.ModificationDate >= afterSet, "a removal re-stamps the delta");

            var dto = ProjectVertex(id);
            Assert.AreEqual(model.GetModificationDate(), dto.ModificationDate);
            Assert.IsTrue(dto.ModificationDate > dto.CreationDate);
        }

        #endregion

        #region synthetic deltas: boundaries and the historically coinciding case

        [TestMethod]
        public void Projection_MatchesTheEngineAccessor_ForEveryCreationAndDeltaCombination()
        {
            // (creation stamp, modification delta) -> expected absolute modification date.
            var cases = new List<Tuple<UInt32, UInt32, DateTime>>
            {
                // A genuine epoch element with no modification: 1970 is the CORRECT answer here, and
                // it is the one case where the old and the fixed rendering coincide (this is why the
                // published doc example never showed the defect).
                Tuple.Create(0u, 0u, Epoch),
                // An epoch element modified 5 seconds after creation.
                Tuple.Create(0u, 5u, Epoch.AddSeconds(5)),
                // The realistic cases: an untouched and a touched element with a real creation stamp.
                Tuple.Create(PastStamp, 0u, new DateTime(2025, 4, 22, 10, 0, 0)),
                Tuple.Create(PastStamp, 90u, new DateTime(2025, 4, 22, 10, 1, 30)),
                Tuple.Create(1u, 0u, Epoch.AddSeconds(1)),
                // The top of the representable range: creation + delta is exactly UInt32.MaxValue,
                // so the sum still fits and must match the engine accessor bit for bit.
                Tuple.Create(UInt32.MaxValue - 50u, 50u, DateHelper.GetDateTimeFromUnixTimeStamp(UInt32.MaxValue)),
            };

            foreach (var testCase in cases)
            {
                var creation = testCase.Item1;
                var delta = testCase.Item2;
                var expected = testCase.Item3;
                var because = "creation=" + creation + ", delta=" + delta;

                var vertexModel = new VertexModel(1, creation, "person");
                vertexModel.ModificationDate = delta;
                var vertexDto = new VertexDto(vertexModel);

                Assert.AreEqual(DateHelper.GetDateTimeFromUnixTimeStamp(creation), vertexDto.CreationDate, because);
                Assert.AreEqual(expected, vertexDto.ModificationDate, because);
                Assert.AreEqual(vertexModel.GetModificationDate(), vertexDto.ModificationDate, because);

                // Edge.cs shares the same DTO base constructor, so it must render identically.
                var edgeModel = new EdgeModel(2, creation, vertexModel, vertexModel, "knows", "knows");
                edgeModel.ModificationDate = delta;
                var edgeDto = new EdgeDto(edgeModel);

                Assert.AreEqual(expected, edgeDto.ModificationDate, because);
                Assert.AreEqual(edgeModel.GetModificationDate(), edgeDto.ModificationDate, because);
            }
        }

        [TestMethod]
        public void Projection_OfAPropertylessElement_StillCarriesBothDates()
        {
            // The properties-empty branch of the DTO base constructor: no properties must not stop
            // the timestamps from being projected.
            var model = new VertexModel(7, PastStamp);
            var dto = new VertexDto(model);

            Assert.AreEqual(0, dto.Properties.Count);
            Assert.AreEqual(new DateTime(2025, 4, 22, 10, 0, 0), dto.CreationDate);
            Assert.AreEqual(dto.CreationDate, dto.ModificationDate);
        }

        [TestMethod]
        public void AnUnknownElement_IsNotReadable_SoNothingIsProjected()
        {
            AddVertex(PastStamp);

            Assert.IsFalse(_fallen8.TryGetGraphElement(out var missing, 987654),
                "an absent id reports false through the Try* contract instead of throwing");
            Assert.IsNull(missing);
        }

        #endregion
    }
}
