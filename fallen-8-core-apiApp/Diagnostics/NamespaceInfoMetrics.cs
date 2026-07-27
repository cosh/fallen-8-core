// MIT License
//
// NamespaceInfoMetrics.cs
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
using System.Diagnostics.Metrics;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Diagnostics
{
    /// <summary>
    ///   The <c>fallen8_namespace_info</c> gauge (feature fleet-observability, §3.4): value 1 per
    ///   namespace, carrying the namespace id + name, so Grafana can join engine metrics (keyed on
    ///   <c>fallen8.scope.id</c>) to the namespace name at query time -
    ///   <c>&lt;metric&gt; * on(fallen8_scope_id, fallen8_instance_id) group_left(fallen8_namespace_name) fallen8_namespace_info</c>.
    ///
    ///   <para>The process tenant + instance ids are NOT set as gauge tags: they are OTel resource
    ///   attributes (feature fleet-observability Phase 1) and the Collector promotes them onto every
    ///   metric, including this one, so they arrive as labels without risking a duplicate-label
    ///   collision. The join therefore also keys on <c>fallen8.instance.id</c> to stay
    ///   one-to-one across a fleet where every instance shares the reserved <c>default</c> scope id.</para>
    ///
    ///   <para>An OBSERVABLE gauge reading <see cref="Fallen8Namespaces.Snapshot"/> on each
    ///   collection, so a runtime rename is reflected with no create/rename/drop bookkeeping. Lives
    ///   on a meter named <see cref="AppDiagnostics.SourceName"/>, already subscribed by the metrics
    ///   pipeline, so no change to the <c>WithMetrics</c> registration is needed. Identity dimensions
    ///   are the narrowed tag-hygiene exception (§3.3); the id is the collection-assigned id, never a
    ///   user-supplied name used as a key.</para>
    /// </summary>
    public sealed class NamespaceInfoMetrics : IDisposable
    {
        private readonly Meter _meter;
        private readonly Fallen8Namespaces _namespaces;

        public NamespaceInfoMetrics(Fallen8Namespaces namespaces)
        {
            _namespaces = namespaces;

            // Same NAME as the app meter, so metrics.AddMeter(AppDiagnostics.SourceName) collects it
            // without any change to the WithMetrics registration.
            _meter = new Meter(AppDiagnostics.SourceName);
            _meter.CreateObservableGauge("fallen8.namespace.info", Observe, null,
                "One measurement (value 1) per namespace carrying its id + name - the Grafana id->name join.");
        }

        private IEnumerable<Measurement<Int32>> Observe()
        {
            // Snapshot() is a name-ordered copy, safe to iterate off the collection thread.
            var snapshot = _namespaces.Snapshot();
            var measurements = new List<Measurement<Int32>>(snapshot.Count);
            foreach (var ns in snapshot)
            {
                measurements.Add(new Measurement<Int32>(1,
                    new KeyValuePair<String, Object>(NamespaceEnrichmentMiddleware.ScopeIdTag, ns.Id),
                    new KeyValuePair<String, Object>(NamespaceEnrichmentMiddleware.NamespaceNameTag, ns.Name)));
            }

            return measurements;
        }

        public void Dispose()
        {
            _meter.Dispose();
        }
    }
}
