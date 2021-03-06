﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using System.Buffers;

namespace SimpleHttpClient
{
    internal partial class HttpConnection : IDisposable
    {
        private const int InitialReadBufferSize = 4096;
        private const int InitialWriteBufferSize = InitialReadBufferSize;
        private static readonly byte[] s_spaceHttp11NewlineAsciiBytes = Encoding.ASCII.GetBytes(" HTTP/1.1\r\n");
        private static readonly byte[] s_contentLength0NewlineAsciiBytes = Encoding.ASCII.GetBytes("Content-Length: 0\r\n");
        private static readonly byte[] s_http1DotBytes = Encoding.ASCII.GetBytes("HTTP/1.");
        private static readonly ulong s_http10Bytes = BitConverter.ToUInt64(Encoding.ASCII.GetBytes("HTTP/1.0"));
        private static readonly ulong s_http11Bytes = BitConverter.ToUInt64(Encoding.ASCII.GetBytes("HTTP/1.1"));
        private readonly Socket _socket;
        private readonly Stream _stream;
        internal readonly HttpConnectionPool _pool;
        private readonly WeakReference<HttpConnection> _weakThisRef;

        private bool _connectionClose;
        private ValueTask<int>? _readAheadTask;
        private int _readAheadTaskLock = 0; // 0 == free, 1 == held

        private readonly byte[] _writeBuffer;
        private int _writeOffset;

        private byte[] _readBuffer;
        private int _readOffset;
        private int _readLength;

        internal int _allowedReadLineBytes;

        private bool disposed;

        internal int ReadBufferSize => _readBuffer.Length;
        internal ReadOnlyMemory<byte> RemainingBuffer => new ReadOnlyMemory<byte>(_readBuffer, _readOffset, _readLength - _readOffset);

        internal void ConsumeFromRemainingBuffer(int bytesToConsume)
        {
            _readOffset += bytesToConsume;
        }

        private ValueTask<int>? ConsumeReadAheadTask()
        {
            if (Interlocked.CompareExchange(ref _readAheadTaskLock, 1, 0) == 0)
            {
                ValueTask<int>? t = _readAheadTask;
                _readAheadTask = null;
                Volatile.Write(ref _readAheadTaskLock, 0);
                return t;
            }

            return null;
        }

        public HttpConnection(
            HttpConnectionPool pool,
            Socket socket,
            Stream stream)
        {
            _pool = pool;
            _socket = socket;
            _stream = stream;
            _writeBuffer = new byte[InitialWriteBufferSize];
            _readBuffer = new byte[InitialReadBufferSize];
            _weakThisRef = new WeakReference<HttpConnection>(this);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CancellationTokenRegistration cancellationRegistration = RegisterCancellation(cancellationToken);
            try
            {
                var _canRetry = true;

                //write raw request into stream(NetworkStream)
                //method
                await WriteStringAsync(request.Method.Method).ConfigureAwait(false);
                //blank space
                await WriteByteAsync((byte)' ').ConfigureAwait(false);
                //host and querystring
                if (_pool._kind == HttpConnectionKind.ProxyConnect)
                {
                    await WriteStringAsync(request.Headers.Host).ConfigureAwait(false);
                }
                else
                {
                    await WriteStringAsync(request.RequestUri.AbsoluteUri).ConfigureAwait(false);
                }
                //protocol
                await WriteBytesAsync(s_spaceHttp11NewlineAsciiBytes).ConfigureAwait(false);
                //header
                if (request.Headers.Host == null)
                {
                    await WriteStringAsync($"Host: {request.RequestUri.IdnHost}\r\n").ConfigureAwait(false);
                }
                await WriteHeaderAsync(request.Headers);

                if (request.Content == null)
                {
                    await WriteBytesAsync(s_contentLength0NewlineAsciiBytes).ConfigureAwait(false);
                    await WriteTwoBytesAsync((byte)'\r', (byte)'\n').ConfigureAwait(false);
                }
                else
                {
                    var body = await request.Content.ReadAsStringAsync();
                    await WriteHeaderAsync(request.Content.Headers).ConfigureAwait(false);
                    await WriteStringAsync($"Content-Length: {body.Length}\r\n").ConfigureAwait(false);
                    await WriteTwoBytesAsync((byte)'\r', (byte)'\n').ConfigureAwait(false);

                    //TODO
                    await WriteStringAsync(body).ConfigureAwait(false);
                }

                var d = Encoding.UTF8.GetString(_writeBuffer);
                await FlushAsync().ConfigureAwait(false);

                var t = ConsumeReadAheadTask();
                if (t != null)
                {
                    int bytesRead = await t.GetValueOrDefault().ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        throw new IOException("net_http_invalid_response_premature_eof");
                    }

                    _readOffset = 0;
                    _readLength = bytesRead;
                }

                _canRetry = false;

                _allowedReadLineBytes = (int)Math.Min(int.MaxValue, 64 * 1024L);

                var response = new HttpResponseMessage() { RequestMessage = request, Content = new HttpConnectionResponseContent() };
                ParseStatusLine(await ReadNextResponseHeaderLineAsync().ConfigureAwait(false), response);

                //parse herader
                while (true)
                {
                    var headerLine = await ReadNextResponseHeaderLineAsync(true).ConfigureAwait(false);
                    if (headerLine.Count == 0)
                        break;

                    ParseHeaderLine(headerLine, response);
                }

                if (response.Headers.ConnectionClose.GetValueOrDefault())
                {
                    _connectionClose = true;
                }

                cancellationRegistration.Dispose();
                cancellationToken.ThrowIfCancellationRequested();

                Stream responseStream = null;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    responseStream = new MemoryStream();//empty
                    CompleteResponse();
                }

