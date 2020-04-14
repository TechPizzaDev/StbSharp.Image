using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

            public static PngChunkHeader ReadChunkHeader(ReadContext s)
            {
                return new PngChunkHeader(
                    length: s.ReadInt32BE(),
                    type: s.ReadInt32BE());
            }

            public static bool CheckSignature(ReadContext s)
            {
                for (int i = 0; i < 8; i++)
                    if (s.ReadByte() != Signature[i])
                        return false;
                return true;
            }

            public static bool ProcessFilteredRow(
                ReadContext s, ReadState ri, int width, int y, int color,
                in Transparency? transparency,
                in Palette? palette,
                Span<byte> row,
                Span<byte> palettizedRow)
            {
                int comp = ri.Components;
                int comp_out = ri.OutComponents;
                int depth = ri.Depth;
                int width_bytes = ((width * comp * depth) + 7) / 8;

                int k;
                if (depth < 8)
                {
                    int rowOff = 0;
                    byte scale = (byte)((color == 0) ? DepthScaleTable[depth] : 1);
                    var input = row.Slice(width * comp_out - width_bytes);
                    int inOff = 0;
                    if (depth == 4)
                    {
                        for (k = width * comp; k >= 2; k -= 2, inOff++)
                        {
                            row[rowOff++] = (byte)(scale * (input[inOff] >> 4));
                            row[rowOff++] = (byte)(scale * ((input[inOff]) & 0x0f));
                        }
                        if (k > 0)
                            row[rowOff++] = (byte)(scale * (input[inOff] >> 4));
                    }
                    else if (depth == 2)
                    {
                        for (k = width * comp; k >= 4; k -= 4, inOff++)
                        {
                            row[rowOff++] = (byte)(scale * (input[inOff] >> 6));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 4) & 0x03));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 2) & 0x03));
                            row[rowOff++] = (byte)(scale * ((input[inOff]) & 0x03));
                        }

                        if (k > 0)
                            row[rowOff++] = (byte)(scale * (input[inOff] >> 6));
                        if (k > 1)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 4) & 0x03));
                        if (k > 2)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 2) & 0x03));
                    }
                    else if (depth == 1)
                    {
                        for (k = width * comp; k >= 8; k -= 8, inOff++)
                        {
                            row[rowOff++] = (byte)(scale * (input[inOff] >> 7));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 6) & 0x01));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 5) & 0x01));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 4) & 0x01));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 3) & 0x01));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 2) & 0x01));
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 1) & 0x01));
                            row[rowOff++] = (byte)(scale * ((input[inOff]) & 0x01));
                        }

                        if (k > 0)
                            row[rowOff++] = (byte)(scale * (input[inOff] >> 7));
                        if (k > 1)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 6) & 0x01));
                        if (k > 2)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 5) & 0x01));
                        if (k > 3)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 4) & 0x01));
                        if (k > 4)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 3) & 0x01));
                        if (k > 5)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 2) & 0x01));
                        if (k > 6)
                            row[rowOff++] = (byte)(scale * ((input[inOff] >> 1) & 0x01));
                    }

                    if (comp != comp_out)
                    {
                        if (comp == 1)
                        {
                            for (int q = width; q-- > 0;)
                            {
                                row[q * 2 + 1] = 255;
                                row[q * 2 + 0] = row[q];
                            }
                        }
                        else
                        {
                            for (int q = width; q-- > 0;)
                            {
                                row[q * 4 + 3] = 255;
                                row[q * 4 + 2] = row[q * 3 + 2];
                                row[q * 4 + 1] = row[q * 3 + 1];
                                row[q * 4 + 0] = row[q * 3 + 0];
                            }
                        }
                    }
                }
                else if (depth == 16)
                {
                    Span<ushort> cur16 = MemoryMarshal.Cast<byte, ushort>(row);
                    for (int i = 0; i < cur16.Length; i++)
                    {
                        cur16[i] = (ushort)((row[i * 2] << 8) | row[i * 2 + 1]);
                    }
                }

                if (palette.HasValue)
                {
                    var paletteData = palette.Value.Data.Span;
                    var paletteComp = palette.Value.Components;

                    for (int x = 0; x < width; x++)
                    {
                        int n = row[x] * 4;
                        var src = paletteData.Slice(n, paletteComp);
                        var dst = palettizedRow.Slice(x * paletteComp);
                        src.CopyTo(dst);
                    }
                }

                var resultRow = palette.HasValue ? palettizedRow : row;
                if (transparency.HasValue)
                {
                    if (ri.Depth == 16)
                    {
                        if (!ComputeTransparency16(resultRow, transparency.Value.Tc16.Span, comp_out))
                            return false;
                    }
                    else
                    {
                        if (!ComputeTransparency8(resultRow, transparency.Value.Tc8.Span, comp_out))
                            return false;
                    }
                }

                return true;
            }

            public static bool ProcessFilter(
                ReadContext s, ReadState ri, int width, int y,
                ReadOnlySpan<byte> data,
                ReadOnlySpan<byte> previousRow,
                Span<byte> row)
            {
                int comp = ri.Components;
                int comp_out = ri.OutComponents;
                int depth = ri.Depth;
                int bytespc = depth == 16 ? 2 : 1;
                int bytespp = comp_out * bytespc;
                int w = width;
                int filter_bytes = comp * bytespc;
                int width_bytes = ((width * comp * depth) + 7) / 8;

                int dataOff = 0;
                int rowOff = 0;
                int prevOff = 0;

                if (data.Length < width_bytes + 1)
                {
                    s.Error(ErrorCode.NotEnoughPixels);
                    return false;
                }

                var filter = (FilterType)data[dataOff++];
                if ((int)filter > 4)
                {
                    s.Error(ErrorCode.InvalidFilter);
                    return false;
                }

                if (depth < 8)
                {
                    int extra = width * comp_out - width_bytes;
                    rowOff += extra;
                    prevOff += extra;
                    w = width_bytes;
                    filter_bytes = 1;
                }

                if (y == 0)
                    filter = FirstRowFilters[(int)filter];

                int k = 0;
                switch (filter)
                {
                    case FilterType.None:
                    case FilterType.Sub:
                    case FilterType.AverageFirst:
                    case FilterType.PaethFirst:
                        for (; k < filter_bytes; ++k)
                            row[rowOff + k] = data[dataOff + k];
                        break;

                    case FilterType.Up:
                        for (; k < filter_bytes; ++k)
                            row[rowOff + k] = (byte)((data[dataOff + k] + previousRow[prevOff + k]) & 255);
                        break;

                    case FilterType.Average:
                        for (; k < filter_bytes; ++k)
                            row[rowOff + k] = (byte)((data[dataOff + k] + (previousRow[prevOff + k] >> 1)) & 255);
                        break;

                    case FilterType.Paeth:
                        for (; k < filter_bytes; ++k)
                            row[rowOff + k] = (byte)(
                                (data[dataOff + k] + CRuntime.Paeth32(0, previousRow[prevOff + k], 0)) & 255);
                        break;
                }

                if (depth == 8)
                {
                    if (comp != comp_out)
                        row[rowOff + comp] = 255;

                    dataOff += comp;
                    rowOff += comp_out;
                    prevOff += comp_out;
                }
                else if (depth == 16)
                {
                    if (comp != comp_out)
                    {
                        row[rowOff + filter_bytes] = 255;
                        row[rowOff + filter_bytes + 1] = 255;
                    }

                    dataOff += filter_bytes;
                    rowOff += bytespp;
                    prevOff += bytespp;
                }
                else
                {
                    dataOff += 1;
                    rowOff += 1;
                    prevOff += 1;
                }

                if ((depth < 8) || (comp == comp_out))
                {
                    int nk = (w - 1) * filter_bytes;
                    var raw = data.Slice(dataOff);
                    var dst = row.Slice(rowOff);
                    var prior = previousRow.Slice(prevOff);

                    k = 0;
                    switch (filter)
                    {
                        case FilterType.None:
                            raw.Slice(0, nk).CopyTo(dst);
                            break;

                        case FilterType.Sub:
                            for (; k < nk; ++k)
                                dst[k] = (byte)((raw[k] + row[rowOff + k - filter_bytes]) & 255);
                            break;

                        case FilterType.Up:
                            for (; k < nk; ++k)
                                dst[k] = (byte)((raw[k] + prior[k]) & 255);
                            break;

                        case FilterType.Average:
                            for (; k < nk; ++k)
                                dst[k] = (byte)(
                                    (raw[k] + ((prior[k] + row[rowOff + k - filter_bytes]) >> 1)) & 255);
                            break;

                        case FilterType.Paeth:
                            for (; k < nk; ++k)
                                dst[k] = (byte)(raw[k] + CRuntime.Paeth32(
                                    row[rowOff + k - filter_bytes], prior[k], previousRow[prevOff + k - filter_bytes]) & 255);
                            break;

                        case FilterType.AverageFirst:
                            for (; k < nk; ++k)
                                dst[k] = (byte)((raw[k] + (row[rowOff + k - filter_bytes] >> 1)) & 255);
                            break;

                        case FilterType.PaethFirst:
                            for (; k < nk; ++k)
                                dst[k] = (byte)(raw[k] + CRuntime.Paeth32(
                                    row[rowOff + k - filter_bytes], 0, 0) & 255);
                            break;
                    }
                }
                else
                {
                    int i = width - 1;
                    switch (filter)
                    {
                        case FilterType.None:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = data[dataOff + k];
                            }
                            break;

                        case FilterType.Sub:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = (byte)((data[dataOff + k] + row[rowOff + k - bytespp]) & 255);
                            }
                            break;

                        case FilterType.Up:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = (byte)((data[dataOff + k] + previousRow[prevOff + k]) & 255);
                            }
                            break;

                        case FilterType.Average:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = (byte)(
                                        (data[dataOff + k] + ((previousRow[prevOff + k] + row[rowOff + k - bytespp]) >> 1)) & 255);
                            }
                            break;

                        case FilterType.Paeth:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = (byte)(data[dataOff + k] + CRuntime.Paeth32(
                                        row[rowOff + k - bytespp], previousRow[prevOff + k], previousRow[prevOff + k - bytespp]) & 255);
                            }
                            break;

                        case FilterType.AverageFirst:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = (byte)((data[dataOff + k] + (row[rowOff + k - bytespp] >> 1)) & 255);
                            }
                            break;

                        case FilterType.PaethFirst:
                            for (; i >= 1; i--, row[rowOff + filter_bytes] = 255,
                                dataOff += filter_bytes, rowOff += bytespp, prevOff += bytespp)
                            {
                                for (k = 0; k < filter_bytes; ++k)
                                    row[rowOff + k] = (byte)((data[dataOff + k] + CRuntime.Paeth32(
                                        row[rowOff + k - bytespp], 0, 0)) & 255);
                            }
                            break;
                    }

                    if (depth == 16)
                    {
                        for (i = 0; i < width; i++)
                            row[i * bytespp + filter_bytes + 1] = 255;
                    }
                }


                return true;
            }

            public static bool CreateImage(
                ReadContext s, ReadState ri, int raw_comp, int color, int interlaced,
                Transparency? transparency, Palette? palette, Stream data)
            {
                int width = ri.Width;
                int height = ri.Height;
                int bytes_per_comp = ri.OutDepth == 16 ? 2 : 1;
                int bytes_per_pixel = ri.OutComponents * bytes_per_comp;
                int data_width_bytes = (width * raw_comp * ri.Depth + 7) / 8 + 1;

                var buffer = new byte[data_width_bytes];
                var firstRow = new byte[ri.Width * bytes_per_pixel];
                var secondRow = new byte[ri.Width * bytes_per_pixel];
                var thirdRow = new byte[ri.Width * bytes_per_pixel];

                var palettizedRow = palette.HasValue ? new byte[ri.Width * palette.Value.Components] : null;

                if (interlaced == 0)
                {
                    var a = new stbi__png();
                    
                    var ms = new MemoryStream();
                    data.CopyTo(ms);

                    int raw_out_comp = raw_comp + (transparency.HasValue ? 1 : 0);

                    var rawdata = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                    bool r = CreateImageRaw(s, a, rawdata, raw_comp, raw_out_comp, width, height, ri.Depth, color);
                    if (r)
                    {
                        if (transparency.HasValue)
                        {
                            var ppp = new Span<byte>(a._out_, width * height * bytes_per_pixel);
                            if (ri.Depth == 16)
                            {
                                if (!ComputeTransparency16(ppp, transparency.Value.Tc16.Span, raw_out_comp))
                                    return false;
                            }
                            else
                            {
                                if (!ComputeTransparency8(ppp, transparency.Value.Tc8.Span, raw_out_comp))
                                    return false;
                            }
                        }

                        //if ((((is_iphone) != 0) && ((stbi__de_iphone_flag) != 0)) && (raw_out_comp > (2)))
                        //    stbi__de_iphone(z);

                        if (palette.HasValue)
                        {
                            var paletteComp = palette.Value.Components;
                            var paletteSpan = palette.Value.Data.Span;
                            var resultrow = new byte[width * bytes_per_pixel].AsSpan();
                            var indexrows = new Span<byte>(a._out_, height * width);
                            for (int y = 0; y < height; y++)
                            {
                                var indexrow = indexrows.Slice(y * width, width);
                                ExpandPalette(indexrow, resultrow, paletteSpan, paletteComp);
                                ri.OutputLine(AddressingMajor.Row, y, 0, resultrow);
                            }
                        }
                        else
                        {
                            int stride = width * bytes_per_pixel;
                            var rows = new Span<byte>(a._out_, height * stride);
                            for (int y = 0; y < height; y++)
                            {
                                var row = rows.Slice(y * stride, stride);
                                ri.OutputLine(AddressingMajor.Row, y, 0, row);
                            }
                        }
                    }

                    return r;

                    //bool ProcessFiltered(Span<byte> filtered, int row)
                    //{
                    //    if (!ProcessFilteredRow(
                    //        s, ri, width, row, color,
                    //        transparency, palette, filtered, palettizedRow))
                    //        return false;
                    //
                    //    var result = palettizedRow ?? filtered;
                    //    ri.OutputLine(AddressingMajor.Row, row, 0, result);
                    //    return true;
                    //}
                    //
                    //for (int y = 0; y < height; y++)
                    //{
                    //    if (data.Read(buffer) != buffer.Length)
                    //    {
                    //        s.Error(ErrorCode.EndOfStream);
                    //        return false;
                    //    }
                    //
                    //    if (!ProcessFilter(s, ri, width, y, buffer, secondRow, firstRow))
                    //        return false;
                    //
                    //    // swap
                    //    var first = firstRow;
                    //    var second = secondRow;
                    //    var third = thirdRow;
                    //    firstRow = third;
                    //    secondRow = first;
                    //    thirdRow = second;
                    //
                    //    if (y < 2)
                    //        continue;
                    //
                    //    if (!ProcessFiltered(third, y - 2))
                    //        return false;
                    //}
                    //
                    //return ProcessFiltered(secondRow, height - 2) && ProcessFiltered(firstRow, height - 1);
                }

                throw new NotImplementedException();

                ReadOnlySpan<int> xorig = stackalloc int[7] { 0, 4, 0, 2, 0, 1, 0, };
                ReadOnlySpan<int> yorig = stackalloc int[7] { 0, 0, 4, 0, 2, 0, 1 };
                ReadOnlySpan<int> xspc = stackalloc int[7] { 8, 8, 4, 4, 2, 2, 1 };
                ReadOnlySpan<int> yspc = stackalloc int[7] { 8, 8, 8, 4, 4, 2, 2 };

                //var ww = new System.Diagnostics.Stopwatch();
                for (int p = 0; p < 7; p++)
                {
                    //ww.Restart();
                    int orig_x = xorig[p];
                    int orig_y = yorig[p];
                    int spc_x = xspc[p];
                    int spc_y = yspc[p];

                    int w = (width - orig_x + spc_x - 1) / spc_x;
                    int h = (height - orig_y + spc_y - 1) / spc_y;
                    if (w != 0 && h != 0)
                    {
                        //Console.Write("Interlace step: " + p);

                        //if (!ProcessRow(s, ri, buffer, w, h, color))
                        //    return false;

                        // TODO: read row by row instead of buffer the whole file

                        //for (int y = 0; y < h; y++)
                        //{
                        //    int stride = w * bytes_per_pixel;
                        //    var pixels = primaryRowSpan.Slice(0, stride);
                        //
                        //    var src = row.Slice(y * stride);
                        //    new Span<byte>(src, stride).CopyTo(pixels);
                        //
                        //    int out_y = y * spc_y + orig_y;
                        //    ri.OutputInterleaved(AddressingMajor.Row, out_y, orig_x, spc_x, pixels);
                        //}
                        //
                        //int img_len = ((((ri.Components * w * ri.Depth) + 7) / 8) + 1) * h;
                        //data = data.Slice(img_len);
                    }

                    //ww.Stop();
                    //Console.WriteLine(", took " + ww.ElapsedMilliseconds + "ms");
                }
                return true;
            }

            public static bool ComputeTransparency8(Span<byte> row, ReadOnlySpan<byte> tc, int out_n)
            {
                if (out_n == 2)
                {
                    for (int i = 0; i < row.Length; i += 2)
                        row[i + 1] = (byte)(row[i] == tc[0] ? 0 : 255);
                }
                else
                {
                    for (int i = 0; i < row.Length; i += 4)
                    {
                        if (row[i + 0] == tc[0] &&
                            row[i + 1] == tc[1] &&
                            row[i + 2] == tc[2])
                            row[i + 3] = 0;
                    }
                }
                return true;
            }

            public static bool ComputeTransparency16(Span<byte> row, ReadOnlySpan<ushort> tc, int out_n)
            {
                var row16 = MemoryMarshal.Cast<byte, ushort>(row);
                if (out_n == 2)
                {
                    for (int i = 0; i < row16.Length; i += 2)
                        row16[i + 1] = (ushort)(row16[i] == tc[0] ? 0 : 65535);
                }
                else
                {
                    for (int i = 0; i < row16.Length; i += 4)
                    {
                        if (row16[i + 0] == tc[0] &&
                            row16[i + 1] == tc[1] &&
                            row16[i + 2] == tc[2])
                            row16[i + 3] = 0;
                    }
                }
                return true;
            }

            public static void DeIphone(ref PngContext z, ref ReadState ri)
            {
                ReadContext s = z.s;
                int i;
                int pixel_count = ri.Width * ri.Height;
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

            public enum HandleChunkResult
            {
                None = 0,
                Error,
                Skip,
                Manual,
                Include,
                Header
            }

            public delegate HandleChunkResult HandleChunkDelegate(
                PngChunkHeader chunkHeader, DataStream stream);

            public class DataStream : Stream
            {
                public ReadContext Context { get; }
                private HandleChunkDelegate _handleChunk;
                private int _chunkLeftToRead;

                public HandleChunkResult LastResult { get; private set; }

                public override bool CanRead => true;
                public override bool CanSeek => throw new NotSupportedException();
                public override bool CanWrite => throw new NotSupportedException();

                public override long Length => throw new NotSupportedException();
                public override long Position
                {
                    get => throw new NotSupportedException();
                    set => Seek(value, SeekOrigin.Begin);
                }

                public DataStream(ReadContext context, HandleChunkDelegate handleChunk)
                {
                    Context = context ?? throw new ArgumentNullException(nameof(context));
                    _handleChunk = handleChunk ?? throw new ArgumentNullException(nameof(handleChunk));

                    _chunkLeftToRead = -1;
                }

                private void FinishChunk()
                {
                    // TODO: validate crc
                    int crc = Context.ReadInt32BE();
                }

                public override int Read(Span<byte> buffer)
                {
                    Start:
                    if (_chunkLeftToRead >= 0)
                    {
                        int toRead = Math.Min(buffer.Length, _chunkLeftToRead);
                        if (!Context.ReadBytes(buffer.Slice(0, toRead)))
                        {
                            Context.Error(ErrorCode.EndOfStream);
                            LastResult = HandleChunkResult.Error;
                            return 0;
                        }

                        _chunkLeftToRead -= toRead;
                        if (_chunkLeftToRead == 0)
                        {
                            FinishChunk();
                            _chunkLeftToRead = -1;
                        }
                        return toRead;
                    }

                    if (LastResult == HandleChunkResult.Error ||
                        LastResult == HandleChunkResult.Header)
                        return 0;

                    Next:
                    var chunkHeader = ReadChunkHeader(Context);
                    LastResult = _handleChunk.Invoke(chunkHeader, this);

                    switch (LastResult)
                    {
                        case HandleChunkResult.Error:
                            return 0;

                        case HandleChunkResult.Header:
                            FinishChunk();
                            return 0;

                        case HandleChunkResult.Manual:
                            FinishChunk();
                            goto Next;

                        case HandleChunkResult.Skip:
                            Context.Skip(chunkHeader.Length);
                            FinishChunk();
                            goto Next;

                        case HandleChunkResult.Include:
                            _chunkLeftToRead = chunkHeader.Length;
                            goto Start;

                        default:
                            throw new InvalidOperationException();
                    }
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    return Read(buffer.AsSpan(offset, count));
                }

                public override long Seek(long offset, SeekOrigin origin)
                {
                    throw new NotSupportedException();
                }

                public override void SetLength(long value)
                {
                    throw new NotSupportedException();
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    throw new NotSupportedException();
                }

                public override void Flush()
                {
                    throw new NotSupportedException();
                }
            }

            public static bool Load(ReadContext s, ReadState ri, ScanMode scan)
            {
                if (!CheckSignature(s))
                {
                    if (scan != ScanMode.Type)
                        s.Error(ErrorCode.NotPNG);

                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                byte[] paletteData = null;
                byte[] tc8 = new byte[3];
                ushort[] tc16 = new ushort[3];

                int raw_comp = 0;
                byte pal_img_n = 0;
                bool has_transparency = false;
                bool is_iphone = false;

                int interlace = 0;
                int color = 0;
                int compression;
                int filter;
                int pal_len = 0;

                var seenChunkTypes = new HashSet<int>();

                // TODO: add custom deflate algo support
                Stream deflateStream = null;

                // TODO: add state object and make this method static
                HandleChunkResult HandleChunk(PngChunkHeader chunk, DataStream stream)
                {
                    if (seenChunkTypes.Count == 0 && chunk.Type != PngChunkHeader.IHDR)
                    {
                        s.Error(ErrorCode.IHDRNotFirst);
                        return HandleChunkResult.Error;
                    }

                    try
                    {
                        switch (chunk.Type)
                        {
                            #region IHDR

                            case PngChunkHeader.IHDR:
                            {
                                if (seenChunkTypes.Count > 0)
                                {
                                    s.Error(ErrorCode.MultipleIHDR);
                                    return HandleChunkResult.Error;
                                }

                                if (chunk.Length != 13)
                                {
                                    s.Error(ErrorCode.BadIHDRLength);
                                    return HandleChunkResult.Error;
                                }

                                ri.Width = s.ReadInt32BE();
                                if (ri.Width > (1 << 24))
                                {
                                    s.Error(ErrorCode.TooLarge);
                                    return HandleChunkResult.Error;
                                }

                                ri.Height = s.ReadInt32BE();
                                if (ri.Height > (1 << 24))
                                {
                                    s.Error(ErrorCode.TooLarge);
                                    return HandleChunkResult.Error;
                                }

                                if ((ri.Width == 0) || (ri.Height == 0))
                                {
                                    s.Error(ErrorCode.EmptyImage);
                                    return HandleChunkResult.Error;
                                }

                                ri.Depth = s.ReadByte();
                                if (ri.Depth != 1 &&
                                    ri.Depth != 2 &&
                                    ri.Depth != 4 &&
                                    ri.Depth != 8 &&
                                    ri.Depth != 16)
                                {
                                    s.Error(ErrorCode.UnsupportedBitDepth);
                                    return HandleChunkResult.Error;
                                }

                                if (ri.Depth < 8)
                                    ri.OutDepth = 8;
                                else
                                    ri.OutDepth = ri.Depth;

                                color = s.ReadByte();
                                if (color > 6)
                                {
                                    s.Error(ErrorCode.BadColorType);
                                    return HandleChunkResult.Error;
                                }
                                if ((color == 3) && (ri.Depth == 16))
                                {
                                    s.Error(ErrorCode.BadColorType);
                                    return HandleChunkResult.Error;
                                }

                                if (color == 3)
                                {
                                    pal_img_n = 3;
                                }
                                else if ((color & 1) != 0)
                                {
                                    s.Error(ErrorCode.BadColorType);
                                    return HandleChunkResult.Error;
                                }

                                compression = s.ReadByte();
                                if (compression != 0)
                                {
                                    s.Error(ErrorCode.BadCompressionMethod);
                                    return HandleChunkResult.Error;
                                }

                                filter = s.ReadByte();
                                if (filter != 0)
                                {
                                    s.Error(ErrorCode.BadFilterMethod);
                                    return HandleChunkResult.Error;
                                }

                                interlace = s.ReadByte();
                                if (interlace > 1)
                                {
                                    s.Error(ErrorCode.BadInterlaceMethod);
                                    return HandleChunkResult.Error;
                                }

                                ri.Orientation = ImageOrientation.TopLeftOrigin;

                                if (pal_img_n != 0)
                                {
                                    raw_comp = 1;
                                    if (((1 << 30) / ri.Width / 4) < ri.Height)
                                    {
                                        s.Error(ErrorCode.TooLarge);
                                        return HandleChunkResult.Error;
                                    }
                                }
                                else
                                {
                                    raw_comp = ((color & 2) != 0 ? 3 : 1) + ((color & 4) != 0 ? 1 : 0);

                                    if (((1 << 30) / ri.Width / raw_comp) < ri.Height)
                                    {
                                        s.Error(ErrorCode.TooLarge);
                                        return HandleChunkResult.Error;
                                    }

                                    if (scan == ScanMode.Header)
                                        return HandleChunkResult.Header;
                                }
                                ri.Components = raw_comp;

                                return HandleChunkResult.Manual;
                            }

                            #endregion

                            #region CgBI

                            case PngChunkHeader.CgBI:
                            {
                                is_iphone = true;
                                return HandleChunkResult.Skip;
                            }

                            #endregion

                            #region PLTE

                            case PngChunkHeader.PLTE:
                            {
                                if (chunk.Length > 256 * 3)
                                {
                                    s.Error(ErrorCode.InvalidPalette);
                                    return HandleChunkResult.Error;
                                }

                                pal_len = chunk.Length / 3;
                                if (pal_len * 3 != chunk.Length)
                                {
                                    s.Error(ErrorCode.InvalidPalette);
                                    return HandleChunkResult.Error;
                                }

                                paletteData = new byte[pal_len * 4];
                                for (int i = 0; i < paletteData.Length; i += 4)
                                {
                                    paletteData[i + 0] = s.ReadByte();
                                    paletteData[i + 1] = s.ReadByte();
                                    paletteData[i + 2] = s.ReadByte();
                                    paletteData[i + 3] = 255;
                                }

                                // TODO: PaletteReady()
                                return HandleChunkResult.Manual;
                            }

                            #endregion

                            #region tRNS

                            case PngChunkHeader.tRNS:
                            {
                                if (seenChunkTypes.Contains(PngChunkHeader.IDAT))
                                {
                                    s.Error(ErrorCode.tRNSAfterIDAT);
                                    return HandleChunkResult.Error;
                                }

                                if (pal_img_n != 0)
                                {
                                    if (pal_len == 0)
                                    {
                                        s.Error(ErrorCode.tRNSBeforePLTE);
                                        return HandleChunkResult.Error;
                                    }

                                    if (chunk.Length > pal_len)
                                    {
                                        s.Error(ErrorCode.BadtRNSLength);
                                        return HandleChunkResult.Error;
                                    }

                                    pal_img_n = 4;
                                    for (int i = 0; i < chunk.Length; i++)
                                        paletteData[i * 4 + 3] = s.ReadByte();
                                }
                                else
                                {
                                    if ((ri.Components & 1) == 0)
                                    {
                                        s.Error(ErrorCode.tRNSWithAlpha);
                                        return HandleChunkResult.Error;
                                    }
                                    if (chunk.Length != ri.Components * 2)
                                    {
                                        s.Error(ErrorCode.BadtRNSLength);
                                        return HandleChunkResult.Error;
                                    }

                                    if (ri.Depth == 16)
                                    {
                                        for (int k = 0; k < ri.Components; ++k)
                                            tc16[k] = (ushort)s.ReadInt16BE();
                                    }
                                    else
                                    {
                                        for (int k = 0; k < ri.Components; ++k)
                                            tc8[k] = (byte)((byte)(s.ReadInt16BE() & 255) * DepthScaleTable[ri.Depth]);
                                    }
                                    has_transparency = true;
                                }
                                return HandleChunkResult.Manual;
                            }

                            #endregion

                            #region IDAT

                            case PngChunkHeader.IDAT:
                            {
                                if (pal_img_n != 0 && pal_len == 0)
                                {
                                    s.Error(ErrorCode.NoPLTE);
                                    return HandleChunkResult.Error;
                                }

                                if (scan == ScanMode.Header)
                                    return HandleChunkResult.Header;

                                if (!seenChunkTypes.Contains(PngChunkHeader.IDAT))
                                {
                                    if (pal_img_n != 0)
                                    {
                                        ri.Components = pal_img_n;
                                    }
                                    else
                                    {
                                        ri.Components = raw_comp;
                                        if (has_transparency)
                                            ri.Components++;
                                    }
                                    ri.OutComponents = ri.Components;

                                    ri.StateReady();

                                    deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
                                }
                                return HandleChunkResult.Include;
                            }

                            #endregion

                            #region IEND

                            case PngChunkHeader.IEND:
                            {
                                if (!seenChunkTypes.Contains(PngChunkHeader.IDAT))
                                {
                                    s.Error(ErrorCode.NoIDAT);
                                    return HandleChunkResult.Error;
                                }

                                if (scan == ScanMode.Header)
                                {
                                    ri.StateReady();
                                    return HandleChunkResult.Header;
                                }
                                return HandleChunkResult.Include;
                            }

                            #endregion

                            default:
                            {
                                // check if the chunk is important 
                                if ((chunk.Type & (1 << 29)) == 0)
                                {
                                    s.Error(ErrorCode.UnknownChunk);
                                    return HandleChunkResult.Error;
                                }
                                return HandleChunkResult.Skip;
                            }
                        }
                    }
                    finally
                    {
                        seenChunkTypes.Add(chunk.Type);
                    }
                }

                //uint bpl = (uint)((ri.Width * ri.Depth + 7) / 8);
                //int raw_len = (int)(bpl * ri.Height * ri.Components + ri.Height);

                using (var dataStream = new DataStream(s, HandleChunk))
                {
                    dataStream.Read(Span<byte>.Empty);
                    if (dataStream.LastResult == HandleChunkResult.Header)
                    {
                        if (scan == ScanMode.Header)
                            return true;
                    }
                    else if (dataStream.LastResult != HandleChunkResult.Include)
                    {
                        return false;
                    }

                    // iphone streams dont have deflate header
                    // and we just skip it for normal streams
                    if (!is_iphone)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            if (dataStream.ReadByte() == -1)
                            {
                                s.Error(ErrorCode.EndOfStream);
                                return false;
                            }
                        }
                    }

                    var palette = pal_img_n != 0
                        ? new Palette(paletteData.AsMemory(0, pal_len * 4), pal_img_n)
                        : (Palette?)null;

                    var transparency = has_transparency
                        ? new Transparency(tc8, tc16)
                        : (Transparency?)null;

                    if (!CreateImage(s, ri, raw_comp, color, interlace, transparency, palette, deflateStream))
                        return false;

                    //if (is_iphone && s.DeIphoneFlag && ri.OutComponents > 2)
                    //    DeIphone(ref z, ref ri);

                    if (dataStream.LastResult == HandleChunkResult.Error)
                        return false;
                    return true;
                }
            }

            public readonly struct Palette
            {
                public ReadOnlyMemory<byte> Data { get; }
                public int Components { get; }

                public Palette(ReadOnlyMemory<byte> data, int components)
                {
                    Data = data;
                    Components = components;
                }
            }

            public readonly struct Transparency
            {
                public ReadOnlyMemory<byte> Tc8 { get; }
                public ReadOnlyMemory<ushort> Tc16 { get; }

                public Transparency(ReadOnlyMemory<byte> tc, ReadOnlyMemory<ushort> tc16)
                {
                    Tc8 = tc;
                    Tc16 = tc16;
                }
            }

            public static bool Test(ReadContext s)
            {
                bool r = CheckSignature(s);
                s.Rewind();
                return r;
            }

            public static bool Info(ReadContext s, out ReadState ri)
            {
                ri = new ReadState();
                if (!Load(s, ri, ScanMode.Header))
                {
                    s.Rewind();
                    return false;
                }
                return true;
            }


            // TODO: THIS NEEDS TO BE GONE
            public struct PngContext
            {
                public readonly ReadContext s;

                public byte* _out_;

                public PngContext(ReadContext s) : this()
                {
                    this.s = s;
                }

                public void Dispose()
                {
                    CRuntime.Free(_out_);
                    _out_ = null;
                }
            }











            public static int stbi__mad2sizes_valid(int a, int b, int add)
            {
                return (stbi__mul2sizes_valid(a, b) != 0) && (stbi__addsizes_valid(a * b, add) != 0) ? 1 : 0;
            }

            public static void* stbi__malloc_mad2(int a, int b, int add)
            {
                if (stbi__mad2sizes_valid(a, b, add) == 0)
                    return null;
                return CRuntime.MAlloc(a * b + add);
            }

            public static void* stbi__malloc_mad3(int a, int b, int c, int add)
            {
                if (stbi__mad3sizes_valid(a, b, c, add) == 0)
                    return null;
                return CRuntime.MAlloc(a * b * c + add);
            }

            public static int stbi__mad3sizes_valid(int a, int b, int c, int add)
            {
                return (stbi__mul2sizes_valid(a, b) != 0) && (stbi__mul2sizes_valid(a * b, c) != 0) && (stbi__addsizes_valid(a * b * c, add) != 0) ? 1 : 0;
            }

            public static int stbi__mul2sizes_valid(int a, int b)
            {
                if ((a < 0) || (b < 0))
                    return 0;
                if (b == 0)
                    return 1;
                return (a <= 2147483647 / b) ? 1 : 0;
            }

            public static int stbi__addsizes_valid(int a, int b)
            {
                if (b < 0)
                    return 0;
                return (a <= 2147483647 - b) ? 1 : 0;
            }

            public const int STBI__F_none = 0;
            public const int STBI__F_sub = 1;
            public const int STBI__F_up = 2;
            public const int STBI__F_avg = 3;
            public const int STBI__F_paeth = 4;
            public const int STBI__F_avg_first = 5;
            public const int STBI__F_paeth_first = 6;

            public static byte[] first_row_filter = { STBI__F_none, STBI__F_sub, STBI__F_none, STBI__F_avg_first, STBI__F_paeth_first };

            public static byte[] stbi__depth_scale_table = { 0, 0xff, 0x55, 0, 0x11, 0, 0, 0, 0x01 };

            public static bool CreateImageRaw(
                ReadContext s, stbi__png a, ReadOnlySpan<byte> raw, int img_n, int out_n, int x, int y, int depth, int color)
            {
                int bytes = depth == 16 ? 2 : 1;
                int stride = x * out_n * bytes;
                int output_bytes = out_n * bytes;
                int filter_bytes = img_n * bytes;
                int width = x;
                a._out_ = (byte*)stbi__malloc_mad3(x, y, output_bytes, 0);
                if (a._out_ == null)
                    return false;
                if (stbi__mad3sizes_valid(img_n, x, depth, 7) == 0)
                    return false;
                int img_width_bytes = ((img_n * x * depth) + 7) >> 3;
                int img_len = (img_width_bytes + 1) * y;
                if (raw.Length < img_len)
                {
                    s.Error(ErrorCode.NotEnoughPixels);
                    return false;
                }

                int i = 0;
                int k = 0;
                int rawOff = 0;
                for (int j = 0; j < y; ++j)
                {
                    int filter = raw[rawOff++];
                    if (filter > 4)
                    {
                        s.Error(ErrorCode.InvalidFilter);
                        return false;
                    }

                    byte* cur = a._out_ + stride * j;
                    if (depth < 8)
                    {
                        cur += x * out_n - img_width_bytes;
                        filter_bytes = 1;
                        width = (int)img_width_bytes;
                    }
                    byte* prior = cur - stride;
                    if (j == 0)
                        filter = first_row_filter[filter];

                    for (k = 0; k < filter_bytes; ++k)
                    {
                        var rawslice = raw.Slice(rawOff);
                        switch (filter)
                        {
                            case STBI__F_none:
                                cur[k] = rawslice[k];
                                break;
                            case STBI__F_sub:
                                cur[k] = rawslice[k];
                                break;
                            case STBI__F_up:
                                cur[k] = (byte)((rawslice[k] + prior[k]) & 255);
                                break;
                            case STBI__F_avg:
                                cur[k] = (byte)((rawslice[k] + (prior[k] >> 1)) & 255);
                                break;
                            case STBI__F_paeth:
                                cur[k] = (byte)((rawslice[k] + CRuntime.Paeth32(0, prior[k], 0)) & 255);
                                break;
                            case STBI__F_avg_first:
                                cur[k] = rawslice[k];
                                break;
                            case STBI__F_paeth_first:
                                cur[k] = rawslice[k];
                                break;
                        }
                    }

                    if (depth == 8)
                    {
                        if (img_n != out_n)
                            cur[img_n] = 255;
                        rawOff += img_n;
                        cur += out_n;
                        prior += out_n;
                    }
                    else if (depth == 16)
                    {
                        if (img_n != out_n)
                        {
                            cur[filter_bytes] = 255;
                            cur[filter_bytes + 1] = 255;
                        }
                        rawOff += filter_bytes;
                        cur += output_bytes;
                        prior += output_bytes;
                    }
                    else
                    {
                        rawOff += 1;
                        cur += 1;
                        prior += 1;
                    }

                    if ((depth < 8) || (img_n == out_n))
                    {
                        int nk = (width - 1) * filter_bytes;
                        var rawslice = raw.Slice(rawOff, nk);
                        switch (filter)
                        {
                            case STBI__F_none:
                                rawslice.CopyTo(new Span<byte>(cur, nk));
                                break;

                            case STBI__F_sub:
                                for (k = 0; k < rawslice.Length; ++k)
                                {
                                    cur[k] = (byte)((rawslice[k] + cur[k - filter_bytes]) & 255);
                                }
                                break;
                            case STBI__F_up:
                                for (k = 0; k < rawslice.Length; ++k)
                                {
                                    cur[k] = (byte)((rawslice[k] + prior[k]) & 255);
                                }
                                break;
                            case STBI__F_avg:
                                for (k = 0; k < rawslice.Length; ++k)
                                {
                                    cur[k] = (byte)(
                                        (rawslice[k] + ((prior[k] + cur[k - filter_bytes]) >> 1)) & 255);
                                }
                                break;
                            case STBI__F_paeth:
                                for (k = 0; k < rawslice.Length; ++k)
                                {
                                    cur[k] = (byte)(
                                        (rawslice[k] + CRuntime.Paeth32(
                                            cur[k - filter_bytes], prior[k], prior[k - filter_bytes])) & 255);
                                }
                                break;
                            case STBI__F_avg_first:
                                for (k = 0; k < rawslice.Length; ++k)
                                {
                                    cur[k] = (byte)((rawslice[k] + (cur[k - filter_bytes] >> 1)) & 255);
                                }
                                break;
                            case STBI__F_paeth_first:
                                for (k = 0; k < rawslice.Length; ++k)
                                {
                                    cur[k] = (byte)(
                                        (rawslice[k] + CRuntime.Paeth32(cur[k - filter_bytes], 0, 0)) & 255);
                                }
                                break;
                        }
                        rawOff += nk;
                    }
                    else
                    {
                        switch (filter)
                        {
                            case STBI__F_none:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = raw[rawOff + k];
                                    }
                                }
                                break;
                            case STBI__F_sub:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = (byte)((raw[rawOff + k] + cur[k - output_bytes]) & 255);
                                    }
                                }
                                break;
                            case STBI__F_up:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = (byte)((raw[rawOff + k] + prior[k]) & 255);
                                    }
                                }
                                break;
                            case STBI__F_avg:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = (byte)(
                                            (raw[rawOff + k] + ((prior[k] + cur[k - output_bytes]) >> 1)) & 255);
                                    }
                                }
                                break;
                            case STBI__F_paeth:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = (byte)(
                                            (raw[rawOff + k] + CRuntime.Paeth32(
                                                cur[k - output_bytes], prior[k], prior[k - output_bytes])) & 255);
                                    }
                                }
                                break;
                            case STBI__F_avg_first:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = (byte)((raw[rawOff + k] + (cur[k - output_bytes] >> 1)) & 255);
                                    }
                                }
                                break;
                            case STBI__F_paeth_first:
                                for (i = x - 1; i >= 1; --i, cur[filter_bytes] = 255,
                                    rawOff += filter_bytes, cur += output_bytes, prior += output_bytes)
                                {
                                    for (k = 0; k < filter_bytes; ++k)
                                    {
                                        cur[k] = (byte)(
                                            (raw[rawOff + k] + CRuntime.Paeth32(
                                                cur[k - output_bytes], 0, 0)) & 255);
                                    }
                                }
                                break;
                        }

                        if (depth == 16)
                        {
                            cur = a._out_ + stride * j;
                            for (i = 0; i < x; ++i, cur += output_bytes)
                            {
                                cur[filter_bytes + 1] = 255;
                            }
                        }
                    }
                }

                if (depth < 8)
                {
                    for (int j = 0; j < y; ++j)
                    {
                        byte* cur = a._out_ + stride * j;
                        byte* _in_ = a._out_ + stride * j + x * out_n - img_width_bytes;
                        byte scale = (byte)((color == 0) ? stbi__depth_scale_table[depth] : 1);
                        if (depth == 4)
                        {
                            for (k = x * img_n; k >= 2; k -= 2, ++_in_)
                            {
                                *cur++ = (byte)(scale * (*_in_ >> 4));
                                *cur++ = (byte)(scale * ((*_in_) & 0x0f));
                            }
                            if (k > 0)
                                *cur++ = (byte)(scale * (*_in_ >> 4));
                        }
                        else if (depth == 2)
                        {
                            for (k = x * img_n; k >= 4; k -= 4, ++_in_)
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
                            for (k = x * img_n; k >= 8; k -= 8, ++_in_)
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
                        if (img_n != out_n)
                        {
                            int q = 0;
                            cur = a._out_ + stride * j;
                            if (img_n == 1)
                            {
                                for (q = x - 1; q >= 0; --q)
                                {
                                    cur[q * 2 + 1] = 255;
                                    cur[q * 2 + 0] = cur[q];
                                }
                            }
                            else
                            {
                                for (q = x - 1; q >= 0; --q)
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
                    for (int jj = 0; jj < y; jj++)
                    {
                        for (i = 0; i < (x * out_n); ++i, cur16++, cur += 2)
                        {
                            *cur16 = (ushort)((cur[0] << 8) | cur[1]);
                        }
                    }
                }

                return true;
            }

            public static void ExpandPalette(
                ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> palette, int pal_img_n)
            {
                if (pal_img_n == 3)
                {
                    for (int i = 0; i < source.Length; i++)
                    {
                        int n = source[i] * 4;
                        destination[i * 3 + 0] = palette[n];
                        destination[i * 3 + 1] = palette[n + 1];
                        destination[i * 3 + 2] = palette[n + 2];
                    }
                }
                else
                {
                    for (int i = 0; i < source.Length; i++)
                    {
                        int n = source[i] * 4;
                        destination[i * 4 + 0] = palette[n];
                        destination[i * 4 + 1] = palette[n + 1];
                        destination[i * 4 + 2] = palette[n + 2];
                        destination[i * 4 + 3] = palette[n + 3];
                    }
                }
            }

            public class stbi__png
            {
                public byte* _out_;
            }
        }
    }
}
