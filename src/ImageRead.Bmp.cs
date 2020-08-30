using System;
using System.Buffers;
using System.Numerics;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static class Bmp
        {
            public const int HeaderSize = 2;

            public struct BmpInfo
            {
                public int bitsPerPixel;
                public int offset;
                public int headerSize;

                [CLSCompliant(false)]
                public uint mr;
                [CLSCompliant(false)]
                public uint mg;
                [CLSCompliant(false)]
                public uint mb;
                [CLSCompliant(false)]
                public uint ma;
            }

            public static bool Test(ReadOnlySpan<byte> header)
            {
                if (header.Length < HeaderSize)
                    return false;

                if (header[0] != 'B' ||
                    header[1] != 'M')
                    return false;

                return true;
            }

            public static BmpInfo Info(BinReader reader, out ReadState state)
            {
                state = new ReadState();
                var header = ParseHeader(reader, state);
                return header ?? throw new StbImageReadException(ErrorCode.UnknownHeader);
            }

            public static BmpInfo? ParseHeader(BinReader s, ReadState ri)
            {
                if (s == null)
                    throw new ArgumentNullException(nameof(s));
                if (ri == null)
                    throw new ArgumentNullException(nameof(ri));

                Span<byte> tmp = stackalloc byte[HeaderSize];
                if (!s.TryReadBytes(tmp))
                    return null;

                if (!Test(tmp))
                    throw new StbImageReadException(ErrorCode.UnknownFormat);

                var info = new BmpInfo();

                s.ReadInt32LE();
                s.ReadInt16LE();
                s.ReadInt16LE();

                info.offset = s.ReadInt32LE();
                info.headerSize = s.ReadInt32LE();
                info.mr = info.mg = info.mb = info.ma = 0;

                if (info.headerSize != 12 &&
                    info.headerSize != 40 &&
                    info.headerSize != 56 &&
                    info.headerSize != 108 &&
                    info.headerSize != 124)
                    throw new StbImageReadException(ErrorCode.UnknownHeader);

                if (info.headerSize == 12)
                {
                    ri.Width = s.ReadInt16LE();
                    ri.Height = s.ReadInt16LE();
                }
                else
                {
                    ri.Width = s.ReadInt32LE();
                    ri.Height = s.ReadInt32LE();
                }

                ri.Orientation = ri.Height > 0
                    ? ImageOrientation.BottomLeftOrigin
                    : ImageOrientation.TopLeftOrigin;

                ri.Height = Math.Abs(ri.Height);

                if (s.ReadInt16LE() != 1)
                    throw new StbImageReadException(ErrorCode.BadColorPlane);

                info.bitsPerPixel = s.ReadInt16LE();
                if (info.bitsPerPixel == 1)
                    throw new StbImageReadException(ErrorCode.MonochromeNotSupported);

                if (info.headerSize != 12)
                {
                    int compress = s.ReadInt32LE();
                    if ((compress == 1) || (compress == 2))
                        throw new StbImageReadException(ErrorCode.RLENotSupported);

                    s.ReadInt32LE();
                    s.ReadInt32LE();
                    s.ReadInt32LE();
                    s.ReadInt32LE();
                    s.ReadInt32LE();

                    if (info.headerSize == 40 ||
                        info.headerSize == 56)
                    {
                        if (info.headerSize == 56)
                        {
                            s.ReadInt32LE();
                            s.ReadInt32LE();
                            s.ReadInt32LE();
                            s.ReadInt32LE();
                        }

                        if ((info.bitsPerPixel == 16) || (info.bitsPerPixel == 32))
                        {
                            if (compress == 0)
                            {
                                if (info.bitsPerPixel == 32)
                                {
                                    info.mr = 0xffu << 16;
                                    info.mg = 0xffu << 8;
                                    info.mb = 0xffu << 0;
                                    info.ma = 0xffu << 24;
                                }
                                else
                                {
                                    info.mr = 31 << 10;
                                    info.mg = 31 << 5;
                                    info.mb = 31 << 0;
                                }
                            }
                            else if (compress == 3)
                            {
                                info.mr = s.ReadUInt32LE();
                                info.mg = s.ReadUInt32LE();
                                info.mb = s.ReadUInt32LE();

                                if ((info.mr == info.mg) && (info.mg == info.mb))
                                    throw new StbImageReadException(ErrorCode.BadMasks);
                            }
                            else
                            {
                                throw new StbImageReadException(ErrorCode.BadCompression);
                            }
                        }
                    }
                    else
                    {
                        if (info.headerSize != 108 &&
                            info.headerSize != 124)
                        {
                            throw new StbImageReadException(ErrorCode.UnknownHeader);
                        }

                        info.mr = s.ReadUInt32LE();
                        info.mg = s.ReadUInt32LE();
                        info.mb = s.ReadUInt32LE();
                        info.ma = s.ReadUInt32LE();

                        s.ReadInt32LE();

                        for (int i = 0; i < 12; ++i)
                            s.ReadInt32LE();

                        if (info.headerSize == 124)
                            for (int i = 0; i < 4; ++i)
                                s.ReadInt32LE();
                    }
                }

                if (info.bitsPerPixel == 24 &&
                    info.ma == 0xffu << 24)
                    ri.Components = 3;
                else
                    ri.Components = info.ma != 0 ? 4 : 3;

                ri.Depth = info.bitsPerPixel / ri.Components;

                return info;
            }

            public static BmpInfo Load(
                BinReader s, ReadState ri, ArrayPool<byte>? bytePool = null)
            {
                // TODO: optimize by pulling out some branching from loops

                bytePool ??= ArrayPool<byte>.Shared;

                var info = ParseHeader(s, ri) ??
                    throw new StbImageReadException(ErrorCode.UnknownHeader);

                ri.OutComponents = ri.Components;
                ri.OutDepth = Math.Max(ri.Depth, 8);

                ri.StateReady();

                int psize = 0;
                if (info.headerSize == 12)
                {
                    if (info.bitsPerPixel < 24)
                        psize = (info.offset - 14 - 24) / 3;
                }
                else
                {
                    if (info.bitsPerPixel < 16)
                        psize = (info.offset - 14 - info.headerSize) >> 2;
                }

                int easy = 0;
                if (info.bitsPerPixel == 32)
                {
                    if (info.mb == 0xffu << 0 &&
                        info.mg == 0xffu << 8 &&
                        info.mr == 0xffu << 16 &&
                        info.ma == 0xffu << 24)
                    {
                        easy = 2;
                    }
                }
                else if (info.bitsPerPixel == 24)
                {
                    easy = 1;
                }

                bool flipRows = (ri.Orientation & ImageOrientation.BottomToTop) == ImageOrientation.BottomToTop;

                int rowByteSize = ri.Width * ri.OutComponents;
                var rowBuffer = bytePool.Rent(rowByteSize);
                try
                {
                    if (info.bitsPerPixel < 16)
                    {
                        if ((psize == 0) || (psize > 256))
                            throw new StbImageReadException(ErrorCode.InvalidPLTE);

                        // TODO: output palette

                        Span<byte> palette = stackalloc byte[256 * 4];
                        for (int x = 0; x < psize; ++x)
                        {
                            palette[x * 4 + 2] = s.ReadByte();
                            palette[x * 4 + 1] = s.ReadByte();
                            palette[x * 4 + 0] = s.ReadByte();

                            palette[x * 4 + 3] = info.headerSize == 12
                                ? (byte)255
                                : s.ReadByte();
                        }

                        s.Skip(
                           info.offset - 14 - info.headerSize -
                           psize * (info.headerSize == 12 ? 3 : 4));

                        int width;
                        if (info.bitsPerPixel == 4)
                            width = (ri.Width + 1) / 2;
                        else if (info.bitsPerPixel == 8)
                            width = ri.Width;
                        else
                            throw new StbImageReadException(ErrorCode.BadBitsPerPixel);

                        int pad = (-width) & 3;
                        for (int y = 0; y < ri.Height; ++y)
                        {
                            for (int x = 0, z = 0; x < ri.Width; x += 2)
                            {
                                void WriteFromPalette(int comp, Span<byte> palette)
                                {
                                    rowBuffer[z + 0] = palette[0];
                                    rowBuffer[z + 1] = palette[1];
                                    rowBuffer[z + 2] = palette[2];

                                    if (comp == 4)
                                        rowBuffer[z + 3] = palette[3];
                                    z += comp;
                                }

                                int v2 = 0;
                                int v1 = s.ReadByte();
                                if (info.bitsPerPixel == 4)
                                {
                                    v2 = v1 & 15;
                                    v1 >>= 4;
                                }
                                WriteFromPalette(ri.OutComponents, palette.Slice(v1 * 4));

                                if ((x + 1) == ri.Width)
                                    break;
                                v2 = info.bitsPerPixel == 8 ? s.ReadByte() : v2;
                                WriteFromPalette(ri.OutComponents, palette.Slice(v2 * 4));
                            }
                            s.Skip(pad);

                            int row = flipRows ? (ri.Height - y - 1) : y;
                            ri.OutputPixelLine(AddressingMajor.Row, row, 0, rowBuffer.AsSpan(0, rowByteSize));
                        }
                    }
                    else
                    {
                        s.Skip(info.offset - 14 - info.headerSize);

                        int width;
                        if (info.bitsPerPixel == 32)
                            width = 4 * ri.Width;
                        else if (info.bitsPerPixel == 24)
                            width = 3 * ri.Width;
                        else if (info.bitsPerPixel == 16)
                            width = 2 * ri.Width;
                        else
                            throw new StbImageReadException(ErrorCode.BadBitsPerPixel);

                        int pad = (-width) & 3;
                        if (easy != 0)
                        {
                            for (int y = 0; y < ri.Height; ++y)
                            {
                                s.ReadBytes(rowBuffer.AsSpan(0, rowByteSize));

                                for (int x = 0, o = 0; x < rowByteSize; x += ri.OutComponents)
                                {
                                    byte b = rowBuffer[o++];
                                    byte g = rowBuffer[o++];
                                    byte r = rowBuffer[o++];
                                    rowBuffer[x + 2] = b;
                                    rowBuffer[x + 1] = g;
                                    rowBuffer[x + 0] = r;

                                    if (ri.OutComponents == 4)
                                        rowBuffer[x + 3] = easy == 2 ? rowBuffer[o++] : (byte)255;
                                }
                                s.Skip(pad);

                                int row = flipRows ? (ri.Height - y - 1) : y;
                                ri.OutputPixelLine(AddressingMajor.Row, row, 0, rowBuffer.AsSpan(0, rowByteSize));
                            }
                        }
                        else
                        {
                            if (info.mr == 0 || info.mg == 0 || info.mb == 0)
                                throw new StbImageReadException(ErrorCode.BadMasks);

                            int rBits = BitOperations.PopCount(info.mr);
                            int gBits = BitOperations.PopCount(info.mg);
                            int bBits = BitOperations.PopCount(info.mb);
                            int aBits = BitOperations.PopCount(info.ma);
                            int rShift = 32 - BitOperations.LeadingZeroCount(info.mr) - rBits;
                            int gShift = 32 - BitOperations.LeadingZeroCount(info.mg) - gBits;
                            int bShift = 32 - BitOperations.LeadingZeroCount(info.mb) - bBits;
                            int aShift = 32 - BitOperations.LeadingZeroCount(info.ma) - aBits;

                            bool is8Bpc = rBits == 8 && gBits == 8 && bBits == 8 && aBits == 8;

                            for (int y = 0; y < ri.Height; y++)
                            {
                                if (is8Bpc)
                                {
                                    for (int x = 0; x < rowByteSize; x += ri.OutComponents)
                                    {
                                        uint v = info.bitsPerPixel == 16
                                            ? s.ReadUInt16LE()
                                            : s.ReadUInt32LE();

                                        if (ri.OutComponents == 4)
                                            rowBuffer[x + 3] = info.ma != 0
                                                ? (byte)((v & info.ma) >> aShift)
                                                : (byte)255;

                                        rowBuffer[x + 2] = (byte)((v & info.mb) >> bShift);
                                        rowBuffer[x + 1] = (byte)((v & info.mg) >> gShift);
                                        rowBuffer[x + 0] = (byte)((v & info.mr) >> rShift);
                                    }
                                }
                                else
                                {
                                    for (int x = 0; x < rowByteSize; x += ri.OutComponents)
                                    {
                                        uint v = info.bitsPerPixel == 16
                                            ? s.ReadUInt16LE()
                                            : s.ReadUInt32LE();

                                        if (ri.OutComponents == 4)
                                            rowBuffer[x + 3] = info.ma != 0
                                                ? (byte)(((v & info.ma) >> aShift) & 0xff)
                                                : (byte)255;

                                        rowBuffer[x + 2] = (byte)(((v & info.mb) >> bShift) & 0xff);
                                        rowBuffer[x + 1] = (byte)(((v & info.mg) >> gShift) & 0xff);
                                        rowBuffer[x + 0] = (byte)(((v & info.mr) >> rShift) & 0xff);
                                    }
                                }
                                s.Skip(pad);

                                int row = flipRows ? (ri.Height - y - 1) : y;
                                ri.OutputPixelLine(AddressingMajor.Row, row, 0, rowBuffer.AsSpan(0, rowByteSize));
                            }
                        }
                    }
                }
                finally
                {
                    bytePool.Return(rowBuffer);
                }
                return info;
            }
        }
    }
}
