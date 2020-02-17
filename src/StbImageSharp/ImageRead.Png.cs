
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
                public const uint CgBI = ('C' << 24) + ('g' << 16) + ('B' << 8) + 'I';
                public const uint IHDR = ('I' << 24) + ('H' << 16) + ('D' << 8) + 'R';
                public const uint PLTE = ('P' << 24) + ('L' << 16) + ('T' << 8) + 'E';
                public const uint tRNS = ('t' << 24) + ('R' << 16) + ('N' << 8) + 'S';
                public const uint IDAT = ('I' << 24) + ('D' << 16) + ('A' << 8) + 'T';
                public const uint IEND = ('I' << 24) + ('E' << 16) + ('N' << 8) + 'D';

                public readonly uint Length;
                public readonly uint Type;

                public PngChunkHeader(uint length, uint type)
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
            }

            public static PngChunkHeader GetChunkHeader(ReadContext s)
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
                ref PngContext a, byte* raw, uint raw_len, int out_n,
                int width, int height, int comp, int depth, int color)
            {
                int bytes = (int)(depth == 16 ? 2 : 1);
                int out_bytes = (int)(out_n * bytes);
                int x = (int)width;

                a._out_ = (byte*)MAllocMad3((int)width, (int)height, (int)out_bytes, 0);
                if (a._out_ == null)
                {
                    Error("outofmem");
                    return false;
                }

                uint img_width_bytes = (uint)(((comp * width * depth) + 7) >> 3);
                uint img_len = (uint)((img_width_bytes + 1) * height);
                if (raw_len < img_len)
                {
                    Error("not enough pixels");
                    return false;
                }

                uint stride = (uint)(width * out_n * bytes);
                int filter_bytes = (int)(comp * bytes);
                uint i;
                uint j;
                int k;

                for (j = (uint)0; j < height; ++j)
                {
                    byte* cur = a._out_ + stride * j;
                    byte* prior;
                    var filter = (FilterType)(*raw++);
                    if ((int)filter > 4)
                    {
                        Error("invalid filter");
                        return false;
                    }

                    if (depth < 8)
                    {
                        cur += width * out_n - img_width_bytes;
                        filter_bytes = 1;
                        x = (int)img_width_bytes;
                    }

                    prior = cur - stride;
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
                                cur[k] = (byte)raw[k];
                            break;

                        case FilterType.Up:
                            for (; k < filter_bytes; ++k)
                                cur[k] = (byte)((raw[k] + prior[k]) & 255);
                            break;

                        case FilterType.Average:
                            for (; k < filter_bytes; ++k)
                                cur[k] = (byte)((raw[k] + (prior[k] >> 1)) & 255);
                            break;

                        case FilterType.Paeth:
                            for (; k < filter_bytes; ++k)
                                cur[k] = (byte)((raw[k] + CRuntime.Paeth32(0, (int)prior[k], 0)) & 255);
                            break;
                    }

                    if (depth == 8)
                    {
                        if (comp != out_n)
                            cur[comp] = 255;
                        raw += comp;
                        cur += out_n;
                        prior += out_n;
                    }
                    else if (depth == 16)
                    {
                        if (comp != out_n)
                        {
                            cur[filter_bytes] = 255;
                            cur[filter_bytes + 1] = 255;
                        }

                        raw += filter_bytes;
                        cur += out_bytes;
                        prior += out_bytes;
                    }
                    else
                    {
                        raw += 1;
                        cur += 1;
                        prior += 1;
                    }

                    if ((depth < 8) || (comp == out_n))
                    {
                        int nk = (int)((x - 1) * filter_bytes);
                        k = 0;
                        switch (filter)
                        {
                            case FilterType.None:
                                CRuntime.MemCopy(cur, raw, nk);
                                break;

                            case FilterType.Sub:
                                for (; k < nk; ++k)
                                    cur[k] = (byte)((raw[k] + cur[k - filter_bytes]) & 255);
                                break;

                            case FilterType.Up:
                                for (; k < nk; ++k)
                                    cur[k] = (byte)((raw[k] + prior[k]) & 255);
                                break;

                            case FilterType.Average:
                                for (; k < nk; ++k)
                                    cur[k] = (byte)((raw[k] + ((prior[k] + cur[k - filter_bytes]) >> 1)) & 255);
                                break;

                            case FilterType.Paeth:
                                for (; k < nk; ++k)
                                    cur[k] = (byte)(raw[k] + CRuntime.Paeth32(
                                        cur[k - filter_bytes], prior[k], prior[k - filter_bytes]) & 255);
                                break;

                            case FilterType.AverageFirst:
                                for (; k < nk; ++k)
                                    cur[k] = (byte)((raw[k] + (cur[k - filter_bytes] >> 1)) & 255);
                                break;

                            case FilterType.PaethFirst:
                                for (; k < nk; ++k)
                                    cur[k] = (byte)(raw[k] + CRuntime.Paeth32(cur[k - filter_bytes], 0, 0) & 255);
                                break;
                        }
                        raw += nk;
                    }
                    else
                    {
                        i = (uint)(width - 1);
                        switch (filter)
                        {
                            case FilterType.None:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)raw[k];
                                }
                                break;

                            case FilterType.Sub:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)((raw[k] + cur[k - out_bytes]) & 255);
                                }
                                break;

                            case FilterType.Up:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)((raw[k] + prior[k]) & 255);
                                }
                                break;

                            case FilterType.Average:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)((raw[k] + ((prior[k] + cur[k - out_bytes]) >> 1)) & 255);
                                }
                                break;

                            case FilterType.Paeth:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)(raw[k] + CRuntime.Paeth32(
                                            (int)cur[k - out_bytes], (int)prior[k],
                                            (int)prior[k - out_bytes]) & 255);
                                }
                                break;

                            case FilterType.AverageFirst:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)((raw[k] + (cur[k - out_bytes] >> 1)) & 255);
                                }
                                break;

                            case FilterType.PaethFirst:
                                for (; i >= 1; --i, cur[filter_bytes] = 255,
                                    raw += filter_bytes, cur += out_bytes, prior += out_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                        cur[k] = (byte)((raw[k] + CRuntime.Paeth32(
                                            (int)cur[k - out_bytes], 0, 0)) & 255);
                                }
                                break;
                        }

                        if (depth == 16)
                        {
                            cur = a._out_ + stride * j;
                            for (i = (uint)0; i < width; ++i, cur += out_bytes)
                                cur[filter_bytes + 1] = 255;
                        }
                    }
                }

                if (depth < 8)
                {
                    for (j = (uint)0; j < height; ++j)
                    {
                        byte* cur = a._out_ + stride * j;
                        byte* _in_ = a._out_ + stride * j + width * out_n - img_width_bytes;
                        byte scale = (byte)((color == 0) ? DepthScaleTable[depth] : 1);
                        if (depth == 4)
                        {
                            for (k = (int)(width * comp); k >= 2; k -= (int)2, ++_in_)
                            {
                                *cur++ = (byte)(scale * (*_in_ >> 4));
                                *cur++ = (byte)(scale * ((*_in_) & 0x0f));
                            }
                            if (k > 0)
                                *cur++ = (byte)(scale * (*_in_ >> 4));
                        }
                        else if (depth == 2)
                        {
                            for (k = (int)(width * comp); k >= 4; k -= (int)4, ++_in_)
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
                            for (k = (int)(width * comp); k >= 8; k -= (int)8, ++_in_)
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
                                for (q = (int)(width - 1); q >= 0; --q)
                                {
                                    cur[q * 2 + 1] = 255;
                                    cur[q * 2 + 0] = (byte)cur[q];
                                }
                            }
                            else
                            {
                                for (q = (int)(width - 1); q >= 0; --q)
                                {
                                    cur[q * 4 + 3] = 255;
                                    cur[q * 4 + 2] = (byte)cur[q * 3 + 2];
                                    cur[q * 4 + 1] = (byte)cur[q * 3 + 1];
                                    cur[q * 4 + 0] = (byte)cur[q * 3 + 0];
                                }
                            }
                        }
                    }
                }
                else if (depth == 16)
                {
                    byte* cur = a._out_;
                    ushort* cur16 = (ushort*)cur;
                    for (i = (uint)0; i < (width * height * out_n); ++i, cur16++, cur += 2)
                        *cur16 = (ushort)((cur[0] << 8) | cur[1]);
                }

                return true;
            }

            public static bool CreateImage(
                ref PngContext a, byte* image_data, uint image_data_len, int out_n,
                int width, int height, int comp, int depth, int color, int interlaced)
            {
                int bytes = (int)(depth == 16 ? 2 : 1);
                int out_bytes = (int)(out_n * bytes);

                if (interlaced == 0)
                    return CreateImageCore(
                        ref a, image_data, (uint)image_data_len, (int)out_n,
                        width, height, comp, (int)depth, (int)color);

                byte* final = (byte*)MAllocMad3((int)width, (int)height, (int)out_bytes, 0);

                int* xorig = stackalloc int[7];
                xorig[0] = 0;
                xorig[1] = (int)4;
                xorig[2] = 0;
                xorig[3] = (int)2;
                xorig[4] = 0;
                xorig[5] = 1;
                xorig[6] = 0;

                int* yorig = stackalloc int[7];
                yorig[0] = 0;
                yorig[1] = 0;
                yorig[2] = (int)4;
                yorig[3] = 0;
                yorig[4] = (int)2;
                yorig[5] = 0;
                yorig[6] = 1;

                int* xspc = stackalloc int[7];
                xspc[0] = (int)8;
                xspc[1] = (int)8;
                xspc[2] = (int)4;
                xspc[3] = (int)4;
                xspc[4] = (int)2;
                xspc[5] = (int)2;
                xspc[6] = 1;

                int* yspc = stackalloc int[7];
                yspc[0] = (int)8;
                yspc[1] = (int)8;
                yspc[2] = (int)8;
                yspc[3] = (int)4;
                yspc[4] = (int)4;
                yspc[5] = (int)2;
                yspc[6] = (int)2;

                for (int p = 0; p < 7; ++p)
                {
                    int i;
                    int j;
                    int x = (int)((width - xorig[p] + xspc[p] - 1) / xspc[p]);
                    int y = (int)((height - yorig[p] + yspc[p] - 1) / yspc[p]);
                    if ((x != 0) && (y != 0))
                    {
                        uint img_len = (uint)(((((comp * x * depth) + 7) >> 3) + 1) * y);
                        if (!CreateImageCore(
                            ref a, image_data, (uint)image_data_len, (int)out_n,
                            x, y, comp, (int)depth, (int)color))
                        {
                            CRuntime.Free(final);
                            return false;
                        }

                        for (j = 0; j < y; ++j)
                        {
                            for (i = 0; i < x; ++i)
                            {
                                int out_y = (int)(j * yspc[p] + yorig[p]);
                                int out_x = (int)(i * xspc[p] + xorig[p]);
                                CRuntime.MemCopy(
                                    final + out_y * width * out_bytes + out_x * out_bytes,
                                    a._out_ + (j * x + i) * out_bytes,
                                    out_bytes);
                            }
                        }

                        CRuntime.Free(a._out_);
                        image_data += img_len;
                        image_data_len -= (uint)img_len;
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
                    for (uint i = (uint)0; i < pixel_count; ++i)
                    {
                        p[1] = (byte)(p[0] == tc[0] ? 0 : 255);
                        p += 2;
                    }
                }
                else
                {
                    for (uint i = (uint)0; i < pixel_count; ++i)
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
                    for (uint i = (uint)0; i < pixel_count; ++i)
                    {
                        p[1] = (ushort)(p[0] == tc[0] ? 0 : 65535);
                        p += 2;
                    }
                }
                else
                {
                    for (uint i = (uint)0; i < pixel_count; ++i)
                    {
                        if ((p[0] == tc[0]) && (p[1] == tc[1]) && (p[2] == tc[2]))
                            p[3] = (ushort)0;
                        p += 4;
                    }
                }

                return true;
            }

            public static bool ExpandPalette(
                ref PngContext a, int width, int height, byte* palette, int len, int pal_img_n)
            {
                uint pixel_count = (uint)(width * height);
                byte* p = (byte*)MAllocMad2((int)pixel_count, (int)pal_img_n, 0);
                if (p == null)
                {
                    Error("outofmem");
                    return false;
                }

                byte* orig = a._out_;
                byte* tmp_out = p;
                if (pal_img_n == 3)
                {
                    for (uint i = 0; i < pixel_count; ++i)
                    {
                        int n = (int)(orig[i] * 4);
                        p[0] = (byte)palette[n];
                        p[1] = (byte)palette[n + 1];
                        p[2] = (byte)palette[n + 2];
                        p += 3;
                    }
                }
                else
                {
                    for (uint i = 0; i < pixel_count; ++i)
                    {
                        int n = (int)(orig[i] * 4);
                        p[0] = (byte)palette[n];
                        p[1] = (byte)palette[n + 1];
                        p[2] = (byte)palette[n + 2];
                        p[3] = (byte)palette[n + 3];
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
                if (ri.OutComponents == 3)
                {
                    for (i = (uint)0; i < pixel_count; ++i)
                    {
                        byte t = (byte)p[0];
                        p[0] = (byte)p[2];
                        p[2] = (byte)t;
                        p += 3;
                    }
                }
                else
                {
                    if (s.unpremultiply_on_load)
                    {
                        for (i = (uint)0; i < pixel_count; ++i)
                        {
                            byte a = (byte)p[3];
                            byte t = (byte)p[0];
                            if (a != 0)
                            {
                                byte half = (byte)(a / 2);
                                p[0] = (byte)((p[2] * 255 + half) / a);
                                p[1] = (byte)((p[1] * 255 + half) / a);
                                p[2] = (byte)((t * 255 + half) / a);
                            }
                            else
                            {
                                p[0] = (byte)p[2];
                                p[2] = (byte)t;
                            }
                            p += 4;
                        }
                    }
                    else
                    {
                        for (i = (uint)0; i < pixel_count; ++i)
                        {
                            byte t = (byte)p[0];
                            p[0] = (byte)p[2];
                            p[2] = (byte)t;
                            p += 4;
                        }
                    }
                }

            }

            public static bool Load(ref PngContext z, ref ReadState ri, ScanMode scan)
            {
                ReadContext s = z.s;
                z.idata = null;
                z._out_ = null;

                if (!CheckSignature(s))
                {
                    if (scan != ScanMode.Type)
                        Error("bad png sig");
                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                byte* palette = stackalloc byte[1024];
                byte pal_img_n = 0;
                bool has_transparency = false;
                byte* tc = stackalloc byte[3];
                ushort* tc16 = stackalloc ushort[3];
                uint ioff = (uint)0;
                uint idata_limit = (uint)0;
                uint i;
                int pal_len = 0;
                int first = 1;
                int k;
                int interlace = 0;
                int color = 0;
                bool is_iphone = false;

                for (; ; )
                {
                    PngChunkHeader c = GetChunkHeader(s);
                    switch (c.Type)
                    {
                        case PngChunkHeader.CgBI:
                            is_iphone = true;
                            s.Skip((int)c.Length);
                            break;

                        case PngChunkHeader.IHDR:
                        {
                            int comp;
                            int filter;
                            if (first == 0)
                            {
                                Error("multiple IHDR");
                                return false;
                            }
                            first = 0;

                            if (c.Length != 13)
                            {
                                Error("bad IHDR length");
                                return false;
                            }

                            ri.Width = (int)s.ReadInt32BE();
                            if (ri.Width > (1 << 24))
                            {
                                Error("too large");
                                return false;
                            }

                            ri.Height = (int)s.ReadInt32BE();
                            if (ri.Height > (1 << 24))
                            {
                                Error("too large");
                                return false;
                            }

                            if ((ri.Width == 0) || (ri.Height == 0))
                            {
                                Error("0-pixel image");
                                return false;
                            }

                            ri.Depth = (int)s.ReadByte();
                            if (ri.Depth != 1 &&
                                ri.Depth != 2 &&
                                ri.Depth != 4 &&
                                ri.Depth != 8 &&
                                ri.Depth != 16)
                            {
                                Error("1/2/4/8/16-bit only");
                                return false;
                            }

                            if (ri.Depth < 8)
                                ri.OutDepth = 8;
                            else
                                ri.OutDepth = ri.Depth;

                            color = (int)s.ReadByte();
                            if (color > 6)
                            {
                                Error("bad ctype");
                                return false;
                            }
                            if ((color == 3) && (ri.Depth == 16))
                            {
                                Error("bad ctype");
                                return false;
                            }

                            if (color == 3)
                                pal_img_n = (byte)3;
                            else if ((color & 1) != 0)
                            {
                                Error("bad ctype");
                                return false;
                            }

                            comp = (int)s.ReadByte();
                            if (comp != 0)
                            {
                                Error("bad comp method");
                                return false;
                            }

                            filter = (int)s.ReadByte();
                            if (filter != 0)
                            {
                                Error("bad filter method");
                                return false;
                            }

                            interlace = (int)s.ReadByte();
                            if (interlace > 1)
                            {
                                Error("bad interlace method");
                                return false;
                            }

                            if (pal_img_n == 0)
                            {
                                ri.Components = (int)(((color & 2) != 0 ? 3 : 1) + ((color & 4) != 0 ? 1 : 0));
                                if (((1 << 30) / ri.Width / ri.Components) < ri.Height)
                                {
                                    Error("too large");
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
                                    Error("too large");
                                    return false;
                                }
                            }
                            break;
                        }

                        case PngChunkHeader.PLTE:
                        {
                            if (first != 0)
                            {
                                Error("first not IHDR");
                                return false;
                            }
                            if (c.Length > (256 * 3))
                            {
                                Error("invalid PLTE");
                                return false;
                            }

                            pal_len = (int)(c.Length / 3);
                            if (pal_len * 3 != c.Length)
                            {
                                Error("invalid PLTE");
                                return false;
                            }

                            for (i = (uint)0; i < pal_len; ++i)
                            {
                                palette[i * 4 + 0] = (byte)s.ReadByte();
                                palette[i * 4 + 1] = (byte)s.ReadByte();
                                palette[i * 4 + 2] = (byte)s.ReadByte();
                                palette[i * 4 + 3] = 255;
                            }
                            break;
                        }

                        case PngChunkHeader.tRNS:
                        {
                            if (first != 0)
                            {
                                Error("first not IHDR");
                                return false;
                            }
                            if (z.idata != null)
                            {
                                Error("tRNS after IDAT");
                                return false;
                            }

                            if (pal_img_n != 0)
                            {
                                if (scan == ScanMode.Header)
                                {
                                    ri.Components = (int)4;
                                    return true;
                                }

                                if (pal_len == 0)
                                {
                                    Error("tRNS before PLTE");
                                    return false;
                                }
                                if (c.Length > pal_len)
                                {
                                    Error("bad tRNS len");
                                    return false;
                                }
                                pal_img_n = (byte)4;
                                for (i = (uint)0; i < c.Length; ++i)
                                    palette[i * 4 + 3] = (byte)s.ReadByte();
                            }
                            else
                            {
                                if ((ri.Components & 1) == 0)
                                {
                                    Error("tRNS with alpha");
                                    return false;
                                }
                                if (c.Length != (uint)ri.Components * 2)
                                {
                                    Error("bad tRNS len");
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
                                Error("first not IHDR");
                                return false;
                            }
                            if ((pal_img_n != 0) && (pal_len == 0))
                            {
                                Error("no PLTE");
                                return false;
                            }
                            if (scan == ScanMode.Header)
                            {
                                ri.Components = (int)pal_img_n;
                                return true;
                            }

                            if (((int)(ioff + c.Length)) < ((int)ioff))
                                return false;

                            if ((ioff + c.Length) > idata_limit)
                            {
                                uint idata_limit_old = (uint)idata_limit;
                                if (idata_limit == 0)
                                    idata_limit = (uint)(c.Length > 4096 ? c.Length : 4096);
                                while ((ioff + c.Length) > idata_limit)
                                    idata_limit *= (uint)2;

                                byte* p = (byte*)CRuntime.ReAlloc(z.idata, idata_limit);
                                if (p == null)
                                {
                                    Error("outofmem");
                                    return false;
                                }
                                z.idata = p;
                            }

                            if (!s.ReadBytes(new Span<byte>(z.idata + ioff, (int)c.Length)))
                            {
                                Error("outofdata");
                                return false;
                            }

                            ioff += (uint)c.Length;
                            break;
                        }

                        case PngChunkHeader.IEND:
                        {
                            if (first != 0)
                            {
                                Error("first not IHDR");
                                return false;
                            }
                            if (scan != ScanMode.Load)
                                return true;

                            if (z.idata == null)
                            {
                                Error("no IDAT");
                                return false;
                            }

                            uint bpl = (uint)((ri.Width * ri.Depth + 7) / 8);
                            int raw_len = (int)(bpl * ri.Height * ri.Components + ri.Height);
                            bool skipHeader = !is_iphone; // iphone png's don't have a deflate header

                            IMemoryResult decompressed;
                            try
                            {
                                var data = new ReadOnlySpan<byte>(z.idata, (int)ioff);
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
                                    ri.OutComponents = (int)(ri.Components + 1);
                                else
                                    ri.OutComponents = (int)ri.Components;

                                if (!CreateImage(
                                    ref z, (byte*)decompressed.Pointer, (uint)decompressed.Length, ri.OutComponents,
                                    ri.Width, ri.Height, ri.Components, ri.Depth, color, interlace))
                                    return false;

                                if (has_transparency)
                                {
                                    if (ri.Depth == 16)
                                        if (!ComputeTransparency16(ref z, ri.Width, ri.Height, tc16, (int)ri.OutComponents))
                                            return false;
                                        else if (!ComputeTransparency8(ref z, ri.Width, ri.Height, tc, (int)ri.OutComponents))
                                            return false;
                                }

                                if (is_iphone && s.de_iphone_flag && ri.OutComponents > 2)
                                    DeIphone(ref z, ref ri);

                                if (pal_img_n != 0)
                                {
                                    ri.Components = (int)pal_img_n;
                                    ri.OutComponents = (int)pal_img_n;

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
                                Error("first not IHDR");
                                return false;
                            }

                            if ((c.Type & (1 << 29)) == 0)
                            {
                                string invalid_chunk = "XXXX PNG chunk not known";
                                Error(invalid_chunk);
                                return false;
                            }

                            s.Skip((int)c.Length);
                            break;
                    }

                    s.ReadInt32BE();
                }
            }

            public static IMemoryResult Load(ref PngContext p, ref ReadState ri)
            {
                if ((ri.RequestedComponents < 0) || (ri.RequestedComponents > 4))
                {
                    Error("bad comp request");
                    return null;
                }

                IMemoryResult result = null;
                if (Load(ref p, ref ri, ScanMode.Load))
                {
                    result = new HGlobalMemoryResult(p._out_, ri.OutComponents * ri.Width * ri.Height * ri.OutDepth / 8);
                    p._out_ = null;
                    result = ConvertFormat(result, ref ri);
                }

                CRuntime.Free(p._out_);
                p._out_ = null;
                CRuntime.Free(p.idata);
                p.idata = null;
                return result;
            }

            public static IMemoryResult LoadImage(ReadContext s, ref ReadState ri)
            {
                var p = new PngContext(s);
                return Load(ref p, ref ri);
            }

            public static bool Test(ReadContext s)
            {
                bool r = CheckSignature(s);
                s.Rewind();
                return r;
            }

            public static bool InfoCore(ref PngContext p, out ReadState ri)
            {
                ri = new ReadState();
                if (!Load(ref p, ref ri, ScanMode.Header))
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
