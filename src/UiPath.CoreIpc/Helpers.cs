﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
namespace UiPath.CoreIpc
{
    public static class Helpers
    {
        public const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        public static bool Enabled(this ILogger logger) => logger != null && logger.IsEnabled(LogLevel.Information);
        public static TDelegate MakeGenericDelegate<TDelegate>(this MethodInfo genericMethod, Type genericArgument) where TDelegate : Delegate =>
            (TDelegate)genericMethod.MakeGenericMethod(genericArgument).CreateDelegate(typeof(TDelegate));
        public static MethodInfo GetStaticMethod(this Type type, string name) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        public static MethodInfo GetInterfaceMethod(this Type type, string name)
        {
            var method = type.GetMethod(name, InstanceFlags) ?? 
                type.GetInterfaces().Select(t => t.GetMethod(name, InstanceFlags)).FirstOrDefault(m => m != null) ??
                throw new ArgumentOutOfRangeException(nameof(name), name, $"Method '{name}' not found in interface '{type}'.");
            if (method.IsGenericMethod)
            {
                throw new ArgumentOutOfRangeException(nameof(name), name, "Generic methods are not supported " + method);
            }
            return method;
        }
        public static IEnumerable<MethodInfo> GetInterfaceMethods(this Type type) =>
            type.GetMethods().Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()));
        public static object GetDefaultValue(this ParameterInfo parameter) => parameter switch
        {
            { HasDefaultValue: false } => null,
            { ParameterType: { IsValueType: true }, DefaultValue: null } => Activator.CreateInstance(parameter.ParameterType),
            _ => parameter.DefaultValue
        };
        public static ReadOnlyDictionary<TKey, TValue> ToReadOnlyDictionary<TKey, TValue>(this IDictionary<TKey, TValue> dictionary) => new(dictionary);
        public static void LogException(this ILogger logger, Exception ex, object tag)
        {
            var message = $"{tag} # {ex}";
            if (logger != null)
            {
                logger.LogError(message);
            }
            else
            {
                Trace.TraceError(message);
            }
        }
        public static void LogException(this Task task, ILogger logger, object tag) => task.ContinueWith(result => logger.LogException(result.Exception, tag), TaskContinuationOptions.NotOnRanToCompletion);
        /// <summary>
        /// Asynchronously waits for the task to complete, or for the cancellation token to be canceled.
        /// </summary>
        /// <param name="task">The task to wait for. May not be <c>null</c>.</param>
        /// <param name="cancellationToken">The cancellation token that cancels the wait.</param>
        public static Task WaitAsync(this Task task, CancellationToken cancellationToken)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));
            if (!cancellationToken.CanBeCanceled)
                return task;
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            return DoWaitAsync(task, cancellationToken);
        }
        private static async Task DoWaitAsync(Task task, CancellationToken cancellationToken)
        {
            using var cancelTaskSource = new CancellationTokenTaskSource(cancellationToken);
            await (await Task.WhenAny(task, cancelTaskSource.Task).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// Holds the task for a cancellation token, as well as the token registration. The registration is disposed when this instance is disposed.
    /// </summary>
    public readonly struct CancellationTokenTaskSource : IDisposable
    {
        private readonly IDisposable _registration;
        public CancellationTokenTaskSource(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            _registration = cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetCanceled(), tcs);
            Task = tcs.Task;
        }
        public Task Task { get; }
        /// <summary>
        /// Disposes the cancellation token registration, if any. Note that this may cause <see cref="Task"/> to never complete.
        /// </summary>
        public void Dispose() => _registration.Dispose();
    }
    public static class IOHelpers
    {
        internal const int HeaderLength = sizeof(int) + 1;
        internal static NamedPipeServerStream NewNamedPipeServerStream(string pipeName, PipeDirection direction, int maxNumberOfServerInstances, PipeTransmissionMode transmissionMode, PipeOptions options, Func<PipeSecurity> pipeSecurity)
        {
#if NET461
            return new(pipeName, direction, maxNumberOfServerInstances, transmissionMode, options, inBufferSize: 0, outBufferSize: 0, pipeSecurity());
#elif NET5_0_WINDOWS
            return NamedPipeServerStreamAcl.Create(pipeName, direction, maxNumberOfServerInstances, transmissionMode, options, inBufferSize: 0, outBufferSize: 0, pipeSecurity());
#else
            return new(pipeName, direction, maxNumberOfServerInstances, transmissionMode, options);
#endif
        }

        public static PipeSecurity LocalOnly(this PipeSecurity pipeSecurity) => pipeSecurity.Deny(WellKnownSidType.NetworkSid, PipeAccessRights.FullControl);

        public static PipeSecurity Deny(this PipeSecurity pipeSecurity, WellKnownSidType sid, PipeAccessRights pipeAccessRights) =>
            pipeSecurity.Deny(new SecurityIdentifier(sid, null), pipeAccessRights);

        public static PipeSecurity Deny(this PipeSecurity pipeSecurity, IdentityReference sid, PipeAccessRights pipeAccessRights)
        {
            pipeSecurity.SetAccessRule(new(sid, pipeAccessRights, AccessControlType.Deny));
            return pipeSecurity;
        }

        public static PipeSecurity Allow(this PipeSecurity pipeSecurity, WellKnownSidType sid, PipeAccessRights pipeAccessRights) =>
            pipeSecurity.Allow(new SecurityIdentifier(sid, null), pipeAccessRights);

        public static PipeSecurity Allow(this PipeSecurity pipeSecurity, IdentityReference sid, PipeAccessRights pipeAccessRights)
        {
            pipeSecurity.SetAccessRule(new(sid, pipeAccessRights, AccessControlType.Allow));
            return pipeSecurity;
        }

        public static PipeSecurity AllowCurrentUser(this PipeSecurity pipeSecurity, bool onlyNonAdmin = false)
        {
            using (var currentIdentity = WindowsIdentity.GetCurrent())
            {
                if (onlyNonAdmin && new WindowsPrincipal(currentIdentity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    return pipeSecurity;
                }
                pipeSecurity.Allow(currentIdentity.User, PipeAccessRights.ReadWrite|PipeAccessRights.CreateNewInstance);
            }
            return pipeSecurity;
        }

        public static bool PipeExists(string pipeName, int timeout = 1)
        {
            try
            {
                using (var client = new NamedPipeClientStream(pipeName))
                {
                    client.Connect(timeout);
                }
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.ToString());
            }
            return false;
        }

        internal static Task WriteMessage(this Stream stream, WireMessage message, CancellationToken cancellationToken = default)
        {
            var buffer = message.Data.GetBuffer();
            var totalLength = (int)message.Data.Length;
            buffer[0] = (byte)message.MessageType;
            var payloadLength = totalLength - HeaderLength;
            // https://github.com/dotnet/runtime/blob/85441ce69b81dfd5bf57b9d00ba525440b7bb25d/src/libraries/System.Private.CoreLib/src/System/BitConverter.cs#L133
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer.AsSpan(1, sizeof(int))), payloadLength);
            return stream.WriteAsync(buffer, 0, totalLength, cancellationToken);
        }
        internal static Task WriteBuffer(this Stream stream, byte[] buffer, CancellationToken cancellationToken) => 
            stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);

        internal static async Task<WireMessage> ReadMessage(this Stream stream, int maxMessageSize, CancellationToken cancellationToken = default)
        {
            var header = await stream.ReadBufferCore(sizeof(int)+1, cancellationToken);
            if (header.Length == 0)
            {
                return new();
            }
            var messageType = (MessageType)header[0];
            var length = BitConverter.ToInt32(header, 1);
            if(length > maxMessageSize)
            {
                throw new InvalidDataException($"Message too large. The maximum message size is {maxMessageSize/(1024*1024)} megabytes.");
            }
            var data = await stream.ReadBufferCore(length, cancellationToken);
            if (data.Length == 0)
            {
                return new();
            }
            return new(messageType, new MemoryStream(data));
        }

        public static async ValueTask<byte[]> ReadBufferCore(this Stream stream, int length, CancellationToken cancellationToken)
        {
            var bytes = new byte[length];
            int offset = 0;
            int remaining = length;
            while(remaining > 0)
            {
                var read = await stream.ReadAsync(bytes, offset, remaining, cancellationToken);
                if(read == 0)
                {
                    return Array.Empty<byte>();
                }
                remaining -= read;
                offset += read;
            }
            return bytes;
        }
        public static T Deserialize<T>(this ISerializer serializer, Stream binary) => (T)serializer.Deserialize(binary, typeof(T));
    }
    public static class Validator
    {
        public static void Validate(ServiceHostBuilder serviceHostBuilder)
        {
            foreach (var endpointSettings in serviceHostBuilder.Endpoints.Values)
            {
                endpointSettings.Validate();
            }
        }

        public static void Validate<TDerived, TInterface>(ServiceClientBuilder<TDerived, TInterface> builder) where TInterface : class where TDerived : ServiceClientBuilder<TDerived, TInterface>
            => Validate(typeof(TInterface), builder.CallbackContract);

        public static void Validate(params Type[] contracts)
        {
            foreach (var contract in contracts.Where(c => c != null))
            {
                if (!contract.IsInterface)
                {
                    throw new ArgumentOutOfRangeException(nameof(contract), "The contract must be an interface! " + contract);
                }
                foreach (var method in contract.GetInterfaceMethods())
                {
                    Validate(method);
                }
            }
        }

        private static void Validate(MethodInfo method)
        {
            var returnType = method.ReturnType;
            CheckMethod();
            var parameters = method.GetParameters();
            int streamCount = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                CheckMessageParameter(index, parameter);
                CheckCancellationToken(index, parameter);
                if (parameter.ParameterType == typeof(Stream))
                {
                    CheckStreamParameter();
                }
                else
                {
                    CheckDerivedStream(method, parameter.ParameterType);
                }
            }
            void CheckStreamParameter()
            {
                streamCount++;
                if (streamCount > 1)
                {
                    throw new ArgumentException($"Only one Stream parameter is allowed! {method}");
                }
                if (!method.ReturnType.IsGenericType)
                {
                    throw new ArgumentException($"Upload methods must return a value! {method}");
                }
            }
            void CheckMethod()
            {
                if (!typeof(Task).IsAssignableFrom(returnType))
                {
                    throw new ArgumentException($"Method does not return Task! {method}");
                }
                if (returnType.IsGenericType)
                {
                    var returnValueType = returnType.GenericTypeArguments[0];
                    if (returnValueType != typeof(Stream))
                    {
                        CheckDerivedStream(method, returnValueType);
                    }
                }
            }
            void CheckMessageParameter(int index, ParameterInfo parameter)
            {
                if (typeof(Message).IsAssignableFrom(parameter.ParameterType) && index != parameters.Length - 1 &&
                    parameters[parameters.Length - 1].ParameterType != typeof(CancellationToken))
                {
                    throw new ArgumentException($"The message must be the last parameter before the cancellation token! {method}");
                }
            }
            void CheckCancellationToken(int index, ParameterInfo parameter)
            {
                if (parameter.ParameterType == typeof(CancellationToken) && index != parameters.Length - 1)
                {
                    throw new ArgumentException($"The CancellationToken parameter must be the last! {method}");
                }
            }
        }

        private static void CheckDerivedStream(MethodInfo method, Type type)
        {
            if (typeof(Stream).IsAssignableFrom(type))
            {
                throw new ArgumentException($"Stream parameters must be typed as Stream! {method}");
            }
        }
    }
    public readonly struct ConcurrentDictionaryWrapper<TKey, TValue>
    {
        private readonly ConcurrentDictionary<TKey, TValue> _dictionary;
        private readonly Func<TKey, TValue> _valueFactory;
        public ConcurrentDictionaryWrapper(Func<TKey, TValue> valueFactory, int capacity = 31)
        {
            _dictionary = new(Environment.ProcessorCount, capacity);
            _valueFactory = valueFactory;
        }
        public TValue GetOrAdd(TKey key) => _dictionary.GetOrAdd(key, _valueFactory);
        public bool TryGetValue(TKey key, out TValue value) => _dictionary.TryGetValue(key, out value);
        public bool TryRemove(TKey key, out TValue value) => _dictionary.TryRemove(key, out value);
    }
    public readonly struct TimeoutHelper : IDisposable
    {
        private readonly CancellationTokenSource _timeoutCancellationSource;
        private readonly CancellationTokenSource _linkedCancellationSource;
        public TimeoutHelper(TimeSpan timeout, List<CancellationToken> cancellationTokens)
        {
            _timeoutCancellationSource = new CancellationTokenSource(timeout);
            cancellationTokens.Add(_timeoutCancellationSource.Token);
            _linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokens.ToArray());
        }
        private TimeoutHelper(TimeSpan timeout, CancellationToken token)
        {
            _timeoutCancellationSource = new CancellationTokenSource(timeout);
            _linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token, _timeoutCancellationSource.Token);
        }
        public Exception CheckTimeout(Exception exception, string message)
        {
            if (_timeoutCancellationSource.IsCancellationRequested)
            {
                return new TimeoutException(message + " timed out.", exception);
            }
            if (_linkedCancellationSource.IsCancellationRequested && exception is not TaskCanceledException)
            {
                return new TaskCanceledException(message, exception);
            }
            return exception;
        }
        public void ThrowTimeout(Exception exception, string message)
        {
            var newException = CheckTimeout(exception, message);
            if (newException != exception)
            {
                throw newException;
            }
        }
        public void Dispose()
        {
            _timeoutCancellationSource.Dispose();
            _linkedCancellationSource.Dispose();
        }
        public CancellationToken Token => _linkedCancellationSource.Token;
        public static TimeoutHelper Creaate(TimeSpan timeout, CancellationToken token) => new(timeout, token);
    }
}