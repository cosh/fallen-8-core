﻿// MIT License
//
// ServiceFactory.cs
//
// Copyright (c) 2022 Henning Rauch
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
        private readonly Fallen8 _fallen8;

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
        public ServiceFactory(Fallen8 fallen8)
        {
            _fallen8 = fallen8;
            Services = new Dictionary<string, IService>();
            _logger = _fallen8._loggerFactory.CreateLogger<ServiceFactory>();
        }

        #endregion

        #region public methods


        /// <summary>
        ///   Gets the available service plugins.
        /// </summary>
        /// <returns> The available service plugins. </returns>
        public IEnumerable<String> GetAvailableServicePlugins()
        {
            Dictionary<String, string> result;

            PluginFactory.TryGetAvailablePluginsWithDescriptions<IService>(out result);

            return result.Select(_ => _.Value);
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
                if (PluginFactory.TryFindPlugin(out service, servicePluginName))
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
                    _logger.LogError(String.Format("Fallen-8 did not fine the {0} service plugin",
                        servicePluginName));
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
        internal void OpenService(string serviceName, string servicePluginName, SerializationReader reader, Fallen8 fallen8, Boolean startService)
        {
            IService service;
            if (PluginFactory.TryFindPlugin(out service, servicePluginName))
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

            _logger.LogError(String.Format("Could not find service plugin with name \"{0}\".", servicePluginName));
        }

        #endregion
    }
}
