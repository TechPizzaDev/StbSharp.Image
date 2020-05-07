using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public class BinReader : IAsyncDisposable
        {
            private byte[] _buffer;
            private int _bufferOffset;
            private int _bufferLength;
            private long _position;

            public Stream Stream { get; }
            public bool LeaveOpen { get; }
            public CancellationToken CancellationToken { get; }

            public long Position => _position + _bufferOffset;

            public BinReader(
                Stream stream,
                byte[] buffer,
                bool leaveOpen,
                CancellationToken cancellationToken = default)
            {
                Stream = stream ?? throw new ArgumentNullException(nameof(stream));
                _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
                LeaveOpen = leaveOpen;
                CancellationToken = cancellationToken;
            }

            private Span<byte> Take(int count)
            {
                if (count > _bufferLength)
                    throw new EndOfStreamException();

                var slice = _buffer.AsSpan(_bufferOffset, count);
                _bufferOffset += count;
                _bufferLength -= count;
                return slice;
            }

            private async ValueTask FillBuffer()
            {
                _buffer.AsSpan(_bufferOffset, _bufferLength).CopyTo(_buffer);

                _position += _bufferOffset;
                _bufferOffset = 0;

                while (_bufferLength < _buffer.Length)
                {
                    var slice = _buffer.AsMemory(_bufferLength);
                    int read = await Stream.ReadAsync(slice, CancellationToken);
                    if (read == 0)
                        break;

                    _bufferLength += read;
                }
            }

            private async ValueTask FillBufferAndCheck(int requiredCount)
            {
                await FillBuffer();

                if (requiredCount > _bufferLength)
                    throw new EndOfStreamException();
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask Skip(long count)
            {
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));

                if (count == 0)
                    return;

                if (_bufferLength > 0)
                {
                    int toRead = (int)Math.Min(count, _bufferLength);
                    _bufferOffset += toRead;
                    _bufferLength -= toRead;
                    count -= toRead;
                }

                while (count > 0)
                {
                    int toRead = (int)Math.Min(count, _buffer.Length);
                    var slice = _buffer.AsMemory(0, toRead);
                    int read = await Stream.ReadAsync(slice, CancellationToken);
                    if (read == 0)
                        break;

                    count -= read;
                    _position += read;
                }

                if (count > 0)
                    throw new EndOfStreamException();
            }

            public async ValueTask<bool> TryReadBytes(Memory<byte> destination)
            {
                if (destination.IsEmpty)
                    return true;

                // TODO: read into buffer if destination is small

                if (_bufferLength > 0)
                {
                    int toRead = Math.Min(destination.Length, _bufferLength);
                    Take(toRead).CopyTo(destination.Span);
                    destination = destination.Slice(toRead);
                }

                while (destination.Length > 0)
                {
                    int read = await Stream.ReadAsync(destination, CancellationToken);
                    if (read == 0)
                        break;

                    destination = destination.Slice(read);
                    _position += read;
                }

                return destination.IsEmpty;
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask ReadBytes(Memory<byte> destination)
            {
                if (!await TryReadBytes(destination))
                    throw new EndOfStreamException();
            }

            public async ValueTask<int> TryReadByte()
            {
                if (_bufferLength < sizeof(byte))
                {
                    await FillBuffer();

                    if (_bufferLength < sizeof(byte))
                        return -1;
                }
                return Take(sizeof(byte))[0];
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<byte> ReadByte()
            {
                if (_bufferLength < sizeof(byte))
                    await FillBufferAndCheck(sizeof(byte));

                byte value = _buffer[_bufferOffset];
                _bufferOffset += sizeof(byte);
                _bufferLength -= sizeof(byte);
                return value;
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<short> ReadInt16LE()
            {
                if (_bufferLength < sizeof(short))
                    await FillBufferAndCheck(sizeof(short));
                return BinaryPrimitives.ReadInt16LittleEndian(Take(sizeof(short)));
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<short> ReadInt16BE()
            {
                if (_bufferLength < sizeof(short))
                    await FillBufferAndCheck(sizeof(short));
                return BinaryPrimitives.ReadInt16BigEndian(Take(sizeof(short)));
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<ushort> ReadUInt16LE()
            {
                if (_bufferLength < sizeof(ushort))
                    await FillBufferAndCheck(sizeof(ushort));
                return BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<ushort> ReadUInt16BE()
            {
                if (_bufferLength < sizeof(ushort))
                    await FillBufferAndCheck(sizeof(ushort));
                return BinaryPrimitives.ReadUInt16BigEndian(Take(sizeof(ushort)));
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<int> ReadInt32LE()
            {
                if (_bufferLength < sizeof(int))
                    await FillBufferAndCheck(sizeof(int));
                return BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<int> ReadInt32BE()
            {
                if (_bufferLength < sizeof(int))
                    await FillBufferAndCheck(sizeof(int));
                return BinaryPrimitives.ReadInt32BigEndian(Take(sizeof(int)));
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public async ValueTask<uint> ReadUInt32BE()
            {
                if (_bufferLength < sizeof(uint))
                    await FillBufferAndCheck(sizeof(uint));
                return BinaryPrimitives.ReadUInt32BigEndian(Take(sizeof(uint)));
            }

            #region IAsyncDisposable

            protected virtual async ValueTask DisposeAsync(bool disposing)
            {
                if (disposing)
                {
                    if (!LeaveOpen)
                        await Stream.DisposeAsync();
                }
            }

            public ValueTask DisposeAsync()
            {
                return DisposeAsync(true);
            }

            #endregion
        }
    }
}