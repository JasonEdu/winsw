﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using log4net;
using WinSW.Extensions;
using WinSW.Logging;
using WinSW.Native;
using WinSW.Util;

namespace WinSW
{
    public sealed class WrapperService : ServiceBase, IEventLogger, IServiceEventLog
    {
        private readonly Process process = new Process();
        private readonly XmlServiceConfig config;
        private Dictionary<string, string>? envs;

        internal WinSWExtensionManager ExtensionManager { get; }

        private static readonly ILog Log = LogManager.GetLogger(
#if NETCOREAPP
            Assembly.GetExecutingAssembly(),
#endif
            "WinSW");

        internal static readonly WrapperServiceEventLogProvider eventLogProvider = new WrapperServiceEventLogProvider();

        /// <summary>
        /// Indicates to the watch dog thread that we are going to terminate the process,
        /// so don't try to kill us when the child exits.
        /// </summary>
        private bool orderlyShutdown;
        private bool shuttingdown;

        /// <summary>
        /// Version of Windows service wrapper
        /// </summary>
        /// <remarks>
        /// The version will be taken from <see cref="AssemblyInfo"/>
        /// </remarks>
        public static Version Version => Assembly.GetExecutingAssembly().GetName().Version!;

        public WrapperService(XmlServiceConfig config)
        {
            this.config = config;
            this.ServiceName = config.Id;
            this.ExtensionManager = new WinSWExtensionManager(config);
            this.CanShutdown = true;
            this.CanStop = true;
            this.CanPauseAndContinue = false;
            this.AutoLog = true;
            this.shuttingdown = false;

            // Register the event log provider
            eventLogProvider.Service = this;

            if (config.Preshutdown)
            {
                this.AcceptPreshutdown();
            }

            Environment.CurrentDirectory = config.WorkingDirectory;
        }

