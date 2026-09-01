// MIT License
//
// IntegrationsArxmlReaderTest.cs
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
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Providers.AutosarArxml;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The AUTOSAR reader (feature autosar-arxml, phase 1): path reconstruction, the interest set,
    ///   two-stage reference resolution, the triggering indirections, the unit denormalisation, and
    ///   every refusal.
    ///
    ///   <para>Every fixture here is HAND-AUTHORED and describes an invented network. No content
    ///   derived from a real manufacturer's export appears in this repository in any form, which is a
    ///   rule of the feature rather than a preference (spec section 11).</para>
    /// </summary>
    [TestClass]
    public class IntegrationsArxmlReaderTest
    {
        #region the synthetic fixture

        /// <summary>
        ///   An invented three-ECU FlexRay network. It is deliberately shaped to exercise every rule
        ///   the reader has: a container PDU carrying a secured PDU carrying a signal PDU (so both
        ///   triggering indirections are on one chain), ports in both directions, a frame with a
        ///   schedule, a signal whose unit has to be reached two hops away, and a signal with no
        ///   compu-method chain at all so the ABSENT case is covered too.
        ///
        ///   <para>The odometer signal is the semantic-search subject (spec section 9): its
        ///   descriptions deliberately never say "kilometer", so only its unit connects it to one.
        ///   The speed signal is its near miss, carrying km/h.</para>
        /// </summary>
        private const String Fixture = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Units</SHORT-NAME>
                  <ELEMENTS>
                    <UNIT>
                      <SHORT-NAME>UNIT_KM</SHORT-NAME>
                      <DISPLAY-NAME>km</DISPLAY-NAME>
                    </UNIT>
                    <UNIT>
                      <SHORT-NAME>UNIT_KMH</SHORT-NAME>
                      <DISPLAY-NAME>km/h</DISPLAY-NAME>
                    </UNIT>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>CompuMethods</SHORT-NAME>
                  <ELEMENTS>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_TotalDistance</SHORT-NAME>
                      <CATEGORY>LINEAR</CATEGORY>
                      <UNIT-REF DEST="UNIT">/Units/UNIT_KM</UNIT-REF>
                    </COMPU-METHOD>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_VehicleSpeed</SHORT-NAME>
                      <CATEGORY>LINEAR</CATEGORY>
                      <UNIT-REF DEST="UNIT">/Units/UNIT_KMH</UNIT-REF>
                    </COMPU-METHOD>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>SystemSignals</SHORT-NAME>
                  <ELEMENTS>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_OdoTotalDist</SHORT-NAME>
                      <DESC>
                        <L-2 L="DE">Gesamtstrecke seit Auslieferung</L-2>
                        <L-2 L="EN">Accumulated distance travelled since delivery</L-2>
                      </DESC>
                      <PHYSICAL-PROPS>
                        <SW-DATA-DEF-PROPS-VARIANTS>
                          <SW-DATA-DEF-PROPS-CONDITIONAL>
                            <COMPU-METHOD-REF DEST="COMPU-METHOD">/CompuMethods/CM_TotalDistance</COMPU-METHOD-REF>
                          </SW-DATA-DEF-PROPS-CONDITIONAL>
                        </SW-DATA-DEF-PROPS-VARIANTS>
                      </PHYSICAL-PROPS>
                    </SYSTEM-SIGNAL>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_VehicleSpeed</SHORT-NAME>
                      <DESC>
                        <L-2 L="DE">Fahrzeuggeschwindigkeit</L-2>
                        <L-2 L="EN">Vehicle speed</L-2>
                        <L-2 L="FR">Vitesse du vehicule</L-2>
                      </DESC>
                      <PHYSICAL-PROPS>
                        <SW-DATA-DEF-PROPS-VARIANTS>
                          <SW-DATA-DEF-PROPS-CONDITIONAL>
                            <COMPU-METHOD-REF DEST="COMPU-METHOD">/CompuMethods/CM_VehicleSpeed</COMPU-METHOD-REF>
                          </SW-DATA-DEF-PROPS-CONDITIONAL>
                        </SW-DATA-DEF-PROPS-VARIANTS>
                      </PHYSICAL-PROPS>
                    </SYSTEM-SIGNAL>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_DoorLatch</SHORT-NAME>
                      <DESC>
                        <L-2 L="EN">Door latch state</L-2>
                      </DESC>
                    </SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_OdoTotalDist</SHORT-NAME>
                      <DESC>
                        <L-2 L="DE">Gesamtstrecke seit Auslieferung</L-2>
                        <L-2 L="EN">Accumulated distance travelled since delivery</L-2>
                      </DESC>
                      <I-SIGNAL-TYPE>PRIMITIVE</I-SIGNAL-TYPE>
                      <INIT-VALUE>
                        <NUMERICAL-VALUE-SPECIFICATION>
                          <VALUE>0</VALUE>
                        </NUMERICAL-VALUE-SPECIFICATION>
                      </INIT-VALUE>
                      <LENGTH>32</LENGTH>
                      <NETWORK-REPRESENTATION-PROPS>
                        <SW-DATA-DEF-PROPS-VARIANTS>
                          <SW-DATA-DEF-PROPS-CONDITIONAL>
                            <BASE-TYPE-REF DEST="SW-BASE-TYPE">/AUTOSAR_Platform/BaseTypes/uint32</BASE-TYPE-REF>
                          </SW-DATA-DEF-PROPS-CONDITIONAL>
                        </SW-DATA-DEF-PROPS-VARIANTS>
                      </NETWORK-REPRESENTATION-PROPS>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_OdoTotalDist</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_VehicleSpeed</SHORT-NAME>
                      <DESC>
                        <L-2 L="DE">Fahrzeuggeschwindigkeit</L-2>
                        <L-2 L="EN">Vehicle speed</L-2>
                      </DESC>
                      <LENGTH>16</LENGTH>
                      <NETWORK-REPRESENTATION-PROPS>
                        <SW-DATA-DEF-PROPS-VARIANTS>
                          <SW-DATA-DEF-PROPS-CONDITIONAL>
                            <BASE-TYPE-REF DEST="SW-BASE-TYPE">/AUTOSAR_Platform/BaseTypes/uint16</BASE-TYPE-REF>
                          </SW-DATA-DEF-PROPS-CONDITIONAL>
                        </SW-DATA-DEF-PROPS-VARIANTS>
                      </NETWORK-REPRESENTATION-PROPS>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_VehicleSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_DoorLatch</SHORT-NAME>
                      <DESC>
                        <L-2 L="EN">Door latch state</L-2>
                      </DESC>
                      <LENGTH>4</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_DoorLatch</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_DistanceReport</SHORT-NAME>
                      <DESC>
                        <L-2 L="DE">Streckenmeldung</L-2>
                        <L-2 L="EN">Distance report</L-2>
                      </DESC>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_OdoTotalDist</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_OdoTotalDist</I-SIGNAL-REF>
                          <START-POSITION>0</START-POSITION>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_VehicleSpeed</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_VehicleSpeed</I-SIGNAL-REF>
                          <START-POSITION>32</START-POSITION>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_VehicleSpeed_Repeated</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_VehicleSpeed</I-SIGNAL-REF>
                          <START-POSITION>48</START-POSITION>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                    <SECURED-I-PDU>
                      <SHORT-NAME>PDU_DistanceReport_Secured</SHORT-NAME>
                      <LENGTH>12</LENGTH>
                      <PAYLOAD-REF DEST="PDU-TRIGGERING">/Clusters/DEMOBUS/DEMOBUS_CH_A/PT_DistanceReport</PAYLOAD-REF>
                    </SECURED-I-PDU>
                    <CONTAINER-I-PDU>
                      <SHORT-NAME>PDU_AlphaContainer</SHORT-NAME>
                      <LENGTH>32</LENGTH>
                      <CONTAINED-PDU-TRIGGERING-REFS>
                        <CONTAINED-PDU-TRIGGERING-REF DEST="PDU-TRIGGERING">/Clusters/DEMOBUS/DEMOBUS_CH_A/PT_DistanceReport_Secured</CONTAINED-PDU-TRIGGERING-REF>
                      </CONTAINED-PDU-TRIGGERING-REFS>
                    </CONTAINER-I-PDU>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_BetaStatus</SHORT-NAME>
                      <LENGTH>4</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_DoorLatch</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_DoorLatch</I-SIGNAL-REF>
                          <START-POSITION>0</START-POSITION>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                    <NM-PDU>
                      <SHORT-NAME>PDU_NetworkManagement</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                    </NM-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-FRAME>
                      <SHORT-NAME>FRM_AlphaMain</SHORT-NAME>
                      <FRAME-LENGTH>32</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>FMAP_AlphaContainer</SHORT-NAME>
                          <PDU-REF DEST="CONTAINER-I-PDU">/Pdus/PDU_AlphaContainer</PDU-REF>
                          <START-POSITION>0</START-POSITION>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </FLEXRAY-FRAME>
                    <FLEXRAY-FRAME>
                      <SHORT-NAME>FRM_BetaStatus</SHORT-NAME>
                      <FRAME-LENGTH>16</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>FMAP_BetaStatus</SHORT-NAME>
                          <PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_BetaStatus</PDU-REF>
                          <START-POSITION>0</START-POSITION>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </FLEXRAY-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>EcuInstances</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ALPHA_CTRL</SHORT-NAME>
                      <COMM-CONTROLLERS>
                        <FLEXRAY-COMMUNICATION-CONTROLLER>
                          <SHORT-NAME>ALPHA_CTRL_CC</SHORT-NAME>
                        </FLEXRAY-COMMUNICATION-CONTROLLER>
                      </COMM-CONTROLLERS>
                      <CONNECTORS>
                        <FLEXRAY-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>ALPHA_CTRL_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_AlphaMain_Out</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                            <I-SIGNAL-PORT>
                              <SHORT-NAME>SP_OdoTotalDist_Out</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </I-SIGNAL-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </FLEXRAY-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                    <ECU-INSTANCE>
                      <SHORT-NAME>BETA_CTRL</SHORT-NAME>
                      <CONNECTORS>
                        <FLEXRAY-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>BETA_CTRL_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_AlphaMain_In</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_BetaStatus_Out</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                            <I-SIGNAL-PORT>
                              <SHORT-NAME>SP_OdoTotalDist_In</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </I-SIGNAL-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </FLEXRAY-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                    <ECU-INSTANCE>
                      <SHORT-NAME>GAMMA_CTRL</SHORT-NAME>
                      <CONNECTORS>
                        <FLEXRAY-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>GAMMA_CTRL_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_AlphaMain_In</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </FLEXRAY-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>DEMOBUS</SHORT-NAME>
                      <FLEXRAY-CLUSTER-VARIANTS>
                        <FLEXRAY-CLUSTER-CONDITIONAL>
                          <PHYSICAL-CHANNELS>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>DEMOBUS_CH_A</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/EcuInstances/ALPHA_CTRL/ALPHA_CTRL_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/EcuInstances/BETA_CTRL/BETA_CTRL_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/EcuInstances/GAMMA_CTRL/GAMMA_CTRL_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <FRAME-TRIGGERINGS>
                                <FLEXRAY-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_AlphaMain</SHORT-NAME>
                                  <FRAME-PORT-REFS>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/ALPHA_CTRL/ALPHA_CTRL_CONN/FP_AlphaMain_Out</FRAME-PORT-REF>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/BETA_CTRL/BETA_CTRL_CONN/FP_AlphaMain_In</FRAME-PORT-REF>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/GAMMA_CTRL/GAMMA_CTRL_CONN/FP_AlphaMain_In</FRAME-PORT-REF>
                                  </FRAME-PORT-REFS>
                                  <FRAME-REF DEST="FLEXRAY-FRAME">/Frames/FRM_AlphaMain</FRAME-REF>
                                  <PDU-TRIGGERINGS>
                                    <PDU-TRIGGERING-REF-CONDITIONAL>
                                      <PDU-TRIGGERING-REF DEST="PDU-TRIGGERING">/Clusters/DEMOBUS/DEMOBUS_CH_A/PT_AlphaContainer</PDU-TRIGGERING-REF>
                                    </PDU-TRIGGERING-REF-CONDITIONAL>
                                  </PDU-TRIGGERINGS>
                                  <ABSOLUTELY-SCHEDULED-TIMINGS>
                                    <FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                      <COMMUNICATION-CYCLE>
                                        <CYCLE-REPETITION>
                                          <BASE-CYCLE>0</BASE-CYCLE>
                                          <CYCLE-REPETITION>CYCLE-REPETITION-1</CYCLE-REPETITION>
                                        </CYCLE-REPETITION>
                                      </COMMUNICATION-CYCLE>
                                      <SLOT-ID>3</SLOT-ID>
                                    </FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                  </ABSOLUTELY-SCHEDULED-TIMINGS>
                                </FLEXRAY-FRAME-TRIGGERING>
                                <FLEXRAY-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_BetaStatus</SHORT-NAME>
                                  <FRAME-PORT-REFS>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/BETA_CTRL/BETA_CTRL_CONN/FP_BetaStatus_Out</FRAME-PORT-REF>
                                  </FRAME-PORT-REFS>
                                  <FRAME-REF DEST="FLEXRAY-FRAME">/Frames/FRM_BetaStatus</FRAME-REF>
                                  <ABSOLUTELY-SCHEDULED-TIMINGS>
                                    <FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                      <COMMUNICATION-CYCLE>
                                        <CYCLE-REPETITION>
                                          <BASE-CYCLE>1</BASE-CYCLE>
                                          <CYCLE-REPETITION>CYCLE-REPETITION-4</CYCLE-REPETITION>
                                        </CYCLE-REPETITION>
                                      </COMMUNICATION-CYCLE>
                                      <SLOT-ID>7</SLOT-ID>
                                    </FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                  </ABSOLUTELY-SCHEDULED-TIMINGS>
                                </FLEXRAY-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                              <I-SIGNAL-TRIGGERINGS>
                                <I-SIGNAL-TRIGGERING>
                                  <SHORT-NAME>ST_OdoTotalDist</SHORT-NAME>
                                  <I-SIGNAL-PORT-REFS>
                                    <I-SIGNAL-PORT-REF DEST="I-SIGNAL-PORT">/EcuInstances/ALPHA_CTRL/ALPHA_CTRL_CONN/SP_OdoTotalDist_Out</I-SIGNAL-PORT-REF>
                                    <I-SIGNAL-PORT-REF DEST="I-SIGNAL-PORT">/EcuInstances/BETA_CTRL/BETA_CTRL_CONN/SP_OdoTotalDist_In</I-SIGNAL-PORT-REF>
                                  </I-SIGNAL-PORT-REFS>
                                  <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_OdoTotalDist</I-SIGNAL-REF>
                                </I-SIGNAL-TRIGGERING>
                              </I-SIGNAL-TRIGGERINGS>
                              <PDU-TRIGGERINGS>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_AlphaContainer</SHORT-NAME>
                                  <I-PDU-REF DEST="CONTAINER-I-PDU">/Pdus/PDU_AlphaContainer</I-PDU-REF>
                                </PDU-TRIGGERING>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_DistanceReport_Secured</SHORT-NAME>
                                  <I-PDU-REF DEST="SECURED-I-PDU">/Pdus/PDU_DistanceReport_Secured</I-PDU-REF>
                                </PDU-TRIGGERING>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_DistanceReport</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_DistanceReport</I-PDU-REF>
                                </PDU-TRIGGERING>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_BetaStatus</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_BetaStatus</I-PDU-REF>
                                </PDU-TRIGGERING>
                              </PDU-TRIGGERINGS>
                            </FLEXRAY-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </FLEXRAY-CLUSTER-CONDITIONAL>
                      </FLEXRAY-CLUSTER-VARIANTS>
                    </FLEXRAY-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        #endregion

        #region a document is read from its BYTES

        /// <summary>
        ///   The two input shapes describe the SAME network, asserted on the order-sensitive serialisation
        ///   rather than on counts. This is the whole safety argument for the provider switching to bytes:
        ///   the reader was already streaming, so nothing about what it collects may change with how the
        ///   document was handed over.
        /// </summary>
        [TestMethod]
        public void TheSameDocumentReadAsBytesAndAsTextDescribesTheSameNetwork()
        {
            var asText = ArxmlReader.Read(Fixture);

            using var bytes = new MemoryStream(new UTF8Encoding(false).GetBytes(Fixture), writable: false);
            var asBytes = ArxmlReader.Read(bytes);

            Assert.AreEqual(Describe(asText), Describe(asBytes),
                "reading a document from its bytes must describe exactly what reading its text described, " +
                "element for element and relation for relation, in the same order: the provider now hands " +
                "over bytes for every extract, so any difference here is a difference in what the graph " +
                "gets");
        }

        /// <summary>
        ///   THE CORRECTNESS GAIN, and it is the reason to prefer bytes rather than a bonus. Decoding a
        ///   document to text first can only guess at its encoding - a byte-order mark, else UTF-8 - while
        ///   an XmlReader given the bytes honours the document's own declaration. So a document declaring a
        ///   single-byte encoding with no mark is read correctly from bytes and CORRUPTED through text.
        ///
        ///   <para>The unit is where a real extract carries a non-ASCII character: a short name may not hold
        ///   one, and a unit's display name is DENORMALISED onto every signal that reaches it, so a wrong
        ///   decode here is wrong data on many elements rather than one cosmetic string.</para>
        ///
        ///   <para>Asserted in both directions on purpose: the second half is the mutation check. If the byte
        ///   path ever grows a decode-to-string step, the first assertion starts failing - and if the text
        ///   path stops corrupting it, this test has stopped testing anything.</para>
        /// </summary>
        [TestMethod]
        public void ADocumentDeclaringASingleByteEncodingIsReadByItsDeclaration_OnlyFromBytes()
        {
            var document = Fixture
                .Replace("encoding=\"UTF-8\"", "encoding=\"iso-8859-1\"", StringComparison.Ordinal)
                .Replace("<DISPLAY-NAME>km</DISPLAY-NAME>", "<DISPLAY-NAME>km\u00b2</DISPLAY-NAME>",
                    StringComparison.Ordinal);
            var encoded = Encoding.GetEncoding("iso-8859-1").GetBytes(document);

            using var stream = new MemoryStream(encoded, writable: false);
            var fromBytes = ArxmlReader.Read(stream);
            var fromText = ArxmlReader.Read(Encoding.UTF8.GetString(encoded));

            Assert.AreEqual("km\u00b2", Element(fromBytes, "/ISignals/SIG_OdoTotalDist")[ArxmlProperties.Unit],
                "an XmlReader over the bytes honours encoding=\"iso-8859-1\", so the superscript arrives as " +
                "itself. This is the case a mount-era reader got right and decoding to text silently broke");
            Assert.AreNotEqual("km\u00b2", Element(fromText, "/ISignals/SIG_OdoTotalDist")[ArxmlProperties.Unit],
                "and the text path CANNOT get it right, because by the time it is text the declaration is " +
                "unreadable. If this ever starts failing, the two paths have converged and the assertion " +
                "above is passing for a different reason than the one it names");
        }

        /// <summary>
        ///   UTF-16 with a mark still reads, which the decode-to-text path handled and a byte path must not
        ///   regress: an extract written by a tool that emits UTF-16 is the ordinary case that seam was
        ///   careful about before it streamed.
        /// </summary>
        [TestMethod]
        public void AUtf16DocumentWithAMarkReadsFromBytes()
        {
            var document = Fixture.Replace("encoding=\"UTF-8\"", "encoding=\"utf-16\"",
                StringComparison.Ordinal);
            var preamble = Encoding.Unicode.GetPreamble();
            var body = Encoding.Unicode.GetBytes(document);
            var encoded = new Byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, encoded, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, encoded, preamble.Length, body.Length);

            using var stream = new MemoryStream(encoded, writable: false);
            var network = ArxmlReader.Read(stream);

            Assert.AreEqual(Describe(ArxmlReader.Read(Fixture)), Describe(network),
                "a UTF-16 extract must read from bytes exactly as its UTF-8 twin does, mark detection " +
                "included: the transport carries whatever the tool wrote, and this is the encoding an " +
                "AUTOSAR export is most likely to arrive in after UTF-8");
        }

        /// <summary>
        ///   A SET of documents read as bytes resolves across them exactly as the text path does. Not implied
        ///   by the single-document parity: the union table and the first-declaration-wins rule live across
        ///   Add calls, so this is what says a per-file stream did not become a per-file read.
        /// </summary>
        [TestMethod]
        public void ASetOfDocumentsReadAsBytesResolvesAcrossThemAsTheTextPathDoes()
        {
            var asText = ReadSet(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));
            var asBytes = ReadSetAsBytes(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));

            Assert.AreEqual(Describe(asText), Describe(asBytes),
                "a frame in one extract carrying a signal defined in another is the whole reason a job may " +
                "carry several, and that resolution has to survive the switch to bytes");
        }

        /// <summary>
        ///   The byte overload goes through the SAME gate as the text one. A separate implementation that
        ///   skipped it would silently add a document to a read whose resolution has already happened, and
        ///   that document would be described by nothing.
        /// </summary>
        [TestMethod]
        public void ADocumentAddedAsBytesAfterCompleteIsRefused()
        {
            var reader = new ArxmlReader();
            reader.Add("first.arxml", ChassisExtract);
            reader.Complete();

            using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(BodyExtract), false);

            var refused = Assert.ThrowsException<InvalidOperationException>(
                () => reader.Add("second.arxml", stream),
                "adding a document after Complete must be refused whichever shape it arrives in");
            StringAssert.Contains(refused.Message, "resolution has already run", refused.Message);
        }

        /// <summary>
        ///   A malformed document read as bytes is still an ArxmlFormatException naming the FILE, not an
        ///   XmlException escaping to the caller: the provider turns the former into a failed run and would
        ///   let the latter through as an unexplained crash.
        /// </summary>
        [TestMethod]
        public void MalformedBytesAreRefusedWithTheFileNamed()
        {
            var reader = new ArxmlReader();
            using var stream = new MemoryStream(
                new UTF8Encoding(false).GetBytes("<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\">"), false);

            var refused = Assert.ThrowsException<ArxmlFormatException>(
                () => reader.Add("truncated.arxml", stream));

            StringAssert.Contains(refused.Message, "truncated.arxml",
                "the refusal has to name the file, which is the only actionable part when several extracts " +
                "arrived in one job: " + refused.Message);
        }

        [TestMethod]
        public void AMissingByteArgumentIsRefusedRatherThanTreatedAsAnEmptyDocument()
        {
            var reader = new ArxmlReader();

            Assert.ThrowsException<ArgumentNullException>(() => reader.Add("x.arxml", (Stream)null),
                "a null stream is a caller defect, and reading it as 'a document with nothing in it' would " +
                "describe an empty network - which withdraws every element the identity claimed");
            Assert.ThrowsException<ArgumentNullException>(
                () => reader.Add(null, new MemoryStream(Array.Empty<Byte>())),
                "and a document with no name cannot be named by the refusal that mentions it");
        }

        #endregion

        #region helpers

        private static ArxmlNetwork Read()
        {
            return ArxmlReader.Read(Fixture);
        }

        private static ArxmlElement Element(ArxmlNetwork network, String path)
        {
            var found = network.Elements.SingleOrDefault(e => e.Path == path);
            Assert.IsNotNull(found, "the fixture must describe '" + path + "'");
            return found;
        }

        private static List<String> Targets(ArxmlNetwork network, String fromPath, String type)
        {
            return network.Relations
                .Where(r => r.FromPath == fromPath && r.Type == type)
                .Select(r => r.ToPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        private static List<String> Sources(ArxmlNetwork network, String type, String toPath)
        {
            return network.Relations
                .Where(r => r.ToPath == toPath && r.Type == type)
                .Select(r => r.FromPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        private static ArxmlFormatException Refused(String xml)
        {
            return Assert.ThrowsException<ArxmlFormatException>(() => ArxmlReader.Read(xml));
        }

        /// <summary>Reads several NAMED documents as ONE source, in the order given.</summary>
        private static ArxmlNetwork ReadSet(params (String Name, String Xml)[] documents)
        {
            var reader = new ArxmlReader();
            foreach (var document in documents)
            {
                reader.Add(document.Name, document.Xml);
            }

            return reader.Complete();
        }

        /// <summary>The same set, handed over as BYTES: one stream per document, disposed as a run does.</summary>
        private static ArxmlNetwork ReadSetAsBytes(params (String Name, String Xml)[] documents)
        {
            var reader = new ArxmlReader();
            foreach (var document in documents)
            {
                using var bytes = new MemoryStream(new UTF8Encoding(false).GetBytes(document.Xml), false);
                reader.Add(document.Name, bytes);
            }

            return reader.Complete();
        }

        private static ArxmlFormatException RefusedSet(params (String Name, String Xml)[] documents)
        {
            return Assert.ThrowsException<ArxmlFormatException>(() => ReadSet(documents));
        }

        /// <summary>
        ///   Everything a read produced, as one string: every element with its properties in the order they
        ///   were written, every relation, every diagnostic. The whole point is that it is ORDER-SENSITIVE,
        ///   because the conformance suite compares two runs on a serialised snapshot and a merge that leaked
        ///   dictionary iteration order would pass a count-based comparison and fail that one.
        /// </summary>
        private static String Describe(ArxmlNetwork network)
        {
            var text = new StringBuilder();
            foreach (var element in network.Elements)
            {
                text.Append(element.Kind).Append(' ').Append(element.Path);
                foreach (var property in element.Properties)
                {
                    text.Append(" |").Append(property.Key).Append('=').Append(property.Value);
                }

                text.Append('\n');
            }

            foreach (var relation in network.Relations)
            {
                text.Append(relation.Type).Append(' ').Append(relation.FromPath)
                    .Append(" to ").Append(relation.ToPath).Append('\n');
            }

            foreach (var diagnostic in network.Diagnostics)
            {
                text.Append(diagnostic.Kind).Append(' ').Append(diagnostic.Subject)
                    .Append(' ').Append(diagnostic.Message).Append('\n');
            }

            return text.ToString();
        }

        #endregion

        #region what the fixture yields

        [TestMethod]
        public void TheFixture_IsDescribedWhole_WithNoDiagnostics()
        {
            var network = Read();

            Assert.AreEqual(0, network.Diagnostics.Count,
                "the fixture defines every path it references, so a diagnostic here means the reader " +
                "cannot resolve a reference the standard writes plainly: " +
                String.Join("; ", network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));

            var byKind = network.Elements.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
            Assert.AreEqual(1, byKind[ArxmlKinds.Network], "one cluster is one network");
            Assert.AreEqual(3, byKind[ArxmlKinds.Ecu]);
            Assert.AreEqual(2, byKind[ArxmlKinds.Frame]);
            Assert.AreEqual(5, byKind[ArxmlKinds.Pdu], "four flavours plus the NM PDU");
            Assert.AreEqual(3, byKind[ArxmlKinds.Signal]);
            Assert.AreEqual(3, byKind[ArxmlKinds.SystemSignal]);
            Assert.AreEqual(2, byKind[ArxmlKinds.CompuMethod]);
            Assert.AreEqual(7, byKind.Count, "a UNIT is not an entity, it is where a unit's display name comes from");
        }

        [TestMethod]
        public void APathIsRebuiltFromShortNames_SkippingTheStandardsUnnamedWrappers()
        {
            var network = Read();

            // AR-PACKAGES and ELEMENTS carry no short name and contribute nothing, which is exactly the
            // standard's path semantics; a reader that counted them would produce paths no reference matches.
            Element(network, "/ISignals/SIG_OdoTotalDist");
            Element(network, "/Pdus/PDU_AlphaContainer");
            Element(network, "/Clusters/DEMOBUS");

            // A NAMED sibling inside the same element (the communication controller) must not disturb the
            // stack either: if it did, the connector path would gain a segment and every port reference
            // in the file would stop resolving.
            Assert.AreEqual(3, network.Relations.Count(r => r.Type == ArxmlRelations.AttachedTo),
                "all three ECUs attach to the network, which they can only do if their connector paths " +
                "were rebuilt exactly as the channel writes them");
        }

        [TestMethod]
        public void TheNetworkIsTheCluster_NotItsPhysicalChannel()
        {
            var network = Read();
            var bus = Element(network, "/Clusters/DEMOBUS");

            Assert.AreEqual(ArxmlKinds.Network, bus.Kind);
            Assert.AreEqual("DEMOBUS", bus[ArxmlProperties.Name]);
            Assert.AreEqual(ArxmlProperties.FlexRayProtocol, bus[ArxmlProperties.Protocol]);
            Assert.AreEqual("1", bus[ArxmlProperties.ChannelCount]);
            Assert.IsFalse(network.Elements.Any(e => e.Path.StartsWith("/Clusters/DEMOBUS/", StringComparison.Ordinal)),
                "a channel must not become an element of its own: FlexRay channels A and B are physical " +
                "redundancy of ONE bus carrying one schedule, so an element per channel would split one " +
                "network into two that nothing on the bus experiences as separate, and would double every frame");
        }

        #endregion

        #region flow, containment and the two triggering indirections

        [TestMethod]
        public void APortsDirectionDecidesWhichWayTheFlowEdgePoints()
        {
            var network = Read();

            CollectionAssert.AreEqual(new[] { "/Frames/FRM_AlphaMain", "/ISignals/SIG_OdoTotalDist" },
                Targets(network, "/EcuInstances/ALPHA_CTRL", ArxmlRelations.Sends),
                "an OUT port makes the ECU the sender of what the triggering names");

            CollectionAssert.AreEqual(new[] { "/EcuInstances/BETA_CTRL", "/EcuInstances/GAMMA_CTRL" },
                Targets(network, "/Frames/FRM_AlphaMain", ArxmlRelations.DeliversTo),
                "an IN port makes the frame deliver TO the ECU, so a path query from a sender to a " +
                "receiver never has to traverse an edge backwards");

            CollectionAssert.AreEqual(new[] { "/EcuInstances/BETA_CTRL" },
                Targets(network, "/ISignals/SIG_OdoTotalDist", ArxmlRelations.DeliversTo),
                "signal-level triggerings carry the same direction rule as frame-level ones");

            Assert.AreEqual(0, Targets(network, "/EcuInstances/GAMMA_CTRL", ArxmlRelations.Sends).Count,
                "an ECU with only IN ports sends nothing");
        }

        [TestMethod]
        public void AContainerAndASecuredPdu_ResolveThroughTheChannelsTriggerings()
        {
            var network = Read();

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_DistanceReport_Secured" },
                Targets(network, "/Pdus/PDU_AlphaContainer", ArxmlRelations.Carries),
                "a container PDU points at a PDU TRIGGERING, not at a PDU, so the edge only exists if " +
                "the reader resolved that indirection through the channel's triggering table");

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_DistanceReport" },
                Targets(network, "/Pdus/PDU_DistanceReport_Secured", ArxmlRelations.Secures),
                "a secured PDU's PAYLOAD-REF is the same kind of indirection");
        }

        [TestMethod]
        public void ContainmentRunsFrameToPduToSignal()
        {
            var network = Read();

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_AlphaContainer" },
                Targets(network, "/Frames/FRM_AlphaMain", ArxmlRelations.Contains));
            CollectionAssert.AreEqual(new[] { "/ISignals/SIG_OdoTotalDist", "/ISignals/SIG_VehicleSpeed" },
                Targets(network, "/Pdus/PDU_DistanceReport", ArxmlRelations.Contains));
        }

        [TestMethod]
        public void OneSignalMappedTwiceInOnePdu_IsOneEdge_AndNotADiagnostic()
        {
            var network = Read();

            Assert.AreEqual(1, network.Relations.Count(r =>
                    r.FromPath == "/Pdus/PDU_DistanceReport" &&
                    r.Type == ArxmlRelations.Contains &&
                    r.ToPath == "/ISignals/SIG_VehicleSpeed"),
                "the fixture maps the speed signal at two byte positions, which is ordinary in a real " +
                "extract and says nothing new about containment");
            Assert.AreEqual(0, network.Diagnostics.Count,
                "the repeat is dropped SILENTLY: a diagnostic per repeated mapping would bury the ones " +
                "that mean something");
        }

        [TestMethod]
        public void AFramesScheduleLandsOnTheFrame_NotOnItsTriggering()
        {
            var network = Read();
            var frame = Element(network, "/Frames/FRM_AlphaMain");

            Assert.AreEqual("3", frame[ArxmlProperties.SlotId]);
            Assert.AreEqual("0", frame[ArxmlProperties.BaseCycle]);
            Assert.AreEqual("CYCLE-REPETITION-1", frame[ArxmlProperties.CycleRepetition]);
            Assert.AreEqual("32", frame[ArxmlProperties.FrameLengthBytes]);

            var second = Element(network, "/Frames/FRM_BetaStatus");
            Assert.AreEqual("7", second[ArxmlProperties.SlotId], "each frame keeps its own slot");
            Assert.AreEqual("CYCLE-REPETITION-4", second[ArxmlProperties.CycleRepetition]);
        }

        #endregion

        #region the semantic payload

        [TestMethod]
        public void TheUnitIsDenormalisedOntoTheSignal_AsTheDisplayName()
        {
            var network = Read();

            Assert.AreEqual("km", Element(network, "/ISignals/SIG_OdoTotalDist")[ArxmlProperties.Unit],
                "THE semantic-search requirement (spec section 9): an odometer's descriptions never say " +
                "kilometer, so its unit is the only thing that connects it to one. Reaching it means two " +
                "hops the signal itself does not carry: implements then scaledBy");
            Assert.AreEqual("km/h", Element(network, "/ISignals/SIG_VehicleSpeed")[ArxmlProperties.Unit],
                "the near miss carries a different unit, which is what makes the ranking claim a real one");
            Assert.AreEqual("km", Element(network, "/CompuMethods/CM_TotalDistance")[ArxmlProperties.Unit],
                "the compu method keeps it too, since that is where the standard puts it");
        }

        [TestMethod]
        public void ASignalWhoseChainHasNoCompuMethod_HasNoUnitAtAll()
        {
            var network = Read();
            var latch = Element(network, "/ISignals/SIG_DoorLatch");

            Assert.IsNull(latch[ArxmlProperties.Unit],
                "an ABSENT value must stay absent rather than becoming an empty string: an empty property " +
                "exists, and writing one overwrites what another integration knows about the same element");
            Assert.IsFalse(latch.Properties.ContainsKey(ArxmlProperties.Unit),
                "absent means the key is not there at all");
        }

        [TestMethod]
        public void BothDescriptionLanguagesAreRead_AndAnyOtherIsIgnored()
        {
            var network = Read();
            var signal = Element(network, "/ISignals/SIG_OdoTotalDist");

            Assert.AreEqual("Gesamtstrecke seit Auslieferung", signal[ArxmlProperties.DescriptionDe]);
            Assert.AreEqual("Accumulated distance travelled since delivery", signal[ArxmlProperties.DescriptionEn]);
            Assert.IsFalse(signal[ArxmlProperties.DescriptionEn].Contains("kilomet", StringComparison.OrdinalIgnoreCase),
                "the fixture's odometer must NOT say kilometer in its prose, or the unit test above would " +
                "pass for the wrong reason and the semantic claim would be untested");

            var speed = Element(network, "/SystemSignals/SYS_VehicleSpeed");
            Assert.AreEqual("Fahrzeuggeschwindigkeit", speed[ArxmlProperties.DescriptionDe]);
            Assert.AreEqual("Vehicle speed", speed[ArxmlProperties.DescriptionEn]);

            // The fixture also carries a French variant. Asserting "exactly two of the two keys I am
            // willing to look at" would be a tautology, so the whole property set is pinned: a third
            // language landing under any key at all fails here.
            CollectionAssert.AreEquivalent(
                new[] { ArxmlProperties.Name, ArxmlProperties.DescriptionDe, ArxmlProperties.DescriptionEn },
                speed.Properties.Keys.ToList(),
                "a language nothing here reads must not land as a property. The system signal's whole " +
                "property set is pinned rather than filtered, because a filter that only admits the " +
                "expected keys can never see an unexpected one");
        }

        [TestMethod]
        public void APdusFlavourIsAProperty_NotAnEntityKind()
        {
            var network = Read();

            Assert.AreEqual("CONTAINER-I-PDU", Element(network, "/Pdus/PDU_AlphaContainer")[ArxmlProperties.PduKind]);
            Assert.AreEqual("SECURED-I-PDU", Element(network, "/Pdus/PDU_DistanceReport_Secured")[ArxmlProperties.PduKind]);
            Assert.AreEqual("NM-PDU", Element(network, "/Pdus/PDU_NetworkManagement")[ArxmlProperties.PduKind]);

            // The real invariant: the fixture carries three DIFFERENT flavours and they all arrive under
            // the one kind, so no flavour leaked into the label. Asserting that PDUs are PDUs would be a
            // tautology; asserting that three distinct flavours share one kind cannot pass by accident.
            var flavours = network.Elements
                .Where(e => e.Kind == ArxmlKinds.Pdu)
                .Select(e => e[ArxmlProperties.PduKind])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Assert.IsTrue(flavours.Count >= 3,
                "the fixture must exercise several flavours for this to mean anything; it carries " +
                flavours.Count);

            var kinds = network.Elements.Select(e => e.Kind).Distinct(StringComparer.Ordinal).ToList();
            foreach (var flavour in flavours)
            {
                CollectionAssert.DoesNotContain(kinds, flavour,
                    "the PDU flavour '" + flavour + "' became an entity kind. A dozen flavours must not " +
                    "become a dozen labels: a query for what a frame carries would have to enumerate " +
                    "them all, and the next flavour the standard adds would silently fall outside it");
            }
        }

        [TestMethod]
        public void ASignalCarriesWhatAnEngineerAsksFor()
        {
            var network = Read();
            var signal = Element(network, "/ISignals/SIG_OdoTotalDist");

            Assert.AreEqual("SIG_OdoTotalDist", signal[ArxmlProperties.Name]);
            Assert.AreEqual("32", signal[ArxmlProperties.LengthBits]);
            Assert.AreEqual("0", signal[ArxmlProperties.InitValue]);
            Assert.AreEqual("uint32", signal[ArxmlProperties.BaseType],
                "the base type is the last segment of a platform reference, not the whole path");
        }

        #endregion

        #region refusals and diagnostics

        [TestMethod]
        public void ADocumentCarryingADtd_IsRefused()
        {
            // The DOCTYPE carries an entity, and the same document WITHOUT it parses, so what is being
            // pinned is that the DTD itself is the cause of the refusal. Asserting that the message
            // mentions "DTD" would prove nothing: the reader appends that sentence to every XML failure.
            const String withEntity =
                "<?xml version=\"1.0\"?><!DOCTYPE AUTOSAR [<!ENTITY x \"y\">]>" +
                "<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\"><AR-PACKAGES /></AUTOSAR>";
            const String withoutEntity =
                "<?xml version=\"1.0\"?>" +
                "<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\"><AR-PACKAGES /></AUTOSAR>";

            Refused(withEntity);

            var accepted = ArxmlReader.Read(withoutEntity);
            Assert.AreEqual(0, accepted.Elements.Count,
                "the control document is the same file with the DOCTYPE removed: it must be READ (an " +
                "extract with no packages describes nothing, which is a different outcome from a " +
                "refusal), or this test would pass for a reader that rejects everything");
        }

        [TestMethod]
        public void AForeignRootOrNamespace_IsRefused_NamingWhatWasFound()
        {
            var wrongNamespace = Refused(
                "<AUTOSAR xmlns=\"http://example.invalid/other\"><AR-PACKAGES /></AUTOSAR>");
            Assert.IsTrue(wrongNamespace.Message.Contains("http://example.invalid/other", StringComparison.Ordinal),
                "the refusal names what it found, so an operator who exported the wrong thing can see it");

            var wrongRoot = Refused(
                "<SOMETHING-ELSE xmlns=\"http://autosar.org/schema/r4.0\" />");
            Assert.IsTrue(wrongRoot.Message.Contains("SOMETHING-ELSE", StringComparison.Ordinal));
        }

        [TestMethod]
        public void MalformedXml_IsRefusedRatherThanPartiallyRead()
        {
            var thrown = Refused("<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\"><AR-PACKAGES>");

            Assert.IsFalse(String.IsNullOrWhiteSpace(thrown.Message));
        }

        [TestMethod]
        public void AnEmptyDocument_IsRefused_RatherThanDescribingAnEmptyNetwork()
        {
            Refused("   ");
        }

        [TestMethod]
        public void TwoElementsOnOnePath_KeepTheFirstAndReportIt()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>ISignals</SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Twice</SHORT-NAME>
                          <LENGTH>8</LENGTH>
                        </I-SIGNAL>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Twice</SHORT-NAME>
                          <LENGTH>16</LENGTH>
                        </I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            Assert.AreEqual(1, network.Elements.Count, "only the first survives");
            Assert.AreEqual("8", network.Elements[0][ArxmlProperties.LengthBits],
                "the FIRST wins, so which one survives does not depend on the order the file happens " +
                "to be written in");
            Assert.AreEqual(1, network.Diagnostics.Count);
            Assert.AreEqual(ArxmlDiagnosticKind.DuplicatePath, network.Diagnostics[0].Kind);
            Assert.AreEqual("/ISignals/SIG_Twice", network.Diagnostics[0].Subject);
        }

        [TestMethod]
        public void ALanguageNeutralDescription_FillsBothLanguages_RatherThanNeither()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>ISignals</SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Neutral</SHORT-NAME>
                          <DESC>
                            <L-2 L="FOR-ALL">Odometer reading</L-2>
                          </DESC>
                        </I-SIGNAL>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Mixed</SHORT-NAME>
                          <DESC>
                            <L-2 L="FOR-ALL">Neutral text</L-2>
                            <L-2 L="EN">English text</L-2>
                          </DESC>
                        </I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            var neutral = Element(network, "/ISignals/SIG_Neutral");
            Assert.AreEqual("Odometer reading", neutral[ArxmlProperties.DescriptionEn],
                "an element described ONLY in the standard's language-neutral variant must still carry " +
                "prose, or it drops out of every semantic query for reasons no reader of the file could " +
                "guess");
            Assert.AreEqual("Odometer reading", neutral[ArxmlProperties.DescriptionDe]);

            var mixed = Element(network, "/ISignals/SIG_Mixed");
            Assert.AreEqual("English text", mixed[ArxmlProperties.DescriptionEn],
                "a real language must win over the neutral fallback where both exist");
            Assert.AreEqual("Neutral text", mixed[ArxmlProperties.DescriptionDe],
                "and the fallback still fills the language that is genuinely absent");
        }

        [TestMethod]
        public void ABlankShortName_LeavesItsElementUnnamed_RatherThanNamingItNothing()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>   </SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Orphaned</SHORT-NAME>
                        </I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            Assert.AreEqual("/SIG_Orphaned", network.Elements.Single().Path,
                "a blank ancestor short name must contribute NO segment. Naming it the empty string " +
                "composes '//SIG_Orphaned', which no reference in the file can ever match, so the " +
                "element and everything pointing at it would silently lose its identity");
        }

        [TestMethod]
        public void ARefusedDuplicate_TakesItsReferencesWithIt()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>SystemSignals</SHORT-NAME>
                      <ELEMENTS>
                        <SYSTEM-SIGNAL><SHORT-NAME>SYS_First</SHORT-NAME></SYSTEM-SIGNAL>
                        <SYSTEM-SIGNAL><SHORT-NAME>SYS_Second</SHORT-NAME></SYSTEM-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                    <AR-PACKAGE>
                      <SHORT-NAME>ISignals</SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Twice</SHORT-NAME>
                          <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_First</SYSTEM-SIGNAL-REF>
                        </I-SIGNAL>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Twice</SHORT-NAME>
                          <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_Second</SYSTEM-SIGNAL-REF>
                        </I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            var implementsEdges = network.Relations
                .Where(r => r.Type == ArxmlRelations.Implements)
                .Select(r => r.ToPath)
                .ToList();

            CollectionAssert.AreEqual(new[] { "/SystemSignals/SYS_First" }, implementsEdges,
                "the refused twin's reference must be dropped with it. The element table refused the " +
                "second signal, but its SYSTEM-SIGNAL-REF is keyed by the same path, so recording it " +
                "anyway gave the SURVIVING element an edge from a twin that is invisible in the " +
                "element list: the graph would carry topology no described element accounts for");
            Assert.AreEqual(1, network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.DuplicatePath));
        }

        [TestMethod]
        public void APortWhoseDirectionIsNeitherInNorOut_DropsTheEdgeAndSaysSo()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>Frames</SHORT-NAME>
                      <ELEMENTS>
                        <FLEXRAY-FRAME><SHORT-NAME>FRM_X</SHORT-NAME></FLEXRAY-FRAME>
                      </ELEMENTS>
                    </AR-PACKAGE>
                    <AR-PACKAGE>
                      <SHORT-NAME>EcuInstances</SHORT-NAME>
                      <ELEMENTS>
                        <ECU-INSTANCE>
                          <SHORT-NAME>ECU_X</SHORT-NAME>
                          <CONNECTORS>
                            <FLEXRAY-COMMUNICATION-CONNECTOR>
                              <SHORT-NAME>CONN_X</SHORT-NAME>
                              <ECU-COMM-PORT-INSTANCES>
                                <FRAME-PORT>
                                  <SHORT-NAME>FP_Weird</SHORT-NAME>
                                  <COMMUNICATION-DIRECTION>INOUT</COMMUNICATION-DIRECTION>
                                </FRAME-PORT>
                                <FRAME-PORT>
                                  <SHORT-NAME>FP_Silent</SHORT-NAME>
                                </FRAME-PORT>
                              </ECU-COMM-PORT-INSTANCES>
                            </FLEXRAY-COMMUNICATION-CONNECTOR>
                          </CONNECTORS>
                        </ECU-INSTANCE>
                      </ELEMENTS>
                    </AR-PACKAGE>
                    <AR-PACKAGE>
                      <SHORT-NAME>Clusters</SHORT-NAME>
                      <ELEMENTS>
                        <FLEXRAY-CLUSTER>
                          <SHORT-NAME>BUS</SHORT-NAME>
                          <PHYSICAL-CHANNELS>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH</SHORT-NAME>
                              <FRAME-TRIGGERINGS>
                                <FLEXRAY-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_X</SHORT-NAME>
                                  <FRAME-PORT-REFS>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/ECU_X/CONN_X/FP_Weird</FRAME-PORT-REF>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/ECU_X/CONN_X/FP_Silent</FRAME-PORT-REF>
                                  </FRAME-PORT-REFS>
                                  <FRAME-REF DEST="FLEXRAY-FRAME">/Frames/FRM_X</FRAME-REF>
                                </FLEXRAY-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                            </FLEXRAY-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </FLEXRAY-CLUSTER>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            Assert.AreEqual(0, network.Relations.Count(r =>
                    r.Type == ArxmlRelations.Sends || r.Type == ArxmlRelations.DeliversTo),
                "neither port says IN or OUT, so no flow edge may be invented. Defaulting an unknown " +
                "word to IN turns a sender into a receiver, and a wrong edge answers an impact query " +
                "confidently while a missing one shows up as a gap");

            var undecidable = network.Diagnostics
                .Where(d => d.Kind == ArxmlDiagnosticKind.UndecidablePortDirection)
                .Select(d => d.Subject)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            CollectionAssert.AreEqual(
                new[] { "/EcuInstances/ECU_X/CONN_X/FP_Silent", "/EcuInstances/ECU_X/CONN_X/FP_Weird" },
                undecidable,
                "both cases are reported, and each names the PORT: a port that exists with an unusable " +
                "direction is a different problem from a port the file never defined, and reporting the " +
                "second for the first sends an operator looking for a missing package");
        }

        [TestMethod]
        public void AFrameScheduledTwice_TakesItsWholeScheduleFromOneTiming()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>Frames</SHORT-NAME>
                      <ELEMENTS>
                        <FLEXRAY-FRAME><SHORT-NAME>FRM_Twice</SHORT-NAME></FLEXRAY-FRAME>
                      </ELEMENTS>
                    </AR-PACKAGE>
                    <AR-PACKAGE>
                      <SHORT-NAME>Clusters</SHORT-NAME>
                      <ELEMENTS>
                        <FLEXRAY-CLUSTER>
                          <SHORT-NAME>BUS</SHORT-NAME>
                          <PHYSICAL-CHANNELS>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH</SHORT-NAME>
                              <FRAME-TRIGGERINGS>
                                <FLEXRAY-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_Twice</SHORT-NAME>
                                  <FRAME-REF DEST="FLEXRAY-FRAME">/Frames/FRM_Twice</FRAME-REF>
                                  <ABSOLUTELY-SCHEDULED-TIMINGS>
                                    <FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                      <COMMUNICATION-CYCLE>
                                        <CYCLE-REPETITION>
                                          <BASE-CYCLE>0</BASE-CYCLE>
                                          <CYCLE-REPETITION>CYCLE-REPETITION-2</CYCLE-REPETITION>
                                        </CYCLE-REPETITION>
                                      </COMMUNICATION-CYCLE>
                                      <SLOT-ID>11</SLOT-ID>
                                    </FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                    <FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                      <COMMUNICATION-CYCLE>
                                        <CYCLE-REPETITION>
                                          <BASE-CYCLE>1</BASE-CYCLE>
                                          <CYCLE-REPETITION>CYCLE-REPETITION-8</CYCLE-REPETITION>
                                        </CYCLE-REPETITION>
                                      </COMMUNICATION-CYCLE>
                                      <SLOT-ID>22</SLOT-ID>
                                    </FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                  </ABSOLUTELY-SCHEDULED-TIMINGS>
                                </FLEXRAY-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                            </FLEXRAY-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </FLEXRAY-CLUSTER>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            var frame = Element(network, "/Frames/FRM_Twice");

            // The two timings are deliberately disjoint in every field, so a per-field search would
            // report slot 11 with base cycle 0 and repetition 2 only by luck of ordering; any mixing
            // shows up here as a combination that appears in the file nowhere.
            Assert.AreEqual("11", frame[ArxmlProperties.SlotId]);
            Assert.AreEqual("0", frame[ArxmlProperties.BaseCycle]);
            Assert.AreEqual("CYCLE-REPETITION-2", frame[ArxmlProperties.CycleRepetition],
                "the three fields must describe ONE scheduled transmission. Searching the triggering per " +
                "field takes each from wherever it first appears, which for a frame scheduled twice " +
                "invents a schedule the file does not contain");
        }

        [TestMethod]
        public void AReferenceToSomethingTheFileDoesNotDefine_DropsTheEdgeAndReportsIt()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>ISignals</SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Orphan</SHORT-NAME>
                          <LENGTH>8</LENGTH>
                          <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_Missing</SYSTEM-SIGNAL-REF>
                        </I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            Assert.AreEqual(1, network.Elements.Count, "the signal itself is still described");
            Assert.AreEqual(0, network.Relations.Count, "the edge to nowhere is dropped rather than invented");
            Assert.AreEqual(1, network.Diagnostics.Count);
            Assert.AreEqual(ArxmlDiagnosticKind.UnresolvedReference, network.Diagnostics[0].Kind);
            Assert.AreEqual("/SystemSignals/SYS_Missing", network.Diagnostics[0].Subject,
                "the diagnostic names the PATH that could not be found, which is the only thing an " +
                "operator can act on");
        }

        [TestMethod]
        public void AnExtractWithNoFlexRayCluster_IsDescribedWithNoNetwork_SoTheProviderCanRefuseIt()
        {
            var network = ArxmlReader.Read("""
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>ISignals</SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL>
                          <SHORT-NAME>SIG_Lonely</SHORT-NAME>
                          <LENGTH>8</LENGTH>
                        </I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """);

            Assert.IsFalse(network.Elements.Any(e => e.Kind == ArxmlKinds.Network),
                "the READER reports what it saw and refuses nothing here; deciding that a comm matrix " +
                "without a bus is a failed observation rather than an empty one is the provider's call, " +
                "because only the provider knows that an empty complete snapshot withdraws everything");
        }

        #endregion

        #region several extracts, one system

        /// <summary>
        ///   The chassis half of an invented two-extract system, and every cross-file case is in it: a PDU
        ///   that maps a signal only the body extract defines, a container PDU pointing at a PDU TRIGGERING
        ///   only the body extract's channel declares, and the shared package both extracts repeat.
        /// </summary>
        private const String ChassisExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_Shared</SHORT-NAME>
                      <CATEGORY>LINEAR</CATEGORY>
                    </COMPU-METHOD>
                    <SYSTEM-SIGNAL><SHORT-NAME>SYS_Shared</SHORT-NAME></SYSTEM-SIGNAL>
                    <SYSTEM-SIGNAL><SHORT-NAME>SYS_SharedToo</SHORT-NAME></SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Chassis</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_Body</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_Body</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                    <CONTAINER-I-PDU>
                      <SHORT-NAME>PDU_ChassisContainer</SHORT-NAME>
                      <LENGTH>32</LENGTH>
                      <CONTAINED-PDU-TRIGGERING-REFS>
                        <CONTAINED-PDU-TRIGGERING-REF DEST="PDU-TRIGGERING">/Clusters/BODYBUS/BODYBUS_CH_A/PT_Body</CONTAINED-PDU-TRIGGERING-REF>
                      </CONTAINED-PDU-TRIGGERING-REFS>
                    </CONTAINER-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-FRAME>
                      <SHORT-NAME>FRM_Chassis</SHORT-NAME>
                      <FRAME-LENGTH>32</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>FMAP_Chassis</SHORT-NAME>
                          <PDU-REF DEST="CONTAINER-I-PDU">/Pdus/PDU_ChassisContainer</PDU-REF>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </FLEXRAY-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>CHASSISBUS</SHORT-NAME>
                      <PHYSICAL-CHANNELS>
                        <FLEXRAY-PHYSICAL-CHANNEL>
                          <SHORT-NAME>CHASSISBUS_CH_A</SHORT-NAME>
                          <FRAME-TRIGGERINGS>
                            <FLEXRAY-FRAME-TRIGGERING>
                              <SHORT-NAME>FT_Chassis</SHORT-NAME>
                              <FRAME-REF DEST="FLEXRAY-FRAME">/Frames/FRM_Chassis</FRAME-REF>
                              <ABSOLUTELY-SCHEDULED-TIMINGS>
                                <FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                  <SLOT-ID>5</SLOT-ID>
                                </FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                              </ABSOLUTELY-SCHEDULED-TIMINGS>
                            </FLEXRAY-FRAME-TRIGGERING>
                          </FRAME-TRIGGERINGS>
                        </FLEXRAY-PHYSICAL-CHANNEL>
                      </PHYSICAL-CHANNELS>
                    </FLEXRAY-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>
        ///   The body half. It repeats the shared package with DIFFERENT content, which is what lets a test
        ///   say which extract won a re-declared path rather than merely that one of them did.
        /// </summary>
        private const String BodyExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_Shared</SHORT-NAME>
                      <CATEGORY>TEXTTABLE</CATEGORY>
                    </COMPU-METHOD>
                    <SYSTEM-SIGNAL><SHORT-NAME>SYS_Shared</SHORT-NAME></SYSTEM-SIGNAL>
                    <SYSTEM-SIGNAL><SHORT-NAME>SYS_SharedToo</SHORT-NAME></SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_Body</SHORT-NAME>
                      <LENGTH>4</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_Shared</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Body</SHORT-NAME>
                      <LENGTH>4</LENGTH>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>BODYBUS</SHORT-NAME>
                      <PHYSICAL-CHANNELS>
                        <FLEXRAY-PHYSICAL-CHANNEL>
                          <SHORT-NAME>BODYBUS_CH_A</SHORT-NAME>
                          <PDU-TRIGGERINGS>
                            <PDU-TRIGGERING>
                              <SHORT-NAME>PT_Body</SHORT-NAME>
                              <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Body</I-PDU-REF>
                            </PDU-TRIGGERING>
                          </PDU-TRIGGERINGS>
                        </FLEXRAY-PHYSICAL-CHANNEL>
                      </PHYSICAL-CHANNELS>
                    </FLEXRAY-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>A bus and nothing else, for the gate tests that are only about where a cluster is.</summary>
        private const String BusOnlyExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>CHASSISBUS</SHORT-NAME>
                      <PHYSICAL-CHANNELS>
                        <FLEXRAY-PHYSICAL-CHANNEL><SHORT-NAME>CHASSISBUS_CH_A</SHORT-NAME></FLEXRAY-PHYSICAL-CHANNEL>
                      </PHYSICAL-CHANNELS>
                    </FLEXRAY-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>A domain extract with no bus in it at all, which is ordinary rather than broken: not
        /// every extract of a system carries a cluster.</summary>
        private const String NoBusExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>BodyISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL><SHORT-NAME>SIG_BodyOnly</SHORT-NAME><LENGTH>2</LENGTH></I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        [TestMethod]
        public void AReferenceAcrossTwoExtracts_ResolvesExactlyLikeOneInsideAFile()
        {
            var network = ReadSet(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));

            CollectionAssert.AreEqual(new[] { "/ISignals/SIG_Body" },
                Targets(network, "/Pdus/PDU_Chassis", ArxmlRelations.Contains),
                "THE headline of multi-file: the chassis extract maps a signal only the body extract " +
                "defines. The edge exists only if both documents streamed into ONE path table and " +
                "resolution ran over their union - resolving each file on its own drops it, and the graph " +
                "silently loses the topology that made the second extract worth importing");

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Body" },
                Targets(network, "/Pdus/PDU_ChassisContainer", ArxmlRelations.Carries),
                "and the harder half: the container PDU points at a PDU TRIGGERING, and the channel that " +
                "declares that triggering is in the OTHER file, so the indirection resolves through a " +
                "table the second document filled");

            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "nothing here is a partial export: every path either extract names is defined by one of " +
                "them, so an unresolved reference would mean the union was never formed: " +
                String.Join("; ", network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        [TestMethod]
        public void APathASecondFileRedeclares_StaysTheFirstFiles_AndIsReportedOncePerFile()
        {
            var network = ReadSet(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));

            Assert.AreEqual("LINEAR", Element(network, "/Shared/CM_Shared")[ArxmlProperties.Category],
                "the EARLIER file owns a path both declare. The two shared compu methods differ in their " +
                "category on purpose, so this says which one survived rather than merely that one did");
            Assert.AreEqual(1, network.Elements.Count(e => e.Path == "/Shared/CM_Shared"),
                "and the twin is gone rather than sitting beside it: two elements on one path would give " +
                "one AUTOSAR path two identities, which is exactly what the claim type forbids");

            var redeclared = network.Diagnostics
                .Where(d => d.Kind == ArxmlDiagnosticKind.RedeclaredPaths)
                .ToList();

            Assert.AreEqual(1, redeclared.Count,
                "ONE aggregate for the one re-declaring file. The body extract re-declares THREE paths, so " +
                "a per-path diagnostic would show three here; a real four-extract job repeats the whole " +
                "platform package and would drown the report in hundreds of them: " +
                String.Join("; ", network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
            Assert.AreEqual("body.arxml", redeclared[0].Subject,
                "the subject is the re-declaring FILE, because that is the only thing a reader can act on");
            StringAssert.Contains(redeclared[0].Message, "3",
                "and it says HOW MANY, or an operator cannot tell a repeated shared package from a whole " +
                "extract imported twice: " + redeclared[0].Message);

            Assert.AreEqual(0, network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.DuplicatePath),
                "a path in two files is not a file contradicting itself, and reporting it as one would " +
                "send an operator hunting for a fault in an extract that is exactly as the standard says");
        }

        [TestMethod]
        public void ADuplicateInsideOneFileOfASet_KeepsItsOwnPerPathDiagnostic()
        {
            // The regression this pairs with the test above: the two duplicate cases share one code path in
            // the reader, so an aggregate that swallowed the within-file case would hide a file that
            // genuinely contradicts itself - and there would be no diagnostic naming the path at all.
            var network = ReadSet(
                ("chassis.arxml", BusOnlyExtract),
                ("body.arxml", """
                    <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                      <AR-PACKAGES>
                        <AR-PACKAGE>
                          <SHORT-NAME>ISignals</SHORT-NAME>
                          <ELEMENTS>
                            <I-SIGNAL><SHORT-NAME>SIG_Twice</SHORT-NAME><LENGTH>8</LENGTH></I-SIGNAL>
                            <I-SIGNAL><SHORT-NAME>SIG_Twice</SHORT-NAME><LENGTH>16</LENGTH></I-SIGNAL>
                          </ELEMENTS>
                        </AR-PACKAGE>
                      </AR-PACKAGES>
                    </AUTOSAR>
                    """));

            Assert.AreEqual(1, network.Diagnostics.Count,
                "one file declaring one path twice is a fault in that file and nothing else happened here: " +
                String.Join("; ", network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
            Assert.AreEqual(ArxmlDiagnosticKind.DuplicatePath, network.Diagnostics[0].Kind);
            Assert.AreEqual("/ISignals/SIG_Twice", network.Diagnostics[0].Subject,
                "and it still names the PATH rather than the file, because the path is what the author has " +
                "to go and look at");
            Assert.AreEqual("8", Element(network, "/ISignals/SIG_Twice")[ArxmlProperties.LengthBits],
                "with the first of the twins surviving, exactly as in a single-document read");
        }

        [TestMethod]
        public void TheBusGateJudgesTheUnion_NotEachFile()
        {
            var withOne = ReadSet(("chassis.arxml", BusOnlyExtract), ("body.arxml", NoBusExtract));

            Assert.AreEqual(1, withOne.Elements.Count(e => e.Kind == ArxmlKinds.Network),
                "a body-domain extract with no bus in it is ordinary beside a chassis extract that has " +
                "one, so the SET describes a network and the provider's gate has something to pass");
            Element(withOne, "/BodyISignals/SIG_BodyOnly");
            Assert.AreEqual(0, withOne.Diagnostics.Count,
                "and the bus-less file costs nothing: " +
                String.Join("; ", withOne.Diagnostics.Select(d => d.Kind + " " + d.Subject)));

            var withNone = ReadSet(("body.arxml", NoBusExtract), ("doors.arxml", """
                <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                  <AR-PACKAGES>
                    <AR-PACKAGE>
                      <SHORT-NAME>DoorISignals</SHORT-NAME>
                      <ELEMENTS>
                        <I-SIGNAL><SHORT-NAME>SIG_DoorOnly</SHORT-NAME><LENGTH>1</LENGTH></I-SIGNAL>
                      </ELEMENTS>
                    </AR-PACKAGE>
                  </AR-PACKAGES>
                </AUTOSAR>
                """));

            Assert.AreEqual(0, withNone.Elements.Count(e => e.Kind == ArxmlKinds.Network),
                "and a set where NO file carries a cluster describes no network at all, which is what the " +
                "provider turns into a failed run rather than an empty complete snapshot");
        }

        [TestMethod]
        public void TheOrderOfTheFilesDecidesWhoOwnsARedeclaredPath_AndEachOrderIsWholeInItself()
        {
            var chassisFirst = ReadSet(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));
            var bodyFirst = ReadSet(("body.arxml", BodyExtract), ("chassis.arxml", ChassisExtract));

            Assert.AreEqual("LINEAR", Element(chassisFirst, "/Shared/CM_Shared")[ArxmlProperties.Category]);
            Assert.AreEqual("TEXTTABLE", Element(bodyFirst, "/Shared/CM_Shared")[ArxmlProperties.Category],
                "the same two files in the other order give the re-declared path to the other file. Order " +
                "is part of the meaning, which is why the job's order is kept rather than sorted");

            Assert.AreEqual("body.arxml",
                chassisFirst.Diagnostics.Single(d => d.Kind == ArxmlDiagnosticKind.RedeclaredPaths).Subject);
            Assert.AreEqual("chassis.arxml",
                bodyFirst.Diagnostics.Single(d => d.Kind == ArxmlDiagnosticKind.RedeclaredPaths).Subject,
                "and the aggregate follows, naming whichever file arrived second");

            // Neither order may be a half-formed graph. First-wins decides WHICH twin survives; it must
            // never decide whether an edge exists, because that would make importing the same system in
            // another order a different network.
            foreach (var network in new[] { chassisFirst, bodyFirst })
            {
                Assert.AreEqual(10, network.Elements.Count);
                CollectionAssert.AreEqual(new[] { "/ISignals/SIG_Body" },
                    Targets(network, "/Pdus/PDU_Chassis", ArxmlRelations.Contains));
                CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Body" },
                    Targets(network, "/Pdus/PDU_ChassisContainer", ArxmlRelations.Carries));
                CollectionAssert.AreEqual(new[] { "/Shared/SYS_Shared" },
                    Targets(network, "/ISignals/SIG_Body", ArxmlRelations.Implements));
                Assert.AreEqual(0,
                    network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference));
            }
        }

        [TestMethod]
        public void TheSameFilesInTheSameOrder_DescribeTheSystemIdentically()
        {
            var first = ReadSet(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));
            var second = ReadSet(("chassis.arxml", ChassisExtract), ("body.arxml", BodyExtract));

            Assert.AreEqual(Describe(first), Describe(second),
                "the conformance suite compares two runs over one fixture on the SERIALISED snapshot, so a " +
                "merge that leaked dictionary iteration order into element, relation or diagnostic order " +
                "would make every run a write: the change feed would churn and the write-ahead log grow " +
                "with nothing to show for it");
        }

        [TestMethod]
        public void ASecondDocumentThatIsNotAutosar_FailsTheReadNamingThatFile()
        {
            var refused = RefusedSet(
                ("chassis.arxml", BusOnlyExtract),
                ("body.arxml", "<SOMETHING-ELSE xmlns=\"http://autosar.org/schema/r4.0\" />"));

            StringAssert.Contains(refused.Message, "body.arxml",
                "the root gate is per DOCUMENT, and the refusal has to say which of the extracts that " +
                "arrived together is the unreadable one: " + refused.Message);
            Assert.IsFalse(refused.Message.Contains("chassis.arxml", StringComparison.Ordinal),
                "and only that one, or naming every file in the job leaves the operator opening four of " +
                "them: " + refused.Message);
            StringAssert.Contains(refused.Message, "SOMETHING-ELSE",
                "while still saying what was found there: " + refused.Message);
        }

        [TestMethod]
        public void AReadWithNoDocumentAtAll_IsRefusedRatherThanDescribingAnEmptySystem()
        {
            // Unreachable through the provider, which only loops over files the job carried - and that is
            // the point: an empty set becoming an empty COMPLETE snapshot would withdraw and then delete
            // everything the identity ever claimed, so it refuses instead of describing nothing.
            Assert.ThrowsException<ArxmlFormatException>(() => new ArxmlReader().Complete());
        }

        [TestMethod]
        public void ADocumentAddedAfterTheReadIsFinished_IsRefused()
        {
            var reader = new ArxmlReader();
            reader.Add("chassis.arxml", BusOnlyExtract);
            reader.Complete();

            Assert.ThrowsException<InvalidOperationException>(
                () => reader.Add("body.arxml", NoBusExtract),
                "resolution has already run, so this document would be in the path table and in no " +
                "description: silently collecting it is how a reused reader reports a network that " +
                "matches neither read");
        }

        #endregion
    }
}
