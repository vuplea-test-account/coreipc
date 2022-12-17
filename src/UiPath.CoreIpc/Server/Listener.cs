﻿namespace UiPath.CoreIpc;
public class ListenerSettings
{
    public ListenerSettings(string name) => Name = name;
    public byte ConcurrentAccepts { get; set; } = 5;
    public byte MaxReceivedMessageSizeInMegabytes { get; set; } = 2;
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
    public ListenerSettings Settings { get; }
    public int MaxMessageSize { get; }
    public Task Listen(CancellationToken token)
    {
        Logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        if (LogEnabled)
        {
            Log($"Starting listener {Name}...");
        }
        var concurrentAccepts = Settings.ConcurrentAccepts;
        var accepts = new Task[concurrentAccepts];
        for (int index = 0; index < concurrentAccepts; index++)
        {
            accepts[index] = AcceptLoop(token);
        }
        return Task.WhenAll(accepts);
        async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var serverConnection = CreateServerConnection();
                try
                {
                    var network = await serverConnection.AcceptClient(token);
                    serverConnection.Listen(network, token).LogException(Logger, Name);
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
        }
    }
    protected abstract ServerConnection CreateServerConnection();
    protected virtual void Dispose(bool disposing) { }
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
    internal void SetValues(IServiceProvider serviceProvider, Dictionary<string, EndpointSettings> endpoints)
    {
        Settings.ServiceProvider = serviceProvider;
        Settings.Endpoints = endpoints;
    }
    public bool LogEnabled => Logger.Enabled();
}