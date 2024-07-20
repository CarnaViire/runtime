// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebSockets
{
    internal sealed partial class ManagedWebSocket : WebSocket
    {
        /* RFC 6455

        5.5.2.  Ping

            The Ping frame contains an opcode of 0x9.

            A Ping frame MAY include "Application data".

            Upon receipt of a Ping frame, an endpoint MUST send a Pong frame in
            response, unless it already received a Close frame.  It SHOULD
            respond with Pong frame as soon as is practical.  Pong frames are
            discussed in Section 5.5.3.

            An endpoint MAY send a Ping frame any time after the connection is
            established and before the connection is closed.

            NOTE: A Ping frame may serve either as a keepalive or as a means to
            verify that the remote endpoint is still responsive.

        5.5.3.  Pong

            The Pong frame contains an opcode of 0xA.

            Section 5.5.2 details requirements that apply to both Ping and Pong
            frames.

            A Pong frame sent in response to a Ping frame must have identical
            "Application data" as found in the message body of the Ping frame
            being replied to.

            If an endpoint receives a Ping frame and has not yet sent Pong
            frame(s) in response to previous Ping frame(s), the endpoint MAY
            elect to send a Pong frame for only the most recently processed Ping
            frame.

            A Pong frame MAY be sent unsolicited.  This serves as a
            unidirectional heartbeat.  A response to an unsolicited Pong frame is
            not expected.
        */

        private readonly KeepAliveStrategy _keepAliveStrategy;

        /// <summary>Timer used to send periodic pings to the server, at the interval specified</summary>
        private Timer? _keepAliveTimer;

        private KeepAlive? _keepAlive;

        private void StartUnsolicitedPongTimer(TimeSpan keepAliveInterval)
        {
            Debug.Assert(keepAliveInterval > TimeSpan.Zero, "Keep-alive interval should be a positive value.");

            // We use a weak reference from the timer to the web socket to avoid a cycle
            // that could keep the web socket rooted in erroneous cases.
            _keepAliveTimer = new Timer(static s =>
                {
                    var wr = (WeakReference<ManagedWebSocket>)s!;
                    if (wr.TryGetTarget(out ManagedWebSocket? thisRef))
                    {
                        thisRef.SendPongNoThrow();
                    }
                }, new WeakReference<ManagedWebSocket>(this), keepAliveInterval, keepAliveInterval);
        }

        private void StartKeepAlivePingTimer(TimeSpan keepAliveInterval, TimeSpan keepAliveTimeout)
        {
            Debug.Assert(keepAliveInterval > TimeSpan.Zero, "Keep-alive interval should be a positive value.");
            Debug.Assert(keepAliveTimeout > TimeSpan.Zero, "Keep-alive timeout should be a positive value.");

            _keepAlive = new()
            {
                IntervalMs = TimeSpanToMs(keepAliveInterval),
                TimeoutMs = TimeSpanToMs(keepAliveTimeout),
            };

            _keepAlive.NextRequestTimestamp = Environment.TickCount64 + _keepAlive.IntervalMs;

            long heartBeatIntervalMs = (long)Math.Max(1000, Math.Min(keepAliveInterval.TotalMilliseconds, keepAliveTimeout.TotalMilliseconds) / 4); // similar to HTTP/2

            _keepAliveTimer = new Timer(static s =>
                {
                    var wr = (WeakReference<ManagedWebSocket>)s!;
                    if (wr.TryGetTarget(out ManagedWebSocket? thisRef))
                    {
                        thisRef.HeartBeat();
                    }
                }, new WeakReference<ManagedWebSocket>(this), heartBeatIntervalMs, heartBeatIntervalMs);

            static long TimeSpanToMs(TimeSpan value)
            {
                double milliseconds = value.TotalMilliseconds;
                return (long)(milliseconds > int.MaxValue ? int.MaxValue : milliseconds);
            }
        }

        private void MarkAsAlive()
        {
            Debug.Assert(_keepAlive is not null);
            _keepAlive.NextRequestTimestamp = Environment.TickCount64 + _keepAlive.IntervalMs;
        }

        private void HeartBeat()
        {
            Debug.Assert(_keepAlive is not null);

            if (IsStateTerminal(_state))
            {
                return;
            }

            try
            {
                VerifyKeepAlive();
            }
            catch
            {
                Abort();
            }
        }

        private void VerifyKeepAlive()
        {
            Debug.Assert(_keepAlive is not null);

            long now = Environment.TickCount64;
            switch (_keepAlive.State)
            {
                case KeepAliveState.None:
                    // Check whether keep alive delay has passed since last frame received
                    if (now > _keepAlive.NextRequestTimestamp)
                    {
                        // Set the status directly to ping sent and set the timestamp
                        _keepAlive.State = KeepAliveState.PingSent;
                        _keepAlive.NextTimeoutTimestamp = now + _keepAlive.TimeoutMs;

                        long pingPayload = Interlocked.Increment(ref _keepAlive.Payload);
                        SendPingNoThrow(pingPayload);
                        return;
                    }
                    break;
                case KeepAliveState.PingSent:
                    if (now > _keepAlive.NextTimeoutTimestamp)
                    {
                        throw new Exception(); // todo
                    }
                    break;
                default:
                    Debug.Fail($"Unexpected keep alive state ({_keepAlive.State})");
                    break;
            }
        }

        private void SendPingNoThrow(long pingPayload)
        {
            ValueTask t = SendPingAsync(pingPayload, CancellationToken.None);
            if (t.IsCompletedSuccessfully)
            {
                t.GetAwaiter().GetResult();
            }
            else
            {
                // ignore the failures -- if not sent, the timeout mechanism will abort the connection anyway
                ObserveExceptions(t.AsTask());
            }
        }

        private async ValueTask SendPingAsync(long pingPayload, CancellationToken cancellationToken)
        {
            byte[] pingPayloadBuffer = ArrayPool<byte>.Shared.Rent(sizeof(long));
            BinaryPrimitives.WriteInt64BigEndian(pingPayloadBuffer, pingPayload);
            try
            {
                await SendFrameAsync(MessageOpcode.Ping, endOfMessage: true, disableCompression: true, pingPayloadBuffer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pingPayloadBuffer);
            }
        }

        private void SendPongNoThrow()
        {
            // This exists purely to keep the connection alive; don't wait for the result, and ignore any failures.
            // The call will handle releasing the lock.  We send a pong rather than ping, since it's allowed by
            // the RFC as a unidirectional heartbeat and we're not interested in waiting for a response.
            ValueTask t = SendFrameAsync(MessageOpcode.Pong, endOfMessage: true, disableCompression: true, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

            if (t.IsCompletedSuccessfully)
            {
                t.GetAwaiter().GetResult();
            }
            else
            {
                ObserveExceptions(t.AsTask());
            }
        }

        private static void ObserveExceptions(Task t)
        {
            // "Observe" any exception, ignoring it to prevent the unobserved exception event from being raised.
            t.ContinueWith(static p => { _ = p.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /*
        private void ProcessPingFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.Ping);

            if (frameHeader.StreamId != 0)
            {
                ThrowProtocolError();
            }

            if (frameHeader.PayloadLength != FrameHeader.PingLength)
            {
                ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
            }

            // We don't wait for SendPingAckAsync to complete before discarding
            // the incoming buffer, so we need to take a copy of the data. Read
            // it as a big-endian integer here to avoid allocating an array.
            Debug.Assert(sizeof(long) == FrameHeader.PingLength);
            ReadOnlySpan<byte> pingContent = _incomingBuffer.ActiveSpan.Slice(0, FrameHeader.PingLength);
            long pingContentLong = BinaryPrimitives.ReadInt64BigEndian(pingContent);

            if (NetEventSource.Log.IsEnabled()) Trace($"Received PING frame, content:{pingContentLong} ack: {frameHeader.AckFlag}");

            if (frameHeader.AckFlag) // == receive PONG
            {
                ProcessPingAck(pingContentLong);
            }
            else
            {
                LogExceptions(SendPingAsync(pingContentLong, isAck: true)); // == send PONG
            }
            _incomingBuffer.Discard(frameHeader.PayloadLength);
        }

        private void ProcessPingAck(long payload) // == receive PONG
        {
            // RttEstimator is using negative values in PING payloads.
            // _keepAlivePingPayload is always non-negative.
            if (payload < 0) // RTT ping
            {
                _rttEstimator.OnPingAckReceived(payload, this);
            }
            else // Keepalive ping
            {
                if (_keepAliveState != KeepAliveState.PingSent) // N/A
                    ThrowProtocolError();
                if (Interlocked.Read(ref _keepAlivePingPayload) != payload) // N/A???
                    ThrowProtocolError();
                _keepAliveState = KeepAliveState.None;
            }
        }
        */



        /// <summary>Processes a received ping or pong message.</summary>
        /// <param name="header">The message header.</param>
        /// <param name="cancellationToken">The CancellationToken used to cancel the websocket operation.</param>
        private async ValueTask HandleReceivedPingPongAsync(MessageHeader header, CancellationToken cancellationToken)
        {
            // Consume any (optional) payload associated with the ping/pong.
            if (header.PayloadLength > 0 && _receiveBufferCount < header.PayloadLength)
            {
                await EnsureBufferContainsAsync((int)header.PayloadLength, cancellationToken).ConfigureAwait(false);
            }

            // If this was a ping, send back a pong response.
            if (header.Opcode == MessageOpcode.Ping)
            {
                if (_isServer)
                {
                    ApplyMask(_receiveBuffer.Span.Slice(_receiveBufferOffset, (int)header.PayloadLength), header.Mask, 0);
                }

                await SendFrameAsync(
                    MessageOpcode.Pong,
                    endOfMessage: true,
                    disableCompression: true,
                    _receiveBuffer.Slice(_receiveBufferOffset, (int)header.PayloadLength),
                    cancellationToken).ConfigureAwait(false);
            }

            // Regardless of whether it was a ping or pong, we no longer need the payload.
            if (header.PayloadLength > 0)
            {
                ConsumeFromBuffer((int)header.PayloadLength);
            }
        }

        private class KeepAlive
        {
            public long TimeoutMs;
            public long IntervalMs;
            public long NextRequestTimestamp;
            public long NextTimeoutTimestamp;
            public KeepAliveState State;
            public long Payload;
        }

        private enum KeepAliveState
        {
            None = 0,
            PingSent
        }

        private enum KeepAliveStrategy
        {
            None = 0,
            UnsolicitedPong,
            PingPong
        }
    }
}
