﻿// MIT License
//
// IService.cs
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
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Core.Service
{
    /// <summary>
    ///   Fallen-8 service interface.
    /// </summary>
    public interface IService : IPlugin, IFallen8Serializable
    {
        /// <summary>
        ///   Gets the start time.
        /// </summary>
        /// <value> The start time. </value>
        DateTime StartTime { get; }

        /// <summary>
        ///   Gets a value indicating whether this instance is running.
        /// </summary>
        /// <value> <c>true</c> if this instance is running; otherwise, <c>false</c> . </value>
        Boolean IsRunning { get; }

        /// <summary>
        ///   Gets the metadata.
        /// </summary>
        /// <value> The metadata. </value>
        IDictionary<String, String> Metadata { get; }

        /// <summary>
        ///   Tries to stop this service.
        /// </summary>
        /// <returns> <c>true</c> if this instance is stopped; otherwise, <c>false</c> . </returns>
        bool TryStop();

        /// <summary>
        ///   Tries to start this service.
        /// </summary>
        /// <returns> <c>true</c> if this instance is started; otherwise, <c>false</c> . </returns>
        bool TryStart();

        /// <summary>
        /// Called when the service plugin was restarted.
        /// </summary>
        void OnServiceRestart();
    }
}
