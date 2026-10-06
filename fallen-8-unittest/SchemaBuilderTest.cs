// MIT License
//
// SchemaBuilderTest.cs
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
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The schema builder's own contract (feature mcp-plugin-gaps, spec section 3). The defect it
    ///   pins: <c>Add</c> used to overwrite an existing name, so a tool that declared one argument
    ///   twice advertised whichever declaration came last and nothing noticed.
    /// </summary>
    [TestClass]
    public class SchemaBuilderTest
    {
        [TestMethod]
        public void Add_SameNameTwice_Throws_NamingTheDuplicate()
        {
            var builder = SchemaBuilder.Create().Obj("properties", "a map");

            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => builder.ObjArray("properties", "a batch"));

            StringAssert.Contains(error.Message, "properties", "the message names the duplicated argument");
        }

        [TestMethod]
        public void Add_SameNameTwice_Throws_EvenWhenTheShapeIsIdentical()
        {
            // Declaring the same thing twice is still a defect in the tool, not a harmless repeat.
            var builder = SchemaBuilder.Create().Str("op", "the op", required: true);

            Assert.ThrowsExactly<InvalidOperationException>(() => builder.Str("op", "the op", required: true));
        }

        [TestMethod]
        public void IntArray_DeclaresIntegerItems()
        {
            var schema = SchemaBuilder.Create().IntArray("ids", "element ids").Build();

            var ids = schema.GetProperty("properties").GetProperty("ids");
            Assert.AreEqual("array", ids.GetProperty("type").GetString());
            Assert.AreEqual("integer", ids.GetProperty("items").GetProperty("type").GetString());
        }

        [TestMethod]
        public void Num_DeclaresANumber()
        {
            var schema = SchemaBuilder.Create().Num("maxPathWeight", "weight ceiling").Build();

            Assert.AreEqual("number",
                schema.GetProperty("properties").GetProperty("maxPathWeight").GetProperty("type").GetString());
        }

        [TestMethod]
        public void Build_ListsEachRequiredNameOnce()
        {
            var schema = SchemaBuilder.Create()
                .Str("a", "first", required: true)
                .Int("b", "second", required: true)
                .Build();

            var required = schema.GetProperty("required");
            Assert.AreEqual(2, required.GetArrayLength());
            Assert.AreEqual(JsonValueKind.Array, required.ValueKind);
        }
    }
}
