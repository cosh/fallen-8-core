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
using System.Linq;
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
    }
}
