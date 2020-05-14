using System;
using System.Buffers;
using System.Threading.Tasks;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static class Bmp
        {
            public const int HeaderSize = 2;

            public class BmpInfo
            {
                public int bpp;
                public int offset;
                public int headerSize;
                public int mr;
                public int mg;
                public int mb;
                public int ma;
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

            public static Task<BmpInfo> Info(BinReader s, out ReadState ri)
            {
                ri = new ReadState();
                return ParseHeader(s, ri);
            }

            public static int HighBit(int z)
            {
                if (z == 0)
                    return -1;

                int n = 0;

                if (z >= 0x10000)
                {
                    n += 16;
                    z >>= 16;
                }

                if (z >= 0x00100)
                {
                    n += 8;
                    z >>= 8;
                }

                if (z >= 0x00010)
                {
                    n += 4;
                    z >>= 4;
                }

                if (z >= 0x00004)
                {
                    n += 2;
                    z >>= 2;
                }

                if (z >= 0x00002)
                {
                    n += 1;
                    //z >>= 1; redundant assignment
                }

                return n;
            }

            public static int BitCount(int a)
            {
                a = (a & 0x55555555) + ((a >> 1) & 0x55555555);
                a = (a & 0x33333333) + ((a >> 2) & 0x33333333);
                a = (a + (a >> 4)) & 0x0f0f0f0f;
                a += a >> 8;
                a += a >> 16;
                return a & 0xff;
            }

            public static int ShiftSigned(int v, int shift, int bits)
            {
                if (shift < 0)
                    v <<= -shift;
                else
                    v >>= shift;

                int result = v;
                int z = bits;
                while (z < 8)
                {
                    result += v >> z;
                    z += bits;
                }

                return result;
            }

            public static async Task<BmpInfo> ParseHeader(BinReader s, ReadState ri)
            {
                var tmp = new byte[HeaderSize];
                if (!await s.TryReadBytes(tmp))
                    return null;

                if (!Test(tmp))
                    throw new StbImageReadException(ErrorCode.UnknownFormat);

                var info = new BmpInfo();

                await s.ReadInt32LE();
                await s.ReadInt16LE();
                await s.ReadInt16LE();

                info.offset = await s.ReadInt32LE();
                info.headerSize = await s.ReadInt32LE();
                info.mr = info.mg = info.mb = info.ma = 0;

                if (info.headerSize != 12 &&
                    info.headerSize != 40 &&
                    info.headerSize != 56 &&
                    info.headerSize != 108 &&
                    info.headerSize != 124)
                    throw new StbImageReadException(ErrorCode.UnknownHeader);

                if (info.headerSize == 12)
                {
                    ri.Width = await s.ReadInt16LE();
                    ri.Height = await s.ReadInt16LE();
                }
                else
                {
                    ri.Width = await s.ReadInt32LE();
                    ri.Height = await s.ReadInt32LE();
                }

                ri.Orientation = ri.Height > 0
                    ? ImageOrientation.BottomLeftOrigin
                    : ImageOrientation.TopLeftOrigin;

                ri.Height = CRuntime.FastAbs(ri.Height);

                if (await s.ReadInt16LE() != 1)
                    throw new StbImageReadException(ErrorCode.BadColorPlane);

                info.bpp = await s.ReadInt16LE();
                if (info.bpp == 1)
                    throw new StbImageReadException(ErrorCode.MonochromeNotSupported);

                if (info.headerSize != 12)
                {
                    int compress = await s.ReadInt32LE();
                    if ((compress == 1) || (compress == 2))
                        throw new StbImageReadException(ErrorCode.RLENotSupported);

                    await s.ReadInt32LE();
                    await s.ReadInt32LE();
                    await s.ReadInt32LE();
                    await s.ReadInt32LE();
                    await s.ReadInt32LE();

                    if (info.headerSize == 40 ||
                        info.headerSize == 56)
                    {
                        if (info.headerSize == 56)
                        {
                            await s.ReadInt32LE();
                            await s.ReadInt32LE();
                            await s.ReadInt32LE();
                            await s.ReadInt32LE();
                        }

                        if ((info.bpp == 16) || (info.bpp == 32))
                        {
                            if (compress == 0)
                            {
                                if (info.bpp == 32)
                                {
                                    info.mr = 0xff << 16;
                                    info.mg = 0xff << 8;
                                    info.mb = 0xff << 0;
                                    info.ma = 0xff << 24;
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
                                info.mr = await s.ReadInt32LE();
                                info.mg = await s.ReadInt32LE();
                                info.mb = await s.ReadInt32LE();

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

                        info.mr = await s.ReadInt32LE();
                        info.mg = await s.ReadInt32LE();
                        info.mb = await s.ReadInt32LE();
                        info.ma = await s.ReadInt32LE();

                        await s.ReadInt32LE();

                        for (int i = 0; i < 12; ++i)
                            await s.ReadInt32LE();

                        if (info.headerSize == 124)
                            for (int i = 0; i < 4; ++i)
                                await s.ReadInt32LE();
                    }
                }


                if (info.bpp == 24 &&
                    info.ma == 0xff << 24)
                    ri.Components = 3;
                else
                    ri.Components = info.ma != 0 ? 4 : 3;

                ri.Depth = info.bpp / ri.Components;

                return info;
            }

            public static async Task<BmpInfo> Load(
                BinReader s, ReadState ri, ArrayPool<byte> bytePool = null)
            {
                var info = await ParseHeader(s, ri);
                if (info == null)
                    throw new StbImageReadException(ErrorCode.UnknownHeader);

                ri.OutComponents = ri.Components;
                ri.OutDepth = Math.Max(ri.Depth, 8);

                ri.StateReady();

                bytePool ??= ArrayPool<byte>.Shared;

                int psize = 0;
                if (info.headerSize == 12)
                {
                    if (info.bpp < 24)
                        psize = (info.offset - 14 - 24) / 3;
                }
                else
                {
                    if (info.bpp < 16)
                        psize = (info.offset - 14 - info.headerSize) >> 2;
                }

                int easy = 0;
                if (info.bpp == 32)
                {
                    if (info.mb == 0xff << 0 &&
                        info.mg == 0xff << 8 &&
                        info.mr == 0xff << 16 &&
                        info.ma == 0xff << 24)
                    {
                        easy = 2;
                    }
                }
                else if (info.bpp == 24)
                {
                    easy = 1;
                }

                bool flipRows = (ri.Orientation & ImageOrientation.BottomToTop) == ImageOrientation.BottomToTop;

                int rowByteSize = ri.Width * ri.OutComponents;
                var rowBuffer = bytePool.Rent(rowByteSize);
                try
                {
                    if (info.bpp < 16)
                    {
                        if ((psize == 0) || (psize > 256))
                            throw new StbImageReadException(ErrorCode.InvalidPLTE);

                        var palette = new byte[256 * 4];
                        for (int x = 0; x < psize; ++x)
                        {
                            palette[x * 4 + 2] = await s.ReadByte();
                            palette[x * 4 + 1] = await s.ReadByte();
                            palette[x * 4 + 0] = await s.ReadByte();

                            palette[x * 4 + 3] = info.headerSize == 12
                                ? (byte)255
                                : await s.ReadByte();
                        }

                        await s.Skip(
                            info.offset - 14 - info.headerSize -
                            psize * (info.headerSize == 12 ? 3 : 4));

                        int width;
                        if (info.bpp == 4)
                            width = (ri.Width + 1) / 2;
                        else if (info.bpp == 8)
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
                                int v1 = await s.ReadByte();
                                if (info.bpp == 4)
                                {
                                    v2 = v1 & 15;
                                    v1 >>= 4;
                                }
                                WriteFromPalette(ri.OutComponents, palette.AsSpan(v1 * 4));

                                if ((x + 1) == ri.Width)
                                    break;
                                v2 = info.bpp == 8 ? await s.ReadByte() : v2;
                                WriteFromPalette(ri.OutComponents, palette.AsSpan(v2 * 4));
                            }
                            await s.Skip(pad);

                            int row = flipRows ? (ri.Height - y - 1) : y;
                            ri.OutputPixelLine(AddressingMajor.Row, row, 0, rowBuffer.AsSpan(0, rowByteSize));
                        }
                    }
                    else
                    {
                        await s.Skip(info.offset - 14 - info.headerSize);

                        int width;
                        if (info.bpp == 24)
                            width = 3 * ri.Width;
                        else if (info.bpp == 16)
                            width = 2 * ri.Width;
                        else
                            throw new StbImageReadException(ErrorCode.BadBitsPerPixel);

                        int pad = (-width) & 3;
                        if (easy != 0)
                        {
                            for (int y = 0; y < ri.Height; ++y)
                            {
                                await s.ReadBytes(rowBuffer.AsMemory(0, rowByteSize));

                                for (int x = 0, o = 0; x < rowByteSize; x += ri.OutComponents)
                                {
                                    byte b = rowBuffer[o++];
                                    byte g = rowBuffer[o++];
                                    byte r = rowBuffer[o++];
                                    rowBuffer[x + 0] = r;
                                    rowBuffer[x + 1] = g;
                                    rowBuffer[x + 2] = b;

                                    if (ri.OutComponents == 4)
                                        rowBuffer[x + 3] = easy == 2 ? rowBuffer[o++] : (byte)255;
                                }
                                await s.Skip(pad);

                                int row = flipRows ? (ri.Height - y - 1) : y;
                                ri.OutputPixelLine(AddressingMajor.Row, row, 0, rowBuffer.AsSpan(0, rowByteSize));
                            }
                        }
                        else
                        {
                            if (info.mr == 0 || info.mg == 0 || info.mb == 0)
                                throw new StbImageReadException(ErrorCode.BadMasks);

                            int rshift = HighBit(info.mr) - 7;
                            int gshift = HighBit(info.mg) - 7;
                            int bshift = HighBit(info.mb) - 7;
                            int ashift = HighBit(info.ma) - 7;
                            int rcount = BitCount(info.mr);
                            int gcount = BitCount(info.mg);
                            int bcount = BitCount(info.mb);
                            int acount = BitCount(info.ma);

                            for (int y = 0; y < ri.Height; ++y)
                            {
                                for (int x = 0; x < rowByteSize; x += ri.OutComponents)
                                {
                                    int v = info.bpp == 16
                                        ? await s.ReadInt16LE()
                                        : await s.ReadInt32LE();

                                    rowBuffer[x + 0] = (byte)(ShiftSigned(v & info.mr, rshift, rcount) & 0xff);
                                    rowBuffer[x + 1] = (byte)(ShiftSigned(v & info.mg, gshift, gcount) & 0xff);
                                    rowBuffer[x + 2] = (byte)(ShiftSigned(v & info.mb, bshift, bcount) & 0xff);

                                    if (ri.OutComponents == 4)
                                        rowBuffer[x + 3] = info.ma != 0
                                            ? (byte)(ShiftSigned(v & info.ma, ashift, acount) & 0xff)
                                            : (byte)255;
                                }
                                await s.Skip(pad);

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
