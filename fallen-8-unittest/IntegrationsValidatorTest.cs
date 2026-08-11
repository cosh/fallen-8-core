// MIT License
//
// IntegrationsValidatorTest.cs
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
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The verdict <see cref="SnapshotValidator"/> reaches on one snapshot (feature integrations,
    ///   spec section 9), with one test per diagnostic code the contract names, because a code is a stable
    ///   promise to whoever reads a job report.
    ///
    ///   <para>Three levels, three different consequences, and mixing them up loses data. An ENVELOPE error
    ///   is fatal: nothing is applied and nothing is withdrawn, because applying part of a document whose
    ///   one field licensing deletion is broken would be guessing. An ENTITY error skips exactly one
    ///   entity, because one bad row of a spreadsheet must not leave every other row unobserved. A CLAIM
    ///   error drops one claim and KEEPS the entity, because a skipped entity is not claimed this round, so
    ///   in a complete snapshot it is withdrawn and then deleted - and a human-typed hostname column must
    ///   never delete a device whose MAC was perfectly good.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsValidatorTest
    {
        private const String Provider = "csv-device-list";
        private const String Instance = "garage";
        private const String CanaryKind = "device";
        private const String CanaryMacRaw = "44:D2:44:AA:BB:CC";
        private const String CanaryMacKey = "mac:44d244aabbcc";

        private SnapshotValidator _validator;

        [TestInitialize]
        public void TestInitialize()
        {
            _validator = new SnapshotValidator(IdentifierVocabulary.Shipped);
        }

        // --- ENVELOPE: each one fatal, so nothing of the document lands ------------------------------

        [TestMethod]
        public void UnsupportedSchemaVersion_LeavesTheWholeDocumentUnapplied()
        {
            var document = EnvelopeTestDocument();
            document.SchemaVersion = 7;

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "a document written against a later contract would be read with the fields this one happens " +
                "to recognise, so whatever the newer contract added lands as silence - and silence in a " +
                "complete snapshot withdraws and deletes");
            var diagnostic = Only(result, DiagnosticCodes.UnsupportedSchemaVersion,
                "without this code a caller cannot tell a version refusal from a broken source, and retries " +
                "a document that will never be accepted");
            StringAssert.Contains(diagnostic.Message, "schemaVersion",
                "the message has to name the field, or an author fixing the provider guesses");
            StringAssert.Contains(diagnostic.Message, "the document declares 7",
                "quoting the declared version is what tells an author which side is behind");
        }

        [TestMethod]
        public void MissingProviderId_LeavesTheWholeDocumentUnapplied()
        {
            var document = EnvelopeTestDocument();
            document.ProviderId = null;

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "the provider id is a segment of every provider-scoped claim key, so composing keys without " +
                "it would give two providers' different values one key and advertise an overlap that does " +
                "not exist");
            var diagnostic = Only(result, DiagnosticCodes.MissingProviderId,
                "a document nobody can attribute must be refused under its own code rather than failing " +
                "later as an unexplained key");
            StringAssert.Contains(diagnostic.Message, "no provider",
                "the message names what is absent, or the reader has to diff the document against the schema");
        }

        [TestMethod]
        public void MissingInstanceId_LeavesTheWholeDocumentUnapplied()
        {
            var document = EnvelopeTestDocument();
            document.IntegrationInstanceId = null;

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "every element a run creates carries $claim:<instanceId>, and reconciliation is a set " +
                "difference over that property, so applying a document with no identity leaves elements " +
                "claimed by nobody that no later run will ever withdraw");
            var diagnostic = Only(result, DiagnosticCodes.MissingInstanceId,
                "the identity is the one thing a run cannot infer, so its absence has to be its own refusal");
            StringAssert.Contains(diagnostic.Message, "no integration instance",
                "the message names what is absent, or the reader has to diff the document against the schema");
        }

        [TestMethod]
        public void AnInstanceIdWhoseShapeCouldComposeAnotherIdentitysKey_IsRefusedAtTheEnvelope()
        {
            var document = EnvelopeTestDocument();
            document.IntegrationInstanceId = "garage:one";

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "the id is substituted into a claim key of the form <type>@<instanceId>:<canonical> and into " +
                "derived edge keys joined with a pipe, so a colon lets two identities compose one identical " +
                "key and one run then resolves into and reconciles away another integration's elements");
            var diagnostic = Only(result, DiagnosticCodes.MalformedInstanceId,
                "an unusable shape has to be refused as loudly as an absent id, because the damage it does " +
                "is silent and belongs to somebody else's data");
            StringAssert.Contains(diagnostic.Message, "not a valid integration instance id",
                "the message has to say the shape is the problem, or the caller keeps re-sending the same id");
            Assert.AreEqual("garage:one", diagnostic.Subject,
                "the subject quotes the offending id, which is the only way the caller finds it in its own " +
                "configuration");
        }

        [TestMethod]
        public void MalformedCapturedAt_LeavesTheWholeDocumentUnapplied()
        {
            var document = EnvelopeTestDocument();
            document.CapturedAt = "yesterday";

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "a document that cannot say when it looked is a document nobody can place against the graph " +
                "it is about, and guessing the instant would put a run's whole assertion behind a fiction");
            var diagnostic = Only(result, DiagnosticCodes.MalformedCapturedAt,
                "a value that is not an instant must surface under its own code rather than as a " +
                "deserialization failure with no subject");
            StringAssert.Contains(diagnostic.Message, "is not an instant",
                "the message says what was expected, or the author cannot tell a format problem from a " +
                "missing field");
            Assert.AreEqual("yesterday", diagnostic.Subject,
                "the subject quotes the value, which is what an author greps their provider for");
        }

        [TestMethod]
        public void MissingCompleteness_LeavesTheWholeDocumentUnapplied()
        {
            var document = EnvelopeTestDocument();
            document.Completeness = null;

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "completeness is the ONE field that licenses a withdrawal, and the zero value is taken by " +
                "Unspecified precisely so a provider that forgot to declare cannot inherit either of the " +
                "other two and have its silence delete the graph");
            var diagnostic = Only(result, DiagnosticCodes.MissingCompleteness,
                "a document with no declaration must be refused under its own code, not defaulted");
            Assert.AreEqual(SnapshotCompleteness.Unspecified, result.Completeness,
                "the refused verdict must still read Unspecified, because a caller inspecting it must never " +
                "see a licence the document did not give");
            StringAssert.Contains(diagnostic.Message, "licenses a withdrawal",
                "the message has to say why this field is fatal, or the next author adds a default");
        }

        [TestMethod]
        public void UnknownCompleteness_LeavesTheWholeDocumentUnapplied()
        {
            var document = EnvelopeTestDocument();
            document.Completeness = "everything";

            var result = _validator.Validate(document);

            AssertNothingWasApplied(result,
                "a word this contract does not know cannot be read as either declaration, and reading it as " +
                "complete would withdraw and delete everything the run did not mention");
            var diagnostic = Only(result, DiagnosticCodes.UnknownCompleteness,
                "an unrecognised word is a different fix from an absent one, so it carries its own code");
            Assert.AreEqual(SnapshotCompleteness.Unspecified, result.Completeness,
                "an unparsed word must leave the licence at Unspecified, or a caller acts on a declaration " +
                "nobody made");
            StringAssert.Contains(diagnostic.Message, "neither 'complete' nor 'partial'",
                "naming both accepted words is what makes this fixable without reading the spec");
            Assert.AreEqual("everything", diagnostic.Subject,
                "the subject quotes the word, which is what an author greps their provider for");
        }

        [TestMethod]
        public void CompletenessOverDeclared_RefusesTheDocument_RatherThanTrustingTheProvider()
        {
            var cannotSeeEverything = new ProviderDescriptor
            {
                Id = "event-stream",
                CanObserveCompleteState = false,
            };
            var document = EnvelopeTestDocument();
            document.Declares = SnapshotCompleteness.Complete;

            var result = _validator.Validate(document, cannotSeeEverything);

            AssertNothingWasApplied(result,
                "trusting a provider that already said it cannot see the whole source is the worst outcome " +
                "in the feature: every unobserved element becomes a withdrawal and the graph deletes what " +
                "the source still has");
            var diagnostic = Only(result, DiagnosticCodes.CompletenessOverDeclared,
                "the refusal needs its own code, because the fix is in the provider's descriptor rather than " +
                "in the document");
            StringAssert.Contains(diagnostic.Message,
                "withdraw every element the run did not see and delete what the source still has",
                "the message must state the consequence, or a reader 'fixes' it by flipping " +
                "canObserveCompleteState and loses the elements the run never saw");
            Assert.AreEqual("event-stream", diagnostic.Subject,
                "the subject names the provider whose descriptor and snapshot disagree, or the operator has " +
                "no idea which integration to look at");

            var partial = ValidDocument();
            partial.Declares = SnapshotCompleteness.Partial;
            var partialResult = _validator.Validate(partial, cannotSeeEverything);
            Assert.IsTrue(partialResult.EnvelopeAccepted,
                "the refusal is scoped to an over-declaration: a provider that cannot see everything and " +
                "says so honestly must still be able to land its entities, or the partial declaration that " +
                "keeps event-driven sources addable is worthless");

            var honest = new ProviderDescriptor { Id = "csv-device-list", CanObserveCompleteState = true };
            Assert.IsTrue(_validator.Validate(ValidDocument(), honest).EnvelopeAccepted,
                "a provider that CAN observe complete state must be able to declare complete, or " +
                "reconciliation never runs and the graph keeps everything the source deleted");
        }

        [TestMethod]
        public void AValidEnvelope_IsAcceptedForBothCompletenessWords_AndTheTypedDeclaresRoundTrips()
        {
            var complete = ValidDocument();
            complete.Declares = SnapshotCompleteness.Complete;
            Assert.AreEqual(SnapshotCompletenessWords.Complete, complete.Completeness,
                "the typed setter must write the WIRE word, or a C# provider produces a document the " +
                "validator refuses as unknownCompleteness");
            Assert.AreEqual(SnapshotCompleteness.Complete, complete.Declares,
                "what a provider set has to read back, or an author cannot tell what their own snapshot says");

            var completeResult = _validator.Validate(complete);
            Assert.IsTrue(completeResult.EnvelopeAccepted,
                "a document that satisfies the envelope must be applied, or a correct integration writes " +
                "nothing at all");
            Assert.AreEqual(SnapshotCompleteness.Complete, completeResult.Completeness,
                "reconciliation reads its licence off this value: Unspecified here silently switches " +
                "withdrawal off and the graph keeps everything the source deleted");
            Assert.AreEqual(1, completeResult.AcceptedEntities,
                "the canary entity has a kind, a strong claim and a namespaced property, so nothing about it " +
                "may be refused");
            Assert.AreEqual(0, completeResult.Diagnostics.Length,
                "a clean document must produce no diagnostic at all, or a reader learns to ignore the list " +
                "and misses the one that costs an element");

            var partial = ValidDocument();
            partial.Declares = SnapshotCompleteness.Partial;
            Assert.AreEqual(SnapshotCompletenessWords.Partial, partial.Completeness,
                "the typed setter must write the wire word for partial too");
            var partialResult = _validator.Validate(partial);
            Assert.IsTrue(partialResult.EnvelopeAccepted, "partial is a legitimate declaration, not a fault");
            Assert.AreEqual(SnapshotCompleteness.Partial, partialResult.Completeness,
                "mistaking partial for complete withdraws and deletes everything one incremental delivery " +
                "did not happen to mention");

            var unspecified = ValidDocument();
            unspecified.Declares = SnapshotCompleteness.Unspecified;
            Assert.IsNull(unspecified.Completeness,
                "the zero value must round-trip to NO word, or a provider that declared nothing ships a " +
                "document claiming it saw everything");

            var odd = ValidDocument();
            odd.Completeness = "everything";
            Assert.AreEqual(SnapshotCompleteness.Unspecified, odd.Declares,
                "the typed view must not guess at an unknown word; that is exactly why the validator reads " +
                "the raw word and can tell unknownCompleteness from missingCompleteness");
        }

        [TestMethod]
        public void ANullDocument_Raises_RatherThanReadingAsASnapshotThatSawNothing()
        {
            Assert.ThrowsException<ArgumentNullException>(() => _validator.Validate(null),
                "'I could not look' must never become 'there is nothing there': a null document read as an " +
                "empty snapshot would carry no completeness either, and the day that defaulted to complete " +
                "the run would withdraw every claim the instance ever made and delete the elements");
        }

        // --- ENTITY level: exactly one entity is skipped, the rest of the document still lands -------

        [TestMethod]
        public void MissingEntityKind_SkipsOnlyThatEntity()
        {
            var nameless = Entity(null, Claim("mac", "44:D2:44:AA:BB:CD"));

            var result = _validator.Validate(Document(Canary(), nameless));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:CD",
                "the kind becomes the element's label, so an entity without one would land as an unlabelled " +
                "element no query written against this integration can find");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.MissingEntityKind,
                "the skip has to name its reason, or an author sees a row vanish with no way to fix it");
            StringAssert.Contains(diagnostic.Message, "no kind",
                "the message names the absent field, or the author starts guessing at the claim");
        }

        [TestMethod]
        public void EntityWithoutIdentity_SkipsOnlyThatEntity()
        {
            var anonymous = Entity(CanaryKind);
            anonymous.Properties["csv.name"] = "an unidentifiable row";

            var result = _validator.Validate(Document(Canary(), anonymous));

            AssertExactlyOneEntitySkipped(result, "entity#1",
                "an entity nothing can resolve to would be created again by every single run, and each run's " +
                "reconciliation would withdraw the copy the last run made: unbounded churn from one row");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.EntityWithoutIdentity,
                "the one row nobody can identify has to be named, because it is the row a source owner has " +
                "to go and fix");
            StringAssert.Contains(diagnostic.Message, "every run would create another copy",
                "the message must state the consequence, or the next author 'helpfully' creates the element " +
                "anyway");
        }

        [TestMethod]
        public void UnknownStrengthWord_SkipsOnlyThatEntity()
        {
            var confused = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:CE", "quite strong"));

            var result = _validator.Validate(Document(Canary(), confused));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:CE",
                "a strength word this runtime cannot read is a statement the PROVIDER'S CODE got wrong, and " +
                "resolution turns on strength, so acting on the claim would be acting on a declaration " +
                "nobody understood");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.UnknownStrengthWord,
                "the author who typed the word has to see it echoed, or they fix the value instead");
            StringAssert.Contains(diagnostic.Message, "neither 'weak' nor 'strong'",
                "naming both words is the whole fix, and the declaration is accepted only so a " +
                "misunderstanding can be rejected");
        }

        [TestMethod]
        public void DeclaredStrengthMismatch_SkipsOnlyThatEntity()
        {
            var overClaimed = Entity(CanaryKind, Claim("hostname", "printer", "strong"));

            var result = _validator.Validate(Document(Canary(), overClaimed));

            AssertExactlyOneEntitySkipped(result, "hostname=printer",
                "a provider able to call its own weak identifier strong makes a hostname resolve, and the " +
                "run then attaches its data to whichever element last held that name");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.DeclaredStrengthMismatch,
                "this is how an author who has misunderstood the identity model finds out before shipping");
            StringAssert.Contains(diagnostic.Message, "declares strength 'strong' but the vocabulary says 'weak'",
                "the message must name both sides of the disagreement, or the author cannot tell which one " +
                "to change");
        }

        [TestMethod]
        public void ReservedPropertyKey_SkipsOnlyThatEntity()
        {
            var forger = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:CF"));
            forger.Properties[ClaimSchema.ClaimProperty("some-other-instance")] = "some-other-instance";

            var result = _validator.Validate(Document(Canary(), forger));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:CF",
                "a provider-written $ key would forge a claim or a claim set: a forged $claim: makes this " +
                "runtime responsible for withdrawing an element it never asserted, and a forged $identity: " +
                "makes the next run resolve a different device onto it");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.ReservedPropertyKey,
                "the forged key has to be named, because the fix is to rename it into the provider's own " +
                "prefix");
            StringAssert.Contains(diagnostic.Message, "forging a claim or a claim set",
                "the message must say what the key would have done, or a reader treats the sigil as a style " +
                "rule and works around it");
        }

        [TestMethod]
        public void UnprefixedPropertyKey_SkipsOnlyThatEntity()
        {
            var greedy = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D0"));
            greedy.Properties["name"] = "the name";

            var result = _validator.Validate(Document(Canary(), greedy));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:D0",
                "two providers describing 'the name' of one device rarely mean the same thing, so an " +
                "unprefixed key means the value in the graph depends on which integration happened to run last");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.UnprefixedPropertyKey,
                "the key has to be named, because the fix is one prefix on one string in the provider");
            StringAssert.Contains(diagnostic.Message, "depends on which integration ran last",
                "the message must state the consequence, or the next author adds a precedence table instead " +
                "of a prefix");
        }

        [TestMethod]
        public void APropertyKeyWhoseDotSitsAtEitherEnd_CarriesNoProviderPrefixEither()
        {
            var leadingDot = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D1"));
            leadingDot.Properties[".name"] = "no prefix before the dot";

            var trailingDot = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D2"));
            trailingDot.Properties["csv."] = "no name after the dot";

            var leading = _validator.Validate(Document(leadingDot));
            var trailing = _validator.Validate(Document(trailingDot));

            Assert.AreEqual(1, Count(leading, DiagnosticCodes.UnprefixedPropertyKey),
                "a key that is only a dot and a name says whose value it is no better than a bare name, so " +
                "accepting it reopens the 'whichever integration ran last wins' hole the prefix rule closes");
            Assert.AreEqual(1, Count(trailing, DiagnosticCodes.UnprefixedPropertyKey),
                "a prefix with no property after it names nothing, and accepting it would let one provider " +
                "own a key no other provider can see the meaning of");
            Assert.AreEqual(0, leading.Entities.Length, "the entity carrying the bad key is skipped, as for any other unprefixed key");
            Assert.AreEqual(0, trailing.Entities.Length, "the entity carrying the bad key is skipped, as for any other unprefixed key");
        }

        [TestMethod]
        public void APropertyWithNoKeyAtAll_IsRefusedUnderTheUnprefixedKeyCode()
        {
            var keyless = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D6"));
            keyless.Properties[String.Empty] = "a value nothing names";

            var result = _validator.Validate(Document(Canary(), keyless));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:D6",
                "a property with no key at all would land as a nameless value on the element: no query can " +
                "ask for it, no comparison can tell it from another provider's nameless value, and no author " +
                "can find out it is there");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.UnprefixedPropertyKey,
                "an absent key is refused under the same code as an unprefixed one, because the fix is the " +
                "same: give the value a prefixed name");
            StringAssert.Contains(diagnostic.Message, "no key",
                "the message says which of the two shapes of the code this was, or the author looks for a " +
                "prefix on a key that does not exist");
        }

        [TestMethod]
        public void MissingRelationType_SkipsOnlyThatEntity()
        {
            var untyped = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D3"));
            untyped.Relations.Add(new RelationDto
            {
                Target = new ClaimReferenceDto { Type = "mac", Value = CanaryMacRaw },
            });

            var result = _validator.Validate(Document(Canary(), untyped));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:D3",
                "the relation type becomes the edge type traversals key on, and it is a segment of the " +
                "edge's derived key, so an untyped relation could neither be found again nor traversed - the " +
                "run would create a fresh nameless edge on every pass");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.MissingRelationType,
                "the reason has to be named, or an author sees an entity vanish and looks at its claims");
            StringAssert.Contains(diagnostic.Message, "no type",
                "the message names the absent field, which is the whole fix");
        }

        [TestMethod]
        public void MissingRelationTarget_SkipsOnlyThatEntity()
        {
            var aimless = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D4"));
            aimless.Relations.Add(new RelationDto { Type = "uplinks-to" });

            var result = _validator.Validate(Document(Canary(), aimless));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:D4",
                "a relation is addressed by claim and never by an element id, so a relation with no target " +
                "claim addresses nothing: there is no element for the edge to enter and nothing to look up");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.MissingRelationTarget,
                "the reason has to be named, or the author cannot tell an absent target from an unknown one");
            StringAssert.Contains(diagnostic.Message, "addresses no target claim",
                "the message says what is absent, and names the relation it belongs to");
        }

        [TestMethod]
        public void WeakRelationTarget_SkipsOnlyThatEntity()
        {
            var misaimed = Entity(CanaryKind, Claim("mac", "44:D2:44:AA:BB:D5"));
            misaimed.Relations.Add(new RelationDto
            {
                Type = "uplinks-to",
                Target = new ClaimReferenceDto { Type = "ipv4", Value = "192.168.1.1" },
            });

            var result = _validator.Validate(Document(Canary(), misaimed));

            AssertExactlyOneEntitySkipped(result, "mac=44:D2:44:AA:BB:D5",
                "a weak target is an ERROR rather than a warning because no reading does what its author " +
                "meant: an address is a lease, so the edge would either be dropped or attach to whichever " +
                "element last held that address");
            AssertCanarySurvived(result);
            var diagnostic = Only(result, DiagnosticCodes.WeakRelationTarget,
                "the author has to learn that weak identifiers may never address a relation, which is a " +
                "different lesson from an unknown type");
            StringAssert.Contains(diagnostic.Message, "attach to whichever element last held the value",
                "the message must state the consequence, or the next author 'fixes' it by marking ipv4 strong");
        }

        [TestMethod]
        public void ASkippedRowLosesOnlyItself_AndEveryOtherEntityStillLandsAtItsDocumentIndex()
        {
            var first = Entity("switch", Claim("mac", "44:D2:44:AA:BB:01"));
            var broken = Entity(null, Claim("mac", "44:D2:44:AA:BB:02"));
            var third = Entity("camera", Claim("mac", "44:D2:44:AA:BB:03"));

            var result = _validator.Validate(Document(first, broken, third));

            Assert.AreEqual(2, result.AcceptedEntities,
                "one bad row of a spreadsheet must not leave every other entity unobserved: in a complete " +
                "snapshot every unmentioned element is withdrawn and then deleted, so a document-level " +
                "refusal over one row would empty the graph");
            Assert.AreEqual(1, result.SkippedEntities, "exactly the one bad row is skipped, no more and no less");
            Assert.AreEqual("switch", result.Entities[0].Kind, "the row before the bad one still lands");
            Assert.AreEqual("camera", result.Entities[1].Kind,
                "the row AFTER the bad one still lands, or one early typo silently truncates the run and " +
                "reconciliation deletes every device below it in the file");
            Assert.AreEqual(0, result.Entities[0].DocumentIndex,
                "the index is the position in the DOCUMENT, which is what lets a diagnostic point a source " +
                "owner at the row to fix");
            Assert.AreEqual(2, result.Entities[1].DocumentIndex,
                "the survivor keeps its document position rather than being renumbered, or a diagnostic " +
                "sends the reader to the wrong row of their file");
        }

        // --- CLAIM level: the claim is dropped and the ENTITY IS KEPT --------------------------------

        [TestMethod]
        public void InvalidIdentifierValue_DropsTheClaimAndKeepsTheEntity()
        {
            var humanTyped = Entity(CanaryKind, Claim("mac", CanaryMacRaw), Claim("hostname", "My PC"));

            var result = _validator.Validate(Document(humanTyped));

            Assert.AreEqual(1, result.Entities.Length,
                "skipping this entity would withdraw and then DELETE a device whose MAC is perfectly good, " +
                "because a skipped entity is not claimed this round - one typo in a human-edited hostname " +
                "column would remove the device from the graph");
            Assert.AreEqual(1, result.Entities[0].Claims.Length,
                "exactly the bad claim goes and every identity the source got right stands");
            Assert.AreEqual("mac", result.Entities[0].Claims[0].Type,
                "the strong claim is the one that must survive: it is the only thing the next run can resolve " +
                "the element by, and losing it duplicates the device on every run");
            Assert.AreEqual(0, Count(result, DiagnosticCodes.EntitySkipped),
                "a datum the source typed wrongly costs that datum, never the entity");
            var diagnostic = Only(result, DiagnosticCodes.InvalidIdentifierValue,
                "the drop is never SILENT: without the diagnostic an author has no way to learn that a whole " +
                "column of their source never reaches the graph");
            StringAssert.Contains(diagnostic.Message, "every other claim of this entity still stands",
                "the message must say what survived, or a reader assumes the row was lost and re-runs");
        }

        [TestMethod]
        public void UnknownIdentifierType_DropsTheClaimAndKeepsTheEntity()
        {
            var partlyUnknown = Entity(CanaryKind, Claim("mac", CanaryMacRaw), Claim("wifi-ssid", "guest-net"));

            var result = _validator.Validate(Document(partlyUnknown));

            Assert.AreEqual(1, result.Entities.Length,
                "skipping this entity would withdraw and then delete a device whose MAC is perfectly good, " +
                "over one identifier type this runtime has not learned yet");
            Assert.AreEqual(1, result.Entities[0].Claims.Length, "only the unknown claim goes");
            Assert.AreEqual(CanaryMacKey, result.Entities[0].PrimaryKey,
                "the entity keeps resolving by the claim it always resolved by, so the element it wrote last " +
                "run is matched rather than duplicated");
            Assert.AreEqual(0, Count(result, DiagnosticCodes.EntitySkipped),
                "an unknown type is one claim's problem, not the entity's");
            var diagnostic = Only(result, DiagnosticCodes.UnknownIdentifierType,
                "an unknown type is never ignored, because ignoring it drops the identity a provider relies " +
                "on and creates a duplicate on the next run");
            StringAssert.Contains(diagnostic.Message, "the claim was dropped",
                "the message must say what happened to it, or the author cannot tell a dropped claim from a " +
                "dropped entity");
        }

        [TestMethod]
        public void DuplicateClaimWithinEntity_DropsTheRepeatAndKeepsTheEntity()
        {
            var repeated = Entity(CanaryKind,
                Claim("mac", "44:D2:44:AA:BB:CC"),
                Claim("mac", "44-d2-44-aa-bb-cc"));

            var result = _validator.Validate(Document(repeated));

            Assert.AreEqual(1, result.Entities.Length,
                "a source listing one MAC twice in two spellings is describing one device, so skipping the " +
                "entity would withdraw and delete a device the source still has");
            Assert.AreEqual(1, result.Entities[0].Claims.Length,
                "the repeat has to GO rather than land twice: two $identity: ordinals holding one key would " +
                "make the identity index return the element twice for one lookup and every future run see " +
                "duplicateClaimedElements");
            Assert.AreEqual(CanaryMacKey, result.Entities[0].Claims[0].Key,
                "the surviving claim is the canonical key both spellings converge on, which is the whole " +
                "point of canonicalising before composing");
            Assert.AreEqual(0, Count(result, DiagnosticCodes.EntitySkipped),
                "the entity survives: what was wrong was said twice, not wrong");
            var diagnostic = Only(result, DiagnosticCodes.DuplicateClaimWithinEntity,
                "the repeat is reported so an author can see their source lists one value twice");
            StringAssert.Contains(diagnostic.Message, "the repeat was dropped",
                "the message must say which of the two survived, or the author cannot predict the primary key");
        }

        [TestMethod]
        public void AnEntityWhoseEveryClaimWasDropped_FallsBackToEntityWithoutIdentity_AndIsSkipped()
        {
            var hopeless = Entity(CanaryKind, Claim("hostname", "My PC"));

            var result = _validator.Validate(Document(hopeless));

            Assert.AreEqual(0, result.Entities.Length,
                "this is the case the entity-level reading was protecting: with every claim dropped nothing " +
                "could ever resolve to the element, so creating it would add another copy on every run");
            Assert.AreEqual(1, Count(result, DiagnosticCodes.InvalidIdentifierValue),
                "the claim-level drop is still reported, so an author sees WHY the entity ended up with no " +
                "identity rather than only that it did");
            Assert.AreEqual(1, Count(result, DiagnosticCodes.EntityWithoutIdentity),
                "the fallback is what keeps the claim-level split safe: dropping claims may never quietly " +
                "produce an unidentifiable element");
            AssertExactlyOneEntitySkipped(result, "hostname=My PC",
                "one skip diagnostic names the row, however many claims of it failed");
        }

        // --- The one WARNING: reported, and the entity is kept --------------------------------------

        [TestMethod]
        public void WeakOnlyIdentity_IsAWarning_AndTheEntityIsKept()
        {
            var weaklyKnown = Entity("client", Claim("ipv4", "192.168.1.5"));

            var result = _validator.Validate(Document(weaklyKnown));

            Assert.AreEqual(1, result.Entities.Length,
                "reporting a weakly known thing is legitimate, and an entity-level error would drop it from " +
                "the run entirely - the overlap two sources share is exactly what a weak claim is for");
            Assert.AreEqual(0, Count(result, DiagnosticCodes.EntitySkipped),
                "a warning must not skip the entity, or every weakly known client disappears from the graph");
            Assert.AreEqual("ipv4:192.168.1.5", result.Entities[0].PrimaryKey,
                "the weak claim still becomes the element's primary key, so a relation can address it and " +
                "its claim stays queryable");
            var diagnostic = Only(result, DiagnosticCodes.WeakOnlyIdentity,
                "the author has to be told, because nothing can ever resolve to this element and the run " +
                "will keep re-creating it");
            StringAssert.Contains(diagnostic.Message, "every run creates another one and withdraws the last",
                "the message must state the churn, or an author reads the warning as cosmetic and ships a " +
                "provider that duplicates a device per run");
        }

        // --- Properties: one property's shape never costs the entity --------------------------------

        [TestMethod]
        public void UnsupportedPropertyValue_DropsThePropertyAndKeepsTheEntity()
        {
            var structured = Entity(CanaryKind, Claim("mac", CanaryMacRaw));
            structured.Properties["csv.name"] = "Rack switch";
            structured.Properties["csv.features"] = new[] { "poe", "sfp" };

            var result = _validator.Validate(Document(structured));

            Assert.AreEqual(1, result.Entities.Length,
                "dropping one property loses less than skipping an identifiable entity, which in a complete " +
                "snapshot is withdrawn and then deleted");
            Assert.AreEqual(1, result.Entities[0].Properties.Length,
                "the value the property surface cannot carry is refused HERE, where the diagnostic can name " +
                "it, rather than sent and rejected downstream");
            Assert.IsTrue(TryProperty(result.Entities[0], "csv.name", out _),
                "every other property of the same entity still lands");
            Assert.IsFalse(TryProperty(result.Entities[0], "csv.features", out _),
                "an array must not be smuggled through as text, or a set the platform cannot compare makes " +
                "every run a write");
            var diagnostic = Only(result, DiagnosticCodes.UnsupportedPropertyValue,
                "the drop is reported and names the property, or an author never learns their field is " +
                "missing from the graph");
            StringAssert.Contains(diagnostic.Message, "Properties are scalars",
                "the message says what shapes are carried, which is what an author needs to flatten it");
        }

        [TestMethod]
        public void ANullPropertyValue_IsSilentlyAbsent_WithNoDiagnosticAndNoProperty()
        {
            var sparse = Entity(CanaryKind, Claim("mac", CanaryMacRaw));
            sparse.Properties["csv.name"] = "Rack switch";
            sparse.Properties["csv.model"] = null;

            var result = _validator.Validate(Document(sparse));

            Assert.AreEqual(1, result.Entities.Length, "an absent value says nothing about the entity");
            Assert.IsFalse(TryProperty(result.Entities[0], "csv.model", out _),
                "an absent value is ABSENT, not empty: writing an empty string would make the property exist " +
                "and overwrite what another integration knows about the same device");
            Assert.AreEqual(1, result.Entities[0].Properties.Length, "only the answered field lands");
            Assert.AreEqual(0, result.Diagnostics.Length,
                "a field the source did not answer is not a fault, and reporting it would drown the one " +
                "diagnostic that costs an element in noise from every optional column");
        }

        [TestMethod]
        public void EveryScalarShapeTheWireCarries_RendersToTheTypeNameAndTextThePlatformStores()
        {
            var rich = Entity(CanaryKind, Claim("mac", CanaryMacRaw));
            rich.Properties["csv.name"] = "Rack switch";
            rich.Properties["csv.managed"] = true;
            rich.Properties["csv.ports"] = 24;
            rich.Properties["csv.bytes"] = 9_000_000_000L;
            rich.Properties["csv.installed"] = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
            rich.Properties["csv.asset"] = new Guid("2f3c9a04-1111-2222-3333-444455556666");

            var result = _validator.Validate(Document(rich));
            var entity = result.Entities[0];

            AssertRendered(entity, "csv.name", WireValues.StringTypeName, "Rack switch",
                "text is the platform's own type name for a string, and every claim key is stored as one too");
            AssertRendered(entity, "csv.managed", "System.Boolean", "True",
                "Boolean is not IFormattable, so the platform's egress renders it 'True'; anything else " +
                "compares unequal on read-back and makes every run write the same value again");
            AssertRendered(entity, "csv.ports", "System.Int32", "24",
                "the type name has to be the platform's literal, or the property route refuses the write");
            AssertRendered(entity, "csv.bytes", "System.Int64", "9000000000",
                "a long must not be narrowed or grouped: a thousands separator would differ from what the " +
                "platform returns and turn a no-op into a write");
            AssertRendered(entity, "csv.installed", "System.DateTime", "2026-08-11T12:00:00.0000000Z",
                "dates round-trip ('O') because that is exactly what the platform's egress produces, and " +
                "'write only where it differs' can only tell same from different if both sides agree");
            AssertRendered(entity, "csv.asset", "System.Guid", "2f3c9a04-1111-2222-3333-444455556666",
                "a Guid renders in the platform's own lower-case dashed form, or a re-run rewrites it");
            Assert.AreEqual(0, result.Diagnostics.Length,
                "every one of these shapes is carried, so none of them may cost a diagnostic");
        }

        [TestMethod]
        public void ADoubleRendersWithAnInvariantDecimalPoint_NeverACommaForm()
        {
            var original = CultureInfo.CurrentCulture;
            var rendered = default(GraphProperty);
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var inverter = Entity("inverter", Claim("mac", CanaryMacRaw));
                inverter.Properties["fronius.peakPower"] = 8200.5d;

                var result = _validator.Validate(Document(inverter));
                Assert.IsTrue(TryProperty(result.Entities[0], "fronius.peakPower", out rendered),
                    "a double is a shape the wire carries, so the property must land");
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }

            Assert.AreEqual("System.Double", rendered.TypeName,
                "the platform's literal type name is what the property route accepts");
            Assert.AreEqual("8200.5", rendered.Text,
                "the platform stores the TEXT, so a machine whose culture writes a comma decimal would " +
                "store a different string from the one it reads back: every run would rewrite the value, " +
                "and two hosts running the same job would disagree about the graph's contents");
            Assert.IsFalse(rendered.Text.Contains(','),
                "a comma decimal is also ambiguous with the separator a joined set uses, so it must never " +
                "reach a property value");
        }

        [TestMethod]
        public void AJsonElementValue_RendersAsTheScalarItIs_SoAPastedDocumentLandsLikeAProvidersOwn()
        {
            using var json = JsonDocument.Parse(
                "{\"count\":7,\"power\":8200.5,\"label\":\"Rack switch\",\"managed\":true," +
                "\"nested\":{\"a\":1}}");
            var pasted = Entity(CanaryKind, Claim("mac", CanaryMacRaw));
            pasted.Properties["csv.count"] = json.RootElement.GetProperty("count");
            pasted.Properties["csv.power"] = json.RootElement.GetProperty("power");
            pasted.Properties["csv.label"] = json.RootElement.GetProperty("label");
            pasted.Properties["csv.managed"] = json.RootElement.GetProperty("managed");
            pasted.Properties["csv.nested"] = json.RootElement.GetProperty("nested");

            var result = _validator.Validate(Document(pasted));
            var entity = result.Entities[0];

            AssertRendered(entity, "csv.count", "System.Int64", "7",
                "JSON does not distinguish an integer's width, so an integral number is always Int64: " +
                "guessing the narrowest type would change one source's type between runs and make every " +
                "run a write");
            AssertRendered(entity, "csv.power", "System.Double", "8200.5",
                "a non-integral number is always Double, and its text is invariant because the platform " +
                "stores the text");
            AssertRendered(entity, "csv.label", WireValues.StringTypeName, "Rack switch",
                "a pasted document has to validate exactly as a provider's own snapshot would, or the " +
                "validate route gives an author a verdict that does not hold at run time");
            AssertRendered(entity, "csv.managed", "System.Boolean", "True",
                "a JSON true renders as the platform's 'True', not as 'true', or a read-back comparison " +
                "differs and the run rewrites it");
            Assert.IsFalse(TryProperty(entity, "csv.nested", out _),
                "an object is a shape the property surface does not carry, so it is dropped rather than " +
                "flattened into a string nothing can compare");
            Assert.AreEqual(1, Count(result, DiagnosticCodes.UnsupportedPropertyValue),
                "the dropped object is named, or the author of the pasted document never learns it went");
            Assert.AreEqual(0, Count(result, DiagnosticCodes.EntitySkipped),
                "one unusable value never costs the entity");
        }

        [TestMethod]
        public void AJsonNullPropertyValue_IsSilentlyAbsent_ExactlyLikeAClrNullOne()
        {
            using var json = JsonDocument.Parse("{\"model\":null}");
            var pasted = Entity(CanaryKind, Claim("mac", CanaryMacRaw));
            pasted.Properties["csv.name"] = "Rack switch";
            pasted.Properties["csv.model"] = json.RootElement.GetProperty("model");

            var result = _validator.Validate(Document(pasted));

            Assert.IsFalse(TryProperty(result.Entities[0], "csv.model", out _),
                "an absent value is ABSENT: writing an empty string would make the property exist and " +
                "overwrite what another integration knows about the same device");
            Assert.AreEqual(0, result.Diagnostics.Length,
                "a JSON null is how a source's own document says 'this field is not answered', so it must be " +
                "as silent as the CLR null a provider in process would leave: the validate route exists to " +
                "give an author the verdict a RUN would give, and a diagnostic per unanswered column both " +
                "buries the one diagnostic that costs an element and makes the two verdicts disagree " +
                "(diagnostics: " + Codes(result) + ")");
        }

        // --- The primary key a relation addresses an entity by ---------------------------------------

        [TestMethod]
        public void ThePrimaryKeyIsTheStrongestClaim_WithTiesGoingToTheOrdinallyFirstKey()
        {
            var manyClaims = Entity(CanaryKind,
                Claim("ipv4", "192.168.1.5"),
                Claim("serial", "ZZ-9000"),
                Claim("mac", CanaryMacRaw));

            var result = _validator.Validate(Document(manyClaims));

            Assert.AreEqual(3, result.Entities[0].Claims.Length, "all three claims are recorded and indexed");
            Assert.AreEqual(CanaryMacKey, result.Entities[0].PrimaryKey,
                "STRENGTH first, then the ordinally smaller key: 'ipv4:192.168.1.5' sorts before both strong " +
                "keys, so a rule that ignored strength would let a DHCP lease name the element a relation " +
                "points at and re-key every edge when the address moved");
            Assert.AreEqual(IdentifierStrength.Strong, result.Entities[0].Claims
                    .Single(claim => String.Equals(claim.Key, result.Entities[0].PrimaryKey, StringComparison.Ordinal))
                    .Strength,
                "an endpoint contributes a STRONG key or the derived edge key is built on something that " +
                "resolves nothing");

            var swapped = Entity(CanaryKind, Claim("serial", "ZZ-9000"), Claim("mac", CanaryMacRaw));
            var swappedResult = _validator.Validate(Document(swapped));
            Assert.AreEqual(CanaryMacKey, swappedResult.Entities[0].PrimaryKey,
                "among equals of one strength the ordinally first wins, whatever order the provider listed " +
                "them in: deriving from whichever claim came first would compose two edge keys for one " +
                "relation across two runs and create the edge twice");
        }

        // --- helpers ---------------------------------------------------------------------------------

        /// <summary>A document whose envelope is beyond reproach, carrying one entity that must land.</summary>
        private static SnapshotDocument ValidDocument()
        {
            return Document(Canary());
        }

        /// <summary>
        ///   The document every envelope test breaks one field of. Its second entity would raise
        ///   <c>weakOnlyIdentity</c> IF entity validation ran, which is what lets an envelope test prove the
        ///   document was not partly read rather than only that its entities did not survive.
        /// </summary>
        private static SnapshotDocument EnvelopeTestDocument()
        {
            return Document(Canary(), Entity("client", Claim("ipv4", "192.168.1.5")));
        }

        private static SnapshotDocument Document(params EntityDto[] entities)
        {
            return new SnapshotDocument
            {
                ProviderId = Provider,
                IntegrationInstanceId = Instance,
                Completeness = SnapshotCompletenessWords.Complete,
                CapturedAt = "2026-08-11T12:00:00.0000000Z",
                Entities = new List<EntityDto>(entities),
            };
        }

        /// <summary>The entity every other test asserts survived: a kind, a strong claim, a prefixed property.</summary>
        private static EntityDto Canary()
        {
            var canary = Entity(CanaryKind, Claim("mac", CanaryMacRaw));
            canary.Properties["csv.name"] = "Rack switch";
            return canary;
        }

        private static EntityDto Entity(String kind, params IdentityClaimDto[] claims)
        {
            return new EntityDto { Kind = kind, Claims = new List<IdentityClaimDto>(claims) };
        }

        private static IdentityClaimDto Claim(String type, String value)
        {
            return new IdentityClaimDto { Type = type, Value = value };
        }

        private static IdentityClaimDto Claim(String type, String value, String declaredStrength)
        {
            return new IdentityClaimDto { Type = type, Value = value, DeclaredStrength = declaredStrength };
        }

        private static Int32 Count(ValidatedSnapshot result, String code)
        {
            return result.Diagnostics.Count(d => String.Equals(d.Code, code, StringComparison.Ordinal));
        }

        /// <summary>Asserts exactly one diagnostic carries <paramref name="code"/>, and returns it.</summary>
        private static DiagnosticDto Only(ValidatedSnapshot result, String code, String consequence)
        {
            var matches = result.Diagnostics
                .Where(d => String.Equals(d.Code, code, StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(1, matches.Count, consequence + " (diagnostics: " + Codes(result) + ")");
            return matches[0];
        }

        /// <summary>An envelope failure: the whole document is left unapplied, and no entity was even read.</summary>
        private static void AssertNothingWasApplied(ValidatedSnapshot result, String consequence)
        {
            Assert.IsFalse(result.EnvelopeAccepted, consequence);
            Assert.AreEqual(0, result.Entities.Length,
                "applying part of a document whose envelope is broken would be guessing: " + consequence);
            Assert.AreEqual(0, result.AcceptedEntities, "the summary a caller reads must agree with the verdict");
            Assert.AreEqual(1, result.Diagnostics.Length,
                "an envelope refusal stops BEFORE entity validation: this document's second entity is " +
                "weak-only, so any entity-level diagnostic here means it was partly read after all " +
                "(diagnostics: " + Codes(result) + ")");
        }

        /// <summary>
        ///   The good entity of a two-entity document still landed, which is the whole reason an entity-level
        ///   fault is not an envelope-level one.
        /// </summary>
        private static void AssertCanarySurvived(ValidatedSnapshot result)
        {
            Assert.AreEqual(1, result.Entities.Length,
                "one bad entity costs exactly itself: taking the rest of the document down with it would, in " +
                "a complete snapshot, withdraw and then delete every device the run stopped describing " +
                "(diagnostics: " + Codes(result) + ")");
            Assert.AreEqual(CanaryMacKey, result.Entities[0].PrimaryKey,
                "the entity that survived is the good one, still known by the claim the next run resolves it " +
                "by, or the run matches nothing and duplicates the device");
        }

        /// <summary>
        ///   An entity-level failure: that one entity is gone and exactly ONE <c>entitySkipped</c> names the
        ///   entity that went.
        /// </summary>
        private static void AssertExactlyOneEntitySkipped(ValidatedSnapshot result, String subject,
            String consequence)
        {
            Assert.IsTrue(result.EnvelopeAccepted,
                "an entity's fault is not the envelope's: refusing the document would leave every other " +
                "entity unobserved");
            Assert.AreEqual(1, result.SkippedEntities, consequence);
            var skipped = Only(result, DiagnosticCodes.EntitySkipped,
                "exactly one entitySkipped per skipped entity: none leaves the loss invisible on the report, " +
                "and several make a reader think several rows went");
            Assert.AreEqual(subject, skipped.Subject,
                "the skip names the entity, or a source owner cannot find the row to fix: " + consequence);
            StringAssert.Contains(skipped.Message, "not claimed this round",
                "the message says what a skip MEANS - the entity is simply not claimed - which is why a " +
                "complete snapshot then withdraws it");
        }

        private static void AssertRendered(ValidatedEntity entity, String key, String typeName, String text,
            String consequence)
        {
            Assert.IsTrue(TryProperty(entity, key, out var property),
                "the property has to land at all: " + consequence);
            Assert.AreEqual(typeName, property.TypeName, consequence);
            Assert.AreEqual(text, property.Text, consequence);
        }

        private static Boolean TryProperty(ValidatedEntity entity, String key, out GraphProperty property)
        {
            foreach (var candidate in entity.Properties)
            {
                if (String.Equals(candidate.Key, key, StringComparison.Ordinal))
                {
                    property = candidate;
                    return true;
                }
            }

            property = default(GraphProperty);
            return false;
        }

        /// <summary>Every code a verdict raised, so a failing assertion says what the validator actually said.</summary>
        private static String Codes(ValidatedSnapshot result)
        {
            return result.Diagnostics.Length == 0
                ? "none"
                : String.Join(", ", result.Diagnostics.Select(d => d.Code));
        }
    }
}
