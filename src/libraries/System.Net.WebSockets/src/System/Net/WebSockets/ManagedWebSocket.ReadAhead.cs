// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebSockets
{
    internal sealed partial class ManagedWebSocket : WebSocket
    {
        private bool ReadAheadEnabled => _readAhead is not null;

        private ReadAhead? _readAhead;

        private void EnableReadAhead()
        {
            Debug.Assert(_keepAliveStrategy == KeepAliveStrategy.PingPong);
            Debug.Assert(_readAhead is null);

            _readAhead = new ReadAhead();
        }

        private ValueTask<int> StreamReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => ReadAheadEnabled
                ? ConsumeReadAheadData_StreamReadAsync(buffer, cancellationToken)
                : _stream.ReadAsync(buffer, cancellationToken);

        private ValueTask<int> StreamReadAtLeastAsync(Memory<byte> buffer, int minimumBytes, CancellationToken cancellationToken)
            => ReadAheadEnabled
                ? ConsumeReadAheadData_StreamReadAtLeastAsync(buffer, minimumBytes, cancellationToken)
                : _stream.ReadAtLeastAsync(buffer, minimumBytes, throwOnEndOfStream: false, cancellationToken);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask<int> ConsumeReadAheadData_StreamReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Debug.Assert(ReadAheadEnabled);

            // we don't need to take the lock to check whether data is available because we're the only consumer
            if (_readAhead!.Buffer.ActiveLength > 0)
            {
                return ConsumeReadAheadData(buffer); // this method takes _readAhead.BufferLock
            }

            await _readAhead.StreamMutex.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_readAhead.Buffer.ActiveLength > 0)
                {
                    return ConsumeReadAheadData(buffer); // this method takes _readAhead.BufferLock
                }

                return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _readAhead.StreamMutex.Exit();
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask<int> ConsumeReadAheadData_StreamReadAtLeastAsync(Memory<byte> buffer, int minimumBytes, CancellationToken cancellationToken)
        {
            Debug.Assert(ReadAheadEnabled);
            Debug.Assert(minimumBytes <= buffer.Length);

            int readAheadBytes = ConsumeReadAheadData(buffer); // this method takes _readAhead.BufferLock
            if (readAheadBytes >= minimumBytes)
            {
                return readAheadBytes;
            }

            await _readAhead!.StreamMutex.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                readAheadBytes += ConsumeReadAheadData(buffer.Slice(readAheadBytes)); // this method takes _readAhead.BufferLock
                if (readAheadBytes >= minimumBytes)
                {
                    return readAheadBytes;
                }

                int remainingBytes = minimumBytes - readAheadBytes;
                int bytesRead = await _stream.ReadAtLeastAsync(buffer.Slice(readAheadBytes), remainingBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

                return readAheadBytes + bytesRead;
            }
            finally
            {
                _readAhead.StreamMutex.Exit();
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask<int> InternalBuffer_StreamReadAsync(int maxBytes, CancellationToken cancellationToken)
        {
            Debug.Assert(ReadAheadEnabled);

            await ZeroByteRead_StreamReadAsync().ConfigureAwait(false); // this method takes _readAhead.StreamMutex

            await _readAhead!.StreamMutex.EnterAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                int bytesRead = await _stream.ReadAsync(_readAhead.InternalBuffer, cancellationToken).ConfigureAwait(false);

                ProduceReadAheadData(_readAhead.InternalBuffer.AsSpan(0, bytesRead)); // this method takes _readAhead.BufferLock

                return bytesRead;
            }
            finally
            {
                _readAhead.StreamMutex.Exit();
            }
        }

        /*private async Task ReadNextFrameHeaderAsync()
        {
            await _readAhead!.StreamMutex.EnterAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _stream.ReadExactlyAsync(_readAhead.InternalBuffer.AsMemory(0, bytesToSkip)).ConfigureAwait(false);
                ProduceReadAheadData(_readAhead.InternalBuffer.AsSpan(0, bytesToSkip)); // this method takes _readAhead.BufferLock


            }
            finally
            {
                _readAhead.StreamMutex.Exit();
            }
        }*/

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask ZeroByteRead_StreamReadAsync()
        {
            Debug.Assert(ReadAheadEnabled);

            await _readAhead!.StreamMutex.EnterAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _stream.ReadAsync(Memory<byte>.Empty).ConfigureAwait(false);
            }
            finally
            {
                _readAhead.StreamMutex.Exit();
            }
        }

        private int ConsumeReadAheadData(Memory<byte> buffer)
        {
            lock (_readAhead!.BufferLock)
            {
                int bytesToConsume = Math.Min(_readAhead.Buffer.ActiveLength, buffer.Length);

                if (bytesToConsume == 0)
                {
                    return 0;
                }

                _readAhead.Buffer.ActiveMemory.Slice(0, bytesToConsume).CopyTo(buffer);
                _readAhead.Buffer.Discard(bytesToConsume);

                return bytesToConsume;
            }
        }

        private int ProduceReadAheadData(Span<byte> buffer)
        {
            Debug.Assert(_readAhead!.StreamMutex.IsHeld);

            if (buffer.Length == 0)
            {
                return 0;
            }

            lock (_readAhead!.BufferLock)
            {

                _readAhead.Buffer.EnsureAvailableSpace(buffer.Length);

                buffer.CopyTo(_readAhead.Buffer.AvailableSpan);
                _readAhead.Buffer.Commit(buffer.Length);

                return buffer.Length;
            }
        }

        private void ProcessData(Span<byte> buffer, bool commitToDownstream)
        {
            Debug.Assert(_readAhead!.State != FrameReadAheadState.Faulted);

            while (buffer.Length > 0 || _readAhead.State == FrameReadAheadState.HeaderRead || _readAhead.State == FrameReadAheadState.PayloadRead)
            {
                int bytesProcessed;
                switch (_readAhead.State)
                {
                    case FrameReadAheadState.None: // transitions to HeaderStarted or Faulted
                        ProcessFirstHeaderByte(_readAhead, buffer[0], _isServer, commitToDownstream);
                        bytesProcessed = 1;
                        break;
                    case FrameReadAheadState.HeaderStarted: // transitions to HeaderLengthKnown or HeaderRead or Faulted
                        ProcessSecondHeaderByte(_readAhead, buffer[0], commitToDownstream);
                        bytesProcessed = 1;
                        break;
                    case FrameReadAheadState.HeaderLengthKnown: // transitions to HeaderRead (or remains in HeaderLengthKnown)
                        bytesProcessed = ProcessRemainingHeaderBytes(_readAhead, buffer, commitToDownstream);
                        break;
                    case FrameReadAheadState.HeaderRead: // transitions to PayloadStarted or PayloadRead
                        OnHeaderRead();
                        bytesProcessed = 0;
                        break;
                    case FrameReadAheadState.PayloadStarted: // transitions to PayloadRead (or remains in PayloadStarted)
                        bytesProcessed = ProcessPayloadBytes(_readAhead, buffer, commitToDownstream);
                        break;
                    case FrameReadAheadState.PayloadRead: // transitions to None
                        OnPayloadRead();
                        bytesProcessed = 0;
                        break;
                    case FrameReadAheadState.Faulted: // TERMINAL: bad pong frame received, let the downstream handle everything after that
                        CommitToDownstream(_readAhead, buffer);
                        bytesProcessed = buffer.Length;
                        break;
                    default:
                        throw new InvalidOperationException();
                }

                buffer = buffer.Slice(bytesProcessed);
            }
        }

        private static void ProcessFirstHeaderByte(ReadAhead ra, byte firstHeaderByte, bool commitToDownstream)
        {
            Debug.Assert(ra.State == FrameReadAheadState.None);
            Debug.Assert(ra.PongFrameBuffer.ActiveLength == 0);

            MessageOpcode opcode = (MessageOpcode)(firstHeaderByte & 0xF);
            ra.IsPongFrame = opcode == MessageOpcode.Pong; // We will be "holding back" a Pong frame until we've read it completely.

            if (ra.IsPongFrame)
            {
                // RFC 6455: Control frames MUST NOT be fragmented.
                bool fin = (firstHeaderByte & 0x80) != 0;

                if (!fin) // Bad frame. Let the downstream handle everything after that.
                {
                    ra.State = FrameReadAheadState.Faulted;
                    if (commitToDownstream)
                    {
                        CommitToDownstream(ra, new Span<byte>(ref firstHeaderByte));
                    }
                    return;
                }
            }

            Commit(ra, new Span<byte>(ref firstHeaderByte), commitToDownstream);
            ra.State = FrameReadAheadState.HeaderStarted;
        }

        private static void ProcessSecondHeaderByte(ReadAhead ra, byte secondHeaderByte, bool isServer, bool commitToDownstream)
        {
            Debug.Assert(ra.State == FrameReadAheadState.HeaderStarted);

            bool isMasked = (secondHeaderByte & 0x80) != 0;
            // RFC 6455: The server MUST close the connection upon receiving a frame that is not masked.
            // A client MUST close a connection if it detects a masked frame.
            if (isServer != isMasked) // isServer && !isMasked || !isServer && isMasked
            {
                // Bad frame. Let the downstream handle everything after that.
                MarkAsFaulted(ra, secondHeaderByte, commitToDownstream);
                return;
            }

            int payloadLength = secondHeaderByte & 0x7F;

            // RFC 6455: All control frames MUST have a payload length of 125 bytes or less.
            if (ra.IsPongFrame && payloadLength > MaxControlPayloadLength)
            {
                // Bad frame. Let the downstream handle everything after that.
                MarkAsFaulted(ra, secondHeaderByte, commitToDownstream);
                return;
            }

            if (payloadLength == 126)
            {
                ra.ExtPayloadLengthBytes = 2;
                ra.PayloadBytesRemaining = -1; // need to parse later
            }
            else if (payloadLength == 127)
            {
                ra.ExtPayloadLengthBytes = 8;
                ra.PayloadBytesRemaining = -1; // need to parse later
            }
            else
            {
                ra.ExtPayloadLengthBytes = 0;
                ra.PayloadBytesRemaining = payloadLength;
            }

            ra.ExtPayloadLengthBuffer.EnsureAvailableSpace(ra.ExtPayloadLengthBytes);

            ra.HeaderBytesRemaining = ra.ExtPayloadLengthBytes + (isMasked ? 4 : 0);

            Commit(ra, new Span<byte>(ref secondHeaderByte), commitToDownstream);

            ra.State = ra.HeaderBytesRemaining != 0
                ? FrameReadAheadState.HeaderLengthKnown
                : FrameReadAheadState.HeaderRead;

            static void MarkAsFaulted(ReadAhead ra, byte secondHeaderByte, bool commitToDownstream)
            {
                ra.State = FrameReadAheadState.Faulted;
                if (commitToDownstream)
                {
                    if (ra.IsPongFrame)
                    {
                        // Make sure the first byte is passed downstream as well.
                        CommitToDownstream(ra, ra.PongFrameBuffer.ActiveSpan);
                    }
                    CommitToDownstream(ra, new Span<byte>(ref secondHeaderByte));
                }
            }
        }

        private static int ProcessRemainingHeaderBytes(ReadAhead ra, Span<byte> buffer, bool commitToDownstream)
        {
            Debug.Assert(ra.State == FrameReadAheadState.HeaderLengthKnown);
            Debug.Assert(ra.HeaderBytesRemaining > 0);
            Debug.Assert(buffer.Length > 0);

            int bytesToCommit = Math.Min(ra.HeaderBytesRemaining, buffer.Length);
            Commit(ra, buffer.Slice(0, bytesToCommit), commitToDownstream);
            ra.HeaderBytesRemaining -= bytesToCommit;

            if (ra.HeaderBytesRemaining == 0)
            {
                ra.State = FrameReadAheadState.HeaderRead;
            }
            return bytesToCommit;
        }

        private void OnHeaderRead()
        {
            Debug.Assert(_readAhead!.State == FrameReadAheadState.HeaderRead);
            Debug.Assert(_readAhead.ExtPayloadLengthBuffer.ActiveLength == _readAhead.ExtPayloadLengthBytes);

            if (_readAhead.ExtPayloadLengthBytes == 2)
            {
                Debug.Assert(_readAhead.PayloadBytesRemaining == -1);
                _readAhead.PayloadBytesRemaining = BinaryPrimitives.ReadInt16BigEndian(_readAhead.ExtPayloadLengthBuffer.ActiveSpan);
            }
            else if (_readAhead.ExtPayloadLengthBytes == 8)
            {
                Debug.Assert(_readAhead.PayloadBytesRemaining == -1);
                _readAhead.PayloadBytesRemaining = BinaryPrimitives.ReadInt64BigEndian(_readAhead.ExtPayloadLengthBuffer.ActiveSpan);
            }
            else
            {
                Debug.Assert(_readAhead.ExtPayloadLengthBytes == 0 && _readAhead.PayloadBytesRemaining >= 0);
            }

            _readAhead.ExtPayloadLengthBuffer.ClearAndReturnBuffer();

            // RFC 6455: frame-payload-length-63 = %x0000000000000000-7FFFFFFFFFFFFFFF ; 64 bits in length
            if (_readAhead.PayloadBytesRemaining < 0)
            {
                // Bad frame. Let the downstream handle everything after that.
                _readAhead.State = FrameReadAheadState.Faulted;
                return;
            }

            _readAhead.State = _readAhead.PayloadBytesRemaining == 0
                ? FrameReadAheadState.PayloadRead
                : FrameReadAheadState.PayloadStarted;
        }

        private static int ProcessPayloadBytes(ReadAhead ra, Span<byte> buffer, bool commitToDownstream)
        {
            Debug.Assert(ra.State == FrameReadAheadState.PayloadStarted);
            Debug.Assert(ra.PayloadBytesRemaining > 0);
            Debug.Assert(buffer.Length > 0);

            int bytesToCommit = (int)Math.Min(ra.PayloadBytesRemaining, buffer.Length);
            Commit(ra, buffer.Slice(0, bytesToCommit), commitToDownstream);
            ra.PayloadBytesRemaining -= bytesToCommit;

            if (ra.PayloadBytesRemaining == 0)
            {
                ra.State = FrameReadAheadState.PayloadRead;
            }
            return bytesToCommit;
        }

        private void OnPayloadRead()
        {
            Debug.Assert(_readAhead!.State == FrameReadAheadState.PayloadRead);

            if (_readAhead.IsPongFrame)
            {

            }

        }

        private static void Commit(ReadAhead ra, Span<byte> buffer, bool commitToDownstream)
        {
            Debug.Assert(ra.State != FrameReadAheadState.Faulted);

            if (ra.IsPongFrame)
            {
                CommitToPongFrameBuffer(ra, buffer);
            }
            else if (commitToDownstream)
            {
                CommitToDownstream(ra, buffer);
            }
        }

        private static void CommitToDownstream(ReadAhead ra, Span<byte> buffer)
        {
            lock (ra.BufferLock)
            {
                ra.Buffer.EnsureAvailableSpace(buffer.Length);
                buffer.CopyTo(ra.Buffer.AvailableSpan);
                ra.Buffer.Commit(buffer.Length);
            }
        }

        private static void CommitToPongFrameBuffer(ReadAhead ra, Span<byte> buffer)
        {
            Debug.Assert(ra.IsPongFrame);
            Debug.Assert(ra.PongFrameBuffer.AvailableLength >= buffer.Length);

            buffer.CopyTo(ra.PongFrameBuffer.AvailableSpan);
            ra.PongFrameBuffer.Commit(buffer.Length);
        }

        private enum FrameReadAheadState
        {
            None,
            HeaderStarted, // first byte read
            HeaderLengthKnown, // second byte read
            HeaderRead,
            PayloadStarted,
            PayloadRead,
            Faulted
        }

        private class ReadAhead : IDisposable
        {
            public const int MaxPongFrameSize = 2 + 4 + 125; // 2 bytes for header, 4 bytes for mask, 125 bytes for payload
            public const int MaxStreamReadSize = 16384;
            public const int MaxBufferSize = 65535;

            public readonly AsyncMutex StreamMutex = new AsyncMutex();
            public object BufferLock => this;
            public ArrayBuffer Buffer = new ArrayBuffer(0, usePool: true);
            public ArrayBuffer StreamReadBuffer = new ArrayBuffer(0, usePool: true);
            public ArrayBuffer PongFrameBuffer = new ArrayBuffer(MaxPongFrameSize);
            public ArrayBuffer ExtPayloadLengthBuffer = new ArrayBuffer(0, usePool: true);

            public FrameReadAheadState State;
            public long PayloadBytesRemaining;
            public int HeaderBytesRemaining;
            public int ExtPayloadLengthBytes;
            //public bool IsMasked;
            public bool IsPongFrame;

            public bool IsFaulted => State == FrameReadAheadState.Faulted;

            public void Dispose()
            {
                Buffer.Dispose();
                StreamReadBuffer.Dispose();
                PongFrameBuffer.Dispose();
                ExtPayloadLengthBuffer.Dispose();
            }
        }
    }
}