        /// <summary>
        /// Process the file copy instructions, so that we can replace files that are always in use while
        /// the service runs.
        /// </summary>
        private void HandleFileCopies()
        {
            var file = this.config.BasePath + ".copies";
            if (!File.Exists(file))
            {
                return; // nothing to handle
            }

            try
            {
                using var tr = new StreamReader(file, Encoding.UTF8);
                string? line;
                while ((line = tr.ReadLine()) != null)
                {
                    this.LogInfo("Handling copy: " + line);
                    string[] tokens = line.Split('>');
                    if (tokens.Length > 2)
                    {
                        Log.Error("Too many delimiters in " + line);
                        continue;
                    }

                    this.MoveFile(tokens[0], tokens[1]);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// File replacement.
        /// </summary>
        private void MoveFile(string sourceFileName, string destFileName)
        {
            try
            {
                FileHelper.MoveOrReplaceFile(sourceFileName, destFileName);
            }
            catch (IOException e)
            {
                Log.Error("Failed to move :" + sourceFileName + " to " + destFileName + " because " + e.Message);
            }
        }

        /// <summary>
        /// Handle the creation of the logfiles based on the optional logmode setting.
        /// </summary>
        /// <returns>Log Handler, which should be used for the spawned process</returns>
        private LogHandler CreateExecutableLogHandler()
        {
            string logDirectory = this.config.LogDirectory;

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            LogHandler logAppender = this.config.LogHandler;
            logAppender.EventLogger = this;
            return logAppender;
        }

        public void LogEvent(string message)
        {
            if (this.shuttingdown)
            {
                // The Event Log service exits earlier.
                return;
            }

            try
            {
                this.EventLog.WriteEntry(message);
            }
            catch (Exception e)
            {
                Log.Error("Failed to log event in Windows Event Log: " + message + "; Reason: ", e);
            }
        }

        public void LogEvent(string message, EventLogEntryType type)
        {
            if (this.shuttingdown)
            {
                // The Event Log service exits earlier.
                return;
            }

            try
            {
                this.EventLog.WriteEntry(message, type);
            }
            catch (Exception e)
            {
                Log.Error("Failed to log event in Windows Event Log. Reason: ", e);
            }
        }

        void IServiceEventLog.WriteEntry(string message, EventLogEntryType type)
        {
            if (this.shuttingdown)
            {
                // The Event Log service exits earlier.
                return;
            }

            this.EventLog.WriteEntry(message, type);
        }

        private void LogInfo(string message)
        {
            this.LogEvent(message);
            Log.Info(message);
        }

        internal void RaiseOnStart(string[] args) => this.OnStart(args);

        internal void RaiseOnStop() => this.OnStop();

        protected override void OnStart(string[] args)
        {
            try
            {
                this.DoStart();
            }
            catch (Exception e)
            {
                Log.Error("Failed to start service.", e);
                throw;
            }
        }

        protected override void OnStop()
        {
            try
            {
                this.DoStop();
            }
            catch (Exception e)
            {
                Log.Error("Failed to stop service.", e);
                throw;
            }
        }

        protected override void OnShutdown()
        {
            try
            {
                this.shuttingdown = true;
                this.DoStop();
            }
            catch (Exception e)
            {
                Log.Error("Failed to shut down service.", e);
                throw;
            }
        }

        protected override void OnCustomCommand(int command)
        {
            if (command == 0x0000000F)
            {
                // SERVICE_CONTROL_PRESHUTDOWN
                this.Stop();
            }
        }

        private void DoStart()
        {
            this.envs = this.config.EnvironmentVariables;

            this.HandleFileCopies();

            // handle downloads
            List<Download> downloads = this.config.Downloads;
            Task[] tasks = new Task[downloads.Count];
            for (int i = 0; i < downloads.Count; i++)
            {
                Download download = downloads[i];
                string downloadMessage = $"Downloading: {download.From} to {download.To}. failOnError={download.FailOnError.ToString()}";
                this.LogInfo(downloadMessage);
                tasks[i] = download.PerformAsync();
            }

            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException e)
            {
                List<Exception> exceptions = new List<Exception>(e.InnerExceptions.Count);
                for (int i = 0; i < tasks.Length; i++)
                {
                    if (tasks[i].IsFaulted)
                    {
                        Download download = downloads[i];
                        string errorMessage = $"Failed to download {download.From} to {download.To}";
                        AggregateException exception = tasks[i].Exception!;
                        Log.Error(errorMessage, exception);

                        // TODO: move this code into the download logic
                        if (download.FailOnError)
                        {
                            exceptions.Add(new IOException(errorMessage, exception));
                        }
                    }
                }

                throw new AggregateException(exceptions);
            }

            try
            {
                string? prestartExecutable = this.config.PrestartExecutable;
                if (prestartExecutable != null)
                {
                    using Process process = this.StartProcess(prestartExecutable, this.config.PrestartArguments);
                    this.WaitForProcessToExit(process);
                    this.LogInfo($"Pre-start process '{process.Format()}' exited with code {process.ExitCode}.");
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            string startArguments = this.config.StartArguments ?? this.config.Arguments;

            this.LogInfo("Starting " + this.config.Executable);

            // Load and start extensions
            this.ExtensionManager.LoadExtensions();
            this.ExtensionManager.FireOnWrapperStarted();

            LogHandler executableLogHandler = this.CreateExecutableLogHandler();
            this.StartProcess(this.process, startArguments, this.config.Executable, executableLogHandler);
            this.ExtensionManager.FireOnProcessStarted(this.process);

            try
            {
                string? poststartExecutable = this.config.PoststartExecutable;
                if (poststartExecutable != null)
                {
                    using Process process = this.StartProcess(poststartExecutable, this.config.PoststartArguments, process =>
                    {
                        this.LogInfo($"Post-start process '{process.Format()}' exited with code {process.ExitCode}.");
                    });
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        /// <summary>
        /// Called when we are told by Windows SCM to exit.
        /// </summary>
        private void DoStop()
        {
            try
            {
                string? prestopExecutable = this.config.PrestopExecutable;
                if (prestopExecutable != null)
                {
                    using Process process = this.StartProcess(prestopExecutable, this.config.PrestopArguments);
                    this.WaitForProcessToExit(process);
                    this.LogInfo($"Pre-stop process '{process.Format()}' exited with code {process.ExitCode}.");
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            string? stopArguments = this.config.StopArguments;
            this.LogInfo("Stopping " + this.config.Id);
            this.orderlyShutdown = true;
            this.process.EnableRaisingEvents = false;

            if (stopArguments is null)
            {
                Log.Debug("ProcessKill " + this.process.Id);
                ProcessHelper.StopProcessTree(this.process, this.config.StopTimeout);
                this.ExtensionManager.FireOnProcessTerminated(this.process);
            }
            else
            {
                this.SignalPending();

                Process stopProcess = new Process();

                string stopExecutable = this.config.StopExecutable ?? this.config.Executable;

                // TODO: Redirect logging to Log4Net once https://github.com/kohsuke/winsw/pull/213 is integrated
                this.StartProcess(stopProcess, stopArguments, stopExecutable, null);

                Log.Debug("WaitForProcessToExit " + this.process.Id + "+" + stopProcess.Id);
                this.WaitForProcessToExit(this.process);
                this.WaitForProcessToExit(stopProcess);
            }

            try
            {
                string? poststopExecutable = this.config.PoststopExecutable;
                if (poststopExecutable != null)
                {
                    using Process process = this.StartProcess(poststopExecutable, this.config.PoststopArguments);
                    this.WaitForProcessToExit(process);
                    this.LogInfo($"Post-stop process '{process.Format()}' exited with code {process.ExitCode}.");
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            // Stop extensions
            this.ExtensionManager.FireBeforeWrapperStopped();

            if (this.shuttingdown && this.config.BeepOnShutdown)
            {
                Console.Beep();
            }

            Log.Info("Finished " + this.config.Id);
        }

        private void WaitForProcessToExit(Process process)
        {
            this.SignalPending();

            // A good interval is one-tenth of the wait hint but not less than 1 second and not more than 10 seconds.
            while (!process.WaitForExit(1_500))
            {
                this.SignalPending();
            }
        }

        /// <exception cref="MissingFieldException" />
        private void AcceptPreshutdown()
        {
            const string acceptedCommandsFieldName =
#if NETCOREAPP
                "_acceptedCommands";
#else
                "acceptedCommands";
#endif

            FieldInfo? acceptedCommandsField = typeof(ServiceBase).GetField(acceptedCommandsFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (acceptedCommandsField is null)
            {
                throw new MissingFieldException(nameof(ServiceBase), acceptedCommandsFieldName);
            }

            int acceptedCommands = (int)acceptedCommandsField.GetValue(this)!;
            acceptedCommands |= 0x00000100; // SERVICE_ACCEPT_PRESHUTDOWN
            acceptedCommandsField.SetValue(this, acceptedCommands);
        }

        private void SignalPending()
        {
            this.RequestAdditionalTime(15_000);
        }

        private void SignalStopped()
        {
            using ServiceManager scm = ServiceManager.Open();
            using Service sc = scm.OpenService(this.ServiceName, ServiceApis.ServiceAccess.QUERY_STATUS);

            sc.SetStatus(this.ServiceHandle, ServiceControllerStatus.Stopped);
        }

        private void StartProcess(Process processToStart, string arguments, string executable, LogHandler? logHandler)
        {
            // Define handler of the completed process
            void OnProcessCompleted(Process process)
            {
                string display = process.Format();

                if (this.orderlyShutdown)
                {
                    this.LogInfo($"Child process '{display}' terminated with code {process.ExitCode}.");
                }
                else
                {
                    Log.Warn($"Child process '{display}' finished with code {process.ExitCode}.");

                    // if we finished orderly, report that to SCM.
                    // by not reporting unclean shutdown, we let Windows SCM to decide if it wants to
                    // restart the service automatically
                    try
                    {
                        if (process.ExitCode == 0)
                        {
                            this.SignalStopped();
                        }
                    }
                    finally
                    {
                        Environment.Exit(process.ExitCode);
                    }
                }
            }

            // Invoke process and exit
            ProcessHelper.StartProcessAndCallbackForExit(
                processToStart: processToStart,
                executable: executable,
                arguments: arguments,
                envVars: this.envs,
                workingDirectory: this.config.WorkingDirectory,
                priority: this.config.Priority,
                onExited: OnProcessCompleted,
                logHandler: logHandler,
                hideWindow: this.config.HideWindow);
        }

        private Process StartProcess(string executable, string? arguments, Action<Process>? onExited = null)
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = this.config.WorkingDirectory,
            };

            Process process = Process.Start(info);

            if (onExited != null)
            {
                process.Exited += (sender, _) =>
                {
                    try
                    {
                        onExited((Process)sender!);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Unhandled exception in event handler.", e);
                    }
                };

                process.EnableRaisingEvents = true;
            }

            return process;
        }
    }
}