                if (request.Method.Method == "CONNECT")
                {
                    responseStream = new RawConnectionStream(this);
                }
                else if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength > 0)
                {
                    responseStream = new ContentLengthReadStream(this, (ulong)response.Content.Headers.ContentLength.Value);
                }
                else if (response.Headers.TransferEncodingChunked == true)
                {
                    responseStream = new ChunkedEncodingReadStream(this, response);
                }
                else
                {
                    responseStream = new MemoryStream();//empty
                    CompleteResponse();
                }
                ((HttpConnectionResponseContent)response.Content).SetStream(responseStream);

                return response;
            }
            catch (Exception error)
            {
                cancellationRegistration.Dispose();
                Dispose();
                if (error is IOException ioe)
                {
                    throw new HttpRequestException("net_http_client_execution_error");
                }
                else
                {
                    throw;
                }
            }
        }

        public bool PollRead()
        {
            return _socket.Poll(0, SelectMode.SelectRead);
        }

        public bool EnsureReadAheadAndPollRead()
        {
            try
            {
                if (_readAheadTask == null)
                {
                    _readAheadTask = _stream.ReadAsync(new Memory<byte>(_readBuffer));
                }
            }
            catch (Exception error)
            {
                _readAheadTask = new ValueTask<int>(0);
            }

            return _readAheadTask.Value.IsCompleted;
        }

        internal CancellationTokenRegistration RegisterCancellation(CancellationToken cancellationToken)
        {
            return cancellationToken.Register(s =>
            {
                var weakThisRef = (WeakReference<HttpConnection>)s;
                if (weakThisRef.TryGetTarget(out HttpConnection strongThisRef))
                {
                    strongThisRef.Dispose();
                }
            }, _weakThisRef);
        }

        internal void CompleteResponse()
        {
            if (_readLength != _readOffset)
            {
                _readOffset = _readLength = 0;
                _connectionClose = true;
            }
            ReturnConnectionToPool();
        }

        private void ReturnConnectionToPool()
        {
            if (_connectionClose)
            {
                Dispose();
            }
            else
            {
                _pool.ReturnConnection(this);
            }
        }

        private Task WriteStringAsync(string s)
        {
            int offset = _writeOffset;
            if (s.Length <= _writeBuffer.Length - offset)
            {
                byte[] writeBuffer = _writeBuffer;
                foreach (char c in s)
                {
                    if ((c & 0xFF80) != 0)
                    {
                        throw new HttpRequestException(" 小老弟 怎么回事 编码都能编歪？");
                    }
                    writeBuffer[offset++] = (byte)c;
                }
                _writeOffset = offset;
                return Task.CompletedTask;
            }
            return WriteStringAsyncSlow(s);
        }

        private Task WriteAsciiStringAsync(string s)
        {
            int offset = _writeOffset;
            if (s.Length <= _writeBuffer.Length - offset)
            {
                byte[] writeBuffer = _writeBuffer;
                foreach (char c in s)
                {
                    writeBuffer[offset++] = (byte)c;
                }
                _writeOffset = offset;
                return Task.CompletedTask;
            }

            return WriteStringAsyncSlow(s);
        }

        private async Task WriteStringAsyncSlow(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c & 0xFF80) != 0)
                {
                    throw new HttpRequestException("小老弟 怎么回事  编码都能编歪？");
                }
                await WriteByteAsync((byte)c).ConfigureAwait(false);
            }
        }

        private Task WriteByteAsync(byte b)
        {
            if (_writeOffset < _writeBuffer.Length)
            {
                _writeBuffer[_writeOffset++] = b;
                return Task.CompletedTask;
            }
            return WriteByteSlowAsync(b);
        }

        private Task WriteBytesAsync(byte[] bytes)
        {
            if (_writeOffset <= _writeBuffer.Length - bytes.Length)
            {
                Buffer.BlockCopy(bytes, 0, _writeBuffer, _writeOffset, bytes.Length);
                _writeOffset += bytes.Length;
                return Task.CompletedTask;
            }
            return WriteBytesSlowAsync(bytes);
        }

        private Task WriteTwoBytesAsync(byte b1, byte b2)
        {
            if (_writeOffset <= _writeBuffer.Length - 2)
            {
                byte[] buffer = _writeBuffer;
                buffer[_writeOffset++] = b1;
                buffer[_writeOffset++] = b2;
                return Task.CompletedTask;
            }
            return WriteTwoBytesSlowAsync(b1, b2);
        }

        private async Task WriteTwoBytesSlowAsync(byte b1, byte b2)
        {
            await WriteByteAsync(b1).ConfigureAwait(false);
            await WriteByteAsync(b2).ConfigureAwait(false);
        }

        private async Task WriteBytesSlowAsync(byte[] bytes)
        {
            int offset = 0;
            while (true)
            {
                int remaining = bytes.Length - offset;
                int toCopy = Math.Min(remaining, _writeBuffer.Length - _writeOffset);
                Buffer.BlockCopy(bytes, offset, _writeBuffer, _writeOffset, toCopy);
                _writeOffset += toCopy;
                offset += toCopy;

                if (offset == bytes.Length)
                {
                    break;
                }
                else if (_writeOffset == _writeBuffer.Length)
                {
                    await WriteToStreamAsync(_writeBuffer).ConfigureAwait(false);
                    _writeOffset = 0;
                }
            }
        }

        private async Task WriteByteSlowAsync(byte b)
        {
            await WriteToStreamAsync(_writeBuffer).ConfigureAwait(false);

            _writeBuffer[0] = b;
            _writeOffset = 1;
        }

        private ValueTask WriteToStreamAsync(ReadOnlyMemory<byte> source)
        {
            return _stream.WriteAsync(source);
        }

        private async Task WriteHeaderAsync(HttpHeaders headers)
        {
            foreach (var header in headers)
            {
                await WriteAsciiStringAsync(header.Key).ConfigureAwait(false);
                await WriteTwoBytesAsync((byte)':', (byte)' ').ConfigureAwait(false);
                foreach (var value in header.Value)
                {
                    await WriteStringAsync(value).ConfigureAwait(false);
                    if (header.Value.Count() > 1)
                    {
                        await WriteTwoBytesAsync((byte)';', (byte)' ').ConfigureAwait(false);
                    }
                }
                await WriteStringAsync("\r\n").ConfigureAwait(false);
            }
        }

        internal ValueTask FlushAsync()
        {
            if (_writeOffset > 0)
            {
                ValueTask t = WriteToStreamAsync(new ReadOnlyMemory<byte>(_writeBuffer, 0, _writeOffset));
                _writeOffset = 0;
                return t;
            }
            return default(ValueTask);
        }

        internal async ValueTask<ArraySegment<byte>> ReadNextResponseHeaderLineAsync(bool foldedHeadersAllowed = false)
        {
            int previouslyScannedBytes = 0;
            while (true)
            {
                int scanOffset = _readOffset + previouslyScannedBytes;
                int lfIndex = Array.IndexOf(_readBuffer, (byte)'\n', scanOffset, _readLength - scanOffset);
                if (lfIndex < 0)
                {
                    previouslyScannedBytes = _readLength - _readOffset;
                    _allowedReadLineBytes -= _readLength - scanOffset;
                    ThrowIfExceededAllowedReadLineBytes();
                    await FillAsync().ConfigureAwait(false);
                }
                else
                {
                    int startIndex = _readOffset;
                    int length = lfIndex - startIndex;
                    if (lfIndex > 0 && _readBuffer[lfIndex - 1] == '\r')
                    {
                        length--;
                    }

                    if (foldedHeadersAllowed && length > 0)
                    {
                        if (lfIndex + 1 == _readLength)
                        {
                            int backPos = _readBuffer[lfIndex - 1] == '\r' ? lfIndex - 2 : lfIndex - 1;
                            previouslyScannedBytes = backPos - _readOffset;
                            _allowedReadLineBytes -= backPos - scanOffset;
                            ThrowIfExceededAllowedReadLineBytes();
                            await FillAsync().ConfigureAwait(false);
                            continue;
                        }

                        char nextChar = (char)_readBuffer[lfIndex + 1];
                        if (nextChar == ' ' || nextChar == '\t')
                        {

                            if (Array.IndexOf(_readBuffer, (byte)':', _readOffset, lfIndex - _readOffset) == -1)
                            {
                                throw new HttpRequestException("net_http_invalid_response_header_folder");
                            }

                            _readBuffer[lfIndex] = (byte)' ';
                            if (_readBuffer[lfIndex - 1] == '\r')
                            {
                                _readBuffer[lfIndex - 1] = (byte)' ';
                            }

                            previouslyScannedBytes = (lfIndex + 1 - _readOffset);
                            _allowedReadLineBytes -= (lfIndex + 1 - scanOffset);
                            ThrowIfExceededAllowedReadLineBytes();
                            continue;
                        }

                    }

                    _allowedReadLineBytes -= lfIndex + 1 - scanOffset;
                    ThrowIfExceededAllowedReadLineBytes();
                    _readOffset = lfIndex + 1;

                    return new ArraySegment<byte>(_readBuffer, startIndex, length);
                }
            }
        }

        private void ThrowIfExceededAllowedReadLineBytes()
        {
            if (_allowedReadLineBytes < 0)
            {
                throw new HttpRequestException("header length不能大于64kb");
            }
        }

        internal bool TryReadNextLine(out ReadOnlySpan<byte> line)
        {
            var buffer = new ReadOnlySpan<byte>(_readBuffer, _readOffset, _readLength - _readOffset);
            int length = buffer.IndexOf((byte)'\n');
            if (length < 0)
            {
                if (_allowedReadLineBytes < buffer.Length)
                {
                    throw new HttpRequestException("net_http_response_headers_exceeded_length");
                }

                line = default(ReadOnlySpan<byte>);
                return false;
            }

            int bytesConsumed = length + 1;
            _readOffset += bytesConsumed;
            _allowedReadLineBytes -= bytesConsumed;

            line = buffer.Slice(0, length > 0 && buffer[length - 1] == '\r' ? length - 1 : length);
            return true;
        }

        internal void Fill()
        {
            int remaining = _readLength - _readOffset;

            if (remaining == 0)
            {
                _readOffset = _readLength = 0;
            }
            else if (_readOffset > 0)
            {
                Buffer.BlockCopy(_readBuffer, _readOffset, _readBuffer, 0, remaining);
                _readOffset = 0;
                _readLength = remaining;
            }
            else if (remaining == _readBuffer.Length)
            {
                var newReadBuffer = new byte[_readBuffer.Length * 2];
                Buffer.BlockCopy(_readBuffer, 0, newReadBuffer, 0, remaining);
                _readBuffer = newReadBuffer;
                _readOffset = 0;
                _readLength = remaining;
            }

            int bytesRead = _stream.Read(_readBuffer, _readLength, _readBuffer.Length - _readLength);
            if (bytesRead == 0)
            {
                throw new IOException("net_http_invalid_response_premature_eof");
            }

            _readLength += bytesRead;
        }

        internal async Task FillAsync()
        {
            var remaining = _readLength - _readOffset;

            if (remaining == 0)
            {
                _readOffset = _readLength = 0;
            }
            else if (_readOffset > 0)//merge unread buffer to new buffer
            {
                var d = Encoding.UTF8.GetString(this._readBuffer);
                Buffer.BlockCopy(_readBuffer, _readOffset, _readBuffer, 0, remaining);
                _readOffset = 0;
                _readLength = remaining;
            }
            else if (remaining == _readBuffer.Length)//resize
            {
                var newReadBuffer = new byte[_readBuffer.Length * 2];
                Buffer.BlockCopy(_readBuffer, 0, newReadBuffer, 0, remaining);
                _readBuffer = newReadBuffer;
                _readOffset = 0;
                _readLength = remaining;
            }
            var bytesRead = await _stream.ReadAsync(new Memory<byte>(_readBuffer, remaining, _readBuffer.Length - _readLength));
            if (bytesRead == 0)
            {
                throw new IOException("net_http_invalid_response_premature_eof");
            }
            _readLength += bytesRead;
        }

        private void ReadFromBuffer(Span<byte> buffer)
        {
            new Span<byte>(_readBuffer, _readOffset, buffer.Length).CopyTo(buffer);
            _readOffset += buffer.Length;
        }

        internal int Read(Span<byte> destination)
        {
            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                if (destination.Length <= remaining)
                {
                    ReadFromBuffer(destination);
                    return destination.Length;
                }
                else
                {
                    ReadFromBuffer(destination.Slice(0, remaining));
                    return remaining;
                }
            }

            int count = _stream.Read(destination);
            return count;
        }

        internal async ValueTask<int> ReadAsync(Memory<byte> destination)
        {
            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                if (destination.Length <= remaining)
                {
                    ReadFromBuffer(destination.Span);
                    return destination.Length;
                }
                else
                {
                    ReadFromBuffer(destination.Span.Slice(0, remaining));
                    return remaining;
                }
            }


            int count = await _stream.ReadAsync(destination).ConfigureAwait(false);
            return count;
        }


        internal int ReadBuffered(Span<byte> destination)
        {
            // This is called when reading the response body.
            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                if (destination.Length <= remaining)
                {
                    ReadFromBuffer(destination);
                    return destination.Length;
                }
                else
                {
                    ReadFromBuffer(destination.Slice(0, remaining));
                    return remaining;
                }
            }

            // No data in read buffer. 
            _readOffset = _readLength = 0;

            // Do a buffered read directly against the underlying stream.
            int bytesRead = _stream.Read(_readBuffer, 0, _readBuffer.Length);
            _readLength = bytesRead;

            // Hand back as much data as we can fit.
            int bytesToCopy = Math.Min(bytesRead, destination.Length);
            _readBuffer.AsSpan(0, bytesToCopy).CopyTo(destination);
            _readOffset = bytesToCopy;
            return bytesToCopy;
        }

        internal ValueTask<int> ReadBufferedAsync(Memory<byte> destination)
        {
            // If the caller provided buffer, and thus the amount of data desired to be read,
            // is larger than the internal buffer, there's no point going through the internal
            // buffer, so just do an unbuffered read.
            return destination.Length >= _readBuffer.Length ?
                ReadAsync(destination) :
                ReadBufferedAsyncCore(destination);
        }

        internal async ValueTask<int> ReadBufferedAsyncCore(Memory<byte> destination)
        {
            // This is called when reading the response body.

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                if (destination.Length <= remaining)
                {
                    ReadFromBuffer(destination.Span);
                    return destination.Length;
                }
                else
                {
                    ReadFromBuffer(destination.Span.Slice(0, remaining));
                    return remaining;
                }
            }

            // No data in read buffer. 
            _readOffset = _readLength = 0;

            // Do a buffered read directly against the underlying stream.
            int bytesRead = await _stream.ReadAsync(_readBuffer.AsMemory()).ConfigureAwait(false);
            _readLength = bytesRead;

            // Hand back as much data as we can fit.
            int bytesToCopy = Math.Min(bytesRead, destination.Length);
            _readBuffer.AsSpan(0, bytesToCopy).CopyTo(destination.Span);
            _readOffset = bytesToCopy;
            return bytesToCopy;
        }

        internal async Task CopyFromBufferAsync(Stream destination, int count, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new ReadOnlyMemory<byte>(_readBuffer, _readOffset, count), cancellationToken).ConfigureAwait(false);
            _readOffset += count;
        }

        internal async Task CopyToContentLengthAsync(Stream destination, ulong length, int bufferSize, CancellationToken cancellationToken)
        {
            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                if ((ulong)remaining > length)
                {
                    remaining = (int)length;
                }
                await CopyFromBufferAsync(destination, remaining, cancellationToken).ConfigureAwait(false);

                length -= (ulong)remaining;
                if (length == 0)
                {
                    return;
                }
            }

            byte[] origReadBuffer = null;
            try
            {
                while (true)
                {
                    await FillAsync().ConfigureAwait(false);

                    remaining = (ulong)_readLength < length ? _readLength : (int)length;
                    await CopyFromBufferAsync(destination, remaining, cancellationToken).ConfigureAwait(false);

                    length -= (ulong)remaining;
                    if (length == 0)
                    {
                        return;
                    }

                    if (origReadBuffer == null)
                    {
                        byte[] currentReadBuffer = _readBuffer;
                        if (remaining == currentReadBuffer.Length)
                        {
                            int desiredBufferSize = (int)Math.Min((ulong)bufferSize, length);
                            if (desiredBufferSize > currentReadBuffer.Length)
                            {
                                origReadBuffer = currentReadBuffer;
                                _readBuffer = ArrayPool<byte>.Shared.Rent(desiredBufferSize);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (origReadBuffer != null)
                {
                    byte[] tmp = _readBuffer;
                    _readBuffer = origReadBuffer;
                    ArrayPool<byte>.Shared.Return(tmp);

                    _readLength = _readOffset < _readLength ? 1 : 0;
                    _readOffset = 0;
                }
            }
        }

        internal Task CopyToUntilEofAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {

            int remaining = _readLength - _readOffset;
            return remaining > 0 ?
                CopyToUntilEofWithExistingBufferedDataAsync(destination, bufferSize, cancellationToken) :
                _stream.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        internal async Task CopyToUntilEofWithExistingBufferedDataAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            int remaining = _readLength - _readOffset;

            await CopyFromBufferAsync(destination, remaining, cancellationToken).ConfigureAwait(false);
            _readLength = _readOffset = 0;

            await _stream.CopyToAsync(destination, bufferSize).ConfigureAwait(false);
        }

        internal void WriteWithoutBuffering(ReadOnlySpan<byte> source)
        {
            if (_writeOffset != 0)
            {
                int remaining = _writeBuffer.Length - _writeOffset;
                if (source.Length <= remaining)
                {
                    // There's something already in the write buffer, but the content
                    // we're writing can also fit after it in the write buffer.  Copy
                    // the content to the write buffer and then flush it, so that we
                    // can do a single send rather than two.
                    WriteToBuffer(source);
                    Flush();
                    return;
                }

                // There's data in the write buffer and the data we're writing doesn't fit after it.
                // Do two writes, one to flush the buffer and then another to write the supplied content.
                Flush();
            }

            WriteToStream(source);
        }


        internal ValueTask WriteWithoutBufferingAsync(ReadOnlyMemory<byte> source)
        {
            if (_writeOffset == 0)
            {
                return WriteToStreamAsync(source);
            }

            int remaining = _writeBuffer.Length - _writeOffset;
            if (source.Length <= remaining)
            {

                WriteToBuffer(source);
                return FlushAsync();
            }

            return new ValueTask(FlushThenWriteWithoutBufferingAsync(source));
        }

        private async Task FlushThenWriteWithoutBufferingAsync(ReadOnlyMemory<byte> source)
        {
            await FlushAsync().ConfigureAwait(false);
            await WriteToStreamAsync(source).ConfigureAwait(false);
        }

        private void WriteToBuffer(ReadOnlySpan<byte> source)
        {
            source.CopyTo(new Span<byte>(_writeBuffer, _writeOffset, source.Length));
            _writeOffset += source.Length;
        }

        private void WriteToBuffer(ReadOnlyMemory<byte> source)
        {
            source.Span.CopyTo(new Span<byte>(_writeBuffer, _writeOffset, source.Length));
            _writeOffset += source.Length;
        }

        internal void Flush()
        {
            if (_writeOffset > 0)
            {
                WriteToStream(new ReadOnlySpan<byte>(_writeBuffer, 0, _writeOffset));
                _writeOffset = 0;
            }
        }

        private void WriteToStream(ReadOnlySpan<byte> source)
        {
            _stream.Write(source);
        }


        private static bool EqualsOrdinal(string left, Span<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsDigit(byte c) => (uint)(c - '0') <= '9' - '0';

        private static void ParseStatusLine(ArraySegment<byte> line, HttpResponseMessage response) =>
           ParseStatusLine((Span<byte>)line, response);

        private static void ParseStatusLine(Span<byte> line, HttpResponseMessage response)
        {
            var MinStatusLineLength = 13;

            ulong first8Bytes = BitConverter.ToUInt64(line);

            if (first8Bytes == s_http11Bytes)
            {
                response.Version = HttpVersion.Version11;
            }
            else if (first8Bytes == s_http10Bytes)
            {
                response.Version = HttpVersion.Version10;
            }
            else
            {
                byte minorVersion = line[7];
                if (IsDigit(minorVersion) &&
                    line.Slice(0, 7).SequenceEqual(s_http1DotBytes))
                {
                    response.Version = new Version(1, minorVersion - '0');
                }
                else
                {
                    throw new HttpRequestException("无效的响应状态行");
                }
            }
            byte status1 = line[9], status2 = line[10], status3 = line[11];
            if (!IsDigit(status1) || !IsDigit(status2) || !IsDigit(status3))
            {
                throw new HttpRequestException("无效的状态码");
            }
            response.StatusCode = ((HttpStatusCode)(100 * (status1 - '0') + 10 * (status2 - '0') + (status3 - '0')));

            if (line.Length == MinStatusLineLength)
            {
                response.ReasonPhrase = string.Empty;
            }
            else if (line[MinStatusLineLength] == ' ')
            {
                Span<byte> reasonBytes = line.Slice(MinStatusLineLength + 1);
                string knownReasonPhrase = HttpStatusDescription.Get(response.StatusCode);
                response.ReasonPhrase = EqualsOrdinal(knownReasonPhrase, reasonBytes) ?
                    knownReasonPhrase :
                    reasonBytes.ToString();
            }
        }

        internal static void ParseHeaderLine(ReadOnlySpan<byte> line, HttpResponseMessage response)
        {
            var pos = 0;
            while (line[pos] != (byte)':')
            {
                pos++;
                if (pos == line.Length)
                {
                    throw new HttpRequestException(Encoding.ASCII.GetString(line));
                }
            }

            if (pos == 0)
            {
                throw new HttpRequestException("无效的空header");
            }

            var headerName = Encoding.UTF8.GetString(line.Slice(0, pos));

            while (line[pos] != (byte)' ')
            {
                pos++;
                if (pos == line.Length)
                {
                    throw new HttpRequestException(Encoding.ASCII.GetString(line));
                }
            }

            var headerValue = Encoding.UTF8.GetString(line.Slice(++pos));

            if (!response.Headers.TryAddWithoutValidation(headerName, headerValue))
            {
                response.Content.Headers.TryAddWithoutValidation(headerName, headerValue);
            }
        }


        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            if (!disposed && disposing)
            {
                disposed = true;
                _stream.Dispose();
                _pool.DecrementConnectionCount();
                GC.SuppressFinalize(this);
            }
        }

    }
}
