// MIT License
//
// IntegrationsArxmlEthernetTest.cs
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
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Providers.AutosarArxml;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The AUTOSAR reader on ETHERNET (feature arxml-vehicle-model, step 2), which is not "one more
    ///   entry in the protocol table" - the structure below the cluster is different in kind.
    ///
    ///   <para>What these tests are really about: an Ethernet cluster has NO FRAME LAYER. Its channel
    ///   carries PDU triggerings directly, so a traversal that reaches signals through frames finds
    ///   nothing, and a channel is a VLAN rather than redundancy, so several channels under one cluster
    ///   are several broadcast domains an ECU can be on one of.</para>
    ///
    ///   <para>Every fixture here is HAND-AUTHORED and describes an invented network. No content derived
    ///   from a real manufacturer's export appears in this repository in any form.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsArxmlEthernetTest
    {
        #region the cluster, its VLANs, and the missing frame layer

        [TestMethod]
        public void AnEthernetClusterIsANetwork_WithItsVlansAsChannels()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            var bus = Element(network, "/Clusters/BACKBONE");
            Assert.AreEqual(ArxmlKinds.Network, bus.Kind);
            Assert.AreEqual(ArxmlProperties.EthernetProtocol, bus[ArxmlProperties.Protocol],
                "the protocol is what tells a traversal which shape it is standing in: on this one there " +
                "are no frames to reach a signal through");
            Assert.AreEqual("1000", bus[ArxmlProperties.Baudrate],
                "the bit rate is protocol-neutral in the standard and is read once for every bus");

            var channels = network.Elements
                .Where(e => e.Kind == ArxmlKinds.Channel)
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .ToList();
            CollectionAssert.AreEqual(
                new[] { "/Clusters/BACKBONE/CH_DIAG", "/Clusters/BACKBONE/CH_SENSORS" },
                channels.Select(c => c.Path).ToArray(),
                "each VLAN is a channel of its own. On Ethernet this is the whole reason the channel had " +
                "to become an element: these are distinct broadcast domains, not redundancy");

            var sensors = Element(network, "/Clusters/BACKBONE/CH_SENSORS");
            Assert.AreEqual("12", sensors[ArxmlProperties.VlanId]);
            Assert.AreEqual("VLAN_SENSORS", sensors[ArxmlProperties.VlanName],
                "the VLAN's name comes from the VLAN element inside the channel rather than from a field " +
                "on it, which is where the standard puts it");
            Assert.AreEqual(ArxmlProperties.EthernetProtocol, sensors[ArxmlProperties.Protocol]);
        }

        /// <summary>
        ///   NO FRAMES, and the signals still reachable. A reader that treated the missing frame layer as
        ///   an error would refuse a legal extract; one that looked for a frame element called "" would
        ///   quietly find nothing on every bus.
        /// </summary>
        [TestMethod]
        public void AnEthernetBusHasNoFrames_AndItsSignalsAreStillReachable()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            Assert.AreEqual(0, network.Elements.Count(e => e.Kind == ArxmlKinds.Frame),
                "an Ethernet channel carries PDU triggerings directly: the socket layer does what a frame " +
                "does elsewhere, so a frame here would be an element the standard does not describe");

            // PDU to signal to system signal, with nothing between the channel and the PDU.
            CollectionAssert.AreEqual(new[] { "/ISignals/SIG_WheelSpeed" },
                Targets(network, "/Pdus/PDU_Sensors", ArxmlRelations.Contains).ToArray());
            CollectionAssert.AreEqual(new[] { "/Shared/SYS_WheelSpeed" },
                Targets(network, "/ISignals/SIG_WheelSpeed", ArxmlRelations.Implements).ToArray(),
                "and the system signal is reached, which is what makes this bus joinable to another one");
        }

        /// <summary>
        ///   The flow comes from the PDU triggering, which is the protocol-neutral object: a PDU triggering
        ///   names the ports it crosses exactly as a frame triggering does, and on Ethernet it is the only
        ///   flow there is.
        /// </summary>
        [TestMethod]
        public void TheFlowOnAnEthernetBusComesFromThePduTriggering()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Sensors" },
                Targets(network, "/Ecus/SENSOR_HUB", ArxmlRelations.Sends).ToArray(),
                "the sending ECU is the one whose I-PDU port declares OUT");
            CollectionAssert.AreEqual(new[] { "/Ecus/DRIVE_ECU" },
                Targets(network, "/Pdus/PDU_Sensors", ArxmlRelations.DeliversTo).ToArray(),
                "and the receiving one declares IN, so a path query never traverses an edge backwards");
        }

        /// <summary>
        ///   An ECU attaches to the network and to the CHANNELS IT IS ACTUALLY ON. On Ethernet that is the
        ///   fact the channel edge exists for: a unit on the sensor VLAN must not appear on the diagnostic
        ///   one, or every VLAN question in the graph answers "all of them".
        /// </summary>
        [TestMethod]
        public void AnEcuAttachesToTheVlansItIsOn_AndNotToTheOthers()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            CollectionAssert.AreEqual(
                new[] { "/Clusters/BACKBONE", "/Clusters/BACKBONE/CH_SENSORS" },
                Targets(network, "/Ecus/SENSOR_HUB", ArxmlRelations.AttachedTo).ToArray(),
                "the sensor hub is on the sensor VLAN only");
            CollectionAssert.AreEqual(
                new[]
                {
                    "/Clusters/BACKBONE", "/Clusters/BACKBONE/CH_DIAG", "/Clusters/BACKBONE/CH_SENSORS",
                },
                Targets(network, "/Ecus/DRIVE_ECU", ArxmlRelations.AttachedTo).ToArray(),
                "and the drive ECU is on both, which is what a multi-VLAN unit looks like");
        }

        /// <summary>
        ///   Ethernet is no longer reported as a bus this reader skips. The diagnostic existed so an
        ///   operator was not left inferring a skipped bus from a network that never appeared; leaving it in
        ///   place while reading the bus would be worse than either.
        /// </summary>
        [TestMethod]
        public void AnEthernetClusterIsNoLongerReportedAsUnread()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            Assert.AreEqual(0, network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnreadCluster),
                "Ethernet is read now: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
            CollectionAssert.DoesNotContain(network.UnreadClusters.Select(u => u.Element).ToArray(),
                "ETHERNET-CLUSTER",
                "and it is off the unread list, which is what the report is built from");
        }

        [TestMethod]
        public void AnEthernetExtractResolvesEveryReferenceItWrites()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "the fixture defines every path it references, so a diagnostic here means the reader " +
                "cannot resolve a reference the standard writes plainly: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        #endregion

        #region the socket layer, normalised across revisions (step 3)

        /// <summary>
        ///   THE OLDER SPELLING: a connection BUNDLE under the channel, naming a server port and bundling
        ///   client connections under it.
        /// </summary>
        [TestMethod]
        public void TheOlderSocketLayerSpellingReadsOntoTheThreeKinds()
        {
            var network = ArxmlReader.Read(BundleExtract);

            var endpoint = Element(network, "/Clusters/BACKBONE/CH_SOCKETS/EP_Hub");
            Assert.AreEqual(ArxmlKinds.Endpoint, endpoint.Kind);
            Assert.AreEqual("10.0.7.1", endpoint[ArxmlProperties.Address]);
            Assert.AreEqual("ipv4", endpoint[ArxmlProperties.IpVersion]);
            Assert.AreEqual("FIXED", endpoint[ArxmlProperties.AddressSource],
                "the standard's own word, not a boolean: a reader that decided what FIXED means would be " +
                "deciding for every future value too");
            Assert.AreEqual("255.255.255.0", endpoint[ArxmlProperties.NetworkMask]);

            var server = Element(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer");
            Assert.AreEqual(ArxmlKinds.Socket, server.Kind);
            Assert.AreEqual("30490", server[ArxmlProperties.Port]);
            Assert.AreEqual("udp", server[ArxmlProperties.Transport],
                "UDP and TCP are different ELEMENTS in the standard rather than a value, so which one is " +
                "present is the answer");
            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/EP_Hub" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer", ArxmlRelations.BoundTo)
                    .ToArray(),
                "and it says which address it is on");

            var connection = Element(network, "/Clusters/BACKBONE/CH_SOCKETS/SCB_Sensors");
            Assert.AreEqual(ArxmlKinds.Connection, connection.Kind);
            Assert.AreEqual("SOCKET-CONNECTION-BUNDLE", connection[ArxmlProperties.SourceSpelling],
                "the source's own element name is KEPT, which is what makes the normalisation honest rather " +
                "than lossy: a query is written once, and an operator can still see which revision they have");
            Assert.AreEqual("1", connection[ArxmlProperties.HeaderIdCount]);

            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SCB_Sensors", ArxmlRelations.ServerPort)
                    .ToArray());
            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SCB_Sensors", ArxmlRelations.ClientPort)
                    .ToArray(),
                "the two ends are told apart by the REFERENCE's own name, which is what the two revisions " +
                "agree on");

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Sensors" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SCB_Sensors", ArxmlRelations.Carries)
                    .ToArray(),
                "and the PDU it carries is reached THROUGH the triggering, because that is what a PDU " +
                "identifier points at - the same indirection a container PDU uses");
        }

        /// <summary>
        ///   THE NEWER SPELLING: a STATIC socket connection hanging off the serving socket, with no server
        ///   port reference at all because the nesting says it.
        ///
        ///   <para>The two together are the whole point of N5. Their element names do not overlap, so both
        ///   are read unconditionally and no revision detection has to be right for either to work.</para>
        /// </summary>
        [TestMethod]
        public void TheNewerSocketLayerSpellingReadsOntoTheSameThreeKinds()
        {
            var network = ArxmlReader.Read(StaticExtract);

            var connection = Element(network,
                "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer/SSC_Sensors");
            Assert.AreEqual(ArxmlKinds.Connection, connection.Kind,
                "the newer spelling is the same KIND, which is the entire deliverable of the normalisation: " +
                "a query written against one revision's export works on the other's");
            Assert.AreEqual("STATIC-SOCKET-CONNECTION", connection[ArxmlProperties.SourceSpelling]);

            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer/SSC_Sensors",
                    ArxmlRelations.ServerPort).ToArray(),
                "this spelling names NO server port: the connection hangs off the serving socket, so the " +
                "parent IS the server. Read from the nesting rather than guessed, and only when the file " +
                "named none");
            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer/SSC_Sensors",
                    ArxmlRelations.ClientPort).ToArray(),
                "and the remote address is the client end");
            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Sensors" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer/SSC_Sensors",
                    ArxmlRelations.Carries).ToArray());
        }

        /// <summary>
        ///   The two spellings produce the SAME SHAPE, which is asserted as a shape rather than element by
        ///   element: what N5 exists to prevent is the graph's structure being a function of which revision
        ///   an extract was written against.
        /// </summary>
        [TestMethod]
        public void BothSpellingsProduceTheSameShape()
        {
            var older = Shape(ArxmlReader.Read(BundleExtract));
            var newer = Shape(ArxmlReader.Read(StaticExtract));

            // Non-vacuity first: two EMPTY shapes are equal, and that is exactly what a reader that
            // recognised neither spelling would produce.
            CollectionAssert.Contains(older, "connection -carries-> pdu");
            CollectionAssert.Contains(older, "socket -boundTo-> endpoint");

            CollectionAssert.AreEqual(older, newer,
                "the two revisions' socket layers must import as the same kinds joined by the same " +
                "relations. Older: [" + String.Join(", ", older) + "] newer: [" +
                String.Join(", ", newer) + "]");
        }

        /// <summary>
        ///   A socket's port is its APPLICATION ENDPOINT's, even when a transport configuration somewhere
        ///   else in the socket's subtree comes FIRST in document order. In the newer revision a socket
        ///   address also CONTAINS its static connections, and the element order inside a class is the
        ///   schema's rather than this reader's, so first-in-subtree would be a coin toss: the socket could
        ///   be given a port it does not listen on, which is worse than none because nothing downstream can
        ///   tell it is wrong.
        ///
        ///   <para>The interloper is injected BEFORE the application endpoint on purpose. Injected after it,
        ///   this test passes whether or not the precedence exists - which is how it was written the first
        ///   time, and the mutation check is what caught it.</para>
        /// </summary>
        [TestMethod]
        public void ASocketsPortIsItsApplicationEndpointsAndNotOneThatPrecedesIt()
        {
            var network = ArxmlReader.Read(StaticExtract.Replace(
                "<SHORT-NAME>SA_HubServer</SHORT-NAME>",
                "<SHORT-NAME>SA_HubServer</SHORT-NAME>" +
                "<TP-CONFIGURATION><TCP-TP><PORT-NUMBER>1</PORT-NUMBER></TCP-TP></TP-CONFIGURATION>",
                StringComparison.Ordinal));

            var socket = Element(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer");
            Assert.AreEqual("30490", socket[ArxmlProperties.Port],
                "the port is the one the application endpoint declares");
            Assert.AreEqual("udp", socket[ArxmlProperties.Transport],
                "and so is the transport, rather than the TCP the interloper declares");
        }

        /// <summary>
        ///   And the fallback still works: a socket that carries its transport configuration DIRECTLY, with
        ///   no application endpoint, is read rather than left portless. Preferring the endpoint must not
        ///   become requiring one.
        /// </summary>
        [TestMethod]
        public void ASocketCarryingItsTransportDirectlyIsStillRead()
        {
            var network = ArxmlReader.Read(BundleExtract
                .Replace("<APPLICATION-ENDPOINT>", "<NOT-AN-ENDPOINT>", StringComparison.Ordinal)
                .Replace("</APPLICATION-ENDPOINT>", "</NOT-AN-ENDPOINT>", StringComparison.Ordinal));

            var socket = Element(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer");
            Assert.AreEqual("30490", socket[ArxmlProperties.Port],
                "with no application endpoint to prefer, the socket's own subtree is what is read");
        }

        /// <summary>
        ///   A port reference naming the APPLICATION ENDPOINT rather than the socket address still lands on
        ///   the socket. Both spellings exist in the wild, and the alternative to accepting them is an
        ///   unresolved-reference diagnostic on a file that is perfectly clear about what it means.
        /// </summary>
        [TestMethod]
        public void APortReferenceNamingTheApplicationEndpointResolvesToItsSocket()
        {
            var network = ArxmlReader.Read(BundleExtract.Replace(
                "<CLIENT-PORT-REF DEST=\"SOCKET-ADDRESS\">/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient</CLIENT-PORT-REF>",
                "<CLIENT-PORT-REF DEST=\"APPLICATION-ENDPOINT\">/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient/AE_Drive</CLIENT-PORT-REF>",
                StringComparison.Ordinal));

            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SCB_Sensors", ArxmlRelations.ClientPort)
                    .ToArray(),
                "a reference to the application endpoint means its socket, and resolving it there is the " +
                "difference between reading the file and reporting it as broken");
            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "and nothing is reported as unresolved: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        /// <summary>
        ///   A port reference that resolves to neither a socket nor a socket's parent is REPORTED. The
        ///   parent fallback must not become a way for any dangling reference to land on something: it is
        ///   checked against the SOCKETS, not against every element.
        /// </summary>
        [TestMethod]
        public void APortReferenceThatIsNoSocketIsReported()
        {
            var network = ArxmlReader.Read(BundleExtract.Replace(
                "/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient</CLIENT-PORT-REF>",
                "/Clusters/BACKBONE/CH_SOCKETS/SA_NoSuchSocket</CLIENT-PORT-REF>",
                StringComparison.Ordinal));

            var reported = network.Diagnostics
                .Where(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference)
                .ToList();
            Assert.AreEqual(1, reported.Count,
                "one named diagnostic, not a wrong edge: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
            StringAssert.Contains(reported[0].Message, "socket connection's port", reported[0].Message);
            Assert.AreEqual(0,
                network.Relations.Count(r => r.Type == ArxmlRelations.ClientPort),
                "and no client edge was invented from the reference's parent, which happens to be a real " +
                "channel: the fallback resolves against the sockets and nothing else");
        }

        #endregion

        #region the detail layer: services and switch ports (step 5)

        /// <summary>
        ///   SOME/IP service instances, which is the layer a modern vehicle's Ethernet traffic is actually
        ///   organised at: signals still exist below it, but what an application asks for is a service.
        /// </summary>
        [TestMethod]
        public void ServiceInstancesOnASocketAreElements_WithTheirRoleAsAProperty()
        {
            var network = ArxmlReader.Read(ServiceExtract);

            var provided = Element(network,
                "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer/AE_Hub/PSI_WheelSpeed");
            Assert.AreEqual(ArxmlKinds.Service, provided.Kind);
            Assert.AreEqual(ArxmlProperties.ProvidedRole, provided[ArxmlProperties.Role],
                "the role is a PROPERTY and not a kind, so 'what services does this unit take part in' is " +
                "one query rather than two");
            Assert.AreEqual("PROVIDED-SERVICE-INSTANCE", provided[ArxmlProperties.SourceSpelling]);
            Assert.AreEqual("4660", provided[ArxmlProperties.ServiceId]);
            Assert.AreEqual("1", provided[ArxmlProperties.InstanceId]);

            var consumed = Element(network,
                "/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient/AE_Drive/CSI_WheelSpeed");
            Assert.AreEqual(ArxmlProperties.ConsumedRole, consumed[ArxmlProperties.Role]);
            Assert.AreEqual("4660", consumed[ArxmlProperties.ServiceId],
                "the consumer names the same SERVICE, which is what makes the identifier worth keeping as " +
                "a property: the file joins the two by nothing, so a query does it explicitly");
        }

        /// <summary>
        ///   A service instance is <c>partOf</c> its SOCKET, not its application endpoint. The endpoint is
        ///   not an element - it is folded into the socket - so an edge to it would point at nothing.
        /// </summary>
        [TestMethod]
        public void AServiceInstanceBelongsToItsSocket()
        {
            var network = ArxmlReader.Read(ServiceExtract);

            CollectionAssert.AreEqual(new[] { "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer" },
                Targets(network, "/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer/AE_Hub/PSI_WheelSpeed",
                    ArxmlRelations.PartOf).ToArray(),
                "so a walk from a service reaches the port it listens on, and from there the address and " +
                "the channel");
            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "and nothing dangles: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        /// <summary>
        ///   Two instances of ONE service are two elements. Deliberately not merged: an instance is per
        ///   socket, and matching them on the identifier would be inference rather than reading.
        /// </summary>
        [TestMethod]
        public void TwoInstancesOfOneServiceAreTwoElements()
        {
            var network = ArxmlReader.Read(ServiceExtract);

            var services = network.Elements.Where(e => e.Kind == ArxmlKinds.Service).ToList();
            Assert.AreEqual(2, services.Count);
            Assert.AreEqual(2, services.Count(s => s[ArxmlProperties.ServiceId] == "4660"),
                "both carry the same service identifier and stay separate elements: the provided instance " +
                "and the consumed one are different things in the file, and a reader that merged them " +
                "would be asserting a relationship the file does not state");
        }

        /// <summary>
        ///   COUPLING PORTS: the physical ports of the switch fabric, and the links between them. A
        ///   different question from a socket's port number, which is why it is a different kind.
        /// </summary>
        [TestMethod]
        public void CouplingPortsAreTheSwitchTopology()
        {
            var network = ArxmlReader.Read(ServiceExtract);

            var port = Element(network, "/Ecus/SENSOR_HUB/HUB_CTRL/CP_Hub_1");
            Assert.AreEqual(ArxmlKinds.Coupling, port.Kind);
            CollectionAssert.AreEqual(new[] { "/Ecus/SENSOR_HUB" },
                Targets(network, "/Ecus/SENSOR_HUB/HUB_CTRL/CP_Hub_1", ArxmlRelations.PartOf).ToArray(),
                "a coupling port belongs to the ECU whose controller declares it");

            CollectionAssert.AreEqual(new[] { "/Ecus/SWITCH_ECU/SW_CTRL/CP_Switch_1" },
                Targets(network, "/Ecus/SENSOR_HUB/HUB_CTRL/CP_Hub_1", ArxmlRelations.ConnectedTo).ToArray(),
                "and the link is ONE edge in the direction the file states it: a link is one fact, and two " +
                "edges for it would make every count over the topology wrong");
            Assert.AreEqual(1, network.Relations.Count(r => r.Type == ArxmlRelations.ConnectedTo),
                "one connection, one edge");
        }

        /// <summary>
        ///   A coupling-port connection naming only one end produces NO edge and no diagnostic. Emitting one
        ///   would need a target invented; reporting it would be a diagnostic about a reference the file
        ///   never wrote.
        /// </summary>
        [TestMethod]
        public void AHalfDeclaredCouplingConnectionIsNeitherAnEdgeNorADiagnostic()
        {
            var network = ArxmlReader.Read(ServiceExtract.Replace(
                "<SECOND-PORT-REF DEST=\"COUPLING-PORT\">/Ecus/SWITCH_ECU/SW_CTRL/CP_Switch_1</SECOND-PORT-REF>",
                String.Empty, StringComparison.Ordinal));

            Assert.AreEqual(0, network.Relations.Count(r => r.Type == ArxmlRelations.ConnectedTo));
            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "a link the file only half stated is not an unresolved reference: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
            Assert.AreEqual(2, network.Elements.Count(e => e.Kind == ArxmlKinds.Coupling),
                "and both ports are still elements, because they were declared");
        }

        /// <summary>
        ///   A coupling-port connection naming a port nothing declares IS reported: that is a reference the
        ///   file wrote and this reader could not follow, which is a different fact from a link left half
        ///   stated.
        /// </summary>
        [TestMethod]
        public void ACouplingConnectionToAPortNothingDeclaresIsReported()
        {
            var network = ArxmlReader.Read(ServiceExtract.Replace(
                "/Ecus/SWITCH_ECU/SW_CTRL/CP_Switch_1</SECOND-PORT-REF>",
                "/Ecus/SWITCH_ECU/SW_CTRL/CP_NoSuchPort</SECOND-PORT-REF>",
                StringComparison.Ordinal));

            Assert.AreEqual(0, network.Relations.Count(r => r.Type == ArxmlRelations.ConnectedTo),
                "no edge to something that is not there");
            Assert.AreEqual(1,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "and it is named: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        /// <summary>
        ///   A CAN ECU is never asked about coupling ports either. The ECU collector runs on every protocol,
        ///   so this says the search costs nothing where there is nothing to find rather than that it is
        ///   gated - and that a CAN extract cannot grow spurious switch topology.
        /// </summary>
        [TestMethod]
        public void ACanEcuContributesNoCouplingPorts()
        {
            var network = ArxmlReader.Read(EthernetExtract.Replace("ETHERNET-", "CAN-",
                StringComparison.Ordinal));

            Assert.AreEqual(0, network.Elements.Count(e => e.Kind == ArxmlKinds.Coupling));
            Assert.AreEqual(0, network.Elements.Count(e => e.Kind == ArxmlKinds.Service));
        }

        #endregion

        #region a socket layer this reader does not recognise

        /// <summary>
        ///   THE WRONG-GUESS REPORT, which is the part of this feature most likely to earn its keep. The
        ///   socket layer's names differ by revision with no overlap, so a spelling this reader has not met
        ///   would otherwise be silent data loss on a bus that imported and looked complete.
        /// </summary>
        [TestMethod]
        public void AChannelWhoseSocketLayerYieldsNothingIsReported_WithWhatItSaw()
        {
            var network = ArxmlReader.Read(StrangeSocketExtract);

            var said = network.Diagnostics.Single(d =>
                d.Kind == ArxmlDiagnosticKind.SocketLayerUnrecognised);

            Assert.AreEqual("/Clusters/BACKBONE/CH_STRANGE", said.Subject,
                "the subject is the channel, which is what an operator can go and look at");
            StringAssert.Contains(said.Message, "SOME-FUTURE-CONNECTION", said.Message);
            StringAssert.Contains(said.Message, "AUTOSAR_00099.xsd", said.Message);
            StringAssert.Contains(said.Message, "table entry", said.Message);
        }

        /// <summary>
        ///   Reported ONCE for a run however many channels it happens to. A backbone has a couple of dozen
        ///   VLANs, and one line each would bury every diagnostic that means something.
        /// </summary>
        /// <summary>
        ///   STRUCTURAL WRAPPERS DO NOT CROWD THE BOUND. The report is capped at 12 distinct names, and a
        ///   real Ethernet channel wraps every reference in a CONDITIONAL variant element (the standard's
        ///   own pattern - see <c>CollectCluster</c>'s connector-ref handling): a filter that excluded only
        ///   bare <c>-REF</c> elements would let those wrappers spend the budget's slots on structure that
        ///   identifies no vocabulary at all, potentially crowding out the one name a maintainer actually
        ///   needs to see.
        ///
        ///   <para>Twelve distinct filler wrappers are ahead of the genuinely novel element in document
        ///   order, which is exactly the bound: if the wrappers were not filtered, the novel name would
        ///   never appear in the report.</para>
        /// </summary>
        [TestMethod]
        public void TheVocabularyReportIsNotCrowdedByStructuralConditionalWrappers()
        {
            var fillers = new StringBuilder();
            for (var i = 0; i < 12; i++)
            {
                fillers.Append("<FILLER-").Append(i).Append("-CONDITIONAL><SHORT-NAME>filler-")
                    .Append(i).Append("</SHORT-NAME></FILLER-").Append(i).Append("-CONDITIONAL>");
            }

            var network = ArxmlReader.Read(StrangeSocketExtract.Replace(
                "<SOME-FUTURE-CONNECTION>",
                fillers + "<SOME-FUTURE-CONNECTION>", StringComparison.Ordinal));

            var said = network.Diagnostics.Single(d =>
                d.Kind == ArxmlDiagnosticKind.SocketLayerUnrecognised);
            StringAssert.Contains(said.Message, "SOME-FUTURE-CONNECTION", said.Message);
            StringAssert.DoesNotMatch(said.Message, new System.Text.RegularExpressions.Regex(
                "FILLER-\\d+-CONDITIONAL"),
                "a purely structural variant wrapper carries no short name of its own and identifies no " +
                "vocabulary, so it must not occupy one of the report's few slots: " + said.Message);
        }

        [TestMethod]
        public void TheUnrecognisedSocketLayerIsReportedOncePerRun()
        {
            var network = ArxmlReader.Read(StrangeSocketExtract.Replace(
                "<SHORT-NAME>CH_STRANGE</SHORT-NAME>",
                "<SHORT-NAME>CH_STRANGE</SHORT-NAME>", StringComparison.Ordinal));

            // Two channels, both without a recognisable socket layer.
            var twoChannels = ArxmlReader.Read(StrangeSocketExtract.Replace(
                "</ETHERNET-PHYSICAL-CHANNEL>",
                "</ETHERNET-PHYSICAL-CHANNEL><ETHERNET-PHYSICAL-CHANNEL><SHORT-NAME>CH_ALSO_STRANGE" +
                "</SHORT-NAME><SOME-FUTURE-CONNECTION/></ETHERNET-PHYSICAL-CHANNEL>",
                StringComparison.Ordinal));

            Assert.AreEqual(1,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.SocketLayerUnrecognised));
            Assert.AreEqual(2, twoChannels.Elements.Count(e => e.Kind == ArxmlKinds.Channel),
                "the fixture really does have two channels now");
            Assert.AreEqual(1,
                twoChannels.Diagnostics.Count(d =>
                    d.Kind == ArxmlDiagnosticKind.SocketLayerUnrecognised),
                "and two silent channels are still one diagnostic");
        }

        /// <summary>
        ///   A CAN bus is never asked about its socket layer, so it never reports one missing. The flag is
        ///   declared on the protocol rather than derived from "has no frames", which is what makes this
        ///   assertion about a decision rather than a coincidence.
        /// </summary>
        [TestMethod]
        public void ABusWithNoSocketLayerIsNotAskedAboutOne()
        {
            var network = ArxmlReader.Read(EthernetExtract.Replace("ETHERNET-", "CAN-",
                StringComparison.Ordinal));

            Assert.AreEqual(ArxmlProperties.CanProtocol,
                Element(network, "/Clusters/BACKBONE")[ArxmlProperties.Protocol],
                "the fixture is now a CAN bus, which is what makes the next assertion mean anything");
            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.SocketLayerUnrecognised),
                "a CAN channel has no socket layer to be missing");
        }

        #endregion

        #region helpers

        /// <summary>
        ///   The socket layer as a SHAPE: every relation as "kind -type-> kind", sorted. What N5 protects is
        ///   that the structure does not depend on the revision, and comparing paths would fail on the names
        ///   the two spellings legitimately differ in.
        /// </summary>
        private static String[] Shape(ArxmlNetwork network)
        {
            var kinds = network.Elements.ToDictionary(e => e.Path, e => e.Kind, StringComparer.Ordinal);
            return network.Relations
                .Where(r => kinds.ContainsKey(r.FromPath) && kinds.ContainsKey(r.ToPath))
                .Select(r => kinds[r.FromPath] + " -" + r.Type + "-> " + kinds[r.ToPath])
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
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

        #endregion

        #region the fixture

        /// <summary>
        ///   The socket-layer fixture, with the CONNECTION left out: the two AUTOSAR revisions put it in
        ///   different places, so each spelling fills one placeholder and leaves the other empty.
        ///
        ///   <para>ONE fixture with two holes rather than two fixtures, deliberately. What the
        ///   normalisation has to guarantee is that the same network imports as the same shape whichever
        ///   spelling it arrived in, and two hand-written fixtures could differ somewhere else and make that
        ///   comparison meaningless.</para>
        /// </summary>
        private const String SocketFixture = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://autosar.org/schema/r4.0 AUTOSAR_00048.xsd">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_WheelSpeed</SHORT-NAME>
                    </SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_WheelSpeed</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_WheelSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Sensors</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_WheelSpeed</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <ETHERNET-CLUSTER>
                      <SHORT-NAME>BACKBONE</SHORT-NAME>
                      <ETHERNET-CLUSTER-VARIANTS>
                        <ETHERNET-CLUSTER-CONDITIONAL>
                          <BAUDRATE>1000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_SOCKETS</SHORT-NAME>
                              <PDU-TRIGGERINGS>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_Sensors</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Sensors</I-PDU-REF>
                                </PDU-TRIGGERING>
                              </PDU-TRIGGERINGS>
                              <NETWORK-ENDPOINTS>
                                <NETWORK-ENDPOINT>
                                  <SHORT-NAME>EP_Hub</SHORT-NAME>
                                  <NETWORK-ENDPOINT-ADDRESSES>
                                    <IPV-4-CONFIGURATION>
                                      <IPV-4-ADDRESS>10.0.7.1</IPV-4-ADDRESS>
                                      <IPV-4-ADDRESS-SOURCE>FIXED</IPV-4-ADDRESS-SOURCE>
                                      <NETWORK-MASK>255.255.255.0</NETWORK-MASK>
                                    </IPV-4-CONFIGURATION>
                                  </NETWORK-ENDPOINT-ADDRESSES>
                                </NETWORK-ENDPOINT>
                                <NETWORK-ENDPOINT>
                                  <SHORT-NAME>EP_Drive</SHORT-NAME>
                                  <NETWORK-ENDPOINT-ADDRESSES>
                                    <IPV-4-CONFIGURATION>
                                      <IPV-4-ADDRESS>10.0.7.2</IPV-4-ADDRESS>
                                      <IPV-4-ADDRESS-SOURCE>FIXED</IPV-4-ADDRESS-SOURCE>
                                      <NETWORK-MASK>255.255.255.0</NETWORK-MASK>
                                    </IPV-4-CONFIGURATION>
                                  </NETWORK-ENDPOINT-ADDRESSES>
                                </NETWORK-ENDPOINT>
                              </NETWORK-ENDPOINTS>
                              <SO-AD-CONFIG>
                                <SOCKET-ADDRESSS>
                                  <SOCKET-ADDRESS>
                                    <SHORT-NAME>SA_HubServer</SHORT-NAME>
                                    <APPLICATION-ENDPOINT>
                                      <SHORT-NAME>AE_Hub</SHORT-NAME>
                                      <NETWORK-ENDPOINT-REF DEST="NETWORK-ENDPOINT">/Clusters/BACKBONE/CH_SOCKETS/EP_Hub</NETWORK-ENDPOINT-REF>
                                      <TP-CONFIGURATION>
                                        <UDP-TP>
                                          <PORT-NUMBER>30490</PORT-NUMBER>
                                        </UDP-TP>
                                      </TP-CONFIGURATION>
                                    </APPLICATION-ENDPOINT>
            __SOCKET_CONNECTION__
                                  </SOCKET-ADDRESS>
                                  <SOCKET-ADDRESS>
                                    <SHORT-NAME>SA_DriveClient</SHORT-NAME>
                                    <APPLICATION-ENDPOINT>
                                      <SHORT-NAME>AE_Drive</SHORT-NAME>
                                      <NETWORK-ENDPOINT-REF DEST="NETWORK-ENDPOINT">/Clusters/BACKBONE/CH_SOCKETS/EP_Drive</NETWORK-ENDPOINT-REF>
                                      <TP-CONFIGURATION>
                                        <UDP-TP>
                                          <PORT-NUMBER>30491</PORT-NUMBER>
                                        </UDP-TP>
                                      </TP-CONFIGURATION>
                                    </APPLICATION-ENDPOINT>
                                  </SOCKET-ADDRESS>
                                </SOCKET-ADDRESSS>
            __CHANNEL_CONNECTION__
                              </SO-AD-CONFIG>
                            </ETHERNET-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </ETHERNET-CLUSTER-CONDITIONAL>
                      </ETHERNET-CLUSTER-VARIANTS>
                    </ETHERNET-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>The older revisions' spelling: a bundle under the channel, naming its server port.</summary>
        private const String ConnectionBundle = """
                                <CONNECTION-BUNDLES>
                                  <SOCKET-CONNECTION-BUNDLE>
                                    <SHORT-NAME>SCB_Sensors</SHORT-NAME>
                                    <SERVER-PORT-REF DEST="SOCKET-ADDRESS">/Clusters/BACKBONE/CH_SOCKETS/SA_HubServer</SERVER-PORT-REF>
                                    <BUNDLED-CONNECTIONS>
                                      <SOCKET-CONNECTION>
                                        <CLIENT-PORT-REF DEST="SOCKET-ADDRESS">/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient</CLIENT-PORT-REF>
                                        <PDUS>
                                          <SOCKET-CONNECTION-IPDU-IDENTIFIER>
                                            <PDU-TRIGGERING-REF DEST="PDU-TRIGGERING">/Clusters/BACKBONE/CH_SOCKETS/PT_Sensors</PDU-TRIGGERING-REF>
                                            <HEADER-ID>3735928559</HEADER-ID>
                                          </SOCKET-CONNECTION-IPDU-IDENTIFIER>
                                        </PDUS>
                                      </SOCKET-CONNECTION>
                                    </BUNDLED-CONNECTIONS>
                                  </SOCKET-CONNECTION-BUNDLE>
                                </CONNECTION-BUNDLES>
            """;

        /// <summary>
        ///   The newer revision's spelling: a STATIC socket connection hanging off the serving socket, with
        ///   no server port reference because the nesting says it, and the client end named as a remote
        ///   address.
        /// </summary>
        private const String StaticConnection = """
                                        <STATIC-SOCKET-CONNECTIONS>
                                          <STATIC-SOCKET-CONNECTION>
                                            <SHORT-NAME>SSC_Sensors</SHORT-NAME>
                                            <REMOTE-ADDRESSS>
                                              <SOCKET-ADDRESS-REF DEST="SOCKET-ADDRESS">/Clusters/BACKBONE/CH_SOCKETS/SA_DriveClient</SOCKET-ADDRESS-REF>
                                            </REMOTE-ADDRESSS>
                                            <I-PDUS>
                                              <SO-CON-I-PDU-IDENTIFIER>
                                                <PDU-TRIGGERING-REF DEST="PDU-TRIGGERING">/Clusters/BACKBONE/CH_SOCKETS/PT_Sensors</PDU-TRIGGERING-REF>
                                                <HEADER-ID>3735928559</HEADER-ID>
                                              </SO-CON-I-PDU-IDENTIFIER>
                                            </I-PDUS>
                                          </STATIC-SOCKET-CONNECTION>
                                        </STATIC-SOCKET-CONNECTIONS>
            """;

        private static readonly String BundleExtract = SocketFixture
            .Replace("__CHANNEL_CONNECTION__", ConnectionBundle, StringComparison.Ordinal)
            .Replace("__SOCKET_CONNECTION__", String.Empty, StringComparison.Ordinal);

        private static readonly String StaticExtract = SocketFixture
            .Replace("__CHANNEL_CONNECTION__", String.Empty, StringComparison.Ordinal)
            .Replace("__SOCKET_CONNECTION__", StaticConnection, StringComparison.Ordinal);

        /// <summary>
        ///   An invented Ethernet segment carrying the DETAIL layer: a provided SOME/IP service on the hub's
        ///   socket, a consumed one on the drive unit's, and a switch with a coupling port wired to the hub's.
        /// </summary>
        private const String ServiceExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>SENSOR_HUB</SHORT-NAME>
                      <COMM-CONTROLLERS>
                        <ETHERNET-COMMUNICATION-CONTROLLER>
                          <SHORT-NAME>HUB_CTRL</SHORT-NAME>
                          <COUPLING-PORTS>
                            <COUPLING-PORT>
                              <SHORT-NAME>CP_Hub_1</SHORT-NAME>
                            </COUPLING-PORT>
                          </COUPLING-PORTS>
                        </ETHERNET-COMMUNICATION-CONTROLLER>
                      </COMM-CONTROLLERS>
                      <COMM-CONNECTORS>
                        <ETHERNET-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>HUB_CONN</SHORT-NAME>
                        </ETHERNET-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                    <ECU-INSTANCE>
                      <SHORT-NAME>SWITCH_ECU</SHORT-NAME>
                      <COMM-CONTROLLERS>
                        <ETHERNET-COMMUNICATION-CONTROLLER>
                          <SHORT-NAME>SW_CTRL</SHORT-NAME>
                          <COUPLING-PORTS>
                            <COUPLING-PORT>
                              <SHORT-NAME>CP_Switch_1</SHORT-NAME>
                            </COUPLING-PORT>
                          </COUPLING-PORTS>
                        </ETHERNET-COMMUNICATION-CONTROLLER>
                      </COMM-CONTROLLERS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <ETHERNET-CLUSTER>
                      <SHORT-NAME>BACKBONE</SHORT-NAME>
                      <ETHERNET-CLUSTER-VARIANTS>
                        <ETHERNET-CLUSTER-CONDITIONAL>
                          <PHYSICAL-CHANNELS>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_SOCKETS</SHORT-NAME>
                              <COUPLING-PORT-CONNECTIONS>
                                <COUPLING-PORT-CONNECTION>
                                  <FIRST-PORT-REF DEST="COUPLING-PORT">/Ecus/SENSOR_HUB/HUB_CTRL/CP_Hub_1</FIRST-PORT-REF>
                                  <SECOND-PORT-REF DEST="COUPLING-PORT">/Ecus/SWITCH_ECU/SW_CTRL/CP_Switch_1</SECOND-PORT-REF>
                                </COUPLING-PORT-CONNECTION>
                              </COUPLING-PORT-CONNECTIONS>
                              <SO-AD-CONFIG>
                                <SOCKET-ADDRESSS>
                                  <SOCKET-ADDRESS>
                                    <SHORT-NAME>SA_HubServer</SHORT-NAME>
                                    <APPLICATION-ENDPOINT>
                                      <SHORT-NAME>AE_Hub</SHORT-NAME>
                                      <TP-CONFIGURATION>
                                        <UDP-TP>
                                          <PORT-NUMBER>30490</PORT-NUMBER>
                                        </UDP-TP>
                                      </TP-CONFIGURATION>
                                      <PROVIDED-SERVICE-INSTANCES>
                                        <PROVIDED-SERVICE-INSTANCE>
                                          <SHORT-NAME>PSI_WheelSpeed</SHORT-NAME>
                                          <SERVICE-IDENTIFIER>4660</SERVICE-IDENTIFIER>
                                          <INSTANCE-IDENTIFIER>1</INSTANCE-IDENTIFIER>
                                        </PROVIDED-SERVICE-INSTANCE>
                                      </PROVIDED-SERVICE-INSTANCES>
                                    </APPLICATION-ENDPOINT>
                                  </SOCKET-ADDRESS>
                                  <SOCKET-ADDRESS>
                                    <SHORT-NAME>SA_DriveClient</SHORT-NAME>
                                    <APPLICATION-ENDPOINT>
                                      <SHORT-NAME>AE_Drive</SHORT-NAME>
                                      <TP-CONFIGURATION>
                                        <UDP-TP>
                                          <PORT-NUMBER>30491</PORT-NUMBER>
                                        </UDP-TP>
                                      </TP-CONFIGURATION>
                                      <CONSUMED-SERVICE-INSTANCES>
                                        <CONSUMED-SERVICE-INSTANCE>
                                          <SHORT-NAME>CSI_WheelSpeed</SHORT-NAME>
                                          <SERVICE-IDENTIFIER>4660</SERVICE-IDENTIFIER>
                                          <INSTANCE-IDENTIFIER>1</INSTANCE-IDENTIFIER>
                                        </CONSUMED-SERVICE-INSTANCE>
                                      </CONSUMED-SERVICE-INSTANCES>
                                    </APPLICATION-ENDPOINT>
                                  </SOCKET-ADDRESS>
                                </SOCKET-ADDRESSS>
                              </SO-AD-CONFIG>
                            </ETHERNET-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </ETHERNET-CLUSTER-CONDITIONAL>
                      </ETHERNET-CLUSTER-VARIANTS>
                    </ETHERNET-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>
        ///   An Ethernet channel whose socket layer this reader does NOT recognise, and a schema nobody has
        ///   ever shipped. The point is the report: a spelling the reader has not met must not be silent.
        /// </summary>
        private const String StrangeSocketExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://autosar.org/schema/r4.0 AUTOSAR_00099.xsd">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <ETHERNET-CLUSTER>
                      <SHORT-NAME>BACKBONE</SHORT-NAME>
                      <ETHERNET-CLUSTER-VARIANTS>
                        <ETHERNET-CLUSTER-CONDITIONAL>
                          <PHYSICAL-CHANNELS>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_STRANGE</SHORT-NAME>
                              <SOME-FUTURE-CONNECTION>
                                <SHORT-NAME>SFC_Whatever</SHORT-NAME>
                              </SOME-FUTURE-CONNECTION>
                            </ETHERNET-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </ETHERNET-CLUSTER-CONDITIONAL>
                      </ETHERNET-CLUSTER-VARIANTS>
                    </ETHERNET-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>
        ///   An invented Ethernet backbone with TWO VLANs, one ECU on each and one on both, carrying a PDU
        ///   with a signal that implements a shared system signal.
        ///
        ///   <para>Shaped to exercise the three things that make Ethernet different: no frame layer, a
        ///   channel per VLAN, and the flow coming from the PDU triggering's ports. The system signal is
        ///   under /Shared deliberately - it is the join a second bus of another protocol would meet it
        ///   at.</para>
        /// </summary>
        private const String EthernetExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_WheelSpeed</SHORT-NAME>
                    </SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_WheelSpeed</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_WheelSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Sensors</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_WheelSpeed</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>SENSOR_HUB</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <ETHERNET-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>HUB_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-PDU-PORT>
                              <SHORT-NAME>HUB_TX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </I-PDU-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </ETHERNET-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                    <ECU-INSTANCE>
                      <SHORT-NAME>DRIVE_ECU</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <ETHERNET-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>DRIVE_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-PDU-PORT>
                              <SHORT-NAME>DRIVE_RX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </I-PDU-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </ETHERNET-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <ETHERNET-CLUSTER>
                      <SHORT-NAME>BACKBONE</SHORT-NAME>
                      <ETHERNET-CLUSTER-VARIANTS>
                        <ETHERNET-CLUSTER-CONDITIONAL>
                          <BAUDRATE>1000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_SENSORS</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/SENSOR_HUB/HUB_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/DRIVE_ECU/DRIVE_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <PDU-TRIGGERINGS>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_Sensors</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Sensors</I-PDU-REF>
                                  <I-PDU-PORT-REFS>
                                    <I-PDU-PORT-REF DEST="I-PDU-PORT">/Ecus/SENSOR_HUB/HUB_CONN/HUB_TX</I-PDU-PORT-REF>
                                    <I-PDU-PORT-REF DEST="I-PDU-PORT">/Ecus/DRIVE_ECU/DRIVE_CONN/DRIVE_RX</I-PDU-PORT-REF>
                                  </I-PDU-PORT-REFS>
                                </PDU-TRIGGERING>
                              </PDU-TRIGGERINGS>
                              <VLAN>
                                <SHORT-NAME>VLAN_SENSORS</SHORT-NAME>
                                <VLAN-IDENTIFIER>12</VLAN-IDENTIFIER>
                              </VLAN>
                            </ETHERNET-PHYSICAL-CHANNEL>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_DIAG</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/DRIVE_ECU/DRIVE_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <VLAN>
                                <SHORT-NAME>VLAN_DIAG</SHORT-NAME>
                                <VLAN-IDENTIFIER>3</VLAN-IDENTIFIER>
                              </VLAN>
                            </ETHERNET-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </ETHERNET-CLUSTER-CONDITIONAL>
                      </ETHERNET-CLUSTER-VARIANTS>
                    </ETHERNET-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        #endregion
    }
}
