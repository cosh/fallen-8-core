// MIT License
//
// FlatSchemaCheckerTest.cs
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
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The checker is test support that another test's verdict rests on, so it gets one accepted
    ///   and one rejected case per rule: a checker that accepts everything would turn
    ///   <c>EveryTool_SampleCall_ValidatesAgainstItsAdvertisedSchema</c> into a false green.
    /// </summary>
    [TestClass]
    public class FlatSchemaCheckerTest
    {
        private static readonly JsonElement Schema = SchemaBuilder.Create()
            .Str("op", "the op", required: true, choices: new[] { "a", "b" })
            .Int("id", "an id")
            .Num("weight", "a number")
            .Bool("flag", "a flag")
            .Obj("map", "a map")
            .ObjArray("rows", "rows")
            .IntArray("ids", "ids")
            .NumArray("vector", "vector")
            .StrArray("names", "names")
            .Any("value", "anything")
            .Build();

        private static String[] Check(String json)
        {
            return FlatSchemaChecker.Violations(Schema, JsonDocument.Parse(json).RootElement).ToArray();
        }

        [TestMethod]
        public void AValidCall_HasNoViolations()
        {
            var violations = Check("{\"op\":\"a\",\"id\":3,\"weight\":1.5,\"flag\":true,\"map\":{\"k\":1}," +
                "\"rows\":[{\"x\":1}],\"ids\":[1,2],\"vector\":[0.1,2],\"names\":[\"n\"],\"value\":[1,\"mixed\"]}");

            Assert.AreEqual(0, violations.Length, String.Join("; ", violations));
        }

        [TestMethod]
        public void MissingRequired_IsAViolation()
        {
            StringAssert.Contains(String.Join(";", Check("{\"id\":1}")), "required argument 'op' is missing");
        }

        [TestMethod]
        public void UndeclaredArgument_IsAViolation_BecauseAdditionalPropertiesIsFalse()
        {
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"nope\":1}")), "'nope' is not declared");
        }

        [TestMethod]
        public void WrongScalarType_IsAViolation()
        {
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"id\":\"three\"}")), "'id' must be integer");
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"id\":1.5}")), "'id' must be integer");
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"flag\":\"yes\"}")), "'flag' must be boolean");
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"weight\":\"heavy\"}")), "'weight' must be number");
        }

        [TestMethod]
        public void EnumMismatch_IsAViolation()
        {
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"zzz\"}")), "'op' is 'zzz', not one of");
        }

        [TestMethod]
        public void ObjectVersusArray_IsAViolation_BothWays()
        {
            // The exact defect the f8_mutate schema had: a map declared where an array was meant and
            // the other way round.
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"map\":[1]}")), "'map' must be object");
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"rows\":{\"x\":1}}")), "'rows' must be array");
        }

        [TestMethod]
        public void ArrayItemType_IsChecked()
        {
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"ids\":[1,{\"id\":2}]}")), "'ids[1]' must be integer");
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"rows\":[1]}")), "'rows[0]' must be object");
            StringAssert.Contains(String.Join(";", Check("{\"op\":\"a\",\"names\":[1]}")), "'names[0]' must be string");
        }

        [TestMethod]
        public void UntypedAny_AcceptsEveryKind()
        {
            foreach (var literal in new[] { "1", "\"s\"", "true", "null", "{\"k\":1}", "[1]" })
            {
                Assert.AreEqual(0, Check($"{{\"op\":\"a\",\"value\":{literal}}}").Length, "Any must accept " + literal);
            }
        }

        [TestMethod]
        public void NonObjectArguments_AreAViolation()
        {
            var violations = FlatSchemaChecker.Violations(Schema, JsonDocument.Parse("[1]").RootElement);

            Assert.AreEqual(1, violations.Count);
            StringAssert.Contains(violations[0], "must be an object");
        }
    }
}
