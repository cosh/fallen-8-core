// MIT License
//
// IntegrationsIdentityTest.cs
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
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   THE IDENTITY MODEL of the integrations runtime (feature integrations, spec sections 5, 6 and 7):
    ///   the vocabulary, the two reserved property prefixes, the one place a claim key is composed, the
    ///   resolver, and the in-scope rule that narrows what the resolver is even shown.
    ///
    ///   <para>Why every rule here earns its own test: both ways to get one vocabulary entry wrong are
    ///   unrepairable by running again. An entry wrongly marked strong makes a run attach its data to the
    ///   wrong element it claimed before, and one wrongly marked weak - or a canonicalisation that does not
    ///   converge - makes a run fail to find its own element and duplicate its devices on every run. A claim
    ///   key that two identities can both compose lets one run resolve into and reconcile away another
    ///   integration's elements.</para>
    ///
    ///   <para><see cref="ValidatedEntity"/> has an internal constructor and this assembly has no
    ///   InternalsVisibleTo, so every entity here is produced by validating a real
    ///   <see cref="SnapshotDocument"/> through <see cref="SnapshotValidator"/>. That is also the more honest
    ///   fixture: the claims the resolver sees are canonicalised and composed by the same code a run uses.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsIdentityTest
    {
        /// <summary>The provider id the spec's provider-scoped example uses.</summary>
        private const String Provider = "unifi";

        /// <summary>The instance id the spec's instance-scoped example uses.</summary>
        private const String Instance = "garage";

        /// <summary>Another integration's identity, for the "not mine" half of every scope rule.</summary>
        private const String OtherInstance = "attic";

        private const String Uuid = "2f3c9a04-1b2c-4d3e-8f90-a1b2c3d4e5f6";

        /// <summary>One well-formed vocabulary entry, so a load-failure fixture breaks exactly one thing.</summary>
        private const String MacEntryJson =
            "{ \"type\": \"mac\", \"strength\": \"strong\", \"scope\": \"global\", " +
            "\"canonical\": \"lowerHexStripSeparators\", \"accept\": \"^[0-9a-f]{12}$\", " +
            "\"description\": \"IEEE MAC address\" }";

        private static IdentifierVocabulary Shipped
        {
            get { return IdentifierVocabulary.Shipped; }
        }

        #region fixtures

        private static IdentifierType Type(String type)
        {
            Assert.IsTrue(Shipped.TryGet(type, out var identifier),
                "the shipped vocabulary must carry '" + type + "': a type it does not carry is rejected per " +
                "entity, the entity then arrives with no strong claim, nothing resolves to it, and every run " +
                "creates another copy");
            return identifier;
        }

        private static IdentityClaimDto Claim(String type, String value)
        {
            return new IdentityClaimDto { Type = type, Value = value };
        }

        /// <summary>
        ///   One validated entity, built the way a run builds one: a real document through the real validator,
        ///   so the claims the resolver is handed are composed by the code the write path uses.
        /// </summary>
        private static ValidatedEntity Entity(params IdentityClaimDto[] claims)
        {
            var document = new SnapshotDocument
            {
                ProviderId = Provider,
                IntegrationInstanceId = Instance,
                Completeness = SnapshotCompletenessWords.Complete,
                Entities = new List<EntityDto>
                {
                    new EntityDto { Kind = "device", Claims = claims },
                },
            };

            var validated = new SnapshotValidator(Shipped).Validate(document);
            Assert.IsTrue(validated.EnvelopeAccepted,
                "the fixture envelope must be accepted, or this test would be asserting about an entity no " +
                "run would ever apply");
            Assert.AreEqual(1, validated.Entities.Length,
                "the fixture entity must survive validation, or this test would assert the resolver's rules " +
                "against nothing at all");
            return validated.Entities[0];
        }

        /// <summary>The claim key the entity carries for one type, so no test hardcodes the key format twice.</summary>
        private static String KeyOf(ValidatedEntity entity, String type)
        {
            foreach (var claim in entity.Claims)
            {
                if (String.Equals(claim.Type, type, StringComparison.Ordinal))
                {
                    return claim.Key;
                }
            }

            Assert.Fail("the fixture entity carries no '" + type + "' claim, so the lookup it is supposed to " +
                "be found by could not be keyed");
            return null;
        }

        private static GraphProperty Text(String key, String value)
        {
            return new GraphProperty(key, WireValues.StringTypeName, value);
        }

        private static ElementState ElementWith(Int32 id, params GraphProperty[] properties)
        {
            var builder = ImmutableDictionary.CreateBuilder<String, GraphProperty>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                builder[property.Key] = property;
            }

            return new ElementState(id, "device", builder.ToImmutable());
        }

        /// <summary>
        ///   An element carrying <paramref name="identityKeys"/> as dense identity properties and, when
        ///   <paramref name="claimant"/> is not null, that claimant's claim property. A null claimant is the
        ///   orphan case, which the in-scope rule deliberately admits.
        /// </summary>
        private static ElementState ElementClaimedBy(Int32 id, String claimant, params String[] identityKeys)
        {
            var properties = new List<GraphProperty>();
            for (var ordinal = 0; ordinal < identityKeys.Length; ordinal++)
            {
                properties.Add(Text(ClaimSchema.IdentityProperty(ordinal), identityKeys[ordinal]));
            }

            if (claimant != null)
            {
                properties.Add(Text(ClaimSchema.ClaimProperty(claimant), claimant));
            }

            return ElementWith(id, properties.ToArray());
        }

        private static Dictionary<String, IReadOnlyList<Int32>> Index()
        {
            return new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal);
        }

        private static Dictionary<Int32, ElementState> Elements(params ElementState[] states)
        {
            var map = new Dictionary<Int32, ElementState>();
            foreach (var state in states)
            {
                map[state.Id] = state;
            }

            return map;
        }

        private static String VocabularyJson(String schemaVersion, String identifiers)
        {
            return "{ \"schemaVersion\": " + schemaVersion + ", \"identifiers\": " + identifiers + " }";
        }

        /// <summary>One well-formed entry, with the named field left out. Used to prove each field is required.</summary>
        private static String EntryOmitting(String field)
        {
            var fields = new[]
            {
                "\"type\": \"mac\"",
                "\"strength\": \"strong\"",
                "\"scope\": \"global\"",
                "\"canonical\": \"lowerHexStripSeparators\"",
                "\"accept\": \"^[0-9a-f]{12}$\"",
                "\"description\": \"IEEE MAC address\"",
            };

            var kept = new List<String>();
            foreach (var entry in fields)
            {
                if (!entry.StartsWith("\"" + field + "\"", StringComparison.Ordinal))
                {
                    kept.Add(entry);
                }
            }

            Assert.AreEqual(fields.Length - 1, kept.Count,
                "the fixture must actually omit '" + field + "', otherwise this test would prove nothing " +
                "about a vocabulary file missing a field");
            return "[ { " + String.Join(", ", kept) + " } ]";
        }

        private static InvalidOperationException Refused(String json, String consequence)
        {
            return Assert.ThrowsException<InvalidOperationException>(
                () => IdentifierVocabulary.Load(json), consequence);
        }

        #endregion

        #region the vocabulary, per entry

        [DataTestMethod]
        [DataRow("mac", "44:D2:44:AA:BB:CC", "44d244aabbcc")]
        [DataRow("serial", "  st-1234abc  ", "ST-1234ABC")]
        [DataRow("imei", "35-209900-176148-1", "352099001761481")]
        [DataRow("ipv4", "  192.168.1.10  ", "192.168.1.10")]
        [DataRow("ipv6", "  FE80::1  ", "fe80::1")]
        [DataRow("hostname", "  RaspberryPi  ", "raspberrypi")]
        [DataRow("unifi-site-id", "  2F3C9A04-1B2C-4D3E-8F90-A1B2C3D4E5F6  ", Uuid)]
        [DataRow("unifi-device-id", "  2F3C9A04-1B2C-4D3E-8F90-A1B2C3D4E5F6  ", Uuid)]
        [DataRow("unifi-client-id", "  2F3C9A04-1B2C-4D3E-8F90-A1B2C3D4E5F6  ", Uuid)]
        [DataRow("fronius-unique-id", "  476  ", "476")]
        [DataRow("fronius-logger-id", "  240.107620  ", "240.107620")]
        [DataRow("arxml-vehicle-path", "  testcar/ISignals/SIG_VehSpd  ", "testcar/ISignals/SIG_VehSpd")]
        public void EveryVocabularyEntry_CanonicalisesAValueToTheOneFormItsKeyIsComposedFrom(
            String type, String raw, String expected)
        {
            var identifier = Type(type);

            var canonical = identifier.Canonicalise(raw);

            Assert.AreEqual(expected, canonical,
                "'" + type + "' must converge on one canonical form: exact string equality on the composed " +
                "key IS the resolution rule, so a canonicalisation that does not converge makes a run fail " +
                "to find its own element and duplicate the device on every run");
        }

        [DataTestMethod]
        [DataRow("mac", "44:D2:44:AA:BB:CC", "44d244aabb")]
        [DataRow("serial", "st-1234abc", "st 1234 abc")]
        [DataRow("imei", "352099001761481", "35209900176148")]
        [DataRow("ipv4", "192.168.1.10", "999.1.1.1")]
        [DataRow("ipv6", "fe80::1", "192.168.1.10")]
        [DataRow("hostname", "raspberrypi", "my host")]
        [DataRow("unifi-site-id", Uuid, "site-one")]
        [DataRow("unifi-device-id", Uuid, "device-one")]
        [DataRow("unifi-client-id", Uuid, "client-one")]
        [DataRow("fronius-unique-id", "476", "240.107620")]
        [DataRow("fronius-logger-id", "240.107620", "240.107.620")]
        [DataRow("arxml-vehicle-path", "testcar/ISignals/SIG_VehSpd", "/ISignals/SIG_VehSpd")]
        public void EveryVocabularyEntry_AcceptsAValueOfItsOwnShapeAndRejectsOneOfAnother(
            String type, String accepted, String rejected)
        {
            var identifier = Type(type);

            var acceptedOutcome = identifier.TryCanonicalise(accepted, out var acceptedCanonical);
            var rejectedOutcome = identifier.TryCanonicalise(rejected, out _);

            Assert.IsTrue(acceptedOutcome,
                "'" + type + "' must accept '" + accepted + "': a rejected value becomes an " +
                "invalidIdentifierValue diagnostic and the claim is dropped, so the entity loses the " +
                "identity a provider relies on and the next run creates a second element for it");
            Assert.IsTrue(acceptedCanonical.Length > 0,
                "an accepted value must canonicalise to something, or the composed key would be a bare type " +
                "prefix that every value of that type collides on");
            Assert.IsFalse(rejectedOutcome,
                "'" + type + "' must reject '" + rejected + "' rather than keying it as it arrived: a value " +
                "of the wrong shape keyed anyway asserts an identity the source never stated, and a later " +
                "run reporting the right shape resolves to nothing and duplicates the element");
        }

        [TestMethod]
        public void TheStrengthAndScopeOfEveryEntry_AreExactlyWhatTheSpecTableSays()
        {
            var expected = new (String Type, IdentifierStrength Strength, IdentifierScope Scope, String Canonical)[]
            {
                ("mac", IdentifierStrength.Strong, IdentifierScope.Global, "lowerHexStripSeparators"),
                ("serial", IdentifierStrength.Strong, IdentifierScope.Global, "trimUpper"),
                ("imei", IdentifierStrength.Strong, IdentifierScope.Global, "digitsOnly"),
                ("ipv4", IdentifierStrength.Weak, IdentifierScope.Global, "trimLower"),
                ("ipv6", IdentifierStrength.Weak, IdentifierScope.Global, "trimLower"),
                ("hostname", IdentifierStrength.Weak, IdentifierScope.Global, "trimLower"),
                ("unifi-site-id", IdentifierStrength.Strong, IdentifierScope.Provider, "trimLower"),
                ("unifi-device-id", IdentifierStrength.Strong, IdentifierScope.Provider, "trimLower"),
                ("unifi-client-id", IdentifierStrength.Strong, IdentifierScope.Provider, "trimLower"),
                ("fronius-unique-id", IdentifierStrength.Strong, IdentifierScope.Instance, "trimUpper"),
                ("fronius-logger-id", IdentifierStrength.Strong, IdentifierScope.Instance, "trimUpper"),
                ("arxml-vehicle-path", IdentifierStrength.Strong, IdentifierScope.Instance, "trim"),
            };

            var all = Shipped.All;

            Assert.AreEqual(expected.Length, all.Length,
                "the shipped vocabulary must carry exactly the entries this table reviews: an unreviewed " +
                "entry decides whether a claim resolves, and every entry added to the file changes what two " +
                "elements sharing a key are asserting about each other");
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].Type, all[i].Type,
                    "entry " + i + " must be '" + expected[i].Type + "': the file order is what the " +
                    "vocabulary route serves and what a provider author reads as the table");
                Assert.AreEqual(expected[i].Strength, all[i].Strength,
                    "'" + expected[i].Type + "' has the wrong strength. Marked strong wrongly, a run attaches " +
                    "its data to the wrong element it claimed before; marked weak wrongly, a run never finds " +
                    "its own element and duplicates the device on every run");
                Assert.AreEqual(expected[i].Scope, all[i].Scope,
                    "'" + expected[i].Type + "' has the wrong scope. A scope wider than the value's real " +
                    "uniqueness domain composes one key for two installations' different things and " +
                    "advertises an overlap that does not exist");
                Assert.AreEqual(expected[i].Canonical, all[i].CanonicaliserName,
                    "'" + expected[i].Type + "' must be canonicalised by '" + expected[i].Canonical +
                    "': the wrong canonicaliser makes two spellings of one value two keys, and one device " +
                    "becomes two elements");
            }
        }

        [TestMethod]
        public void EveryAcceptPatternIsAnchored_BecauseAnUnanchoredOneWouldMatchASubstring()
        {
            foreach (var identifier in Shipped.All)
            {
                var pattern = identifier.Accept.ToString();

                Assert.IsTrue(pattern.StartsWith("^", StringComparison.Ordinal) &&
                              pattern.EndsWith("$", StringComparison.Ordinal),
                    "'" + identifier.Type + "' carries the unanchored pattern '" + pattern + "'. The pattern " +
                    "is applied with IsMatch, which searches anywhere in the value, so an unanchored one " +
                    "accepts anything CONTAINING an acceptable substring: the whole value is then keyed as it " +
                    "arrived, so a mistyped identifier composes an accepted strong key, and the element it " +
                    "creates is invisible to every later run that reads the value correctly");
            }
        }

        [TestMethod]
        public void AnIdentifierTypeIsFoundCaseInsensitively_BecauseATypeNameIsAWordAnAuthorTypes()
        {
            Assert.IsTrue(Shipped.TryGet("MAC", out var upper),
                "a type name typed in another case must still be found: an unknown type drops the claim, the " +
                "entity arrives with no strong claim, and every run creates another copy of the device");
            Assert.AreEqual("mac", upper.Type,
                "the entry found must be the one the file declares, so the key composed carries the file's " +
                "own type name and not the spelling a provider happened to use");
        }

        [TestMethod]
        public void AMacInEveryPunctuationTheSourcesUse_ComposesOneKey_SoOneDeviceIsNotThreeElements()
        {
            var mac = Type("mac");
            var spellings = new[] { "44:D2:44:AA:BB:CC", "44-d2-44-aa-bb-cc", "44d244aabbcc" };

            var keys = new HashSet<String>(StringComparer.Ordinal);
            foreach (var spelling in spellings)
            {
                Assert.IsTrue(ClaimKeyComposer.TryCompose(mac, spelling, Provider, Instance, out var key, out _),
                    "'" + spelling + "' is a MAC a real source reports, so it must compose a key rather than " +
                    "becoming an invalidIdentifierValue diagnostic that drops the device's only identity");
                keys.Add(key);
            }

            Assert.AreEqual(1, keys.Count,
                "the three punctuations of one MAC must compose ONE key: three keys means one device becomes " +
                "three elements, and no run ever finds the elements the previous run created");
            Assert.IsTrue(keys.Contains("mac:44d244aabbcc"),
                "the one key must be the spec's global form 'mac:44d244aabbcc': the key is the only " +
                "comparison surface in the runtime, so a change to its shape orphans every claim already in " +
                "the graph");
        }

        /// <summary>
        ///   Two spellings of one printed serial CONVERGE, which is the property no single canonical
        ///   value can state. That the canonical form of "  st-1234abc  " is exactly "ST-1234ABC" is the
        ///   serial row of <c>EveryVocabularyEntry_CanonicalisesAValueToTheOneFormItsKeyIsComposedFrom</c>,
        ///   with the same value through the same call, so it is not re-asserted here.
        /// </summary>
        [TestMethod]
        public void Serial_IsTrimmedAndUpperCased_SoOneUnitIsOneKey()
        {
            var serial = Type("serial");

            Assert.AreEqual(serial.Canonicalise("st-1234abc"), serial.Canonicalise("ST-1234ABC "),
                "two spellings of one printed serial must converge, or the run that reads the other spelling " +
                "resolves to nothing and creates a duplicate");
        }

        /// <summary>
        ///   A SPACED fifteen-digit IMEI, which is the shape neither generic row covers.
        ///   <para>The mistyped fourteen-digit form must be REJECTED as a visible diagnostic, never
        ///   padded, re-checksummed or otherwise reinterpreted: reinterpreting a typo invents a strong
        ///   identity that some other handset may really have, and a run would then attach this device's
        ///   data to that element. That rejection is the imei row of
        ///   <c>EveryVocabularyEntry_AcceptsAValueOfItsOwnShapeAndRejectsOneOfAnother</c>, with the same
        ///   value through the same call, so it is not re-asserted here.</para>
        /// </summary>
        [TestMethod]
        public void ASpacedImei_IsAccepted_AndCanonicalisesToItsDigitsAlone()
        {
            var imei = Type("imei");

            Assert.IsTrue(imei.TryCanonicalise("35 209900 176148 1", out var canonical),
                "a spaced fifteen-digit IMEI must be accepted, since the digits are the identity and a source " +
                "that formats them must not lose the device its only strong claim");
            Assert.AreEqual("352099001761481", canonical,
                "an IMEI must canonicalise to its digits alone, or a spaced and an unspaced reading of one " +
                "handset become two elements");
        }

        /// <summary>
        ///   An ipv4 claim refuses a value belonging to the OTHER address type, which is the one ipv4
        ///   rule no generic row covers (the ipv6 row asserts the mirror case). The dotted quad it
        ///   accepts and the out-of-range octet it refuses are the ipv4 row of
        ///   <c>EveryVocabularyEntry_AcceptsAValueOfItsOwnShapeAndRejectsOneOfAnother</c>, with the same
        ///   values through the same call, so they are not re-asserted here.
        /// </summary>
        [TestMethod]
        public void Ipv4_RejectsAnIpv6Value_SoOneAddressSpaceIsNotKeyedByTwoTypes()
        {
            var ipv4 = Type("ipv4");

            Assert.IsFalse(ipv4.TryCanonicalise("fe80::1", out _),
                "an IPv6 value under the ipv4 type must be rejected: two types keying one address space " +
                "would make the same address two different keys, and the overlap would go missing");
        }

        [TestMethod]
        public void Ipv6_AcceptsBothTheLinkLocalAndTheLoopbackForm()
        {
            var ipv6 = Type("ipv6");

            Assert.IsTrue(ipv6.TryCanonicalise("fe80::1", out var linkLocal),
                "a link-local address is what a client on a real network reports, so rejecting it loses the " +
                "only overlap this provider has with any other view of the same device");
            Assert.AreEqual("fe80::1", linkLocal,
                "an IPv6 value is lower-cased and otherwise left alone: full canonicalisation is deliberately " +
                "not attempted, and an imperfectly normalised weak key costs at most a missed overlap");
            Assert.IsTrue(ipv6.TryCanonicalise("::1", out _),
                "the loopback form must be accepted too, since a source is free to report it and a rejection " +
                "would be a diagnostic on every run for a value that is perfectly valid");
        }

        [TestMethod]
        public void Hostname_AcceptsABareNameAndAnFqdn_AndRejectsAValueWithASpace()
        {
            var hostname = Type("hostname");

            Assert.IsTrue(hostname.TryCanonicalise("raspberrypi", out _),
                "a bare name is the ordinary case, and rejecting it drops the evidence a person searches the " +
                "graph by");
            Assert.IsTrue(hostname.TryCanonicalise("Nas.Home.Arpa", out var fqdn),
                "a dotted FQDN must be accepted as well, or every domain-joined machine loses its hostname " +
                "claim");
            Assert.AreEqual("nas.home.arpa", fqdn,
                "a hostname is lower-cased, so two sources differing only in case still share one queryable " +
                "key");
            Assert.IsFalse(hostname.TryCanonicalise("my host", out _),
                "a value with a space is not a hostname and must be rejected: keyed anyway it would be an " +
                "identity claim nothing can ever match, hiding a bad column behind a claim that looks fine");
        }

        [DataTestMethod]
        [DataRow("unifi-site-id")]
        [DataRow("unifi-device-id")]
        [DataRow("unifi-client-id")]
        public void EveryUnifiIdentifier_AcceptsAUuid_AndRejectsAnythingThatIsNotOne(String type)
        {
            var identifier = Type(type);

            Assert.IsTrue(identifier.TryCanonicalise(Uuid.ToUpperInvariant(), out var canonical),
                "'" + type + "' must accept the console's UUID in either case: it is the only strong identity " +
                "a UniFi site or client has, and dropping it means every run creates another element for it");
            Assert.AreEqual(Uuid, canonical,
                "the UUID must be lower-cased, or one console's own id read twice in two cases composes two " +
                "keys and duplicates the element");
            Assert.IsFalse(identifier.TryCanonicalise("site-one", out _),
                "'" + type + "' must reject a value that is not a UUID: this type is strong, so a key made " +
                "from an arbitrary string would let two unrelated things resolve to one element");
        }

        /// <summary>
        ///   The vendor's own logger-id example is what forces two Fronius entries rather than one.
        ///   <para>That the inverter type still accepts a short integer UniqueID like '476' (what
        ///   independent captures actually report) is the fronius-unique-id row of
        ///   <c>EveryVocabularyEntry_AcceptsAValueOfItsOwnShapeAndRejectsOneOfAnother</c>, with the same
        ///   value through the same call, and it is implied again below by <c>Compose</c> succeeding on
        ///   it, so it is not asserted a third time here.</para>
        /// </summary>
        [TestMethod]
        public void TheFroniusLoggerExampleWithADot_IsRefusedByTheInverterTypeAndAcceptedByTheLoggerType()
        {
            var inverter = Type("fronius-unique-id");
            var logger = Type("fronius-logger-id");

            Assert.IsFalse(inverter.TryCanonicalise("240.107620", out _),
                "the inverter type must reject the vendor's own logger-id example '240.107620' (the dot). " +
                "This is what forces fronius-logger-id to be its own entry: sharing one type would leave " +
                "every logging device with no identity and a diagnostic nobody reads");
            Assert.IsTrue(logger.TryCanonicalise("240.107620", out var loggerCanonical),
                "the logger type must accept '240.107620', or the datamanager that fronts the whole Solar " +
                "API has no identity and is created again on every run");
            Assert.AreEqual("240.107620", loggerCanonical,
                "the logger id is trimmed and upper-cased only, so the vendor's value is the key's value");
            Assert.AreEqual(IdentifierScope.Instance, inverter.Scope,
                "the inverter id is unique only inside one inverter's own API, so instance scope is what " +
                "stops two installations' different inverters from composing one key");

            var inverterKey = Compose(inverter, "476");
            var loggerKey = Compose(logger, "476");

            Assert.AreNotEqual(inverterKey, loggerKey,
                "a logger and an inverter reporting the SAME value must not compose the same key: the two id " +
                "spaces are not documented as disjoint, so one type for both would resolve a logging device " +
                "and an inverter to one element inside a single instance");
        }

        [TestMethod]
        public void TwoArxmlPathsDifferingOnlyInCase_ComposeTwoKeys_BecauseAShortNameIsCaseSensitive()
        {
            var path = Type("arxml-vehicle-path");

            var lower = path.Canonicalise("testcar/ISignals/sig_vehspd");
            var mixed = path.Canonicalise("testcar/ISignals/SIG_VehSpd");

            Assert.AreEqual("testcar/ISignals/SIG_VehSpd", mixed,
                "an AUTOSAR reference path must survive canonicalisation with its case intact, which is the " +
                "whole reason this entry names 'trim' rather than one of the folding canonicalisers");
            Assert.AreNotEqual(lower, mixed,
                "two paths differing only in case must compose TWO keys: an AUTOSAR short-name is a " +
                "case-sensitive identifier, so folding them together would resolve two elements the " +
                "standard considers different into one, and the run would attach one signal's data to the " +
                "other and then withdraw whichever it did not describe second");
            Assert.AreNotEqual(Compose(path, "testcar/ISignals/sig_vehspd"),
                    Compose(path, "testcar/ISignals/SIG_VehSpd"),
                "the composed CLAIM KEYS must differ too, since the key is what resolution compares");
        }

        [TestMethod]
        public void AnArxmlPath_AcceptsARealisticallyDeepPath_AndRejectsTheShapesThatAreNotOne()
        {
            var path = Type("arxml-vehicle-path");

            Assert.IsTrue(path.TryCanonicalise(
                    "testcar/ISignals/DEMOBUS/PKG_DEMOBUS_CH_A/PDU_DistanceReport/SIG_OdoTotalDist", out _),
                "a five-segment path is ordinary in a real extract, so rejecting one would drop the identity " +
                "of most of the file");
            Assert.IsTrue(path.TryCanonicalise("testcar/AUTOSAR_Platform/BaseTypes/uint8", out _),
                "a platform base-type path must be accepted: it is the shape that proves the VEHICLE is " +
                "needed, since every extract in existence contains this exact path, so two cars under one " +
                "identity would otherwise claim the same element");
            Assert.IsFalse(path.TryCanonicalise("/AUTOSAR_Platform/BaseTypes/uint8", out _),
                "and the same path WITHOUT a vehicle must be refused. This is the whole change: the " +
                "vehicle-less shape is what let two vehicles resolve onto each other's elements, since " +
                "the standard makes a path unique within one system description and not across several. " +
                "Refusing it here means a provider that forgets the vehicle fails to compose a key at " +
                "all rather than silently fusing cars");
            Assert.IsFalse(path.TryCanonicalise("testcar/ISignals/9SIG_VehSpd", out _),
                "a segment starting with a digit is not an AUTOSAR identifier, and keying it anyway would " +
                "assert an identity the standard cannot express");
            Assert.IsFalse(path.TryCanonicalise("testcar/ISignals/SIG VehSpd", out _),
                "a space is not an AUTOSAR identifier character; a value of the wrong shape keyed as it " +
                "arrived is invisible to every later run that reads the file correctly");
            Assert.IsFalse(path.TryCanonicalise("testcar/ISignals/SIG_VehSpd/", out _),
                "a trailing slash names an empty final segment, which is not an element");
            Assert.IsFalse(path.TryCanonicalise("testcar/" + new String('a', 600), out _),
                "a path past the bound is refused rather than keyed: the composed claim key becomes a " +
                "property value that nothing truncates, so one malformed path would otherwise carry an " +
                "unbounded string into every index holding that key");
        }

        private static String Compose(IdentifierType identifier, String value)
        {
            Assert.IsTrue(ClaimKeyComposer.TryCompose(identifier, value, Provider, Instance, out var key, out _),
                "the fixture value must compose a key for '" + identifier.Type + "', or this test would be " +
                "comparing two nulls");
            return key;
        }

        #endregion

        #region a malformed vocabulary refuses to load

        [TestMethod]
        public void AVocabularyDeclaringAnotherSchemaVersion_FailsToLoad_RatherThanBeingHalfUnderstood()
        {
            var thrown = Refused(
                VocabularyJson("2", "[ " + MacEntryJson + " ]"),
                "a document from another contract version must throw rather than being read with the fields " +
                "this code happens to recognise: a silently ignored field is a strength or a scope, and both " +
                "decide whether a run resolves to the right element");

            StringAssert.Contains(thrown.Message, "schemaVersion",
                "the load failure must name the schema version, or whoever changed the file cannot tell this " +
                "refusal from a syntax error");
        }

        [TestMethod]
        public void AVocabularyNamingACanonicaliserThisRuntimeDoesNotImplement_FailsToLoad()
        {
            var thrown = Refused(
                VocabularyJson("1", "[ { \"type\": \"mac\", \"strength\": \"strong\", \"scope\": \"global\", " +
                    "\"canonical\": \"upperHexWithColons\", \"accept\": \"^[0-9a-f]{12}$\", " +
                    "\"description\": \"a mac\" } ]"),
                "an unknown canonicaliser must throw rather than degrading to no normalisation at all: " +
                "unnormalised values do not converge, so a run never finds its own element and duplicates " +
                "every device on every run");

            StringAssert.Contains(thrown.Message, "upperHexWithColons",
                "the failure must name the canonicaliser the file asked for, since the fix is either the " +
                "file's name or a new canonicaliser");
        }

        [TestMethod]
        public void AVocabularyWithAnUncompilableAcceptPattern_FailsToLoad()
        {
            var thrown = Refused(
                VocabularyJson("1", "[ { \"type\": \"mac\", \"strength\": \"strong\", \"scope\": \"global\", " +
                    "\"canonical\": \"lowerHexStripSeparators\", \"accept\": \"^[0-9a-f\", " +
                    "\"description\": \"a mac\" } ]"),
                "an accept pattern that does not compile must throw at load: every entry carries a pattern " +
                "precisely so a bad value is visible, and an entry whose pattern cannot run would either " +
                "accept everything or fail every value at the first job");

            StringAssert.Contains(thrown.Message, "regular expression",
                "the failure must say the pattern is the problem, or a reviewer reads it as a missing field");
        }

        [TestMethod]
        public void AVocabularyDeclaringOneTypeTwice_FailsToLoad()
        {
            var thrown = Refused(
                VocabularyJson("1", "[ " + MacEntryJson + ", " + MacEntryJson + " ]"),
                "a repeated type must throw: with two entries under one name, which strength, scope and " +
                "canonicaliser a claim gets would depend on load order, and that decides whether a run " +
                "resolves to the right element or to none");

            StringAssert.Contains(thrown.Message, "more than once",
                "the failure must say the type is declared twice, which is the one thing the reviewer has to " +
                "fix in the file");
        }

        [TestMethod]
        public void AVocabularyDeclaringOneTypeTwiceInDifferentCase_AlsoFailsToLoad()
        {
            var upper = "{ \"type\": \"MAC\", \"strength\": \"weak\", \"scope\": \"global\", " +
                "\"canonical\": \"trimLower\", \"accept\": \"^.*$\", \"description\": \"a mac again\" }";

            Refused(
                VocabularyJson("1", "[ " + MacEntryJson + ", " + upper + " ]"),
                "types are looked up case-insensitively, so two entries differing only in case are ONE type " +
                "with two contradictory strengths: whichever the lookup returned would decide whether a claim " +
                "resolves, and the file would read as if both rules applied");
        }

        [DataTestMethod]
        [DataRow("type")]
        [DataRow("strength")]
        [DataRow("scope")]
        [DataRow("canonical")]
        [DataRow("accept")]
        [DataRow("description")]
        public void AVocabularyEntryMissingAnyRequiredField_FailsToLoad(String field)
        {
            var thrown = Refused(
                VocabularyJson("1", EntryOmitting(field)),
                "an entry missing '" + field + "' must throw rather than defaulting: a defaulted strength or " +
                "scope is exactly the half-understood entry that makes a run attach data to the wrong " +
                "element, or duplicate its devices on every run");

            StringAssert.Contains(thrown.Message, field,
                "the failure must name the missing field, since the file is data a reviewer reads as a table");
        }

        [TestMethod]
        public void AVocabularyDeclaringNoIdentifiers_FailsToLoad()
        {
            Refused(
                VocabularyJson("1", "[ ]"),
                "an empty identifier list must throw: with no types, every claim a provider makes is an " +
                "unknown type, every entity arrives with no strong claim, and every run creates another copy " +
                "of everything the source has");
        }

        #endregion

        #region ClaimSchema

        [TestMethod]
        public void TheReservedPrefixesAndIndexIds_AreTheExactStringsTheGraphAndItsIndicesCarry()
        {
            Assert.AreEqual("$", ClaimSchema.ReservedSigil,
                "the reserved sigil is what the validator refuses a provider-supplied key by; changing it " +
                "would let a provider forge a claim or a claim set");
            Assert.AreEqual("$identity:", ClaimSchema.IdentityPrefix,
                "the identity prefix is both a property key and the index's property selector, so a change " +
                "orphans every claim already written and the backfill would restore none of them");
            Assert.AreEqual("$claim:", ClaimSchema.ClaimPrefix,
                "the claim prefix is what 'every element this instance claims' is answered from, so a change " +
                "makes reconciliation see nothing and every element the instance ever wrote becomes an orphan");
            Assert.AreEqual("f8i-identity", ClaimSchema.IdentityIndexId,
                "the identity index id is created, repaired and scanned by name; a renamed index reads as " +
                "empty, which is indistinguishable from 'no element carries this claim' and duplicates " +
                "everything");
            Assert.AreEqual("f8i-claims", ClaimSchema.ClaimsIndexId,
                "the claim index id is what reconciliation's set difference starts from; a renamed one would " +
                "answer 'this instance claims nothing'");

            Assert.IsTrue(ClaimSchema.IsReserved("$identity:0"),
                "an identity property is reserved, or a provider could write one and forge a claim");
            Assert.IsTrue(ClaimSchema.IsIdentityProperty("$identity:7"),
                "an identity property must be recognised at any ordinal, since the write path appends at the " +
                "next free one");
            Assert.IsFalse(ClaimSchema.IsIdentityProperty("$claim:garage"),
                "a claim property is not an identity property: confusing the two would put an instance id " +
                "into the identity index as if it were a claim key");
            Assert.IsFalse(ClaimSchema.IsReserved("unifi.model"),
                "a provider's namespaced property must not read as reserved, or every legitimate property " +
                "would be refused and the run would land no data at all");
        }

        [DataTestMethod]
        [DataRow("garage")]
        [DataRow("GARAGE-2")]
        [DataRow("home.garage")]
        [DataRow("home_garage-2.attic")]
        [DataRow("0123456789")]
        public void AnInstanceIdOfTheAllowedShape_IsAccepted(String instanceId)
        {
            Assert.IsTrue(ClaimSchema.IsValidInstanceId(instanceId),
                "'" + instanceId + "' is letters, digits, dot, dash and underscore only, so refusing it " +
                "would reject a job whose identity is perfectly safe and leave the caller no spelling that " +
                "works");
        }

        [TestMethod]
        public void AnInstanceIdThatCouldComposeAnotherIdentitysKey_IsRejected()
        {
            var refused = new[] { "gar:age", "garage@unifi", "gar|age", "gar$age", "gar age", "gar/age", "", null };

            foreach (var instanceId in refused)
            {
                Assert.IsFalse(ClaimSchema.IsValidInstanceId(instanceId),
                    "'" + (instanceId ?? "<null>") + "' must be refused: the id is substituted into a " +
                    "property key and into every instance-scoped claim key, and derived edge keys join their " +
                    "parts with a pipe, so a colon, at sign, pipe or dollar lets two identities compose one " +
                    "identical key and one run then resolves into and reconciles away another integration's " +
                    "elements");
            }
        }

        [TestMethod]
        public void AnInstanceIdOf64CharactersIsAcceptedAnd65IsNot()
        {
            var longest = new String('a', ClaimSchema.MaxInstanceIdLength);
            var tooLong = new String('a', ClaimSchema.MaxInstanceIdLength + 1);

            Assert.AreEqual(64, ClaimSchema.MaxInstanceIdLength,
                "the bound is part of the contract the route documents; moving it silently would accept ids " +
                "today that a later run refuses, and the elements claimed under one would never be withdrawn");
            Assert.IsTrue(ClaimSchema.IsValidInstanceId(longest),
                "the longest allowed id must be accepted, or the documented bound is off by one and a caller " +
                "at the limit cannot run at all");
            Assert.IsFalse(ClaimSchema.IsValidInstanceId(tooLong),
                "one character past the bound must be refused: the id lands inside a property key and every " +
                "instance-scoped claim key, so it is bounded as well as shape-checked");
        }

        [TestMethod]
        public void IdentityProperty_UsesDenseOrdinalsFromZero_AndRefusesANegativeOrdinal()
        {
            Assert.AreEqual("$identity:0", ClaimSchema.IdentityProperty(0),
                "the first claim of an element lives at ordinal ZERO: the property surface carries no array, " +
                "so the dense ordinal IS the encoding of the set, and a first claim written elsewhere leaves " +
                "ordinal zero free for a later claim to be written over");
            Assert.AreEqual("$identity:1", ClaimSchema.IdentityProperty(1),
                "ordinals are dense, so the second claim is at one and the element's claims can be read back " +
                "without knowing how many there are");
            Assert.AreEqual("$identity:12", ClaimSchema.IdentityProperty(12),
                "an ordinal is rendered in invariant decimal, or a culture with other digits would compose a " +
                "key no backfill and no reader recognises");
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => ClaimSchema.IdentityProperty(-1),
                "a negative ordinal must throw rather than compose '$identity:-1': it would be an identity " +
                "property no ordinal scan reaches, so the claim would be invisible to the next resolve and " +
                "the element duplicated");
        }

        [TestMethod]
        public void ClaimProperty_AndClaimantOf_RoundTrip_SoAWithdrawalTouchesOnlyItsOwnClaim()
        {
            var property = ClaimSchema.ClaimProperty(Instance);

            Assert.AreEqual("$claim:garage", property,
                "the claim property is keyed by the CLAIMANT: two integrations asserting one device must " +
                "never touch the same property, because there is no compare-and-set anywhere in the REST " +
                "contract and a read-modify-write over a shared property silently loses one of them");
            Assert.AreEqual(Instance, ClaimSchema.ClaimantOf(property),
                "the claimant must be readable back off the key, since that is how withdrawal knows the " +
                "property it removes is its own and not another integration's");
            Assert.IsNull(ClaimSchema.ClaimantOf("$identity:0"),
                "an identity property names no claimant: read as one it would make an instance id out of a " +
                "claim key and have reconciliation withdraw from an element nobody claims");
            Assert.IsNull(ClaimSchema.ClaimantOf("unifi.model"),
                "a provider's own property names no claimant either, or a run could be talked into treating " +
                "provider data as a claim");
            Assert.ThrowsException<ArgumentException>(
                () => ClaimSchema.ClaimProperty(String.Empty),
                "an empty instance id must throw rather than compose the bare prefix '$claim:', which every " +
                "instance would then share and every reconciliation would withdraw from each other");
        }

        [TestMethod]
        public void NextIdentityOrdinal_IsTheHighestPresentPlusOne_SoAGapCannotOverwriteAnOldClaim()
        {
            var withGap = ElementWith(1,
                Text(ClaimSchema.IdentityProperty(0), "mac:44d244aabbcc"),
                Text(ClaimSchema.IdentityProperty(3), "serial:SN-0001"),
                Text(ClaimSchema.ClaimProperty(Instance), Instance));

            Assert.AreEqual(4, withGap.NextIdentityOrdinal(),
                "the next ordinal is the highest present plus one, never the COUNT: over a gap the count " +
                "would name an ordinal already in use, and appending a missing claim would overwrite an " +
                "existing one, leaving the element unfindable by the identity it just lost");
            Assert.AreEqual(0, ElementWith(2).NextIdentityOrdinal(),
                "an element carrying no claim starts at zero, or a newly reclaimed orphan's first claim " +
                "would sit at an ordinal nothing reads");
            Assert.AreEqual(0, ElementWith(3, Text("$identity:not-a-number", "mac:44d244aabbcc"))
                    .NextIdentityOrdinal(),
                "a suffix that is not an ordinal must be skipped rather than throwing: a resolve that threw " +
                "on one odd property would fail the whole run over an element some other writer touched");
        }

        #endregion

        #region ClaimKeyComposer

        [TestMethod]
        public void TheThreeScopeForms_ComposeExactlyTheKeysTheSpecShows()
        {
            Assert.IsTrue(ClaimKeyComposer.TryCompose(Type("mac"), "44:D2:44:AA:BB:CC", Provider, Instance,
                    out var global, out var globalFailure),
                "a well-formed MAC must compose a key, or the workhorse strong identifier of the whole " +
                "feature drops out and every device is duplicated on every run");
            Assert.IsTrue(ClaimKeyComposer.TryCompose(Type("unifi-device-id"), Uuid, Provider, Instance,
                    out var provider, out _),
                "a UniFi device UUID must compose a key, or an adopted device has no identity at all");
            Assert.IsTrue(ClaimKeyComposer.TryCompose(Type("fronius-unique-id"), "476", Provider, Instance,
                    out var instance, out _),
                "a Fronius UniqueID must compose a key, since it is the only identity that source has across " +
                "its own runs");

            Assert.AreEqual(ClaimKeyFailure.None, globalFailure,
                "a composed key reports no failure, or a caller reading the failure would drop a claim it " +
                "actually has");
            Assert.AreEqual("mac:44d244aabbcc", global,
                "the global form is '<type>:<canonical>'. Exact string equality on this key IS resolution, " +
                "so a different shape orphans every claim already in the graph and duplicates every element");
            Assert.AreEqual("unifi-device-id@unifi:" + Uuid, provider,
                "the provider form embeds the provider id, which is what keeps a vendor id space from being " +
                "compared against another vendor's identical-looking value");
            Assert.AreEqual("fronius-unique-id@garage:476", instance,
                "the instance form embeds the instance id, because a Fronius UniqueID is unique only inside " +
                "one inverter's API: without it two installations' different inverters compose one key and " +
                "the graph advertises an overlap that does not exist");
        }

        [TestMethod]
        public void AProviderScopedTypeComposedWithoutItsProviderId_IsRefused_AndNeverFallsBackToTheGlobalForm()
        {
            var deviceId = Type("unifi-device-id");

            Assert.IsFalse(ClaimKeyComposer.TryCompose(deviceId, Uuid, null, Instance, out var key,
                    out var failure),
                "a provider-scoped type with no provider id must be REFUSED, not composed: a global fallback " +
                "gives two installations' different values one key, which is the false equality the scope " +
                "field exists to prevent");
            Assert.AreEqual(ClaimKeyFailure.MissingScope, failure,
                "the refusal must be MissingScope rather than InvalidValue, since the value was fine and the " +
                "caller's fix is to supply the scope, not to correct the source");
            Assert.IsNull(key,
                "no key may come back at all: a key composed as 'unifi-device-id:<uuid>' would be indexed and " +
                "then match another installation's element on the next run");

            Assert.IsFalse(ClaimKeyComposer.TryCompose(deviceId, Uuid, String.Empty, Instance, out _, out var empty),
                "an empty provider id is the same absence as a missing one, or a caller could compose the " +
                "degenerate key 'unifi-device-id@:<uuid>' that every provider would share");
            Assert.AreEqual(ClaimKeyFailure.MissingScope, empty,
                "an empty provider id must report MissingScope for the same reason a null one does");
        }

        [TestMethod]
        public void AnInstanceScopedTypeComposedWithoutItsInstanceId_IsRefused_AndNeverFallsBackToTheGlobalForm()
        {
            var uniqueId = Type("fronius-unique-id");

            Assert.IsFalse(ClaimKeyComposer.TryCompose(uniqueId, "476", Provider, null, out var key,
                    out var failure),
                "an instance-scoped type with no instance id must be refused: '476' means something only " +
                "inside one inverter's API, so a global or provider-shaped key for it would resolve two " +
                "different installations' inverters to one element");
            Assert.AreEqual(ClaimKeyFailure.MissingScope, failure,
                "the refusal must be MissingScope, which tells the caller the value is fine and the scope is " +
                "what is missing");
            Assert.IsNull(key,
                "no key may come back: 'fronius-unique-id:476' would be a globally shared key for a value " +
                "the vendor documents as locally unique");
        }

        [TestMethod]
        public void AValueItsTypeCannotAccept_IsRefusedAsInvalidValue_RatherThanKeyedAsItArrived()
        {
            Assert.IsFalse(ClaimKeyComposer.TryCompose(Type("mac"), "44d244aabb", Provider, Instance,
                    out var key, out var failure),
                "a ten-digit MAC must not compose a key: keyed as it arrived it becomes a strong identity no " +
                "correctly read MAC will ever match, so the element is invisible to the next run and " +
                "duplicated");
            Assert.AreEqual(ClaimKeyFailure.InvalidValue, failure,
                "the failure must be InvalidValue, which is the invalidIdentifierValue diagnostic a provider " +
                "author acts on: a MissingScope here would send them looking at configuration instead of at " +
                "their source");
            Assert.IsNull(key,
                "a refused claim yields no key, or the write path would index a claim the composer rejected");
        }

        [TestMethod]
        public void ForEdge_ComposesTheDerivedKeyFromBothEndpointsAndTheType()
        {
            var derived = ClaimKeyComposer.ForEdge("mac:44d244aabbcc", "uplink", "mac:aabbccddeeff");

            Assert.AreEqual("edge:mac:44d244aabbcc|uplink|mac:aabbccddeeff", derived,
                "an edge has no intrinsic identifier and the graph cannot answer 'is there already an edge of " +
                "this type between these two' in one call, so this derived key is the only way a re-run " +
                "recognises its own edge: a different shape creates the edge again on every run");
            Assert.AreEqual("edge", ClaimKeyComposer.EdgeSegment,
                "the leading segment is deliberately NOT a vocabulary type, because a derived key must never " +
                "stand in for an element's identity");
            Assert.ThrowsException<ArgumentException>(
                () => ClaimKeyComposer.ForEdge("mac:44d244aabbcc", String.Empty, "mac:aabbccddeeff"),
                "an empty edge type must throw rather than compose a key with an empty middle segment: two " +
                "different relations between one pair of endpoints would then share one key and only the " +
                "first would ever be created");
        }

        [TestMethod]
        public void APrimaryKey_IsTheStrongestClaim_SoAWeakOneNeverNamesARelationsEndpoint()
        {
            var weak = new ComposedClaim("ipv4:192.168.1.10", "ipv4", "192.168.1.10", IdentifierStrength.Weak);
            var strong = new ComposedClaim("mac:44d244aabbcc", "mac", "44d244aabbcc", IdentifierStrength.Strong);

            Assert.IsTrue(String.CompareOrdinal(weak.Key, strong.Key) < 0,
                "fixture: the weak key must sort BEFORE the strong one, so only a strength-first rule can " +
                "pass this test");
            Assert.IsTrue(ClaimKeyComposer.TryPrimaryKey(new[] { weak, strong }, out var fromWeakFirst),
                "an entity with claims must have a primary key, or no relation could address it");
            Assert.IsTrue(ClaimKeyComposer.TryPrimaryKey(new[] { strong, weak }, out var fromStrongFirst),
                "the same claims in the other order must still yield a primary key");

            Assert.AreEqual(strong.Key, fromWeakFirst,
                "the primary key is the STRONGEST claim: derived from the weak one, an edge key would move " +
                "the moment DHCP moved the address, so the same relation composes a second key and the edge " +
                "is created twice");
            Assert.AreEqual(strong.Key, fromStrongFirst,
                "the pick must not depend on the order the provider listed its claims in, or two runs over " +
                "one unchanged source compose two keys for one relation and create the edge twice");
        }

        [TestMethod]
        public void AmongClaimsOfOneStrength_ThePrimaryKeyIsTheOrdinallyFirst_AndIsStableUnderReordering()
        {
            var mac = new ComposedClaim("mac:44d244aabbcc", "mac", "44d244aabbcc", IdentifierStrength.Strong);
            var serial = new ComposedClaim("serial:SN-0001", "serial", "SN-0001", IdentifierStrength.Strong);
            var imei = new ComposedClaim("imei:352099001761481", "imei", "352099001761481",
                IdentifierStrength.Strong);

            Assert.IsTrue(ClaimKeyComposer.TryPrimaryKey(new[] { mac, serial, imei }, out var listed),
                "three strong claims must yield a primary key, or no relation could address this entity and " +
                "every edge pointing at it would be dropped");
            Assert.IsTrue(ClaimKeyComposer.TryPrimaryKey(new[] { imei, serial, mac }, out var reversed),
                "the same three claims in another order must still yield one");
            Assert.IsTrue(ClaimKeyComposer.TryPrimaryKey(new[] { serial, mac, imei }, out var shuffled),
                "and in any other order too");

            Assert.AreEqual(imei.Key, listed,
                "among claims of one strength the ordinally FIRST key wins, which here is the imei key: " +
                "deriving from whichever claim a provider happened to list first would compose two keys for " +
                "one relation across two runs and create the edge twice");
            Assert.AreEqual(listed, reversed,
                "reversing the claim list must not move the primary key, or a provider that reorders its own " +
                "output between runs re-creates every edge it already created");
            Assert.AreEqual(listed, shuffled,
                "any order must give one answer, since the key is the only durable handle a relation has");

            Assert.IsFalse(ClaimKeyComposer.TryPrimaryKey(new ComposedClaim[0], out var none),
                "an entity with no claim has no primary key, and saying otherwise would let a relation " +
                "address an endpoint by an empty key that every unidentified element shares");
            Assert.IsNull(none,
                "no primary key may come back when there is none to derive");
        }

        #endregion

        #region IdentityResolver

        [TestMethod]
        public void ZeroOfItsOwnMatched_Creates_EvenWhenAnotherInstancesElementCarriesTheIdenticalClaimKey()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"));
            var macKey = KeyOf(entity, "mac");

            var byKey = Index();
            byKey[macKey] = new[] { 9 };
            var lookup = ClaimLookup.Build(byKey, Elements(ElementClaimedBy(9, OtherInstance, macKey)), Instance);

            var resolution = new IdentityResolver().Resolve(entity, lookup.InScope);

            Assert.AreEqual(ResolutionOutcome.Create, resolution.Outcome,
                "an element another instance claims must not be resolved to: writing into it would attach " +
                "this run's data to somebody else's element and make this instance's reconciliation " +
                "responsible for withdrawing and deleting it");
            Assert.AreEqual(0, resolution.MatchedElements.Length,
                "nothing of this instance's own matched, so nothing may be reported as matched: a matched id " +
                "here would be claimed by this run and later withdrawn from another integration's element");
            Assert.IsTrue(lookup.ByKey.ContainsKey(macKey),
                "the foreign element must stay in the raw index answer: two elements sharing one queryable " +
                "claim key is the whole mechanism by which an overlap becomes findable, and dropping it from " +
                "ByKey would also make an edge adopt another instance's edge instead of creating its own");
        }

        [TestMethod]
        public void ExactlyOneOfItsOwnMatched_Matches_AndNamesThatElementAlone()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"));
            var macKey = KeyOf(entity, "mac");

            var inScope = Index();
            inScope[macKey] = new[] { 42 };

            var resolution = new IdentityResolver().Resolve(entity, inScope);

            Assert.AreEqual(ResolutionOutcome.Match, resolution.Outcome,
                "one of this instance's own elements carries this entity's strong key, so the run must write " +
                "to it: creating instead duplicates the device on this run and on every run after it");
            Assert.AreEqual(42, resolution.ElementId,
                "the matched element must be the one the index named, or the run writes its data onto an " +
                "element the source never described");
            Assert.AreEqual(1, resolution.MatchedElements.Length,
                "exactly one matched, so exactly one may be reported: an extra id would be reported as a " +
                "duplicate the graph does not have");
        }

        [TestMethod]
        public void MoreThanOneOfItsOwnMatched_PicksByContentRatherThanById_AndReportsThemAll()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"), Claim("serial", "SN-0001"));
            var macKey = KeyOf(entity, "mac");
            var serialKey = KeyOf(entity, "serial");

            Assert.IsTrue(String.CompareOrdinal(macKey, serialKey) < 0,
                "fixture: the mac key must sort before the serial key, so the content-derived pick is the " +
                "element the mac key found");

            var inScope = Index();
            inScope[macKey] = new[] { 22 };
            inScope[serialKey] = new[] { 11 };

            var resolution = new IdentityResolver().Resolve(entity, inScope);

            Assert.AreEqual(ResolutionOutcome.MatchedMoreThanOne, resolution.Outcome,
                "two of this instance's own elements matched, and the run must say so: resolving to neither " +
                "contributes no element id to what the run claims, so reconciliation withdraws this " +
                "instance's claim from BOTH and deletes them, on every run");
            Assert.AreEqual(22, resolution.ElementId,
                "the pick is CONTENT-derived - the element whose ordinally first matched key sorts first - " +
                "not the lower id: an id-derived rule lands the same entity on a different element after " +
                "HEAD /trim renumbers element ids in place");
            Assert.AreEqual(2, resolution.MatchedElements.Length,
                "every matched element must be reported, because the ones not chosen are exactly the " +
                "elements this run's reconciliation converges away, and a report naming one of two hides " +
                "half of what the run did");
            var matched = new List<Int32>();
            foreach (var elementId in resolution.MatchedElements)
            {
                matched.Add(elementId);
            }

            CollectionAssert.AreEquivalent(new[] { 11, 22 }, matched,
                "the reported set must be both matched elements, or the duplicateClaimedElements diagnostic " +
                "names an element that is not one of the duplicates");
        }

        [TestMethod]
        public void ThePickSurvivesATrimThatRenumbersIdsInPlace_BecauseItFollowsTheKeyAndNotTheId()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"), Claim("serial", "SN-0001"));
            var macKey = KeyOf(entity, "mac");
            var serialKey = KeyOf(entity, "serial");
            var resolver = new IdentityResolver();

            var before = Index();
            before[macKey] = new[] { 22 };
            before[serialKey] = new[] { 11 };

            // The same two elements after HEAD /trim renumbered ids in place: the element carrying the mac
            // claim is now 11 and the one carrying the serial claim is now 22. Nothing about their CONTENT
            // changed.
            var after = Index();
            after[macKey] = new[] { 11 };
            after[serialKey] = new[] { 22 };

            var pickBefore = resolver.Resolve(entity, before);
            var pickAfter = resolver.Resolve(entity, after);

            Assert.AreEqual(22, pickBefore.ElementId,
                "before the trim the mac-carrying element is 22, and it must be the pick even though 11 is " +
                "the lower id: this is the assertion an id-derived rule fails");
            Assert.AreEqual(11, pickAfter.ElementId,
                "after the renumbering the pick must follow the mac key to its new id: a rule that always " +
                "took the lower id would have landed this entity on 11 before the trim and on 11 after it, " +
                "which is a DIFFERENT element, so the run would write this device's data onto the other one " +
                "and withdraw its claim from the element that actually holds its history");
        }

        [TestMethod]
        public void TwoOfItsOwnElementsUnderOneKey_TieToTheLowerId()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"));
            var macKey = KeyOf(entity, "mac");

            var inScope = Index();
            inScope[macKey] = new[] { 22, 11 };

            var resolution = new IdentityResolver().Resolve(entity, inScope);

            Assert.AreEqual(ResolutionOutcome.MatchedMoreThanOne, resolution.Outcome,
                "two elements under one key is the reportable duplicate case, not an ordinary match");
            Assert.AreEqual(11, resolution.ElementId,
                "with one key naming both, content cannot separate them, so the tie goes to the LOWER id: " +
                "the pick must not depend on the order the index listed its posting bucket in, or two runs " +
                "over an unchanged graph write to two different elements and each withdraws the other's claim");
        }

        [TestMethod]
        public void AWeakOnlyEntity_Creates_EvenWhenItsWeakKeyNamesAnElementThisInstanceAlreadyClaims()
        {
            var entity = Entity(Claim("ipv4", "192.168.1.10"));
            Assert.AreEqual(1, entity.Claims.Length,
                "fixture: the entity must carry exactly its one weak claim");
            Assert.IsFalse(entity.Claims[0].IsStrong,
                "fixture: ipv4 must be weak, or this test would be asserting the strong path");

            var ipv4Key = KeyOf(entity, "ipv4");
            var byKey = Index();
            byKey[ipv4Key] = new[] { 5 };
            var lookup = ClaimLookup.Build(byKey, Elements(ElementClaimedBy(5, Instance, ipv4Key)), Instance);

            Assert.IsTrue(lookup.InScope.ContainsKey(ipv4Key),
                "fixture: the weak key must be IN SCOPE, so the only thing that can keep the resolver off it " +
                "is the strength rule itself");

            var resolution = new IdentityResolver().Resolve(entity, lookup.InScope);

            Assert.AreEqual(ResolutionOutcome.Create, resolution.Outcome,
                "a weak claim must never resolve - not across instances and NOT EVEN against an element this " +
                "instance already claims: an address moves between devices, so matching on one attaches this " +
                "run's data to whichever element last held the value, and the most likely victim is this " +
                "runtime's own element");
        }

        [TestMethod]
        public void AWeakMatchNeverStandsInForAStrongOne_SoAnEntityWhoseStrongClaimFoundNothingIsCreated()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"), Claim("ipv4", "192.168.1.10"));
            var ipv4Key = KeyOf(entity, "ipv4");
            var macKey = KeyOf(entity, "mac");

            var inScope = Index();
            inScope[ipv4Key] = new[] { 5 };

            var resolution = new IdentityResolver().Resolve(entity, inScope);

            Assert.AreEqual(ResolutionOutcome.Create, resolution.Outcome,
                "the strong key found nothing and only the weak one hit, so the entity must be created: " +
                "letting the weak hit stand in would attach this device's data to whichever element last " +
                "held that address, which the previous tenant of the lease still owns");
            Assert.IsFalse(inScope.ContainsKey(macKey),
                "fixture: the strong key must be absent from the lookup, or this test would pass for the " +
                "wrong reason");
        }

        [TestMethod]
        public void TheResolverDecidesOnlyFromTheMapItIsGiven_SoNarrowingIsWhatKeepsAForeignElementOut()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"));
            var macKey = KeyOf(entity, "mac");

            var byKey = Index();
            byKey[macKey] = new[] { 9 };
            var lookup = ClaimLookup.Build(byKey, Elements(ElementClaimedBy(9, OtherInstance, macKey)), Instance);
            var resolver = new IdentityResolver();

            var narrowed = resolver.Resolve(entity, lookup.InScope);
            var raw = resolver.Resolve(entity, lookup.ByKey);

            Assert.AreEqual(ResolutionOutcome.Create, narrowed.Outcome,
                "over the narrowed map the foreign element is invisible and the entity is created, which is " +
                "the whole scope guarantee");
            Assert.AreEqual(ResolutionOutcome.Match, raw.Outcome,
                "over the RAW index answer the very same resolver matches that foreign element. The resolver " +
                "has no graph, no network and no clock, so nothing inside it can tell one element from " +
                "another: the narrowing in ClaimLookup.Build is the only thing standing between a run and " +
                "another integration's data, and this asserts that it is still doing that work");
            Assert.AreEqual(9, raw.ElementId,
                "the element the raw answer matches is the foreign one, which is exactly what must never be " +
                "handed to the resolver");
        }

        #endregion

        #region ElementScope and ClaimLookup

        [TestMethod]
        public void AnElementCarryingThisInstancesClaim_IsInScope()
        {
            var element = ElementClaimedBy(1, Instance, "mac:44d244aabbcc");

            Assert.IsTrue(ElementScope.IsInScope(element, Instance),
                "an element this instance already claims is the ordinary match target: excluding it makes " +
                "every run create a second element for a device this integration has claimed all along");
            Assert.IsTrue(element.IsClaimedBy(Instance),
                "the claim property must be recognised by its claimant, since that is what withdrawal and " +
                "the in-scope rule both read");
            Assert.IsFalse(element.IsClaimedBy(OtherInstance),
                "another instance must not appear to claim it, or reconciliation would withdraw a claim that " +
                "was never made and delete an element on its last claim");
        }

        [TestMethod]
        public void AnElementCarryingNoClaimAtAll_IsInScope_BecauseThatIsTheOrphanReclaimPath()
        {
            var entity = Entity(Claim("mac", "44:D2:44:AA:BB:CC"));
            var macKey = KeyOf(entity, "mac");
            var orphan = ElementClaimedBy(4, null, macKey);

            Assert.IsFalse(orphan.HasAnyClaim(),
                "fixture: the orphan carries identity claims and NO claim property, which is exactly what a " +
                "withdrawal followed by a deferred deletion leaves behind");
            Assert.IsTrue(ElementScope.IsInScope(orphan, Instance),
                "an element carrying no claim at all is IN SCOPE: the unclaimed arm is load-bearing, not " +
                "lax. Excluding it makes that orphan invisible forever and the graph gains a duplicate on " +
                "every run, permanently");

            var byKey = Index();
            byKey[macKey] = new[] { 4 };
            var lookup = ClaimLookup.Build(byKey, Elements(orphan), Instance);

            Assert.IsTrue(lookup.InScope.ContainsKey(macKey),
                "the narrowing must keep the orphan, or the resolver never sees the element this run is " +
                "supposed to reclaim");
            Assert.AreEqual(ResolutionOutcome.Match, new IdentityResolver().Resolve(entity, lookup.InScope).Outcome,
                "reclaiming the orphan must be a MATCH end to end: any other answer creates a second element " +
                "and leaves the first carrying this instance's identity claims with nobody to withdraw them");
        }

        [TestMethod]
        public void AForeignClaimedElement_IsOutOfScope_ButStaysInByKey()
        {
            var foreignKey = "mac:44d244aabbcc";
            var foreign = ElementClaimedBy(9, OtherInstance, foreignKey);
            var byKey = Index();
            byKey[foreignKey] = new[] { 9 };

            var lookup = ClaimLookup.Build(byKey, Elements(foreign), Instance);

            Assert.IsFalse(ElementScope.IsInScope(foreign, Instance),
                "an element another instance claims is out of scope: writing to it would make this run's " +
                "reconciliation responsible for another integration's element and eventually delete it");
            Assert.IsFalse(lookup.InScope.ContainsKey(foreignKey),
                "a key whose only element is foreign must not appear in the in-scope map at all, or an entity " +
                "would resolve into somebody else's data");
            Assert.IsTrue(lookup.ByKey.ContainsKey(foreignKey),
                "it must STAY in the raw answer: an edge found by its derived key has to see the foreign hit " +
                "so it can fall through and create its own edge rather than adopting another instance's, and " +
                "two elements sharing one claim key is how an overlap becomes findable");
            Assert.IsTrue(lookup.Elements.ContainsKey(9),
                "the element's state must stay available, since the edge rule reads the claim off it and " +
                "nothing downstream re-reads the graph");
        }

        [TestMethod]
        public void AnElementCarryingThisInstancesClaimAndAForeignOne_IsInScope()
        {
            var element = ElementWith(7,
                Text(ClaimSchema.IdentityProperty(0), "mac:44d244aabbcc"),
                Text(ClaimSchema.ClaimProperty(Instance), Instance),
                Text(ClaimSchema.ClaimProperty(OtherInstance), OtherInstance));

            Assert.IsTrue(ElementScope.IsInScope(element, Instance),
                "an element somebody unified by hand carries two claims, and this instance's own claim is " +
                "what puts it in scope: refusing it would have this run create a duplicate next to the " +
                "element it demonstrably claims");
            Assert.IsTrue(element.HasAnyClaim(),
                "it is claimed, which is what keeps a later withdrawal from deleting it while the other " +
                "instance still asserts it");
        }

        [TestMethod]
        public void AnIdTheIndexNamedButTheBatchReadDidNotReturn_IsNotInScope()
        {
            var key = "mac:44d244aabbcc";
            var byKey = Index();
            byKey[key] = new[] { 77 };

            var lookup = ClaimLookup.Build(byKey, Elements(), Instance);

            Assert.IsFalse(lookup.InScope.ContainsKey(key),
                "an id the index named but the batched read did not return has no state, so nothing says who " +
                "claims it: treating it as in scope would write this run's data into an element that may be " +
                "another instance's, or one the trim already renumbered away");
            Assert.IsTrue(lookup.ByKey.ContainsKey(key),
                "the raw answer is kept as it came, because it is what the edge rule and the overlap story " +
                "read, and silently editing it would hide a stale index entry from every later diagnosis");
        }

        #endregion
    }
}
