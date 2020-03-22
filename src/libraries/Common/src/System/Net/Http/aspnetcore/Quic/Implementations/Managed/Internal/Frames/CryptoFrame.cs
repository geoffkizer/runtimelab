using System.Diagnostics;

namespace System.Net.Quic.Implementations.Managed.Internal.Frames
{
    /// <summary>
    ///     Used to transmit opaque cryptographic handshake messages.
    /// </summary>
    internal readonly ref struct CryptoFrame
    {
        /// <summary>
        ///     Byte offset of the stream carrying the cryptographic data.
        /// </summary>
        internal readonly ulong Offset;

        /// <summary>
        ///     Cryptographic message data;
        /// </summary>
        internal readonly ReadOnlySpan<byte> CryptoData;

        internal CryptoFrame(ulong offset, ReadOnlySpan<byte> cryptoData)
        {
            Offset = offset;
            CryptoData = cryptoData;
        }

        internal static bool Read(QuicReader reader, out CryptoFrame frame)
        {
            var type = reader.ReadFrameType();
            Debug.Assert(type == FrameType.Crypto);

            if (!reader.TryReadVarInt(out ulong offset) ||
                !reader.TryReadLengthPrefixedSpan(out var data))
            {
                frame = default;
                return false;
            }

            frame = new CryptoFrame(offset, data);
            return true;
        }

        internal static void Write(QuicWriter writer, in CryptoFrame frame)
        {
            writer.WriteFrameType(FrameType.Crypto);

            writer.WriteVarInt(frame.Offset);
            writer.WriteLengthPrefixedSpan(frame.CryptoData);
        }
    }
}
