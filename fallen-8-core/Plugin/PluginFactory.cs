using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.Reflection;

namespace NoSQL.GraphDB.Core.Plugin
{
    /// <summary>
    ///   Fallen8 plugin factory.
    /// </summary>
    public static class PluginFactory
    {
        /// <summary>
        /// A TypeEvaluator delegate
        /// </summary>
        /// <param name="type">The to be evaluated type</param>
        /// <returns>True = OK otherwise not</returns>
        public delegate bool TypeEvaluator(Type type);

        /// <summary>
        ///   Tries to find a plugin.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='name'> The unique name of the pluginN. </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        public static Boolean TryFindPlugin<T>(out T result, String name) 
            where T : class, IPlugin
        {
            foreach (var aPluginTypeOfT in GetAllTypes<T>())
            {
                var aPluginInstance = Activate<T>(aPluginTypeOfT);

                if (aPluginInstance != null)
                {
                    if (aPluginInstance.PluginName == name)
                    {
                        result = aPluginInstance;
                        return true;
                    }
                }
            }

            result = default(T);
            return false;
        }

        /// <summary>
        ///   Tries to find a class.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='evaluator'> A type evaluator delegate </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        public static Boolean TryFind<T>(out T result, TypeEvaluator evaluator)
        {
            foreach (var aPluginTypeOfT in GetAllTypes<T>(false).Where(_ => evaluator(_)))
            {
                var aPluginInstance = Activator.CreateInstance(aPluginTypeOfT);
                if (aPluginInstance != null)
                {
                    result = (T)aPluginInstance;
                    return true;
                }
            }

            result = default(T);
            return false;
        }

        /// <summary>
        ///   Tries to get available plugin descriptions.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        public static Boolean TryGetAvailablePluginsWithDescriptions<T>(out Dictionary<String, String> result)
        {
            result = (from aPluginTypeOfT in GetAllTypes<T>()
                      select Activate<IPlugin>(aPluginTypeOfT)
                      into aPluginInstance
                      where aPluginInstance != null
                      select aPluginInstance).ToDictionary(key => key.PluginName, GenerateDescription);
            return result.Any();
        }

        /// <summary>
        ///   Tries to get available plugin descriptions.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        public static Boolean TryGetAvailablePlugins<T>(out IEnumerable<String> result)
        {
            result = (from aPluginTypeOfT in GetAllTypes<T>()
                      select Activate<IPlugin>(aPluginTypeOfT)
                      into aPluginInstance
                      where aPluginInstance != null
                      select aPluginInstance.PluginName);
            return result.Any();
        }

        /// <summary>
        /// Assimilate the specified dllStream.
        /// </summary>
        /// <param name='dllStream'>
        /// Dll stream.
        /// </param>
        /// <param name='path'>
        /// The path where the dll should be assimilated.
        /// </param>
        public static String Assimilate(Stream dllStream, String path = null)
        {
            var assimilationPath = path ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + Path.DirectorySeparatorChar + Path.GetRandomFileName() + ".dll";

            using (var dllFileStream = File.Create(assimilationPath, 1024))
            {
                dllStream.CopyTo(dllFileStream);
            }

            return assimilationPath;
        }

        #region private helper

        /// <summary>
        ///   Generates the description for a plugin
        /// </summary>
        /// <param name="aPluginInstance"> A plugin instance </param>
        /// <returns> </returns>
        private static string GenerateDescription(IPlugin aPluginInstance)
        {
            var sb = new StringBuilder();

            sb.AppendLine(String.Format("NAME: {0}", aPluginInstance.PluginName));
            sb.AppendLine(String.Format("  *DESCRIPTION: {0}", aPluginInstance.Description));
            sb.AppendLine(String.Format("  *MANUFACTURER: {0}", aPluginInstance.Manufacturer));
            sb.AppendLine(String.Format("  *TYPE: {0}", aPluginInstance.GetType().FullName));
            sb.AppendLine(String.Format("  *CATEGORY: {0}", aPluginInstance.PluginCategory.FullName));

            return sb.ToString();
        }

        /// <summary>
        ///   Gets all types.
        /// </summary>
        /// <returns> The all types. </returns>
        /// <typeparam name='T'> The type of the plugin. </typeparam>
        private static IEnumerable<Type> GetAllTypes<T>(Boolean checkForIPlugin = true)
        {
            var result = new List<Type>();

            string currentAssemblyDirectoryName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var files = Directory.EnumerateFiles(currentAssemblyDirectoryName, "*.dll");

            foreach (var file in files)
            {
                result.AddRange(ProcessAFile<T>(file, checkForIPlugin));
            }

            return result;
        }

        /// <summary>
        ///   Determines whether a type is interface of the specified type.
        /// </summary>
        /// <returns> <c>true</c> if this instance is interface of the specified type; otherwise, <c>false</c> . </returns>
        /// <param name='type'> Type. </param>
        /// <typeparam name='T'> The interface type. </typeparam>
        private static Boolean IsInterfaceOf<T>(Type type)
        {
            var interestingInterface = typeof(T).FullName;

            return type.GetInterfaces().Any(i =>
            {
                String fullNameOfInterface = null;

                try
                {
                    fullNameOfInterface = i.FullName;
                }
                catch (Exception)
                {
                }

                return fullNameOfInterface != null && fullNameOfInterface.Equals(interestingInterface);
            });
        }

        /// <summary>
        ///   Activate the specified currentPluginType.
        /// </summary>
        /// <param name='currentPluginType'> Current plugin type. </param>
        private static T Activate<T>(Type currentPluginType) 
            where T : class
        {
            object instance;

            try
            { 
                instance = Activator.CreateInstance(currentPluginType, false);
            }
            catch (TypeLoadException)
            {
                return default(T);
            }

            return instance as T;
        }

        /// <summary>
        /// Processes a file
        /// </summary>
        /// <typeparam name="T">The interface type</typeparam>
        /// <param name="file">The interesting file</param>
        /// <param name="checkForIPlugin">Should there be a check for IPlugin</param>
        /// <returns>Enumerable of matching types</returns>
        private static IEnumerable<Type> ProcessAFile<T>(string file, bool checkForIPlugin)
        {
            Assembly assembly;

            try
            {
                AssemblyName assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(file));
                assembly = Assembly.Load(assemblyName);
            }
            catch (FileLoadException e)
            {
                yield break;
            }

            var types = assembly.GetExportedTypes();

            foreach (var aType in types)
            {
                var NameOfType = aType.Name;

                if (!aType.IsClass || aType.IsAbstract)
                {
                    continue;
                }

                if (!aType.IsPublic)
                {
                    continue;
                }

                if (checkForIPlugin && !IsInterfaceOf<IPlugin>(aType))
                {
                    continue;
                }

                if (!IsInterfaceOf<T>(aType))
                {
                    continue;
                }

                if (aType.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }

                yield return aType;
            }
        }

        #endregion
    }
}
