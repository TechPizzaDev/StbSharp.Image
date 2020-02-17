using System;
using System.IO;
using System.Threading;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public class ReadContext : IDisposable
        {
            public readonly Stream Stream;
            public readonly byte[] ReadBuffer;
            public readonly CancellationToken CancellationToken;

            public readonly ReadCallback ReadCallback;
            public readonly SkipCallback SkipCallback;
            public bool ReadFromCallbacks;

            public int DataLength { get; }
            public byte* DataOriginalStart { get; private set; }
            public byte* DataOriginalEnd { get; }

            public byte* DataStart;
            public byte* Data;
            public byte* DataEnd;

            public bool vertically_flip_on_load = false;
            public bool unpremultiply_on_load = true;
            public bool de_iphone_flag = true;

            #region Constructors

            public ReadContext(byte* data, int len, CancellationToken cancellationToken)
            {
                ReadFromCallbacks = false;
                CancellationToken = cancellationToken;

                DataLength = len;
                DataStart = null;
                DataOriginalStart = data;
                Data = DataOriginalStart;
                DataEnd = DataOriginalEnd = data + len;
            }

            public ReadContext(
                Stream stream, byte[] readBuffer, CancellationToken cancellationToken,
                ReadCallback readCallback, SkipCallback skipCallback)
            {
                ReadFromCallbacks = true;
                Stream = stream;
                ReadBuffer = readBuffer;
                ReadCallback = readCallback;
                SkipCallback = skipCallback;
                CancellationToken = cancellationToken;

                DataLength = 256;
                DataOriginalStart = (byte*)CRuntime.MAlloc(DataLength);

                DataStart = DataOriginalStart;
                RefillBuffer();
                DataOriginalEnd = DataEnd;
            }

            #endregion

            public bool IsAtEndOfStream()
            {
                if (ReadCallback != null)
                {
                    if (Stream.CanRead)
                        return false; // not at eof, figure out an error?

                    if (ReadFromCallbacks)
                        return true;
                }
                return Data >= DataEnd ? true : false;
            }

            public void Skip(int n)
            {
                if (n < 0)
                {
                    Data = DataEnd;
                    return;
                }

                if (ReadCallback != null)
                {
                    int blen = (int)(DataEnd - Data);
                    if (blen < n)
                    {
                        Data = DataEnd;
                        SkipCallback(this, n - blen);
                        return;
                    }
                }

                Data += n;
            }

            public void Rewind()
            {
                Data = DataOriginalStart;
                DataEnd = DataOriginalEnd;
            }

            public void RefillBuffer()
            {
                int n = ReadCallback(this, new Span<byte>(DataStart, DataLength));
                if (n == 0)
                {
                    ReadFromCallbacks = false;
                    Data = DataStart;
                    DataEnd = DataStart;
                    DataEnd++;
                    *Data = 0;
                }
                else
                {
                    Data = DataStart;
                    DataEnd = DataStart;
                    DataEnd += n;
                }
            }
            public bool ReadBytes(Span<byte> destination)
            {
                if (ReadCallback != null)
                {
                    int bufLen = (int)(DataEnd - Data);
                    if (bufLen < destination.Length)
                    {
                        new Span<byte>(Data, bufLen).CopyTo(destination);

                        int count = ReadCallback(this, destination.Slice(bufLen));
                        Data = DataEnd;
                        return count == destination.Length - bufLen ? true : false;
                    }
                }

                if (Data + destination.Length <= DataEnd)
                {
                    new Span<byte>(Data, destination.Length).CopyTo(destination);

                    Data += destination.Length;
                    return true;
                }
                return false;
            }

            public byte ReadByte()
            {
                if (Data < DataEnd)
                    return (byte)*Data++;

                if (ReadFromCallbacks)
                {
                    RefillBuffer();
                    return *Data++;
                }

                return 0;
            }

            public int ReadInt16BE()
            {
                int z = ReadByte();
                return (z << 8) + ReadByte();
            }

            public uint ReadInt32BE()
            {
                uint z = (uint)ReadInt16BE();
                return (uint)((z << 16) + ReadInt16BE());
            }

            public int ReadInt16LE()
            {
                int z = ReadByte();
                return z + (ReadByte() << 8);
            }

            public uint ReadInt32LE()
            {
                uint z = (uint)ReadInt16LE();
                return (uint)(z + (ReadInt16LE() << 16));
            }

            #region IDisposable

            protected virtual void Dispose(bool disposing)
            {
                CRuntime.Free(DataOriginalStart);
                DataOriginalStart = null;
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