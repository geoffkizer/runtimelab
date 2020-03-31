#nullable enable

using System.Diagnostics;
using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.OpenSsl;
using System.Runtime.InteropServices;

namespace System.Net.Quic.Implementations.Managed
{
    /// <summary>
    ///     Class encapsulating TLS related logic and interop.
    /// </summary>
    internal class Tls : IDisposable
    {
        private static readonly int _managedInterfaceIndex =
            Interop.OpenSslQuic.CryptoGetExNewIndex(Interop.OpenSslQuic.CRYPTO_EX_INDEX_SSL, 0, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        private static unsafe OpenSslQuicMethods.NativeCallbacks _callbacks = new OpenSslQuicMethods.NativeCallbacks
        {
            setEncryptionSecrets =
                Marshal.GetFunctionPointerForDelegate(
                    new OpenSslQuicMethods.SetEncryptionSecretsFunc(SetEncryptionSecretsImpl)),
            addHandshakeData =
                Marshal.GetFunctionPointerForDelegate(
                    new OpenSslQuicMethods.AddHandshakeDataFunc(AddHandshakeDataImpl)),
            flushFlight =
                Marshal.GetFunctionPointerForDelegate(new OpenSslQuicMethods.FlushFlightFunc(FlushFlightImpl)),
            sendAlert = Marshal.GetFunctionPointerForDelegate(new OpenSslQuicMethods.SendAlertFunc(SendAlertImpl))
        };

        private readonly IntPtr _ssl;

        private TransportParameters? _remoteTransportParams;

        public Tls(GCHandle handle)
        {
            _ssl = Interop.OpenSslQuic.SslCreate();
            Debug.Assert(handle.Target is ManagedQuicConnection);
            Interop.OpenSslQuic.SslSetQuicMethod(_ssl, ref Callbacks);

            // add the callback as contextual data so we can retrieve it inside the callback
            Interop.OpenSslQuic.SslSetExData(_ssl, _managedInterfaceIndex, GCHandle.ToIntPtr(handle));

            Interop.OpenSslQuic.SslCtrl(_ssl, SslCtrlCommand.SetMinProtoVersion, (long)OpenSslTlsVersion.Tls13,
                IntPtr.Zero);
            Interop.OpenSslQuic.SslCtrl(_ssl, SslCtrlCommand.SetMaxProtoVersion, (long)OpenSslTlsVersion.Tls13,
                IntPtr.Zero);
        }

        private static ref OpenSslQuicMethods.NativeCallbacks Callbacks => ref _callbacks;

        internal bool IsHandshakeFinishhed => Interop.OpenSslQuic.SslIsInInit(_ssl) == 0;

        public void Dispose()
        {
            // call SslSetQuicMethod(ssl, null) to stop callbacks being called
            // Interop.OpenSslQuic.SslSetQuicMethod(ssl, ref Unsafe.AsRef<OpenSslQuicMethods.NativeCallbacks>(null));
            Interop.OpenSslQuic.SslFree(_ssl);
        }

        internal static EncryptionLevel ToManagedEncryptionLevel(OpenSslEncryptionLevel level)
        {
            return level switch
            {
                OpenSslEncryptionLevel.Initial => EncryptionLevel.Initial,
                OpenSslEncryptionLevel.EarlyData => EncryptionLevel.EarlyData,
                OpenSslEncryptionLevel.Handshake => EncryptionLevel.Handshake,
                OpenSslEncryptionLevel.Application => EncryptionLevel.Application,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
        }

        private static OpenSslEncryptionLevel ToOpenSslEncryptionLevel(EncryptionLevel level)
        {
            var osslLevel = level switch
            {
                EncryptionLevel.Initial => OpenSslEncryptionLevel.Initial,
                EncryptionLevel.Handshake => OpenSslEncryptionLevel.Handshake,
                EncryptionLevel.EarlyData => OpenSslEncryptionLevel.EarlyData,
                EncryptionLevel.Application => OpenSslEncryptionLevel.Application,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
            return osslLevel;
        }

        private static ManagedQuicConnection GetCallbackInterface(IntPtr ssl)
        {
            var addr = Interop.OpenSslQuic.SslGetExData(ssl, _managedInterfaceIndex);
            var callback = (ManagedQuicConnection)GCHandle.FromIntPtr(addr).Target!;

            return callback;
        }

        internal int OnDataReceived(EncryptionLevel level, ReadOnlySpan<byte> data)
        {
            return Interop.OpenSslQuic.SslProvideQuicData(_ssl, ToOpenSslEncryptionLevel(level), data);
        }

        internal unsafe void Init(string? cert, string? privateKey, bool isServer,
            TransportParameters localTransportParams)
        {
            if (cert != null)
                Interop.OpenSslQuic.SslUseCertificateFile(_ssl, cert, SslFiletype.Pem);
            if (privateKey != null)
                Interop.OpenSslQuic.SslUsePrivateKeyFile(_ssl, privateKey, SslFiletype.Pem);

            if (isServer)
            {
                Interop.OpenSslQuic.SslSetAcceptState(_ssl);
            }
            else
            {
                Interop.OpenSslQuic.SslSetConnectState(_ssl);
                // TODO-RZ get hostname
                Interop.OpenSslQuic.SslSetTlsExHostName(_ssl, "localhost:2000");
            }

            // init transport parameters
            byte[] buffer = new byte[1024];
            var writer = new QuicWriter(buffer);
            TransportParameters.Write(writer, isServer, localTransportParams);
            fixed (byte* pData = buffer)
            {
                // TODO-RZ: check return value == 1
                Interop.OpenSslQuic.SslSetQuicTransportParams(_ssl, pData, new IntPtr(writer.BytesWritten));
            }
        }

        internal EncryptionLevel GetWriteLevel()
        {
            return ToManagedEncryptionLevel(Interop.OpenSslQuic.SslQuicWriteLevel(_ssl));
        }

        internal SslError DoHandshake()
        {
            if (IsHandshakeFinishhed)
                return SslError.None;

            int status = Interop.OpenSslQuic.SslDoHandshake(_ssl);
            if (status < 0)
            {
                return (SslError)Interop.OpenSslQuic.SslGetError(_ssl, status);
            }

            return SslError.None;
        }

        internal unsafe TransportParameters GetPeerTransportParameters(bool isServer)
        {
            if (_remoteTransportParams == null)
            {
                byte[] buffer = new byte[1024];
                byte* data;
                IntPtr length;
                Interop.OpenSslQuic.SslGetPeerQuicTransportParams(_ssl, out data, out length);

                new Span<byte>(data, length.ToInt32()).CopyTo(buffer);
                var reader = new QuicReader(new ArraySegment<byte>(buffer, 0, length.ToInt32()));
                if (!TransportParameters.Read(reader, !isServer, out _remoteTransportParams))
                {
                    throw new InvalidOperationException("Failed to get peers transport params");
                }
            }

            return _remoteTransportParams!;
        }

        private static unsafe int SetEncryptionSecretsImpl(IntPtr ssl, OpenSslEncryptionLevel level, byte* readSecret,
            byte* writeSecret, UIntPtr secretLen)
        {
            var callback = GetCallbackInterface(ssl);

            var readS = new ReadOnlySpan<byte>(readSecret, (int)secretLen.ToUInt32());
            var writeS = new ReadOnlySpan<byte>(writeSecret, (int)secretLen.ToUInt32());

            return callback.HandleSetEncryptionSecrets(ToManagedEncryptionLevel(level), readS, writeS);
        }

        private static unsafe int AddHandshakeDataImpl(IntPtr ssl, OpenSslEncryptionLevel level, byte* data,
            UIntPtr len)
        {
            var callback = GetCallbackInterface(ssl);

            var span = new ReadOnlySpan<byte>(data, (int)len.ToUInt32());

            return callback.HandleAddHandshakeData(ToManagedEncryptionLevel(level), span);
        }

        private static int FlushFlightImpl(IntPtr ssl)
        {
            var callback = GetCallbackInterface(ssl);

            return callback.HandleFlush();
        }

        private static int SendAlertImpl(IntPtr ssl, OpenSslEncryptionLevel level, byte alert)
        {
            var callback = GetCallbackInterface(ssl);

            return callback.HandleSendAlert(ToManagedEncryptionLevel(level), (TlsAlert)alert);
        }
    }
}
