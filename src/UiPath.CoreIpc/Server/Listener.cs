﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using UiPath.CoreIpc.Telemetry;

namespace UiPath.CoreIpc
{
    public class ListenerSettings
    {
        public ListenerSettings(string name) => Name = name;
        public byte ConcurrentAccepts { get; set; } = 5;
        public byte MaxReceivedMessageSizeInMegabytes { get; set; } = 2;
        public X509Certificate Certificate { get; set; }
        public string Name { get; }
        public TimeSpan RequestTimeout { get; set; } = Timeout.InfiniteTimeSpan;
        internal IServiceProvider ServiceProvider { get; set; }
        internal IDictionary<string, EndpointSettings> Endpoints { get; set; }
    }
    abstract class Listener : IDisposable
    {
        protected Listener(ListenerSettings settings)
        {
            Settings = settings;
            MaxMessageSize = settings.MaxReceivedMessageSizeInMegabytes * 1024 * 1024;
        }
        public string Name => Settings.Name;
        public ILogger Logger { get; private set; }
        public IServiceProvider ServiceProvider => Settings.ServiceProvider;
        public ITelemetryProvider TelemetryProvider;
        public ListenerSettings Settings { get; }
        public int MaxMessageSize { get; }
        public Task Listen(CancellationToken token)
        {
            Logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
            TelemetryProvider = ServiceProvider.GetService<ITelemetryProvider>();
            if (LogEnabled)
            {
                Log($"Starting listener {Name}...");
            }
            return Task.WhenAll(Enumerable.Range(1, Settings.ConcurrentAccepts).Select(async _ =>
            {
                while (!token.IsCancellationRequested)
                {
                    await AcceptConnection(token);
                }
            }));
        }
        protected abstract ServerConnection CreateServerConnection();
        async Task AcceptConnection(CancellationToken token)
        {
            var serverConnection = CreateServerConnection();
            try
            {
                await serverConnection.AcceptClient(token);
                serverConnection.Listen(token).LogException(Logger, Name);
            }
            catch (Exception ex)
            {
                serverConnection.Dispose();
                if (!token.IsCancellationRequested)
                {
                    Logger.LogException(ex, Settings.Name);
                }
            }
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }
            Settings.Certificate?.Dispose();
        }
        public void Dispose()
        {
            if (LogEnabled)
            {
                Log($"Stopping listener {Name}...");
            }
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        public void Log(string message) => Logger.LogInformation(message);
        public bool LogEnabled => Logger.Enabled();
    }
}