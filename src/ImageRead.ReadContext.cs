using System;
using System.IO;
using System.Threading;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public delegate int ReadCallback(ReadContext context, Span<byte> data);
        public delegate int SkipCallback(ReadContext context, int count);

        public class ReadContext : IDisposable
        {
            public Stream Stream { get; }
            public CancellationToken CancellationToken { get; }

            public ReadCallback ReadCallback { get; }
            public SkipCallback SkipCallback { get; }
            public bool ReadFromCallbacks { get; private set; }

            public int DataLength { get; }
            public byte* DataStartOriginal { get; private set; }
            public byte* DataEndOriginal { get; }

            public byte* DataStart { get; private set; }
            public byte* Data { get; private set; }
            public byte* DataEnd { get; private set; }

            public bool VerticallyFlipOnLoad { get; set; } = false;
            public bool UnpremultiplyOnLoad { get; set; } = true;
            public bool DeIphoneFlag { get; set; } = true;

            public ErrorCode ErrorCode { get; private set; }

            #region Constructors

            public ReadContext(byte* data, int len, CancellationToken cancellationToken)
            {
                ReadFromCallbacks = false;
                CancellationToken = cancellationToken;

                DataLength = len;
                DataStart = null;
                DataStartOriginal = data;
                Data = DataStartOriginal;
                DataEnd = DataEndOriginal = data + len;
            }

            public ReadContext(
                Stream stream, CancellationToken cancellationToken,
                ReadCallback readCallback, SkipCallback skipCallback)
            {
                ReadFromCallbacks = true;
                Stream = stream;
                ReadCallback = readCallback;
                SkipCallback = skipCallback;
                CancellationToken = cancellationToken;

                DataLength = 256;
                DataStartOriginal = (byte*)CRuntime.MAlloc(DataLength);

                DataStart = DataStartOriginal;
                RefillBuffer();
                DataEndOriginal = DataEnd;
            }

            #endregion

            public void Error(ErrorCode code)
            {
                ErrorCode = code;
            }

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

            public void Skip(int count)
            {
                if (count < 0)
                {
                    Data = DataEnd;
                    return;
                }

                if (ReadCallback != null)
                {
                    int blen = (int)(DataEnd - Data);
                    if (blen < count)
                    {
                        Data = DataEnd;
                        SkipCallback(this, count - blen);
                        return;
                    }
                }

                Data += count;
            }

            public void Rewind()
            {
                Data = DataStartOriginal;
                DataEnd = DataEndOriginal;
            }

            public void RefillBuffer()
            {
                int count = ReadCallback(this, new Span<byte>(DataStart, DataLength));
                if (count == 0)
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
                    DataEnd += count;
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
                    return *Data++;

                if (ReadFromCallbacks)
                {
                    RefillBuffer();
                    return *Data++;
                }

                return 0;
            }

            public short ReadInt16LE()
            {
                byte z = ReadByte();
                return (short)(z + (ReadByte() << 8));
            }

            public short ReadInt16BE()
            {
                byte z = ReadByte();
                return (short)((z << 8) + ReadByte());
            }

            public int ReadInt32LE()
            {
                short z = ReadInt16LE();
                return z + (ReadInt16LE() << 16);
            }

            public int ReadInt32BE()
            {
                short z = ReadInt16BE();
                return (z << 16) + ReadInt16BE();
            }

            #region IDisposable

            protected virtual void Dispose(bool disposing)
            {
                CRuntime.Free(DataStartOriginal);
                DataStartOriginal = null;
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