
using System;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static unsafe class Png
        {
            public enum FilterType : byte
            {
                None = 0,
                Sub = 1,
                Up = 2,
                Average = 3,
                Paeth = 4,
                AverageFirst = 5,
                PaethFirst = 6
            }

            #region Constants

            private static byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

            private static FilterType[] FirstRowFilters =
            {
                FilterType.None,
                FilterType.Sub,
                FilterType.None,
                FilterType.AverageFirst,
                FilterType.PaethFirst
            };

            private static byte[] DepthScaleTable = { 0, 0xff, 0x55, 0, 0x11, 0, 0, 0, 0x01 };

            #endregion

            [StructLayout(LayoutKind.Sequential)]
            public readonly struct PngChunkHeader
            {
                public const int CgBI = ('C' << 24) + ('g' << 16) + ('B' << 8) + 'I';
                public const int IHDR = ('I' << 24) + ('H' << 16) + ('D' << 8) + 'R';
                public const int PLTE = ('P' << 24) + ('L' << 16) + ('T' << 8) + 'E';
                public const int tRNS = ('t' << 24) + ('R' << 16) + ('N' << 8) + 'S';
                public const int IDAT = ('I' << 24) + ('D' << 16) + ('A' << 8) + 'T';
                public const int IEND = ('I' << 24) + ('E' << 16) + ('N' << 8) + 'D';

                public readonly int Length;
                public readonly int Type;

                public PngChunkHeader(int length, int type)
                {
                    Length = length;
                    Type = type;
                }
            }

            public struct PngContext
            {
                public readonly ReadContext s;

                public byte* idata;
                public byte* _out_;

                public PngContext(ReadContext s) : this()
                {
                    this.s = s;
                }

                public void Dispose()
                {
                    CRuntime.Free(_out_);
                    CRuntime.Free(idata);
                    _out_ = null;
                    idata = null;
                }
            }

            public static PngChunkHeader ReadChunkHeader(ReadContext s)
            {
                return new PngChunkHeader(
                    length: s.ReadInt32BE(),
                    type: s.ReadInt32BE());
            }

            public static bool CheckSignature(ReadContext s)
            {
                for (int i = 0; i < 8; ++i)
                    if (s.ReadByte() != Signature[i])
                        return false;
                return true;
            }

            public static bool CreateImageCore(
                ref PngContext a, ReadOnlySpan<byte> raw, int out_n,
                int width, int height, int comp, int depth, int color)
            {
                int bytes = depth == 16 ? 2 : 1;
                int out_bytes = out_n * bytes;
                int x = width;

                a._out_ = (byte*)ImageReadHelpers.MAllocMad3(width, height, out_bytes, 0);
                if (a._out_ == null)
                {
                    a.s.Error(ErrorCode.OutOfMemory);
                    return false;
                }

                uint img_width_bytes = (uint)(((comp * width * depth) + 7) >> 3);
                uint img_len = (uint)((img_width_bytes + 1) * height);
                if (raw.Length < img_len)
                {
                    a.s.Error(ErrorCode.NotEnoughPixels);
                    return false;
                }

                uint stride = (uint)(width * out_n * bytes);
                int filter_bytes = comp * bytes;
                uint i;
                uint j;
                int k;

                int rawOffset = 0;
                for (j = 0; j < height; ++j)
                {
                    byte* current = a._out_ + stride * j;
                    byte* prior;
                    var filter = (FilterType)raw[rawOffset++];
                    if ((int)filter > 4)
                    {
                        a.s.Error(ErrorCode.InvalidFilter);
                        return false;
                    }

                    if (depth < 8)
                    {
                        current += width * out_n - img_width_bytes;
                        filter_bytes = 1;
                        x = (int)img_width_bytes;
                    }

                    prior = current - stride;
                    if (j == 0)
                        filter = FirstRowFilters[(int)filter];

                    k = 0;
                    switch (filter)
                    {
                        case FilterType.None:
                        case FilterType.Sub:
                        case FilterType.AverageFirst:
                        case FilterType.PaethFirst:
                            for (; k < filter_bytes; ++k)
                                current[k] = raw[k + rawOffset];
                            break;

                        case FilterType.Up:
                            for (; k < filter_bytes; ++k)
                                current[k] = (byte)((raw[k + rawOffset] + prior[k]) & 255);
                            break;

                        case FilterType.Average:
                            for (; k < filter_bytes; ++k)
                                current[k] = (byte)((raw[k + rawOffset] + (prior[k] >> 1)) & 255);
                            break;

                        case FilterType.Paeth:
                            for (; k < filter_bytes; ++k)
                                current[k] = (byte)((raw[k + rawOffset] + CRuntime.Paeth32(0, prior[k], 0)) & 255);
                            break;
                    }

                    if (depth == 8)
                    {
                        if (comp != out_n)
                            current[comp] = 255;
                        rawOffset += comp;
                        current += out_n;
                        prior += out_n;
                    }
                    else if (depth == 16)
                    {
                        if (comp != out_n)
                        {
                            current[filter_bytes] = 255;
                            current[filter_bytes + 1] = 255;
                        }

                        rawOffset += filter_bytes;
                        current += out_bytes;
                        prior += out_bytes;
                    }
                    else
                    {
                        rawOffset += 1;
                        current += 1;
                        prior += 1;
                    }

                    if ((depth < 8) || (comp == out_n))
                    {
                        int nk = (x - 1) * filter_bytes;
                        k = 0;
                        switch (filter)
                        {
                            case FilterType.None:
                                raw.Slice(rawOffset, nk).CopyTo(new Span<byte>(current, nk));
                                break;

                            case FilterType.Sub:
                                for (; k < nk; ++k)
                                    current[k] = (byte)((raw[k + rawOffset] + current[k - filter_bytes]) & 255);
                                break;

                            case FilterType.Up:
                                for (; k < nk; ++k)
                                    current[k] = (byte)((raw[k + rawOffset] + prior[k]) & 255);
                                break;

                            case FilterType.Average:
                                for (; k < nk; ++k)
                                    current[k] = (byte)((raw[k + rawOffset] + ((prior[k] + current[k - filter_bytes]) >> 1)) & 255);
                                break;

                            case FilterType.Paeth:
                                for (; k < nk; ++k)
                                    current[k] = (byte)(raw[k + rawOffset] + CRuntime.Paeth32(
                                        current[k - filter_bytes], prior[k], prior[k - filter_bytes]) & 255);
                                break;

                            case FilterType.AverageFirst:
                                for (; k < nk; ++k)
                                    current[k] = (byte)((raw[k + rawOffset] + (current[k - filter_bytes] >> 1)) & 255);
                                break;

                            case FilterType.PaethFirst:
                                for (; k < nk; ++k)
                                    current[k] = (byte)(raw[k + rawOffset] + CRuntime.Paeth32(current[k - filter_bytes], 0, 0) & 255);
                                break;
                        }
                        rawOffset += nk;
                    }
                    else
                    {
                        i = (uint)(width - 1);
                        switch (filter)
                        {
                            case FilterType.None:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = raw[k + rawOffset];
                                }
                                break;

                            case FilterType.Sub:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = (byte)((raw[k + rawOffset] + current[k - out_bytes]) & 255);
                                }
                                break;

                            case FilterType.Up:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = (byte)((raw[k + rawOffset] + prior[k]) & 255);
                                }
                                break;

                            case FilterType.Average:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = (byte)((raw[k + rawOffset] + ((prior[k] + current[k - out_bytes]) >> 1)) & 255);
                                }
                                break;

                            case FilterType.Paeth:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = (byte)(raw[k + rawOffset] + CRuntime.Paeth32(
                                            current[k - out_bytes], prior[k],
                                            prior[k - out_bytes]) & 255);
                                }
                                break;

                            case FilterType.AverageFirst:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = (byte)((raw[k + rawOffset] + (current[k - out_bytes] >> 1)) & 255);
                                }
                                break;

                            case FilterType.PaethFirst:
                                for (; i >= 1; --i, current[filter_bytes] = 255,
                                    rawOffset += filter_bytes, current += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        current[k] = (byte)((raw[k + rawOffset] + CRuntime.Paeth32(
                                            current[k - out_bytes], 0, 0)) & 255);
                                }
                                break;
                        }

                        if (depth == 16)
                        {
                            current = a._out_ + stride * j;
                            for (i = 0; i < width; ++i, current += out_bytes)
                                current[filter_bytes + 1] = 255;
                        }
                    }
                }

                if (depth < 8)
                {
                    for (j = 0; j < height; ++j)
                    {
                        byte* cur = a._out_ + stride * j;
                        byte* _in_ = a._out_ + stride * j + width * out_n - img_width_bytes;
                        byte scale = (byte)((color == 0) ? DepthScaleTable[depth] : 1);
                        if (depth == 4)
                        {
                            for (k = width * comp; k >= 2; k -= 2, ++_in_)
                            {
                                *cur++ = (byte)(scale * (*_in_ >> 4));
                                *cur++ = (byte)(scale * ((*_in_) & 0x0f));
                            }
                            if (k > 0)
                                *cur++ = (byte)(scale * (*_in_ >> 4));
                        }
                        else if (depth == 2)
                        {
                            for (k = width * comp; k >= 4; k -= 4, ++_in_)
                            {
                                *cur++ = (byte)(scale * (*_in_ >> 6));
                                *cur++ = (byte)(scale * ((*_in_ >> 4) & 0x03));
                                *cur++ = (byte)(scale * ((*_in_ >> 2) & 0x03));
                                *cur++ = (byte)(scale * ((*_in_) & 0x03));
                            }

                            if (k > 0)
                                *cur++ = (byte)(scale * (*_in_ >> 6));
                            if (k > 1)
                                *cur++ = (byte)(scale * ((*_in_ >> 4) & 0x03));
                            if (k > 2)
                                *cur++ = (byte)(scale * ((*_in_ >> 2) & 0x03));
                        }
                        else if (depth == 1)
                        {
                            for (k = width * comp; k >= 8; k -= 8, ++_in_)
                            {
                                *cur++ = (byte)(scale * (*_in_ >> 7));
                                *cur++ = (byte)(scale * ((*_in_ >> 6) & 0x01));
                                *cur++ = (byte)(scale * ((*_in_ >> 5) & 0x01));
                                *cur++ = (byte)(scale * ((*_in_ >> 4) & 0x01));
                                *cur++ = (byte)(scale * ((*_in_ >> 3) & 0x01));
                                *cur++ = (byte)(scale * ((*_in_ >> 2) & 0x01));
                                *cur++ = (byte)(scale * ((*_in_ >> 1) & 0x01));
                                *cur++ = (byte)(scale * ((*_in_) & 0x01));
                            }
                            if (k > 0)
                                *cur++ = (byte)(scale * (*_in_ >> 7));
                            if (k > 1)
                                *cur++ = (byte)(scale * ((*_in_ >> 6) & 0x01));
                            if (k > 2)
                                *cur++ = (byte)(scale * ((*_in_ >> 5) & 0x01));
                            if (k > 3)
                                *cur++ = (byte)(scale * ((*_in_ >> 4) & 0x01));
                            if (k > 4)
                                *cur++ = (byte)(scale * ((*_in_ >> 3) & 0x01));
                            if (k > 5)
                                *cur++ = (byte)(scale * ((*_in_ >> 2) & 0x01));
                            if (k > 6)
                                *cur++ = (byte)(scale * ((*_in_ >> 1) & 0x01));
                        }

                        if (comp != out_n)
                        {
                            int q;
                            cur = a._out_ + stride * j;
                            if (comp == 1)
                            {
                                for (q = width - 1; q >= 0; --q)
                                {
                                    cur[q * 2 + 1] = 255;
                                    cur[q * 2 + 0] = cur[q];
                                }
                            }
                            else
                            {
                                for (q = width - 1; q >= 0; --q)
                                {
                                    cur[q * 4 + 3] = 255;
                                    cur[q * 4 + 2] = cur[q * 3 + 2];
                                    cur[q * 4 + 1] = cur[q * 3 + 1];
                                    cur[q * 4 + 0] = cur[q * 3 + 0];
                                }
                            }
                        }
                    }
                }
                else if (depth == 16)
                {
                    byte* cur = a._out_;
                    ushort* cur16 = (ushort*)cur;
                    for (i = 0; i < (width * height * out_n); ++i, cur16++, cur += 2)
                        *cur16 = (ushort)((cur[0] << 8) | cur[1]);
                }

                return true;
            }

            public static bool CreateImage(
                ref PngContext a, ReadOnlySpan<byte> image_data, int out_n,
                int width, int height, int comp, int depth, int color, int interlaced)
            {
                int bytes = depth == 16 ? 2 : 1;
                int out_bytes = out_n * bytes;

                if (interlaced == 0)
                    return CreateImageCore(
                        ref a, image_data, out_n,
                        width, height, comp, depth, color);

                byte* final = (byte*)ImageReadHelpers.MAllocMad3(width, height, out_bytes, 0);

                int* xorig = stackalloc int[7];
                xorig[0] = 0;
                xorig[1] = 4;
                xorig[2] = 0;
                xorig[3] = 2;
                xorig[4] = 0;
                xorig[5] = 1;
                xorig[6] = 0;

                int* yorig = stackalloc int[7];
                yorig[0] = 0;
                yorig[1] = 0;
                yorig[2] = 4;
                yorig[3] = 0;
                yorig[4] = 2;
                yorig[5] = 0;
                yorig[6] = 1;

                int* xspc = stackalloc int[7];
                xspc[0] = 8;
                xspc[1] = 8;
                xspc[2] = 4;
                xspc[3] = 4;
                xspc[4] = 2;
                xspc[5] = 2;
                xspc[6] = 1;

                int* yspc = stackalloc int[7];
                yspc[0] = 8;
                yspc[1] = 8;
                yspc[2] = 8;
                yspc[3] = 4;
                yspc[4] = 4;
                yspc[5] = 2;
                yspc[6] = 2;

                for (int p = 0; p < 7; ++p)
                {
                    int i;
                    int j;
                    int x = (width - xorig[p] + xspc[p] - 1) / xspc[p];
                    int y = (height - yorig[p] + yspc[p] - 1) / yspc[p];
                    if ((x != 0) && (y != 0))
                    {
                        int img_len = ((((comp * x * depth) + 7) >> 3) + 1) * y;
                        if (!CreateImageCore(
                            ref a, image_data, out_n,
                            x, y, comp, depth, color))
                        {
                            CRuntime.Free(final);
                            return false;
                        }

                        for (j = 0; j < y; ++j)
                        {
                            for (i = 0; i < x; ++i)
                            {
                                int out_y = j * yspc[p] + yorig[p];
                                int out_x = i * xspc[p] + xorig[p];
                                CRuntime.MemCopy(
                                    final + out_y * width * out_bytes + out_x * out_bytes,
                                    a._out_ + (j * x + i) * out_bytes,
                                    out_bytes);
                            }
                        }

                        CRuntime.Free(a._out_);
                        image_data = image_data.Slice(img_len);
                    }
                }

                a._out_ = final;
                return true;
            }

            public static bool ComputeTransparency8(
                ref PngContext z, int width, int height, byte* tc, int out_n)
            {
                uint pixel_count = (uint)(width * height);
                byte* p = z._out_;
                if (out_n == 2)
                {
                    for (uint i = 0; i < pixel_count; ++i)
                    {
                        p[1] = (byte)(p[0] == tc[0] ? 0 : 255);
                        p += 2;
                    }
                }
                else
                {
                    for (uint i = 0; i < pixel_count; ++i)
                    {
                        if ((p[0] == tc[0]) && (p[1] == tc[1]) && (p[2] == tc[2]))
                            p[3] = 0;
                        p += 4;
                    }
                }

                return true;
            }

            public static bool ComputeTransparency16(
                ref PngContext z, int width, int height, ushort* tc, int out_n)
            {
                uint pixel_count = (uint)(width * height);
                ushort* p = (ushort*)z._out_;
                if (out_n == 2)
                {
                    for (uint i = 0; i < pixel_count; ++i)
                    {
                        p[1] = (ushort)(p[0] == tc[0] ? 0 : 65535);
                        p += 2;
                    }
                }
                else
                {
                    for (uint i = 0; i < pixel_count; ++i)
                    {
                        if ((p[0] == tc[0]) && (p[1] == tc[1]) && (p[2] == tc[2]))
                            p[3] = 0;
                        p += 4;
                    }
                }

                return true;
            }

            public static bool ExpandPalette(
                ref PngContext a, int width, int height, byte* palette, int len, int pal_img_n)
            {
                int pixel_count = (width * height);
                byte* p = (byte*)ImageReadHelpers.MAllocMad2(pixel_count, pal_img_n, 0);
                if (p == null)
                {
                    a.s.Error(ErrorCode.OutOfMemory);
                    return false;
                }

                byte* orig = a._out_;
                byte* tmp_out = p;
                if (pal_img_n == 3)
                {
                    for (int i = 0; i < pixel_count; ++i)
                    {
                        int n = orig[i] * 4;
                        p[0] = palette[n];
                        p[1] = palette[n + 1];
                        p[2] = palette[n + 2];
                        p += 3;
                    }
                }
                else
                {
                    for (int i = 0; i < pixel_count; ++i)
                    {
                        int n = orig[i] * 4;
                        p[0] = palette[n];
                        p[1] = palette[n + 1];
                        p[2] = palette[n + 2];
                        p[3] = palette[n + 3];
                        p += 4;
                    }
                }

                CRuntime.Free(a._out_);
                a._out_ = tmp_out;
                return true;
            }

            public static void DeIphone(ref PngContext z, ref ReadState ri)
            {
                ReadContext s = z.s;
                uint i;
                uint pixel_count = (uint)(ri.Width * ri.Height);
                byte* p = z._out_;

                if (ri.Components == 3)
                {
                    for (i = 0; i < pixel_count; ++i)
                    {
                        byte t = p[0];
                        p[0] = p[2];
                        p[2] = t;
                        p += 3;
                    }
                }
                else
                {
                    if (s.UnpremultiplyOnLoad)
                    {
                        for (i = 0; i < pixel_count; ++i)
                        {
                            byte a = p[3];
                            byte t = p[0];
                            if (a != 0)
                            {
                                byte half = (byte)(a / 2);
                                p[0] = (byte)((p[2] * 255 + half) / a);
                                p[1] = (byte)((p[1] * 255 + half) / a);
                                p[2] = (byte)((t * 255 + half) / a);
                            }
                            else
                            {
                                p[0] = p[2];
                                p[2] = t;
                            }
                            p += 4;
                        }
                    }
                    else
                    {
                        for (i = 0; i < pixel_count; ++i)
                        {
                            byte t = p[0];
                            p[0] = p[2];
                            p[2] = t;
                            p += 4;
                        }
                    }
                }

            }

            public static bool LoadCore(ref PngContext z, ref ReadState ri, ScanMode scan)
            {
                ReadContext s = z.s;
                z.idata = null;
                z._out_ = null;

                if (!CheckSignature(s))
                {
                    if (scan != ScanMode.Type)
                        s.Error(ErrorCode.NotPNG);
                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                byte* palette = stackalloc byte[256 * 4];
                byte pal_img_n = 0;
                bool has_transparency = false;
                byte* tc = stackalloc byte[3];
                ushort* tc16 = stackalloc ushort[3];
                int ioff = 0;
                int idata_limit = 0;
                int i;
                int pal_len = 0;
                int first = 1;
                int k;
                int interlace = 0;
                int color = 0;
                bool is_iphone = false;

                for (; ; )
                {
                    PngChunkHeader c = ReadChunkHeader(s);
                    switch (c.Type)
                    {
                        case PngChunkHeader.IHDR:
                        {
                            int compression;
                            int filter;
                            if (first == 0)
                            {
                                s.Error(ErrorCode.MultipleIHDR);
                                return false;
                            }
                            first = 0;

                            if (c.Length != 13)
                            {
                                s.Error(ErrorCode.BadIHDRLength);
                                return false;
                            }

                            ri.Width = s.ReadInt32BE();
                            if (ri.Width > (1 << 24))
                            {
                                s.Error(ErrorCode.TooLarge);
                                return false;
                            }

                            ri.Height = s.ReadInt32BE();
                            if (ri.Height > (1 << 24))
                            {
                                s.Error(ErrorCode.TooLarge);
                                return false;
                            }

                            if ((ri.Width == 0) || (ri.Height == 0))
                            {
                                s.Error(ErrorCode.EmptyImage);
                                return false;
                            }

                            ri.Depth = s.ReadByte();
                            if (ri.Depth != 1 &&
                                ri.Depth != 2 &&
                                ri.Depth != 4 &&
                                ri.Depth != 8 &&
                                ri.Depth != 16)
                            {
                                s.Error(ErrorCode.UnsupportedBitDepth);
                                return false;
                            }

                            color = s.ReadByte();
                            if (color > 6)
                            {
                                s.Error(ErrorCode.BadColorType);
                                return false;
                            }
                            if ((color == 3) && (ri.Depth == 16))
                            {
                                s.Error(ErrorCode.BadColorType);
                                return false;
                            }

                            if (color == 3)
                            {
                                pal_img_n = 3;
                            }
                            else if ((color & 1) != 0)
                            {
                                s.Error(ErrorCode.BadColorType);
                                return false;
                            }

                            compression = s.ReadByte();
                            if (compression != 0)
                            {
                                s.Error(ErrorCode.BadCompressionMethod);
                                return false;
                            }

                            filter = s.ReadByte();
                            if (filter != 0)
                            {
                                s.Error(ErrorCode.BadFilterMethod);
                                return false;
                            }

                            interlace = s.ReadByte();
                            if (interlace > 1)
                            {
                                s.Error(ErrorCode.BadInterlaceMethod);
                                return false;
                            }

                            if (pal_img_n == 0)
                            {
                                ri.Components = ((color & 2) != 0 ? 3 : 1) + ((color & 4) != 0 ? 1 : 0);
                                if (((1 << 30) / ri.Width / ri.Components) < ri.Height)
                                {
                                    s.Error(ErrorCode.TooLarge);
                                    return false;
                                }
                                if (scan == ScanMode.Header)
                                    return true;
                            }
                            else
                            {
                                ri.Components = 1;
                                if (((1 << 30) / ri.Width / 4) < ri.Height)
                                {
                                    s.Error(ErrorCode.TooLarge);
                                    return false;
                                }
                            }

                            ri.Orientation = ImageOrientation.TopLeftOrigin;

                            ri.StateReady();
                            break;
                        }

                        case PngChunkHeader.CgBI:
                            is_iphone = true;
                            s.Skip(c.Length);
                            break;

                        case PngChunkHeader.PLTE:
                        {
                            if (first != 0)
                            {
                                s.Error(ErrorCode.IHDRNotFirst);
                                return false;
                            }
                            if (c.Length > (256 * 3))
                            {
                                s.Error(ErrorCode.InvalidPalette);
                                return false;
                            }

                            pal_len = c.Length / 3;
                            if (pal_len * 3 != c.Length)
                            {
                                s.Error(ErrorCode.InvalidPalette);
                                return false;
                            }

                            for (i = 0; i < pal_len; ++i)
                            {
                                palette[i * 4 + 0] = s.ReadByte();
                                palette[i * 4 + 1] = s.ReadByte();
                                palette[i * 4 + 2] = s.ReadByte();
                                palette[i * 4 + 3] = 255;
                            }

                            // TODO: PaletteReady()
                            break;
                        }

                        case PngChunkHeader.tRNS:
                        {
                            if (first != 0)
                            {
                                s.Error(ErrorCode.IHDRNotFirst);
                                return false;
                            }
                            if (z.idata != null)
                            {
                                s.Error(ErrorCode.tRNSAfterIDAT);
                                return false;
                            }

                            if (pal_img_n != 0)
                            {
                                if (scan == ScanMode.Header)
                                {
                                    ri.Components = 4;
                                    return true;
                                }

                                if (pal_len == 0)
                                {
                                    s.Error(ErrorCode.tRNSBeforePLTE);
                                    return false;
                                }
                                if (c.Length > pal_len)
                                {
                                    s.Error(ErrorCode.BadtRNSLength);
                                    return false;
                                }
                                pal_img_n = 4;
                                for (i = 0; i < c.Length; ++i)
                                    palette[i * 4 + 3] = s.ReadByte();
                            }
                            else
                            {
                                if ((ri.Components & 1) == 0)
                                {
                                    s.Error(ErrorCode.tRNSWithAlpha);
                                    return false;
                                }
                                if (c.Length != (uint)ri.Components * 2)
                                {
                                    s.Error(ErrorCode.BadtRNSLength);
                                    return false;
                                }
                                has_transparency = true;

                                if (ri.Depth == 16)
                                {
                                    for (k = 0; k < ri.Components; ++k)
                                        tc16[k] = (ushort)s.ReadInt16BE();
                                }
                                else
                                {
                                    for (k = 0; k < ri.Components; ++k)
                                        tc[k] = (byte)((byte)(s.ReadInt16BE() & 255) * DepthScaleTable[ri.Depth]);
                                }
                            }
                            break;
                        }

                        case PngChunkHeader.IDAT:
                        {
                            if (first != 0)
                            {
                                s.Error(ErrorCode.IHDRNotFirst);
                                return false;
                            }
                            if ((pal_img_n != 0) && (pal_len == 0))
                            {
                                s.Error(ErrorCode.NoPLTE);
                                return false;
                            }
                            if (scan == ScanMode.Header)
                            {
                                ri.Components = pal_img_n;
                                return true;
                            }

                            if (ioff + c.Length < ioff)
                                return false;

                            if ((ioff + c.Length) > idata_limit)
                            {
                                int idata_limit_old = idata_limit;
                                if (idata_limit == 0)
                                    idata_limit = c.Length > 4096 ? c.Length : 4096;

                                while ((ioff + c.Length) > idata_limit)
                                    idata_limit *= 2;

                                byte* p = (byte*)CRuntime.ReAlloc(z.idata, idata_limit);
                                if (p == null)
                                {
                                    s.Error(ErrorCode.OutOfMemory);
                                    return false;
                                }
                                z.idata = p;
                            }

                            if (!s.ReadBytes(new Span<byte>(z.idata + ioff, c.Length)))
                            {
                                s.Error(ErrorCode.OutOfData);
                                return false;
                            }

                            ioff += c.Length;
                            break;
                        }

                        case PngChunkHeader.IEND:
                        {
                            if (first != 0)
                            {
                                s.Error(ErrorCode.IHDRNotFirst);
                                return false;
                            }

                            if (scan != ScanMode.Load)
                                return true;

                            if (z.idata == null)
                            {
                                s.Error(ErrorCode.NoIDAT);
                                return false;
                            }

                            uint bpl = (uint)((ri.Width * ri.Depth + 7) / 8);
                            int raw_len = (int)(bpl * ri.Height * ri.Components + ri.Height);
                            bool skipHeader = !is_iphone; // iphone png's don't have a deflate header

                            IMemoryHolder decompressed;
                            try
                            {
                                var data = new ReadOnlySpan<byte>(z.idata, ioff);
                                decompressed = Zlib.DeflateDecompress(data, raw_len, skipHeader);
                                if (decompressed == null)
                                    return false;
                            }
                            finally
                            {
                                CRuntime.Free(z.idata);
                                z.idata = null;
                            }

                            using (decompressed)
                            {
                                if (ri.RequestedComponents == ri.Components + 1 &&
                                    ri.RequestedComponents != 3 &&
                                    pal_img_n == 0 ||
                                    has_transparency)
                                    ri.OutComponents = ri.Components + 1;
                                else
                                    ri.OutComponents = ri.Components;

                                if (!CreateImage(
                                    ref z, decompressed.Span, ri.OutComponents,
                                    ri.Width, ri.Height, ri.Components, ri.Depth, color, interlace))
                                    return false;

                                if (has_transparency)
                                {
                                    if (ri.Depth == 16)
                                        if (!ComputeTransparency16(ref z, ri.Width, ri.Height, tc16, ri.OutComponents))
                                            return false;
                                        else if (!ComputeTransparency8(ref z, ri.Width, ri.Height, tc, ri.OutComponents))
                                            return false;
                                }

                                if (is_iphone && s.DeIphoneFlag && ri.OutComponents > 2)
                                    DeIphone(ref z, ref ri);

                                if (pal_img_n != 0)
                                {
                                    ri.Components = pal_img_n;
                                    ri.OutComponents = pal_img_n;

                                    if (ri.RequestedComponents >= 3)
                                        ri.OutComponents = ri.RequestedComponents.Value;

                                    if (!ExpandPalette(
                                        ref z, ri.Width, ri.Height, palette, pal_len, ri.OutComponents))
                                        return false;
                                }
                                else if (has_transparency)
                                {
                                    ri.Components++;
                                }
                                return true;
                            }
                        }

                        default:
                            if (first != 0)
                            {
                                s.Error(ErrorCode.IHDRNotFirst);
                                return false;
                            }

                            if ((c.Type & (1 << 29)) == 0)
                            {
                                s.Error(ErrorCode.UnknownChunk);
                                return false;
                            }

                            s.Skip(c.Length);
                            break;
                    }

                    s.ReadInt32BE();
                }
            }

            /*

            public static IMemoryHolder Load(ref PngContext p, ref ReadState ri)
            {
                try
                {
                    if ((ri.RequestedComponents < 0) || (ri.RequestedComponents > 4))
                    {
                        p.s.Error(ErrorCode.BadCompRequest);
                        return null;
                    }

                    if (!LoadCore(ref p, ref ri, ScanMode.Load))
                        return null;

                    int bits = ri.OutComponents * ri.Width * ri.Height * ri.OutDepth;
                    var result = new HGlobalMemoryHolder(p._out_, (bits + 7) / 8);
                    p._out_ = null;

                    var errorCode = ImageReadHelpers.ConvertFormat(result, ref ri, out var convertedResult);
                    if (errorCode != ErrorCode.Ok)
                        return null;
                    return convertedResult;

                }
                finally
                {
                    p.Dispose();
                }
            }

            public static IMemoryHolder Load(ReadContext s, ref ReadState ri)
            {
                var p = new PngContext(s);
                return Load(ref p, ref ri);
            }

            */

            public static bool Test(ReadContext s)
            {
                bool r = CheckSignature(s);
                s.Rewind();
                return r;
            }

            public static bool InfoCore(ref PngContext p, out ReadState ri)
            {
                ri = new ReadState();
                if (!LoadCore(ref p, ref ri, ScanMode.Header))
                {
                    p.s.Rewind();
                    return false;
                }
                return true;
            }

            public static bool Info(ReadContext s, out ReadState ri)
            {
                var p = new PngContext(s);
                return InfoCore(ref p, out ri);
            }
        }
    }
}
