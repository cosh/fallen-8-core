using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Log
{
    /// <summary>
    ///   The Fallen-8 logger
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// The error log delegates
        /// </summary>
        private static List<LogDelegate> _errorLogs = new List<LogDelegate> { ConsoleLog };

        /// <summary>
        /// The info log delegates
        /// </summary>
        private static List<LogDelegate> _infoLogs = new List<LogDelegate> { ConsoleLog };

        /// <summary>
        /// The console log
        /// </summary>
        /// <param name="toBeloggedString"></param>
        private static void ConsoleLog(String toBeloggedString)
        {
            lock (Console.Title)
            {
                Console.WriteLine(toBeloggedString);
            }
        }

        /// <summary>
        /// The log-delegate
        /// </summary>
        /// <param name="toBeLoggedString">The string that should be logged</param>
        public delegate void LogDelegate(String toBeLoggedString);

        /// <summary>
        /// Registers an error log
        /// </summary>
        /// <param name="logDelegate">The log delegate</param>
        public static void RegisterErrorLog(LogDelegate logDelegate)
        {
            _errorLogs.Add(logDelegate);
        }

        /// <summary>
        /// Registers an info log
        /// </summary>
        /// <param name="logDelegate">The log delegate</param>
        public static void RegisterInfoLog(LogDelegate logDelegate)
        {
            _infoLogs.Add(logDelegate);
        }

        /// <summary>
        /// Log an error
        /// </summary>
        /// <param name="message">Error message</param>
        public static void LogError(String message)
        {
            _errorLogs.ForEach(_ => _(message));
        }

        /// <summary>
        /// Log an info
        /// </summary>
        /// <param name="message">Info message</param>
        public static void LogInfo(string message)
        {
            _infoLogs.ForEach(_ => _(message));
        }
    }
}
