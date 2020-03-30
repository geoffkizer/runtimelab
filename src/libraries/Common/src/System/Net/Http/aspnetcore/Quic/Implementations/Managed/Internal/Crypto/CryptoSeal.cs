using System.Diagnostics;
using System.Linq;

namespace System.Net.Quic.Implementations.Managed.Internal.Crypto
{
    internal class CryptoSeal
    {
        private readonly CryptoSealAlgorithm _algorithm;

        private byte[] IV { get; }
        private byte[] Key { get; }
        private byte[] HeaderKey { get; }

        public int TagLength => _algorithm.TagLength;

        public CryptoSeal(CipherAlgorithm alg, ReadOnlySpan<byte> secret)
        {
            IV = KeyDerivation.DeriveIv(secret);
            Key = KeyDerivation.DeriveKey(secret);
            HeaderKey = KeyDerivation.DeriveHp(secret);
            _algorithm = CryptoSealAlgorithm.Create(alg, Key, HeaderKey);
        }

        public void EncryptPacket(Span<byte> buffer, int pnOffset, int payloadLength, ulong packetNumber)
        {
            int pnLength = HeaderHelpers.GetPacketNumberLength(buffer[0]);

            Span<byte> nonce = stackalloc byte[IV.Length];
            MakeNonce(IV, packetNumber, nonce);

            // split packet buffer into spans
            var header = buffer.Slice(0, pnOffset + pnLength);
            var payload = buffer.Slice(pnOffset + pnLength, payloadLength - pnLength - _algorithm.TagLength);
            var tag = buffer.Slice(pnOffset + payloadLength - _algorithm.TagLength, _algorithm.TagLength);

            // encrypt payload in-place
            _algorithm.Encrypt(nonce, payload, tag, header);

            // apply header protection
            Span<byte> protectionMask = stackalloc byte[5];

            // sample as if pnLength == 4
            _algorithm.CreateProtectionMask(buffer.Slice(pnOffset + 4, _algorithm.SampleLength), protectionMask);

            ProtectHeader(header, protectionMask, pnLength);
        }

        public static void ProtectHeader(Span<byte> header, Span<byte> mask, int pnLength)
        {
            Debug.Assert(mask.Length == 5);

            if (HeaderHelpers.IsLongHeader(header[0]))
            {
                header[0] ^= (byte)(mask[0] & 0x0f);
            }
            else
            {
                header[0] ^= (byte)(mask[0] & 0x1f);
            }

            int ii = 1;
            for (int i = header.Length - pnLength; i < header.Length; i++)
            {
                header[i] ^= mask[ii++ % mask.Length];
            }
        }

        public bool DecryptPacket(Span<byte> buffer, int pnOffset, int payloadLength, ulong largestAckedPn)
        {
            // remove header protection
            Span<byte> protectionMask = stackalloc byte[5];
            _algorithm.CreateProtectionMask(buffer.Slice(pnOffset + 4, _algorithm.SampleLength), protectionMask);

            // pass header span as if packet number had the maximum 4 bytes
            int pnLength = UnprotectHeader(buffer.Slice(0, pnOffset + 4), protectionMask);

            // now we can get correct span size after establishing the packet number length
            var header = buffer.Slice(0, pnOffset + pnLength);

            // read the actual packet number
            // TODO-RZ: Do this in an endian-aware way
            ulong packetNumber = 0;
            for (int i = 0; i < pnLength; ++i)
            {
                packetNumber = (packetNumber << 8) | buffer[pnOffset + i];
            }
            packetNumber = QuicPrimitives.DecodePacketNumber(largestAckedPn, packetNumber, pnLength);

            Span<byte> nonce = stackalloc byte[IV.Length];
            MakeNonce(IV, packetNumber, nonce);

            var ciphertext = buffer.Slice(header.Length, payloadLength - pnLength - _algorithm.TagLength);
            var tag = buffer.Slice(header.Length + ciphertext.Length, _algorithm.TagLength);

            return _algorithm.Decrypt(nonce, ciphertext, tag, header);
        }

        public static int UnprotectHeader(Span<byte> header, Span<byte> mask)
        {
            Debug.Assert(mask.Length == 5);

            if (HeaderHelpers.IsLongHeader(header[0]))
            {
                header[0] ^= (byte)(mask[0] & 0x0f);
            }
            else
            {
                header[0] ^= (byte)(mask[0] & 0x1f);
            }

            // we can get pnLength only after unprotecting the first byte
            int pnLength = HeaderHelpers.GetPacketNumberLength(header[0]);

            int ii = 1;
            // header span is passed as if the packet number had full 4 bytes, so we need to
            // need to leave out a couple of bytes at the end
            for (int i = 0; i < pnLength; i++)
            {
                header[header.Length - 4 + i] ^= mask[ii++ % mask.Length];
            }

            return pnLength;
        }

        private static void MakeNonce(ReadOnlySpan<byte> iv, ulong packetNumber, Span<byte> nonce)
        {
            Debug.Assert(iv.Length == 12);
            Debug.Assert(nonce.Length == 12);

            // From RFC: The nonce, N, is formed by combining the packet
            // protection IV with the packet number.  The 62 bits of the
            // reconstructed QUIC packet number in network byte order are left-
            // padded with zeros to the size of the IV.  The exclusive OR of the
            // padded packet number and the IV forms the AEAD nonce.

            iv.CopyTo(nonce);
            for (int i = 0; i < 8; i++)
            {
                nonce[nonce.Length - 1 - i] ^= (byte) (packetNumber >> (i * 8));
            }
        }

    }
}
