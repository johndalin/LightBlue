﻿using System;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using LightBlue.Host.Stub;
using LightBlue.Infrastructure;
using LightBlue.MultiHost.ViewModel;
using System.Diagnostics;

namespace LightBlue.MultiHost.Runners
{
    class ThreadRunner : IRunner
    {
        private readonly Role _role;
        private readonly string _assemblyFilePath;
        private readonly string _configurationFilePath;
        private readonly string _serviceDefinitionFilePath;
        private readonly string _roleName;
        private readonly TaskCompletionSource<object> _started = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> _completed = new TaskCompletionSource<object>();
        private Thread _thread;
        private LogicalCallContextTraceListener _traceListener;
        private HostStub2 _hostStub;

        public Task Started { get { return _started.Task; } }
        public Task Completed { get { return _completed.Task; } }

        public ThreadRunner(Role role,
            string assemblyPath,
            string configurationFilePath,
            string serviceDefinitionFilePath,
            string roleName)
        {
            _role = role;
            _assemblyFilePath = assemblyPath;
            _configurationFilePath = configurationFilePath;
            _serviceDefinitionFilePath = serviceDefinitionFilePath;
            _roleName = roleName;

            _assemblyFilePath = Path.Combine(ThreadRunnerAssemblyCache.AssemblyCacheFolder, Path.GetFileName(_assemblyFilePath));
        }

        public void Start()
        {
            _thread = new Thread(StartInternal) { IsBackground = true, Name = _role.Title + "Thread" };
            _thread.Start();
        }

        class LogicalCallContextTraceListener : TraceListener
        {
            private const string Key = "LogicalCallContextTraceListenerKey";

            public static bool IsInitialized { get { return CallContext.LogicalGetData(Key) != null; } }

            public static LogicalCallContextTraceListener Current
            {
                get
                {
                    if (!IsInitialized) CallContext.LogicalSetData(Key, new LogicalCallContextTraceListener());
                    return (LogicalCallContextTraceListener)CallContext.LogicalGetData(Key);
                }
            }

            public event Action<string> TraceWrite;
            public event Action<string> TraceWriteLine;

            public override void Write(string message)
            {
                if (IsInitialized && Current == this)
                {
                    var h = TraceWrite;
                    if (h != null) h(message);
                }
            }

            public override void WriteLine(string message)
            {
                if (IsInitialized && Current == this)
                {
                    var h = TraceWriteLine;
                    if (h != null) TraceWriteLine(message);
                }
            }
        }

        public class HostStub2
        {
            private readonly object gate = new object();
            private CancellationTokenSource _cancellationTokenSource;

            public void Run(
                string workerRoleAssembly,
                string configurationPath,
                string serviceDefinitionPath,
                string roleName,
                bool useHostedStorage)
            {
                var roleDirectory = Path.GetDirectoryName(workerRoleAssembly);

                if (string.IsNullOrWhiteSpace(roleDirectory))
                {
                    throw new ArgumentException("The worker role assembly must be in a specified directory.");
                }
                Directory.SetCurrentDirectory(roleDirectory);


                lock (gate)
                {
                    // as hoststub is called asynchronously, it it possible that shutdown has already been requested
                    // by the time we reach this point.
                    if (_cancellationTokenSource != null && _cancellationTokenSource.IsCancellationRequested) return;

                    var getMethod = typeof(LightBlueThreadControl).GetMethod("get_CancellationTokenSource", BindingFlags.NonPublic | BindingFlags.Static);
                    _cancellationTokenSource = (CancellationTokenSource)getMethod.Invoke(null, null);
                }

                var runner = new HostRunner();
                runner.Run(workerRoleAssembly, configurationPath, serviceDefinitionPath, roleName, useHostedStorage);
            }

            public void RequestShutdown()
            {
                lock (gate)
                {
                    if (_cancellationTokenSource == null)
                    {
                        _cancellationTokenSource = new CancellationTokenSource();
                        _cancellationTokenSource.Cancel();
                    }
                    else
                    {
                        _cancellationTokenSource.Cancel();
                    }
                }
            }
        }

        private void StartInternal()
        {
            ConfigurationManipulation.RemoveAzureTraceListenerFromConfiguration(_configurationFilePath);
            //CopyStubAssemblyToRoleDirectory(_appDomainSetup.ApplicationBase, _role);
            _hostStub = new HostStub2();

            if (LogicalCallContextTraceListener.IsInitialized)
                throw new InvalidOperationException("There is already a trace listener configured on this logical call context");
            _traceListener = LogicalCallContextTraceListener.Current;

            Action<string> twh = m => _role.TraceWrite(m);
            Action<string> twlh = m => _role.TraceWriteLine(m);
            _traceListener.TraceWrite += twh;
            _traceListener.TraceWriteLine += twlh;

            Trace.Listeners.Add(_traceListener);

            //AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => TryResolveAssembly(_assemblyFilePath, args);

            try
            {
                _started.SetResult(new object());
                _role.TraceWriteLine("Role started in thread: " + Thread.CurrentThread.ManagedThreadId + " : " + Thread.CurrentThread.Name);
                _hostStub.Run(_assemblyFilePath, _configurationFilePath, _serviceDefinitionFilePath, _roleName, false);
            }
            catch (Exception ex)
            {
                _role.TraceWriteLine(ex.ToString());
            }
            finally
            {
                _traceListener.TraceWrite -= twh;
                _traceListener.TraceWriteLine -= twlh;
                _completed.SetResult(new object());
            }
        }

        

        public void Dispose()
        {
            _role.TraceWriteLine("Request thread shutdown...");
            _hostStub.RequestShutdown();

            if (Completed.Wait(TimeSpan.FromSeconds(30)))
            {
                _role.TraceWriteLine("Thread shutdown sucessful.");
            }
            else
            {
                _role.TraceWriteLine("Thread shutdown failed to complete within 30 seconds.");
                _role.TraceWriteLine("Aborting: " + _thread.Name);
                try
                {
                    _thread.Abort();
                    while (!Completed.Wait(TimeSpan.FromSeconds(5)))
                    {
                        _role.TraceWriteLine("Waiting for thread to be aborted...");
                    }
                    _role.TraceWriteLine("Thread aborted.");
                }
                catch (Exception ex)
                {
                    _role.TraceWriteLine("Could not abort thread: " + ex.Message);
                }
            }

            //Trace.Listeners.Remove(_traceListener);
        }

        public static void CopyStubAssemblyToRoleDirectory(string applicationBase, Role role)
        {
            var destinationHostStubPath = Path.Combine(applicationBase, Path.GetFileName(typeof(HostStub).Assembly.Location));
            try
            {
                File.Copy(typeof(HostStub).Assembly.Location, destinationHostStubPath, true);
            }
            catch (IOException)
            {
                role.TraceWriteLine("Could not copy Host Stub. Assuming this is because it already exists and continuing.");
            }
        }

    }
}
