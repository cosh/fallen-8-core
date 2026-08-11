// MIT License
//
// RedactingLoggerProvider.cs
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
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   Substitutes credential values inside the structured state BEFORE a line is formed, matching on VALUES.
    ///
    ///   <para>Matching on values rather than on key names is the design. A sink may serialise the state rather
    ///   than the message, so scrubbing the rendered string leaves the credential in that sink's JSON; redacting
    ///   by key name misses the line logging a request URL with the password in the query string; and
    ///   pattern-matching misses credentials that do not look like anything. Coverage is the message, the
    ///   structured state including non-string values by what they render as, log scopes, and the exception
    ///   object, which most sinks render in full.</para>
    ///
    ///   <para>Redaction is a SAFETY NET, not a licence: nothing in the runtime logs a credential on purpose,
    ///   but a provider is written by somebody else and one careless line in an HTTP failure path would write a
    ///   network-admin password into the container log.</para>
    /// </summary>
    public sealed class RedactingLoggerProvider : ILoggerProvider
    {
        /// <summary>What a credential value is replaced with.</summary>
        public const String Placeholder = "[redacted credential]";

        private readonly ILoggerProvider _inner;
        private readonly ActiveCredentials _active;

        public RedactingLoggerProvider(ILoggerProvider inner, ActiveCredentials active)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _active = active ?? throw new ArgumentNullException(nameof(active));
        }

        /// <summary>
        ///   Rewrites every registered <see cref="ILoggerProvider"/> so it runs behind this filter.
        ///
        ///   <para>Installed LAST in DI, and REWRITING existing registrations rather than clearing them. The
        ///   OTLP log exporter registers a provider, so installed before it the collector would receive exactly
        ///   what the console was spared; and rewriting is what makes the filter cover the sinks an operator
        ///   configured rather than only the one this code knows about.</para>
        /// </summary>
        public static void WrapRegisteredProviders(IServiceCollection services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            for (var i = 0; i < services.Count; i++)
            {
                var descriptor = services[i];
                if (descriptor.ServiceType != typeof(ILoggerProvider) ||
                    descriptor.ImplementationType == typeof(RedactingLoggerProvider))
                {
                    continue;
                }

                var original = descriptor;
                services[i] = new ServiceDescriptor(typeof(ILoggerProvider),
                    provider => new RedactingLoggerProvider(
                        Materialize(provider, original),
                        provider.GetRequiredService<ActiveCredentials>()),
                    original.Lifetime);
            }
        }

        public ILogger CreateLogger(String categoryName)
        {
            return new RedactingLogger(_inner.CreateLogger(categoryName), _active);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }

        private static ILoggerProvider Materialize(IServiceProvider services, ServiceDescriptor descriptor)
        {
            if (descriptor.ImplementationInstance is ILoggerProvider instance)
            {
                return instance;
            }

            if (descriptor.ImplementationFactory != null)
            {
                return (ILoggerProvider)descriptor.ImplementationFactory(services);
            }

            return (ILoggerProvider)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
        }

        /// <summary>
        ///   Substitutes every value a run currently HOLDS, longest first so a short credential that happens to
        ///   be a substring of a longer one cannot leave the longer one's tail in the line. With no run in
        ///   flight the set is empty and this is a filter with nothing to do.
        /// </summary>
        internal static String? Scrub(String? text, IReadOnlyList<String> values)
        {
            if (text == null || values.Count == 0)
            {
                return text;
            }

            var scrubbed = text;
            foreach (var value in values)
            {
                if (value.Length > 0 && scrubbed.Contains(value, StringComparison.Ordinal))
                {
                    scrubbed = scrubbed.Replace(value, Placeholder, StringComparison.Ordinal);
                }
            }

            return scrubbed;
        }

        private sealed class RedactingLogger : ILogger
        {
            private readonly ILogger _inner;
            private readonly ActiveCredentials _active;

            public RedactingLogger(ILogger inner, ActiveCredentials active)
            {
                _inner = inner;
                _active = active;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                var values = _active.Snapshot();
                if (values.Count == 0)
                {
                    return _inner.BeginScope(state);
                }

                return _inner.BeginScope(new RedactedState(Scrub(state?.ToString(), values) ?? String.Empty,
                    ScrubValues(state, values)));
            }

            public Boolean IsEnabled(LogLevel logLevel)
            {
                return _inner.IsEnabled(logLevel);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, String> formatter)
            {
                var values = _active.Snapshot();
                if (values.Count == 0)
                {
                    _inner.Log(logLevel, eventId, state, exception, formatter);
                    return;
                }

                var message = Scrub(formatter(state, exception), values) ?? String.Empty;
                var redactedState = new RedactedState(message, ScrubValues(state, values));

                // The exception object is replaced rather than edited: a provider whose exception message quotes
                // the request it sent, which is ordinary, would otherwise hand a credential to every sink that
                // renders an exception in full. The type name is kept in the message so the line still says what
                // went wrong.
                var redactedException = exception == null
                    ? null
                    : new RedactedException(Scrub(exception.GetType().FullName + ": " + exception.Message, values)
                                            ?? String.Empty);

                _inner.Log(logLevel, eventId, redactedState, redactedException, (s, _) => s.ToString());
            }

            private static ImmutableArray<KeyValuePair<String, Object?>> ScrubValues(Object? state,
                IReadOnlyList<String> values)
            {
                if (state is not IReadOnlyList<KeyValuePair<String, Object?>> pairs)
                {
                    return ImmutableArray<KeyValuePair<String, Object?>>.Empty;
                }

                var scrubbed = ImmutableArray.CreateBuilder<KeyValuePair<String, Object?>>(pairs.Count);
                for (var i = 0; i < pairs.Count; i++)
                {
                    var pair = pairs[i];

                    // Non-string values are covered too, by WHAT THEY RENDER AS: a credential passed as an
                    // object whose ToString is the value would otherwise reach a structured sink untouched.
                    var rendered = pair.Value is String text
                        ? text
                        : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                    var replacement = Scrub(rendered, values);

                    scrubbed.Add(ReferenceEquals(replacement, rendered) && pair.Value is not String
                        ? pair
                        : new KeyValuePair<String, Object?>(pair.Key, replacement));
                }

                return scrubbed.ToImmutable();
            }
        }

        /// <summary>
        ///   The state a scrubbed line carries: the redacted message, plus the redacted key/value pairs so a sink
        ///   that serialises the state rather than the message is covered too.
        /// </summary>
        private sealed class RedactedState : IReadOnlyList<KeyValuePair<String, Object?>>
        {
            private readonly String _message;
            private readonly ImmutableArray<KeyValuePair<String, Object?>> _values;

            public RedactedState(String message, ImmutableArray<KeyValuePair<String, Object?>> values)
            {
                _message = message;
                _values = values;
            }

            public Int32 Count => _values.Length;

            public KeyValuePair<String, Object?> this[Int32 index] => _values[index];

            public IEnumerator<KeyValuePair<String, Object?>> GetEnumerator()
            {
                foreach (var value in _values)
                {
                    yield return value;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public override String ToString()
            {
                return _message;
            }
        }

        /// <summary>An exception whose rendering carries no credential.</summary>
        private sealed class RedactedException : Exception
        {
            public RedactedException(String message)
                : base(message)
            {
            }
        }
    }
}
