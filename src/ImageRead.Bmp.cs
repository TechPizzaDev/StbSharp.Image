using System;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static unsafe class Bmp
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct BmpInfo
            {
                public int bpp;
                public int offset;
                public int headerSize;
                public uint mr;
                public uint mg;
                public uint mb;
                public uint ma;
                public uint all_a;
            }

            public static bool Test(ReadContext s)
            {
                var info = new BmpInfo();
                var ri = new ReadState();

                bool r = ParseHeader(s, ref info, ref ri, ScanMode.Type);
                s.Rewind();
                return r;
            }

            public static bool Info(ReadContext s, out ReadState ri)
            {
                var info = new BmpInfo();
                info.all_a = 255;

                ri = new ReadState();
                bool success = ParseHeader(s, ref info, ref ri, ScanMode.Header);
                s.Rewind();
                return success;
            }

            public static int HighBit(uint z)
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

            public static int BitCount(uint a)
            {
                a = (a & 0x55555555) + ((a >> 1) & 0x55555555);
                a = (a & 0x33333333) + ((a >> 2) & 0x33333333);
                a = (a + (a >> 4)) & 0x0f0f0f0f;
                a += a >> 8;
                a += a >> 16;
                return (int)(a & 0xff);
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

            public static bool ParseHeader(
                ReadContext s, ref BmpInfo info, ref ReadState ri, ScanMode scan)
            {
                if (s.ReadByte() != 'B' ||
                    s.ReadByte() != 'M')
                {
                    s.Error(ErrorCode.NotBMP);
                    return false;
                }

                s.ReadInt32LE();
                s.ReadInt16LE();
                s.ReadInt16LE();

                info.offset = (int)s.ReadInt32LE();
                info.headerSize = (int)s.ReadInt32LE();
                info.mr = info.mg = info.mb = info.ma = 0;

                if (info.headerSize != 12 &&
                    info.headerSize != 40 &&
                    info.headerSize != 56 &&
                    info.headerSize != 108 &&
                    info.headerSize != 124)
                {
                    s.Error(ErrorCode.UnknownHeader);
                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                if (info.headerSize == 12)
                {
                    ri.Width = s.ReadInt16LE();
                    ri.Height = s.ReadInt16LE();
                }
                else
                {
                    ri.Width = (int)s.ReadInt32LE();
                    ri.Height = (int)s.ReadInt32LE();
                }
                ri.Height = CRuntime.FastAbs(ri.Height);

                if (s.ReadInt16LE() != 1)
                {
                    s.Error(ErrorCode.BadBMP);
                    return false;
                }

                info.bpp = s.ReadInt16LE();
                if (info.bpp == 1)
                {
                    s.Error(ErrorCode.MonochromeNotSupported);
                    return false;
                }

                if (info.headerSize != 12)
                {
                    int compress = (int)s.ReadInt32LE();
                    if ((compress == 1) || (compress == 2))
                    {
                        s.Error(ErrorCode.RLENotSupported);
                        return false;
                    }

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

                        if ((info.bpp == 16) || (info.bpp == 32))
                        {
                            if (compress == 0)
                            {
                                if (info.bpp == 32)
                                {
                                    info.mr = 0xffu << 16;
                                    info.mg = 0xffu << 8;
                                    info.mb = 0xffu << 0;
                                    info.ma = 0xffu << 24;
                                    info.all_a = 0;
                                }
                                else
                                {
                                    info.mr = 31u << 10;
                                    info.mg = 31u << 5;
                                    info.mb = 31u << 0;
                                }
                            }
                            else if (compress == 3)
                            {
                                info.mr = s.ReadInt32LE();
                                info.mg = s.ReadInt32LE();
                                info.mb = s.ReadInt32LE();

                                if ((info.mr == info.mg) && (info.mg == info.mb))
                                {
                                    s.Error(ErrorCode.BadBMP);
                                    return false;
                                }
                            }
                            else
                            {
                                s.Error(ErrorCode.BadBMP);
                                return false;
                            }
                        }
                    }
                    else
                    {
                        if (info.headerSize != 108 &&
                            info.headerSize != 124)
                        {
                            s.Error(ErrorCode.BadBMP);
                            return false;
                        }

                        info.mr = s.ReadInt32LE();
                        info.mg = s.ReadInt32LE();
                        info.mb = s.ReadInt32LE();
                        info.ma = s.ReadInt32LE();

                        s.ReadInt32LE();
                        for (int i = 0; i < 12; ++i)
                            s.ReadInt32LE();

                        if (info.headerSize == 124)
                        {
                            s.ReadInt32LE();
                            s.ReadInt32LE();
                            s.ReadInt32LE();
                            s.ReadInt32LE();
                        }
                    }
                }


                if (info.bpp == 24 && info.ma == 0xff000000)
                    ri.Components = 3;
                else
                    ri.Components = info.ma != 0 ? 4 : 3;

                ri.Depth = info.bpp / ri.Components;

                return true;
            }

            public static IMemoryHolder Load(ReadContext s, ref ReadState ri)
            {
                var info = new BmpInfo();
                info.all_a = 255;

                if (!ParseHeader(s, ref info, ref ri, ScanMode.Load))
                    return null;

                if (ri.RequestedComponents.HasValue && ri.RequestedComponents >= 3)
                    ri.OutComponents = ri.RequestedComponents.Value;
                else
                    ri.OutComponents = ri.Components;
                
                ri.OutDepth = ri.RequestedDepth ?? ri.Depth;

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

                if (AreValidMad3Sizes(ri.OutComponents, ri.Width, ri.Height, 0) == 0)
                {
                    s.Error(ErrorCode.TooLarge);
                    return null;
                }

                byte* _out_ = (byte*)MAllocMad3(ri.OutComponents, ri.Width, ri.Height, 0);
                if (_out_ == null)
                {
                    s.Error(ErrorCode.OutOfMemory);
                    return null;
                }

                int easy = 0;
                if (info.bpp == 24)
                {
                    easy = 1;
                }
                else if (info.bpp == 32)
                {
                    if (info.mb == 0xff &&
                        info.mg == 0xff00 &&
                        info.mr == 0x00ff0000 &&
                        info.ma == 0xff000000)
                    {
                        easy = 2;
                    }
                }

                int rowComp = easy == 2 ? 4 : 3;
                int rowBufferSize = ri.Width * Math.Max(ri.Components, ri.OutComponents);
                byte* rowBuffer = (byte*)CRuntime.MAlloc(rowBufferSize);
                var rowBufferSpan = new Span<byte>(rowBuffer, rowBufferSize);
                try
                {
                    int width;
                    int pad;
                    if (info.bpp < 16)
                    {
                        int z = 0;
                        if ((psize == 0) || (psize > 256))
                        {
                            CRuntime.Free(_out_);
                            s.Error(ErrorCode.InvalidPalette);
                            return null;
                        }

                        byte* pal = stackalloc byte[256 * 4];
                        for (int x = 0; x < psize; ++x)
                        {
                            pal[x * 4 + 2] = s.ReadByte();
                            pal[x * 4 + 1] = s.ReadByte();
                            pal[x * 4 + 0] = s.ReadByte();
                            if (info.headerSize != 12)
                                s.ReadByte();
                            pal[x * 4 + 3] = 255;
                        }

                        s.Skip(info.offset - 14 - info.headerSize - psize * (info.headerSize == 12 ? 3 : 4));

                        if (info.bpp == 4)
                            width = (ri.Width + 1) >> 1;
                        else if (info.bpp == 8)
                            width = ri.Width;
                        else
                        {
                            CRuntime.Free(_out_);
                            s.Error(ErrorCode.BadBitsPerPixel);
                            return null;
                        }

                        pad = (-width) & 3;
                        for (int y = 0; y < ri.Height; ++y)
                        {
                            for (int x = 0; x < ri.Width; x += 2)
                            {
                                int v2 = 0;
                                int v1 = s.ReadByte();
                                if (info.bpp == 4)
                                {
                                    v2 = v1 & 15;
                                    v1 >>= 4;
                                }

                                _out_[z++] = pal[v1 * 4 + 0];
                                _out_[z++] = pal[v1 * 4 + 1];
                                _out_[z++] = pal[v1 * 4 + 2];

                                if (ri.OutComponents == 4)
                                    _out_[z++] = 255;

                                if ((x + 1) == ri.Width)
                                    break;

                                v1 = (info.bpp == 8) ? s.ReadByte() : v2;
                                _out_[z++] = pal[v1 * 4 + 0];
                                _out_[z++] = pal[v1 * 4 + 1];
                                _out_[z++] = pal[v1 * 4 + 2];

                                if (ri.OutComponents == 4)
                                    _out_[z++] = 255;
                            }

                            s.Skip(pad);
                        }
                    }
                    else
                    {
                        s.Skip(info.offset - 14 - info.headerSize);

                        if (info.bpp == 24)
                            width = 3 * ri.Width;
                        else if (info.bpp == 16)
                            width = 2 * ri.Width;
                        else
                            width = 0;
                        pad = (-width) & 3;

                        int rshift = 0, gshift = 0, bshift = 0, ashift = 0;
                        int rcount = 0, gcount = 0, bcount = 0, acount = 0;
                        if (easy == 0)
                        {
                            if (info.mr == 0 || info.mg == 0 || info.mb == 0)
                            {
                                CRuntime.Free(_out_);
                                s.Error(ErrorCode.BadMasks);
                                return null;
                            }

                            rshift = HighBit(info.mr) - 7;
                            rcount = BitCount(info.mr);

                            gshift = HighBit(info.mg) - 7;
                            gcount = BitCount(info.mg);

                            bshift = HighBit(info.mb) - 7;
                            bcount = BitCount(info.mb);

                            ashift = HighBit(info.ma) - 7;
                            acount = BitCount(info.ma);
                        }

                        byte a = 255;
                        int z = 0;
                        if (easy != 0)
                        {
                            var rowBufferSlice = new Span<byte>(rowBuffer, ri.Width * rowComp);

                            if (easy != 2)
                                info.all_a = 255;

                            for (int y = 0; y < ri.Height; ++y)
                            {
                                if (!s.ReadBytes(rowBufferSlice))
                                    break;

                                for (int x = 0, o = 0; x < ri.Width; x++, z += rowComp)
                                {
                                    _out_[z + 2] = rowBuffer[o++];
                                    _out_[z + 1] = rowBuffer[o++];
                                    _out_[z + 0] = rowBuffer[o++];

                                    if (easy == 2)
                                    {
                                        a = rowBuffer[o++];
                                        info.all_a |= a;
                                    }

                                    if (ri.OutComponents == 4)
                                        _out_[z + 3] = a;
                                }
                                s.Skip(pad);
                            }
                        }
                        else
                        {
                            if (info.ma == 0)
                                info.all_a = 255;

                            for (int y = 0; y < ri.Height; ++y)
                            {
                                for (int x = 0; x < ri.Width; x++, z += rowComp)
                                {
                                    uint v = info.bpp == 16 ? (uint)s.ReadInt16LE() : s.ReadInt32LE();
                                    _out_[z + 0] = (byte)(ShiftSigned((int)(v & info.mr), rshift, rcount) & 0xff);
                                    _out_[z + 1] = (byte)(ShiftSigned((int)(v & info.mg), gshift, gcount) & 0xff);
                                    _out_[z + 2] = (byte)(ShiftSigned((int)(v & info.mb), bshift, bcount) & 0xff);

                                    if (info.ma != 0)
                                    {
                                        a = (byte)(ShiftSigned((int)(v & info.ma), ashift, acount) & 0xff);
                                        info.all_a |= a;
                                    }

                                    if (ri.OutComponents == 4)
                                        _out_[z + 3] = a;
                                }
                                s.Skip(pad);
                            }
                        }
                    }

                    int outStride = ri.Width * ri.OutComponents;
                    if (ri.OutComponents == 4 && info.all_a == 0)
                    {
                        for (int x = outStride * ri.Height - 1; x >= 0; x -= 4)
                            _out_[x] = 255;
                    }

                    bool flip_vertically = ri.Height > 0;
                    if (flip_vertically)
                    {
                        int rows = ri.Height >> 1;
                        for (int y = 0; y < rows; ++y)
                        {
                            var row1 = new Span<byte>(_out_ + y * outStride, outStride);
                            var row2 = new Span<byte>(_out_ + (ri.Height - 1 - y) * outStride, outStride);

                            row1.CopyTo(rowBufferSpan);
                            row2.CopyTo(row1);
                            rowBufferSpan.CopyTo(row2);
                        }
                    }

                    IMemoryHolder result = new HGlobalMemoryHolder(_out_, ri.Height * outStride);

                    var errorCode = ConvertFormat(result, ref ri, out var convertedResult);
                    if (errorCode != ErrorCode.Ok)
                        return null;
                    return convertedResult;
                }
                finally
                {
                    CRuntime.Free(rowBuffer);
                }
            }
        }
    }
}
