using System.Diagnostics;
using System.Net.Quic.Implementations.Managed.Internal.Frames;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    /// <summary>
    ///     ///     Wrapper around the currently used QUIC transport parameters.
    /// </summary>
    internal class TransportParameters
    {
        internal const ulong MinimumPacketSize = 1200;
        internal const ulong MaxAckDelayExponent = 20;
        internal const ulong MaxMaxAckDelay = 1 << 14;

        internal const ulong DefaultMaxPacketSize = 65527;
        internal const ulong DefaultAckDelayExponent = 3;
        internal const ulong DefaultMaxAckDelay = 25;

        /// <summary>
        ///     Value of the destination connection ID field from the first Initial packet sent by the client. This transport
        ///     parameter is only sent by a server. This is the same value sent in the "Original Destination Connection ID" field
        ///     of a retry packet. Server MUST include this parameter if it sent a Retry packet.
        /// </summary>
        internal ConnectionId? OriginalConnectionId { get; set; }

        /// <summary>
        ///     The max idle timeout in milliseconds. Value 0 means that the endpoint wishes to disable the timeout.
        /// </summary>
        internal ulong MaxIdleTimeout { get; set; }

        /// <summary>
        ///     A token used in verifying a stateless reset. This parameter may only be sent by a server. Server that does not send
        ///     this parameter cannot use stateless reset for the connection ID negotiated during the handshake.
        /// </summary>
        internal StatelessResetToken? StatelessResetToken { get; set; }

        /// <summary>
        ///     The maximum limit on the packet size that the endpoint is willing to receive. Packets larger than this threshold
        ///     may be dropped by the endpoint. Values below 1200 are invalid, default value is maximum permitted UDP payload of
        ///     65527. This limit applies only to protected packets.
        /// </summary>
        internal ulong MaxPacketSize { get; set; } = DefaultMaxPacketSize;

        /// <summary>
        ///     The initial value for the maximum amount of data that can be sent on the connection. This is equivalent to sending
        ///     a MAX_DATA immediately after completing the handshake.
        /// </summary>
        internal ulong InitialMaxData { get; set; }

        /// <summary>
        ///     Initial flow control limit for locally-initiated bidirectional streams. This limit applies to newly created
        ///     bidirectional streams opened by the endpoint that receives the transport parameter.
        /// </summary>
        internal ulong InitialMaxStreamDataBidiLocal { get; set; }

        /// <summary>
        ///     Initial flow control limit for peer-initiated bidirectional streams. This limit applies to newly created
        ///     bidirectional streams opened by the endpoint that receives the transport parameter.
        /// </summary>
        internal ulong InitialMaxStreamDataBidiRemote { get; set; }

        /// <summary>
        ///     Initial flow control limit for peer-initiated unidirectional streams. This limit applies to newly created
        ///     unidirectional streams opened by the endpoint that receives the transport parameter.
        /// </summary>
        internal ulong InitialMaxStreamDataUni { get; set; }

        /// <summary>
        ///     Initial maximum number of bidirectional streams the peer may initiate. If this parameter is 0, the peer cannot open
        ///     bidirectional streams until a MAX_STREAMS frame is sent.
        /// </summary>
        internal ulong InitialMaxStreamsBidi { get; set; }

        /// <summary>
        ///     Initial maximum number of unidirectional streams the peer may initiate. If this parameter is 0, the peer cannot
        ///     open unidirectional streams until a MAX_STREAMS frame is sent.
        /// </summary>
        internal ulong InitialMaxStreamsUni { get; set; }

        /// <summary>
        ///     Parameter used to encode the ACK Delay field in the ACK frame. The default value is 3 (indicating a multiplier of
        ///     8). Values above 20 are invalid.
        /// </summary>
        internal ulong AckDelayExponent { get; set; } = DefaultAckDelayExponent;

        /// <summary>
        ///     Maximum delay in milliseconds by which the endpoint will delay sending acknowledgments. This value SHOULD include
        ///     the receiver's expected delays in alarms firing. Default value is 25 milliseconds. Values of 2^14 or greater are
        ///     invalid.
        /// </summary>
        internal ulong MaxAckDelay { get; set; } = DefaultMaxAckDelay;

        /// <summary>
        ///     If true, the endpoint does not support active connection migration. The peer MUST NOT send any packets (including
        ///     probing) from any other address than the one used to perform the handshake.
        /// </summary>
        internal bool DisableActiveMigration { get; set; }

        /// <summary>
        ///     Used to effect a change in server address at the end of the handshake. This transport parameter is only sent by the
        ///     server.
        /// </summary>
        internal PreferredAddress? PreferredAddress { get; set; }

        /// <summary>
        ///     The maximum number of connection IDs from the peer that an endpoint is willing to store. This value includes the
        ///     connection ID received during the handshake, that received in the <see cref="PreferredAddress" /> parameter, and
        ///     those received in NEW_CONNECTION_ID frames. Unless a zero-length connection ID is being used, the value MUST be no
        ///     less than 2. When a zero-length connection ID is being used, this parameter must not be sent.
        /// </summary>
        internal ulong ActiveConnectionIdLimit { get; set; }

        private static bool IsServerOnlyParameter(TransportParameterName name) =>
            name switch
            {
                TransportParameterName.OriginalConnectionId => true,
                TransportParameterName.StatelessResetToken => true,
                TransportParameterName.PreferredAddress => true,
                _ => false
            };

        internal static bool Read(QuicReader reader, bool isServer, out TransportParameters? parameters)
        {
            parameters = new TransportParameters();

            while (reader.BytesLeft > 0)
            {
                if (!reader.TryReadTranportParameterName(out TransportParameterName name) ||
                    !isServer && IsServerOnlyParameter(name) ||
                    !reader.TryReadLengthPrefixedSpan(out var data))
                {
                    // encoding failure
                    goto Error;
                }

                ulong varIntValue;
                switch (name)
                {
                    case TransportParameterName.OriginalConnectionId:
                        parameters.OriginalConnectionId = new ConnectionId(data.ToArray());
                        break;
                    case TransportParameterName.MaxIdleTimeout:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.MaxIdleTimeout = varIntValue;
                        break;
                    case TransportParameterName.StatelessResetToken:
                        if (data.Length != Frames.StatelessResetToken.Length) goto Error;
                        parameters.StatelessResetToken = Frames.StatelessResetToken.FromSpan(data);
                        break;
                    case TransportParameterName.MaxPacketSize:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0 ||
                            varIntValue < MinimumPacketSize) goto Error;
                        parameters.MaxPacketSize = varIntValue;
                        break;
                    case TransportParameterName.InitialMaxData:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.InitialMaxData = varIntValue;
                        break;
                    case TransportParameterName.InitialMaxStreamDataBidiLocal:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.InitialMaxStreamDataBidiLocal = varIntValue;
                        break;
                    case TransportParameterName.InitialMaxStreamDataBidiRemote:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.InitialMaxStreamDataBidiRemote = varIntValue;
                        break;
                    case TransportParameterName.InitialMaxStreamDataUni:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.InitialMaxStreamDataUni = varIntValue;
                        break;
                    case TransportParameterName.InitialMaxStreamsBidi:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.InitialMaxStreamsBidi = varIntValue;
                        break;
                    case TransportParameterName.InitialMaxStreamsUni:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.InitialMaxStreamsUni = varIntValue;
                        break;
                    case TransportParameterName.AckDelayExponent:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0 ||
                            varIntValue > MaxAckDelayExponent) goto Error;
                        parameters.AckDelayExponent = varIntValue;
                        break;
                    case TransportParameterName.MaxAckDelay:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0 ||
                            varIntValue > MaxMaxAckDelay) goto Error;
                        parameters.MaxAckDelay = varIntValue;
                        break;
                    case TransportParameterName.DisableActiveMigration:
                        if (data.Length > 0) goto Error;
                        parameters.DisableActiveMigration = true;
                        break;
                    case TransportParameterName.PreferredAddress:
                    {
                        // HACK: move back a bit and read the data inside PreferredAddress.Read
                        int pos = reader.BytesRead;
                        reader.Advance(-data.Length);
                        if (!Internal.PreferredAddress.Read(reader, out var addr)) goto Error;
                        parameters.PreferredAddress = addr;
                        // make sure we are at the correct position now
                        reader.Advance(pos - reader.BytesRead);
                        break;
                    }
                    case TransportParameterName.ActiveConnectionIdLimit:
                        if (QuicPrimitives.ReadVarInt(data, out varIntValue) == 0) goto Error;
                        parameters.ActiveConnectionIdLimit = varIntValue;
                        break;
                    default:
                        // Ignore unknown transport parameters
                        continue;
                }
            }

            return true;

            Error:
            parameters = null;
            return false;
        }


        internal static void Write(QuicWriter writer, bool isServer, TransportParameters parameters)
        {
            static void WriteVarIntParameterIfNotDefault(QuicWriter w, TransportParameterName name, ulong value,
                ulong defaultValue = 0)
            {
                if (value == defaultValue) return;

                w.WriteTransportParameterName(name);
                w.WriteVarInt((ulong)QuicPrimitives.GetVarIntLength(value));
                w.WriteVarInt(value);
            }

            if (parameters.OriginalConnectionId != null)
            {
                Debug.Assert(isServer, "Trying to send server-only parameter as a client.");
                writer.WriteTransportParameterName(TransportParameterName.OriginalConnectionId);
                writer.WriteLengthPrefixedSpan(parameters.OriginalConnectionId.Data);
            }

            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.MaxIdleTimeout, parameters.MaxIdleTimeout);

            if (parameters.StatelessResetToken != null)
            {
                Debug.Assert(isServer, "Trying to send server-only parameter as a client.");
                writer.WriteTransportParameterName(TransportParameterName.StatelessResetToken);
                writer.WriteVarInt(Frames.StatelessResetToken.Length);
                Frames.StatelessResetToken.ToSpan(writer.GetWritableSpan(Frames.StatelessResetToken.Length),
                    parameters.StatelessResetToken.Value);
            }

            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.MaxPacketSize, parameters.MaxPacketSize,
                DefaultMaxPacketSize);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.InitialMaxData, parameters.InitialMaxData);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.InitialMaxStreamDataBidiLocal,
                parameters.InitialMaxStreamDataBidiLocal);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.InitialMaxStreamDataBidiRemote,
                parameters.InitialMaxStreamDataBidiRemote);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.InitialMaxStreamDataUni,
                parameters.InitialMaxStreamDataUni);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.InitialMaxStreamsBidi,
                parameters.InitialMaxStreamsBidi);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.InitialMaxStreamsUni,
                parameters.InitialMaxStreamsUni);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.AckDelayExponent,
                parameters.AckDelayExponent, DefaultAckDelayExponent);
            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.MaxAckDelay, parameters.MaxAckDelay,
                DefaultMaxAckDelay);

            if (parameters.DisableActiveMigration)
            {
                writer.WriteTransportParameterName(TransportParameterName.DisableActiveMigration);
                writer.WriteVarInt(0); // empty parameter
            }

            if (parameters.PreferredAddress != null)
            {
                Debug.Assert(isServer, "Trying to send server-only parameter as a client.");
                writer.WriteTransportParameterName(TransportParameterName.PreferredAddress);
                // the only non-fixed length field is the connection id the rest of the parameter is 41 bytes
                writer.WriteVarInt((ulong)(41 + parameters.PreferredAddress.Value.ConnectionId.Length));
                Internal.PreferredAddress.Write(writer, parameters.PreferredAddress.Value);
            }

            WriteVarIntParameterIfNotDefault(writer, TransportParameterName.ActiveConnectionIdLimit,
                parameters.ActiveConnectionIdLimit);
        }
    }
}
