// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

        private void ProcessFirstHeaderByte(byte firstHeaderByte)
        {
            _readAhead!.State = FrameReadAheadState.Header;
            _readAhead.CurrentMessageOpcode = (MessageOpcode)(firstHeaderByte & 0xF);
            _readAhead.HeaderBuffer.AvailableSpan[0] = firstHeaderByte;
            _readAhead.HeaderBuffer.Commit(1);
        }

        private void ProcessSecondHeaderByte(byte secondHeaderByte)
        {
            int payloadLength = secondHeaderByte & 0x7F;

            if (payloadLength == 126)
            {
                _readAhead!.HeaderPayloadLengthSize = 2;
            }
            else if (payloadLength == 127)
            {
                _readAhead!.HeaderPayloadLengthSize = 8;
            }

            bool masked = (secondHeaderByte & 0x80) != 0;

            _readAhead!.HeaderBuffer.AvailableSpan[0] = secondHeaderByte;
            _readAhead.HeaderBuffer.Commit(1);

            _readAhead.HeaderBytesRemaining = _readAhead.HeaderPayloadLengthSize + (masked ? 4 : 0);
        }

        private int ProcessHeaderBytes(Span<byte> buffer)
        {
            int bytesToRead = Math.Min(_readAhead!.HeaderBytesRemaining, buffer.Length);

            buffer.Slice(0, bytesToRead).CopyTo(_readAhead.HeaderBuffer.AvailableSpan);
            _readAhead.HeaderBuffer.Commit(bytesToRead);

            _readAhead.HeaderBytesRemaining -= bytesToRead;

            return bytesToRead;
        }

        private void ProcessData(Span<byte> buffer)
        {
            //All control frames MUST have a payload length of 125 bytes or less
   //and MUST NOT be fragmented.

            while (buffer.Length > 0)
            {
                if (_readAhead!.State == FrameReadAheadState.None)
                {
                    ProcessFirstHeaderByte(buffer[0]);
                    buffer = buffer.Slice(1);
                }
                else if (_readAhead.State == FrameReadAheadState.Header)
                {
                    if (_readAhead.HeaderBuffer.AvailableLength == 1) // we are reading the second byte of the header
                    {
                        ProcessSecondHeaderByte(buffer[0]);
                        buffer = buffer.Slice(1);
                    }
                    else
                    {
                        int bytesRead = ProcessHeaderBytes(buffer);
                        buffer = buffer.Slice(bytesRead);
                    }

                    if (_readAhead.HeaderBytesRemaining == 0)
                    {
                        _readAhead.State = FrameReadAheadState.HeaderRead;
                    }
                }
                else if (_readAhead.State == FrameReadAheadState.HeaderRead)
                {

                }
            }
        }

        private enum FrameReadAheadState
        {
            None,
            Header,
            HeaderRead,
            Payload,
        }

        private class ReadAhead
        {
            public const int MaxFrameHeaderSize = 14;
            public const int MaxFrameSize = 16384;
            public const int MaxBufferSize = 65535;

            public readonly AsyncMutex StreamMutex = new AsyncMutex();
            public object BufferLock => this;
            public ArrayBuffer Buffer = new ArrayBuffer(0, usePool: true);
            public byte[] InternalBuffer = new byte[MaxFrameSize];
            public ArrayBuffer HeaderBuffer = new ArrayBuffer(MaxFrameHeaderSize, usePool: true);

            public FrameReadAheadState State;
            public int PayloadBytesRemaining;
            public int HeaderBytesRemaining;
            public int HeaderPayloadLengthSize;
            public MessageOpcode CurrentMessageOpcode;
        }
    }
}
