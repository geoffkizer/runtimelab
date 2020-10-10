// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Quic.Implementations.MsQuic.Internal;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    /// <summary>
    ///     Class responsible for serving a socket for QUIC connections.
    /// </summary>
    internal abstract class QuicSocketContext
    {
        private static readonly Task _infiniteTimeoutTask = new TaskCompletionSource<int>().Task;

        private readonly EndPoint? _localEndPoint;
        private readonly EndPoint? _remoteEndPoint;
        private readonly bool _isServer;
        private readonly CancellationTokenSource _socketTaskCts;

        private TaskCompletionSource<int> _signalTcs =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _signalWanted;
        private bool _started;

        private Task? _backgroundWorkerTask;

        private readonly QuicReader _reader;
        private readonly QuicWriter _writer;

        private readonly SendContext _sendContext;
        private readonly RecvContext _recvContext;

        private long _currentTimeout = long.MaxValue;

        protected readonly Socket Socket = new Socket(SocketType.Dgram, ProtocolType.Udp);

        private readonly byte[] _sendBuffer = new byte[64 * 1024];
        private readonly byte[] _recvBuffer = new byte[64 * 1024];

        protected QuicSocketContext(EndPoint? localEndPoint, EndPoint? remoteEndPoint, bool isServer)
        {
            _localEndPoint = localEndPoint;
            _remoteEndPoint = remoteEndPoint;

            _isServer = isServer;

            _socketTaskCts = new CancellationTokenSource();

            _reader = new QuicReader(_recvBuffer);
            _writer = new QuicWriter(_sendBuffer);

            var sentPacketPool = new ObjectPool<SentPacket>(256);
            _sendContext = new SendContext(sentPacketPool);
            _recvContext = new RecvContext(sentPacketPool);

            SetupSocket(localEndPoint, remoteEndPoint);
        }

        private void SetupSocket(EndPoint? localEndPoint, EndPoint? remoteEndPoint)
        {
            if (Socket.AddressFamily == AddressFamily.InterNetwork)
            {
                Socket.DontFragment = true;
            }

            if (localEndPoint != null)
            {
                Socket.Bind(localEndPoint);
            }

            if (remoteEndPoint != null)
            {
                Socket.Connect(remoteEndPoint);
            }

            // TODO-RZ: Find out why I can't use RuntimeInformation when building inside .NET Runtime
#if FEATURE_QUIC_STANDALONE
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
#endif
            {
                // disable exception when client forcibly closes the socket.
                // https://stackoverflow.com/questions/38191968/c-sharp-udp-an-existing-connection-was-forcibly-closed-by-the-remote-host

                const int SIO_UDP_CONNRESET = -1744830452;
                Socket.IOControl(
                    (IOControlCode)SIO_UDP_CONNRESET,
                    new byte[] {0, 0, 0, 0},
                    null
                );
            }
        }

        public IPEndPoint LocalEndPoint => (IPEndPoint)Socket.LocalEndPoint!;

        internal void Start()
        {
            if (_backgroundWorkerTask != null)
            {
                return;
            }

            _started = true;
            _backgroundWorkerTask = Task.Run(BackgroundWorker);
        }

        protected void SignalStop()
        {
            _socketTaskCts.Cancel();

            // if never started also cleanup the socket, since the background worker will not do that
            if (!_started)
            {
                Dispose();
            }
        }

        protected void WaitUntilStop()
        {
            _backgroundWorkerTask?.Wait();
        }

        /// <summary>
        ///     Used to signal the thread that one of the connections has data to send.
        /// </summary>
        internal void Ping()
        {
            _signalWanted = true;
            _signalTcs.TrySetResult(0);
        }

        protected void Update(ManagedQuicConnection connection, QuicConnectionState previousState)
        {
            // TODO-RZ: I would like to have unbound loop there, but this might loop indefinitely
            for (int i = 0; i < 2; i++)
            {
                _writer.Reset(_sendBuffer);
                _sendContext.Timestamp = Timestamp.Now;
                _sendContext.SentPacket.Reset();
                connection.SendData(_writer, out var receiver, _sendContext);

                if (_writer.BytesWritten > 0)
                {
                    if (NetEventSource.IsEnabled)
                        NetEventSource.DatagramSent(connection, _writer.Buffer.Span.Slice(0, _writer.BytesWritten));

                    SendTo(_sendBuffer, _writer.BytesWritten, receiver!);
                }

                var newState = connection.ConnectionState;
                if (newState != previousState && OnConnectionStateChanged(connection, newState) ||
                    _writer.BytesWritten == 0)
                {
                    break;
                }

                previousState = newState;
            }
        }

        protected void Update(ManagedQuicConnection connection)
        {
            Update(connection, connection.ConnectionState);
        }

        protected void UpdateTimeout(long timestamp)
        {
            _currentTimeout = Math.Min(_currentTimeout, timestamp);
        }

        protected abstract ManagedQuicConnection? FindConnection(QuicReader reader, EndPoint sender);

        private void DoReceive(Memory<byte> datagram, EndPoint sender)
        {
            // process only datagrams big enough to contain valid QUIC packets
            if (datagram.Length < QuicConstants.MinimumPacketSize)
            {
                return;
            }

            _reader.Reset(datagram);

            var connection = FindConnection(_reader, sender);
            if (connection != null)
            {
                if (NetEventSource.IsEnabled) NetEventSource.DatagramReceived(connection, _reader.Buffer.Span);

                var previousState = connection.ConnectionState;
                _recvContext.Timestamp = Timestamp.Now;
                connection.ReceiveData(_reader, sender, _recvContext);

                if (connection.GetWriteLevel(_recvContext.Timestamp) != EncryptionLevel.None)
                {
                    // the connection has some data to send in response
                    Update(connection, previousState);
                }
                else
                {
                    // just check if the datagram changed connection state.
                    var newState = connection.ConnectionState;
                    if (newState != previousState)
                    {
                        OnConnectionStateChanged(connection, newState);
                    }
                }

                UpdateTimeout(connection.GetNextTimerTimestamp());
            }
        }

        private void DoSignal()
        {
            _signalWanted = false;
            OnSignal();
        }

        private void DoTimeout()
        {
            long now = Timestamp.Now;

            // The timer might not fire exactly on time, so we need to make sure it is not just below the
            // timer value so that the actual logic in Connection gets executed.
            Debug.Assert(Timestamp.GetMilliseconds(_currentTimeout - now) <= 5);
            now = Math.Max(now, _currentTimeout);

            // clear previous timeout
            _currentTimeout = long.MaxValue;
            OnTimeout(now);
        }

        protected abstract void OnSignal();

        protected abstract void OnTimeout(long now);

        /// <summary>
        ///     Called when a connections <see cref="ManagedQuicConnection.ConnectionState"/> changes.
        /// </summary>
        /// <param name="connection">The connection.</param>
        /// <param name="newState">The new state of the connection.</param>
        /// <returns>True if the processing of the connection should be stopped.</returns>
        protected abstract bool
            OnConnectionStateChanged(ManagedQuicConnection connection, QuicConnectionState newState);

        private int ReceiveFrom(byte[] buffer, out EndPoint sender)
        {
            if (_remoteEndPoint != null)
            {
                // use method without explicit address because we use connected socket

                sender = _remoteEndPoint!;
                return Socket.Receive(buffer);
                // return Socket.ReceiveFrom(buffer, SocketFlags.None, ref sender);
            }
            else
            {
                sender = _localEndPoint!;
                return Socket.ReceiveFrom(buffer, ref sender);
            }
        }

        private void SendTo(byte[] buffer, int size, EndPoint receiver)
        {
            if (_remoteEndPoint != null)
            {
                // Debug.Assert(Equals(receiver, _remoteEndPoint));
                Socket.Send(buffer.AsSpan(0, size), SocketFlags.None, out _);
                // Socket.SendTo(buffer, 0, size, SocketFlags.None, _remoteEndPoint);
            }
            else
            {
                Socket.SendTo(buffer, 0, size, SocketFlags.None, receiver);
            }
        }

        protected class ReceiveOperationAsyncSocketArgs : SocketAsyncEventArgs
        {
            public ResettableCompletionSource<SocketReceiveFromResult> CompletionSource { get; } =
                new ResettableCompletionSource<SocketReceiveFromResult>();

            protected override void OnCompleted(SocketAsyncEventArgs e)
            {
                CompletionSource.Complete(
                    new SocketReceiveFromResult()
                    {
                        ReceivedBytes = e.SocketError == SocketError.Success ? e.BytesTransferred : 0,
                        RemoteEndPoint = e.RemoteEndPoint!
                    });
            }
        }

        protected ReceiveOperationAsyncSocketArgs SocketReceiveEventArgs { get; } =
            new ReceiveOperationAsyncSocketArgs();

        private bool ReceiveFromAsync(ReceiveOperationAsyncSocketArgs args)
        {
            if (_remoteEndPoint != null)
            {
                // we are using connected sockets -> use Receive(...). We also have to set the expected
                // receiver address so that the receiving code later uses it

                // Debug.Assert(Socket.Connected);
                args.RemoteEndPoint = _remoteEndPoint!;
                return Socket.ReceiveAsync(args);
                // return Socket.ReceiveFromAsync(args);
            }

            Debug.Assert(_isServer);
            Debug.Assert(_localEndPoint != null);

            args.RemoteEndPoint = _localEndPoint!;
            return Socket.ReceiveFromAsync(args);
        }

        private ValueTask<SocketReceiveFromResult> ReceiveFromAsync(byte[] buffer, EndPoint sender,
            CancellationToken token)
        {
            SocketReceiveEventArgs.SetBuffer(buffer);

            // TODO-RZ: utilize cancellation token
            if (ReceiveFromAsync(SocketReceiveEventArgs))
            {
                return SocketReceiveEventArgs.CompletionSource.GetValueTask();
            }

            // operation completed synchronously
            return new ValueTask<SocketReceiveFromResult>(
                new SocketReceiveFromResult
                {
                    ReceivedBytes = SocketReceiveEventArgs.BytesTransferred,
                    RemoteEndPoint = SocketReceiveEventArgs.RemoteEndPoint!
                });
        }

        private async Task BackgroundWorker()
        {
            var token = _socketTaskCts.Token;

            Task<SocketReceiveFromResult>? socketReceiveTask = null;

            // TODO-RZ: allow timers for multiple connections on server
            long lastAction = long.MinValue;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool doTimeout;
                    long now;
                    while (!(doTimeout = _currentTimeout <= (now = Timestamp.Now)) &&
                           !_signalWanted)
                    {
                        if (socketReceiveTask != null)
                        {
                            // there is still a pending task from last sleep
                            if (!socketReceiveTask.IsCompleted)
                            {
                                break;
                            }

                            var result = await socketReceiveTask.ConfigureAwait(false);
                            DoReceive(_recvBuffer.AsMemory(0, result.ReceivedBytes), result.RemoteEndPoint);
                            lastAction = now;
                            // discard the completed task.
                            socketReceiveTask = null;
                        }
                        else
                        {
                            // no pending async task, receive synchronously if there is some data
                            if (!Socket.Poll(0, SelectMode.SelectRead))
                            {
                                break;
                            }

                            int result = ReceiveFrom(_recvBuffer, out var remoteEp);
                            DoReceive(_recvBuffer.AsMemory(0, result), remoteEp);
                            lastAction = now;
                        }
                    }

                    if (doTimeout)
                    {
                        DoTimeout();
                        lastAction = now;
                    }

                    if (_signalWanted)
                    {
                        DoSignal();
                        lastAction = now;
                    }

                    const int asyncWaitThreshold = 20;
                    if (Timestamp.GetMilliseconds(now - lastAction) > asyncWaitThreshold)
                    {
                        // there has been no action for some time, stop consuming CPU and wait until an event wakes us
                        int timeoutLength = (int)Timestamp.GetMilliseconds(_currentTimeout - now);
                        Task timeoutTask = _currentTimeout != long.MaxValue
                            ? Task.Delay(timeoutLength, CancellationToken.None)
                            : _infiniteTimeoutTask;

                        // update the recv task only if there is no outstanding async recv
                        socketReceiveTask ??= ReceiveFromAsync(_recvBuffer, _localEndPoint!,
                            CancellationToken.None).AsTask();

                        _signalTcs = new TaskCompletionSource<int>();
                        Task signalTask = _signalTcs.Task;

                        if (_signalWanted) // guard against race condition that would deadlock the wait
                        {
                            _signalTcs.TrySetResult(0);
                        }

                        await Task.WhenAny(timeoutTask, socketReceiveTask, signalTask).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Error(this, e);
                OnException(e);
            }

            // cleanup everything
            Dispose();
        }

        protected abstract void OnException(Exception e);

        /// <summary>
        ///     Detaches the given connection from this context, the connection will no longer be updated from the
        ///     thread running at this socket.
        /// </summary>
        /// <param name="connection"></param>
        protected internal abstract void DetachConnection(ManagedQuicConnection connection);

        internal class ContextBase
        {
            public ContextBase(ObjectPool<SentPacket> sentPacketPool) => SentPacketPool = sentPacketPool;

            /// <summary>
            ///     Timestamp when the next tick of internal processing was requested.
            /// </summary>
            internal long Timestamp { get; set; }

            internal ObjectPool<SentPacket> SentPacketPool { get; }

            internal void ReturnPacket(SentPacket packet)
            {
                SentPacketPool.Return(packet);
            }
        }

        internal sealed class RecvContext : ContextBase
        {
            /// <summary>
            ///     Flag whether TLS handshake should be incremented at the end of packet processing, perhaps due to
            ///     having received crypto data.
            /// </summary>
            internal bool HandshakeWanted { get; set; }

            public RecvContext(ObjectPool<SentPacket> sentPacketPool) : base(sentPacketPool)
            {
            }
        }

        internal sealed class SendContext : ContextBase
        {
            /// <summary>
            ///     Data about next packet that is to be sent.
            /// </summary>
            internal SentPacket SentPacket { get; private set; } = new SentPacket();

            internal void StartNextPacket()
            {
                SentPacket = SentPacketPool.Rent();
            }

            public SendContext(ObjectPool<SentPacket> sentPacketPool) : base(sentPacketPool)
            {
            }
        }

        private void Dispose()
        {
            Socket.Dispose();
        }
    }
}
