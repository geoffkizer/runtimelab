using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.Buffers;
using Xunit;

namespace System.Net.Quic.Tests
{
    public class OutboundBufferTest
    {
        private OutboundBuffer buffer = new OutboundBuffer(0);

        private void EnqueueBytes(int count)
        {
            Span<byte> tmp = stackalloc byte[count];

            // generate ascending integers so that we can test for data correctness
            for (int i = 0; i < tmp.Length; i++)
            {
                tmp[i] = (byte)(buffer.WrittenBytes + i);
            }

            buffer.Enqueue(tmp);
        }

        [Fact]
        public void ReturnsCorrectPendingRange()
        {
            EnqueueBytes(10);
            Assert.False(buffer.HasPendingData); // MaxData is 0 yet
            buffer.UpdateMaxData(10);

            Assert.True(buffer.HasPendingData);
            var (start, count) = buffer.GetNextSendableRange();
            Assert.Equal(0u, start);
            Assert.Equal(10u, count);

            EnqueueBytes(10);
            (start, count) = buffer.GetNextSendableRange();
            Assert.Equal(0u, start);
            Assert.Equal(10u, count); // still MaxData is 10

            buffer.UpdateMaxData(20);
            (_, count) = buffer.GetNextSendableRange();
            Assert.Equal(20u, count);
        }

        [Fact]
        public void ChecksOutPartialChunk()
        {
            EnqueueBytes(10);
            buffer.UpdateMaxData(50);

            byte[] destination = new byte[5];
            buffer.CheckOut(destination);

            Assert.Equal(new byte[]{0,1,2,3,4}, destination);

            var (start, count) = buffer.GetNextSendableRange();

            Assert.Equal(5u, start);
            Assert.Equal(5u, count);
        }

        [Fact]
        public void ChecksOutAcrossChunks()
        {
            EnqueueBytes(10);
            EnqueueBytes(10);
            buffer.UpdateMaxData(50);

            byte[] destination = new byte[10];
            // discard first 5 bytes
            buffer.CheckOut(destination.AsSpan(0, 5));

            buffer.CheckOut(destination);
            var (start, count) = buffer.GetNextSendableRange();

            Assert.Equal(new byte[]{5,6,7,8,9,10,11,12,13,14}, destination);

            Assert.Equal(15u, start);
            Assert.Equal(5u, count);
        }

        [Fact]
        public void RequeuesLostBytes()
        {
            EnqueueBytes(10);
            EnqueueBytes(10);
            buffer.UpdateMaxData(50);

            byte[] destination = new byte[10];
            // discard first 10 bytes
            buffer.CheckOut(destination.AsSpan(0, 5));
            buffer.CheckOut(destination.AsSpan(0, 5));
            // first 5 bytes got lost
            buffer.OnLost(0, 5);

            // the first bytes should be again pending
            var (start, count) = buffer.GetNextSendableRange();
            Assert.Equal(0u, start);
            Assert.Equal(5u, count);

            // if second 5 bytes are lost too, the entire buffer should be pending
            buffer.OnLost(5, 5);
            (start, count) = buffer.GetNextSendableRange();
            Assert.Equal(0u, start);
            Assert.Equal(20u, count);
        }

        [Fact]
        public void ProcessAllTheData()
        {
            EnqueueBytes(10);
            buffer.UpdateMaxData(50);

            Assert.False(buffer.SizeKnown);
            buffer.MarkEndOfData();
            Assert.True(buffer.SizeKnown);
            Assert.False(buffer.Finished);

            byte[] destination = new byte[5];
            buffer.CheckOut(destination);
            buffer.OnAck(0, 5);
            Assert.True(buffer.HasUnackedData);
            Assert.False(buffer.Finished);

            buffer.CheckOut(destination);
            buffer.OnAck(5, 5);

            Assert.True(buffer.Finished);
            Assert.False(buffer.HasUnackedData);
        }
    }
}
