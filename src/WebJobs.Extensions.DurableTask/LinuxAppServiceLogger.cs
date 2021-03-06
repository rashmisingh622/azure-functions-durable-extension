﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Microsoft.Azure.WebJobs.Extensions.DurableTask
{
    /// <summary>
    /// In charge of logging services for our linux App Service offerings: Consumption and Dedicated.
    /// In Consumption, we log to the console and identify our log by a prefix.
    /// In Dedicated, we log asynchronously to a pre-defined logging path using Serilog.
    /// This class is utilized by <c>EventSourceListener</c> to write logs corresponding to
    /// specific EventSource providers.
    /// </summary>
    internal class LinuxAppServiceLogger
    {
        private const string ConsolePrefix = "MS_DURABLE_FUNCTION_EVENTS_LOGS";
        internal const int MaxArchives = 5;

        // variable below is internal static for testing purposes
        // we need to be able to change the logging path for a windows-based CI
#pragma warning disable SA1401 // Fields should be private
        internal static string LoggingPath = "/var/log/functionsLogs/durableevents.log";
#pragma warning restore SA1401 // Fields should be private

        // logging metadata
        private readonly JToken roleInstance;
        private readonly JToken tenant;
        private readonly JToken sourceMoniker;
        private readonly JToken procID;

        // if true, we write to console (linux consumption), else to a file (linux dedicated).
        private readonly bool writeToConsole;

        /// <summary>
        /// Create a LinuxAppServiceLogger instance.
        /// </summary>
        /// <param name="writeToConsole">If true, write to console (linux consumption) else to a file (dedicated).</param>
        /// <param name="containerName">The app's container name.</param>
        /// <param name="tenant">The app's tenant.</param>
        /// <param name="stampName">The app's stamp.</param>
        public LinuxAppServiceLogger(
            bool writeToConsole,
            string containerName,
            string tenant,
            string stampName)
        {
            // Initializing fixed logging metadata
            this.writeToConsole = writeToConsole;
            this.roleInstance = JToken.FromObject("App-" + containerName);
            this.tenant = JToken.FromObject(tenant);

            this.sourceMoniker = JToken.FromObject(
                string.IsNullOrEmpty(stampName) ? string.Empty : "L" + stampName.Replace("-", "").ToUpperInvariant());
            using (var process = Process.GetCurrentProcess())
            {
                this.procID = process.Id;
            }

            // Initialize file logger, if in Linux Dedicated
            if (!writeToConsole)
            {
                var tenMbInBytes = 10000000;
                Serilog.Log.Logger = new LoggerConfiguration()
                    .WriteTo.Async(a =>
                    {
                        a.File(
                            LinuxAppServiceLogger.LoggingPath,
                            outputTemplate: "{Message}{NewLine}",
                            fileSizeLimitBytes: tenMbInBytes,
                            rollOnFileSizeLimit: true,
                            retainedFileCountLimit: 10);
                    })
                    .CreateLogger();
            }
        }

        /// <summary>
        /// Given EventSource message data, we generate a JSON-string that we can log.
        /// </summary>
        /// <param name="eventData">An EventSource message, usually generated by an EventListener.</param>
        /// <returns>A JSON-formatted string representing the input.</returns>
        private string GenerateJsonStr(EventWrittenEventArgs eventData)
        {
            var values = eventData.Payload;
            var keys = eventData.PayloadNames;

            // We pack them into a JSON
            JObject json = new JObject
            {
                { "EventId", eventData.EventId },
                { "TimeStamp", DateTime.UtcNow },
                { "RoleInstance", this.roleInstance },
                { "Tenant", this.tenant },
                { "Pid", this.procID },
                { "Tid", Thread.CurrentThread.ManagedThreadId },
            };

            for (int i = 0; i < values.Count; i++)
            {
                json.Add(keys[i], JToken.FromObject(values[i]));
            }

            // Generate string-representation of JSON. Also remove newlines.
            string jsonString = json.ToString(Newtonsoft.Json.Formatting.None);
            jsonString = jsonString.Replace("\n", "\\n").Replace("\r", "\\r");
            return jsonString;
        }

        /// <summary>
        /// Log EventSource message data in Linux AppService.
        /// </summary>
        /// <param name="eventData">An EventSource message, usually generated by an EventListener.</param>
        public void Log(EventWrittenEventArgs eventData)
        {
            // Generate JSON string to log based on the EventSource message
            string jsonString = this.GenerateJsonStr(eventData);

            // We write to console in Linux Consumption
            if (this.writeToConsole)
            {
                // We're ignoring exceptions in the unobserved Task
                string consoleLine = ConsolePrefix + " " + jsonString;
                _ = Console.Out.WriteLineAsync(consoleLine);
            }
            else
            {
                // We write to a file in Linux Dedicated
                // Serilog handles file rolling (archiving) and deletion of old logs
                // Log-level should also be irrelevant as no minimal level has been configured
                Serilog.Log.Information(jsonString);
            }
        }
    }
}