﻿namespace UiPath.CoreIpc;
public interface IClient
{
    TCallbackInterface GetCallback<TCallbackInterface>(Type callbackContract) where TCallbackInterface : class;
    void Impersonate(Action action);
}
abstract class ServerConnection : IClient, IDisposable
{
    private readonly ConcurrentDictionary<Type, object> _callbacks = new();
    protected readonly Listener _listener;
    private Connection _connection;
    protected ServerConnection(Listener listener) => _listener = listener;
    public ILogger Logger => _listener.Logger;
    public ListenerSettings Settings => _listener.Settings;
    public abstract Task<Stream> AcceptClient(CancellationToken cancellationToken);
    public virtual void Impersonate(Action action) => action();
    TCallbackInterface IClient.GetCallback<TCallbackInterface>(Type callbackContract) where TCallbackInterface : class
    {
        if (callbackContract == null)
        {
            throw new InvalidOperationException($"Callback contract mismatch. Requested {typeof(TCallbackInterface)}, but it's not configured.");
        }
#if !NET461
        return (TCallbackInterface)_callbacks.GetOrAdd(callbackContract, static(callback, server) => server.CreateCallback<TCallbackInterface>(callback), this);
#else
        return (TCallbackInterface)_callbacks.GetOrAdd(callbackContract, CreateCallback<TCallbackInterface>);
#endif
    }
    TCallbackInterface CreateCallback<TCallbackInterface>(Type callbackContract) where TCallbackInterface : class
    {
        if (!typeof(TCallbackInterface).IsAssignableFrom(callbackContract))
        {
            throw new ArgumentException($"Callback contract mismatch. Requested {typeof(TCallbackInterface)}, but it's {callbackContract}.");
        }
        if (_listener.LogEnabled)
        {
            _listener.Log($"Create callback {callbackContract} {_listener.Name}");
        }
        return new ServiceClient<TCallbackInterface>(Settings.RequestTimeout, Logger, _ => _connection).CreateProxy();
    }
    public async Task Listen(Stream network, CancellationToken cancellationToken)
    {
        _connection = new(network, Logger, _listener.Name, _listener.MaxMessageSize);
        _connection.SetServer(Settings, this);
        // close the connection when the service host closes
        using (cancellationToken.UnsafeRegister(state => ((Connection)state).Dispose(), _connection))
        {
            await _connection.Listen();
        }
    }
    protected virtual void Dispose(bool disposing){}
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}