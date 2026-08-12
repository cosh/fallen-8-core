// MIT License
//
// ServiceFactory.cs
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
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Service
{
    /// <summary>
    ///   Service factory
    /// </summary>
    public sealed class ServiceFactory : AThreadSafeElement
    {
        #region Data

        /// <summary>
        /// The Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        /// <summary>
        ///   The created services.
        /// </summary>
        public readonly IDictionary<String, IService> Services;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<ServiceFactory> _logger;

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new service factory
        /// </summary>
        /// <param name="fallen8">Fallen-8</param>
        /// <param name="logger">Logger instance</param>
        public ServiceFactory(IFallen8 fallen8, ILogger<ServiceFactory> logger)
        {
            _fallen8 = fallen8;
            Services = new Dictionary<string, IService>();
            _logger = logger;
        }

        #endregion

        #region public methods


        /// <summary>
        ///   Gets the available service plugin NAMES: the discovered built-ins unioned with this
        ///   namespace's registered service types, by the union rule
        ///   <see cref="PluginFactory.AvailablePluginNames" /> owns - the same surface, from the same
        ///   home, as <c>IndexFactory.GetAvailableIndexPlugins</c>, because a host registers index and
        ///   service types through one registry and neither family may be the odd one out.
        ///
        ///   <para>Names, not the multi-line descriptions this used to return: a name is what
        ///   <see cref="TryAddService" /> takes, and a registered plugin can only be UNIONED in as a
        ///   name (the registry has no description in that shape). Descriptions are still available
        ///   from <see cref="PluginFactory.TryGetAvailablePluginsWithDescriptions{T}" /> for a caller
        ///   that wants them.</para>
        /// </summary>
        /// <returns> The available service plugins. </returns>
        [RequiresUnreferencedCode(PluginFactory.DiscoveryIsNotTrimSafe)]
        public IEnumerable<String> GetAvailableServicePlugins()
        {
            return PluginFactory.AvailablePluginNames(Plugins.PluginContract.Service, _fallen8?.Plugins);
        }

        /// <summary>
        ///   Tries to add a service.
        /// </summary>
        /// <returns> True for success. </returns>
        /// <param name='service'> The added service. </param>
        /// <param name='servicePluginName'> The name of the service plugin. </param>
        /// <param name="serviceName"> The name of the service instance </param>
        /// <param name='parameter'> The parameters of this service. </param>
        public bool TryAddService(out IService service, string servicePluginName, string serviceName,
                                  IDictionary<string, object> parameter)
        {
            try
            {
                if (TryResolveServicePlugin(out service, servicePluginName))
                {
                    if (WriteResource())
                    {
                        if (Services.ContainsKey(serviceName))
                        {
                            _logger.LogError(String.Format("There already exists a service with the name {0}", serviceName));
                            service = null;

                            FinishWriteResource();
                            return false;
                        }

                        service.Initialize(_fallen8, parameter);
                        Services.Add(serviceName, service);

                        FinishWriteResource();
                        return true;
                    }

                    throw new CollisionException();
                }
                else
                {
                    PluginFactory.LogPluginNotFound(_logger, "service", servicePluginName);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(String.Format("Fallen-8 was not able to add the {0} service plugin. Message: {1}",
                    servicePluginName, e.Message));

                FinishWriteResource();

                service = null;
                return false;
            }

            service = null;
            return false;
        }

        /// <summary>
        /// Shuts down all the services
        /// </summary>
        public void ShutdownAllServices()
        {
            if (WriteResource())
            {
                try
                {
                    foreach (var service in Services)
                    {
                        service.Value.TryStop();
                    }

                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        /// <summary>
        /// Starts all the services
        /// </summary>
        public void StartAllServices()
        {
            if (WriteResource())
            {
                try
                {
                    foreach (var service in Services)
                    {
                        service.Value.TryStart();
                    }
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        /// <summary>
        ///   Stops and removes a service, under the same write lock every other mutation of
        ///   <see cref="Services" /> takes. The stop happens BEFORE the removal because this
        ///   dictionary holds the only handle: a service removed while running would keep its timers
        ///   and listeners alive with nothing able to reach it again, not even a later
        ///   <see cref="ShutdownAllServices" />. A misbehaving plugin's <c>TryStop</c> is contained so
        ///   it cannot turn a removal into a fault, and the service is dropped regardless.
        /// </summary>
        /// <param name="serviceName">The key the service was registered under.</param>
        /// <returns><c>true</c> when a service was removed; <c>false</c> when the key was unknown.</returns>
        public Boolean TryRemoveService(String serviceName)
        {
            if (WriteResource())
            {
                try
                {
                    if (!Services.TryGetValue(serviceName, out var service))
                    {
                        return false;
                    }

                    try
                    {
                        service.TryStop();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, String.Format(
                            "The service \"{0}\" threw while being stopped; it is removed anyway.", serviceName));
                    }

                    return Services.Remove(serviceName);
                }
                finally
                {
                    FinishWriteResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region internal methods

        /// <summary>
        /// Opens a serialized service
        /// </summary>
        /// <param name="serviceName">Service name</param>
        /// <param name="servicePluginName">Service plugin name</param>
        /// <param name="reader">Serialization reader</param>
        /// <param name="fallen8">Fallen-8</param>
        /// <param name="startService">Start the service?</param>
        internal void OpenService(string serviceName, string servicePluginName, SerializationReader reader, IFallen8 fallen8, Boolean startService)
        {
            IService service;
            if (TryResolveServicePlugin(out service, servicePluginName))
            {
                if (WriteResource())
                {
                    try
                    {
                        if (Services.ContainsKey(serviceName))
                        {
                            _logger.LogError(String.Format("A service with the same name \"{0}\" already exists.", serviceName));
                        }

                        service.Load(reader, fallen8);

                        if (service.TryStart())
                        {
                            Services.Add(serviceName, service);
                        }
                    }
                    finally
                    {
                        FinishWriteResource();
                    }

                    return;
                }

                throw new CollisionException();
            }

            PluginFactory.LogPluginNotFound(_logger, "service", servicePluginName);
        }

        /// <summary>
        ///   Resolves a service plugin by name, registry-first then discovery, for both an add and a
        ///   checkpoint rehydration. The contract is the one
        ///   <c>IndexFactory.TryResolveIndexPlugin</c> documents; a service is the same shape of plugin
        ///   (each service IS an instance).
        /// </summary>
        private bool TryResolveServicePlugin(out IService service, string servicePluginName)
        {
            var registry = _fallen8?.Plugins;
            if (registry != null && registry.TryActivate(out service, servicePluginName))
            {
                return true;
            }

            return TryFindDiscoveredServiceSuppressed(out service, servicePluginName);
        }

        /// <summary>
        ///   The suppression seam for the discovery half of <see cref="TryResolveServicePlugin" />,
        ///   justified exactly as <c>IndexFactory.TryFindDiscoveredIndexSuppressed</c> is.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "Discovery degrades to a clean not-found; the trim-safe path is host type registration. See the doc comment.")]
        private static bool TryFindDiscoveredServiceSuppressed(out IService service, string servicePluginName)
        {
            return PluginFactory.TryFindPlugin(out service, servicePluginName);
        }

        #endregion
    }
}
