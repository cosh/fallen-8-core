using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Plugin;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using NoSQL.GraphDB.Core.Log;
using NoSQL.GraphDB.Core.Error;
using System.IO;
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
                            Logger.LogError(String.Format("There already exists a service with the name {0}", serviceName));
                            service = null;

                            FinishWriteResource();
                            return false;
                        }

                        service.Initialize(_fallen8, parameter);
                        Services.Add(serviceName, service);

                        FinishWriteResource();
                        return true;
                    }

                    throw new CollisionException(this);
                }
                else
                {
                    Logger.LogError(String.Format("Fallen-8 did not fine the {0} service plugin",
                        servicePluginName));
                }
            }
            catch (Exception e)
            {
                Logger.LogError(String.Format("Fallen-8 was not able to add the {0} service plugin. Message: {1}",
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

            throw new CollisionException(this);
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

            throw new CollisionException(this);
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
                            Logger.LogError(String.Format("A service with the same name \"{0}\" already exists.", serviceName));
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

                throw new CollisionException(this);
            }

            Logger.LogError(String.Format("Could not find service plugin with name \"{0}\".", servicePluginName));
        }

        #endregion
    }
}
