using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public class ReadContext : IDisposable
        {
            public Stream Stream { get; }
            public bool LeaveOpen { get; }
            public CancellationToken CancellationToken { get; }

            public long StreamPosition { get; private set; }

            public bool UnpremultiplyOnLoad { get; set; } = true;
            public bool DeIphoneFlag { get; set; } = true;

            public ReadContext(
                Stream stream,
                bool leaveOpen,
                CancellationToken cancellationToken)
            {
                Stream = stream;
                LeaveOpen = leaveOpen;
                CancellationToken = cancellationToken;
            }

            public void Skip(int count)
            {
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));

                if (count == 0)
                    return;

                int skipped = 0;

                if (Stream.CanSeek)
                {
                    long previous = Stream.Position;
                    long current = Stream.Seek(count, SeekOrigin.Current);
                    skipped = (int)(current - previous);
                }
                else
                {
                    Span<byte> buffer = stackalloc byte[1024];
                    int left = count;
                    while (left > 0)
                    {
                        int toRead = Math.Min(left, buffer.Length);
                        int read = Stream.Read(buffer.Slice(0, toRead));
                        if (read == 0)
                            break;

                        left -= read;
                        skipped += read;
                    }
                }

                if (skipped != count)
                    throw new EndOfStreamException();

                StreamPosition += skipped;
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public void ReadBytes(Span<byte> destination)
            {
                int read = Stream.Read(destination);
                if (read != destination.Length)
                    throw new EndOfStreamException();

                StreamPosition += read;
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public byte ReadByte()
            {
                int value = Stream.ReadByte();
                if (value == -1)
                    throw new EndOfStreamException();

                StreamPosition++;
                return (byte)value;
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public short ReadInt16LE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(short)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadInt16LittleEndian(tmp);
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public ushort ReadUInt16LE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(ushort)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadUInt16LittleEndian(tmp);
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public short ReadInt16BE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(short)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadInt16BigEndian(tmp);
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public ushort ReadUInt16BE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(ushort)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadUInt16BigEndian(tmp);
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public int ReadInt32LE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(int)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadInt32LittleEndian(tmp);
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public int ReadInt32BE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(int)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadInt32BigEndian(tmp);
            }

            /// <summary>
            /// </summary>
            /// <exception cref="EndOfStreamException"/>
            public uint ReadUInt32BE()
            {
                Span<byte> tmp = stackalloc byte[sizeof(uint)];
                ReadBytes(tmp);
                return BinaryPrimitives.ReadUInt32BigEndian(tmp);
            }

            #region IDisposable

            protected virtual void Dispose(bool disposing)
            {
                if (!LeaveOpen)
                    Stream?.Dispose();
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            ~ReadContext()
            {
                Dispose(false);
            }

            #endregion
        }
    }
}