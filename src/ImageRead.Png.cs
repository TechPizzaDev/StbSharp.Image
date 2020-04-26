using System;
using System.Buffers.Binary;
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

            public enum HandleChunkResult
            {
                None = 0,
                Finish,
                Include,
                Header
            }

            public enum ChunkType : uint
            {
                CgBI = ('C' << 24) + ('g' << 16) + ('B' << 8) + 'I',
                IHDR = ('I' << 24) + ('H' << 16) + ('D' << 8) + 'R',
                PLTE = ('P' << 24) + ('L' << 16) + ('T' << 8) + 'E',
                tRNS = ('t' << 24) + ('R' << 16) + ('N' << 8) + 'S',
                IDAT = ('I' << 24) + ('D' << 16) + ('A' << 8) + 'T',
                IEND = ('I' << 24) + ('E' << 16) + ('N' << 8) + 'D'
            }

            public delegate HandleChunkResult HandleChunkDelegate(
                ChunkHeader chunkHeader, DataStream stream);

            #region Constants

            private static ReadOnlySpan<byte> Signature => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

            private static ReadOnlySpan<byte> DepthScaleTable => new byte[] { 0, 0xff, 0x55, 0, 0x11, 0, 0, 0, 0x01 };

            private static ReadOnlySpan<byte> Interlace_xorig => new byte[] { 0, 4, 0, 2, 0, 1, 0, };
            private static ReadOnlySpan<byte> Interlace_yorig => new byte[] { 0, 0, 4, 0, 2, 0, 1 };
            private static ReadOnlySpan<byte> Interlace_xspc => new byte[] { 8, 8, 4, 4, 2, 2, 1 };
            private static ReadOnlySpan<byte> Interlace_yspc => new byte[] { 8, 8, 8, 4, 4, 2, 2 };

            private static FilterType[] FirstRowFilter =
            {
                FilterType.None,
                FilterType.Sub,
                FilterType.None,
                FilterType.AverageFirst,
                FilterType.PaethFirst
            };

            #endregion

            #region Structs

            public readonly struct ChunkHeader
            {
                public int Length { get; }
                public ChunkType Type { get; }

                public bool IsCritical => ((uint)Type & (1 << 29)) == 0;
                public bool IsPublic => ((uint)Type & (1 << 21)) == 0;
                public bool IsSafeToCopy => ((uint)Type & (1 << 21)) == 1;

                public ChunkHeader(uint length, ChunkType type)
                {
                    if (length > int.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(length));

                    Length = (int)length;
                    Type = type;
                }

                public static ChunkHeader Read(ReadContext s)
                {
                    uint length = s.ReadUInt32BE();
                    uint type = s.ReadUInt32BE();
                    return new ChunkHeader(length, (ChunkType)type);
                }
            }

            public struct Header
            {
                public int Width { get; }
                public int Height { get; }
                public int Depth { get; }
                public int ColorType { get; }
                public int Compression { get; }
                public int Filter { get; }
                public int Interlace { get; }
                public bool HasCgbi { get; }

                public int PaletteComp { get; set; }

                public readonly int RawComp
                {
                    get
                    {
                        if (PaletteComp != 0)
                            return 1;
                        return (((ColorType & 2) != 0 ? 3 : 1) + ((ColorType & 4) != 0 ? 1 : 0));
                    }
                }

                public Header(
                    int width, int height, int depth, int color,
                    int compression, int filter, int interlace, bool hasCgbi)
                {
                    Width = width;
                    Height = height;
                    Depth = depth;
                    ColorType = color;
                    Compression = compression;
                    Filter = filter;
                    Interlace = interlace;
                    HasCgbi = hasCgbi;

                    PaletteComp = color == 3 ? 3 : 0;
                }
            }

            public readonly struct Palette
            {
                public ReadOnlyMemory<Rgba32> Data { get; }
                public int Components { get; }

                public Palette(ReadOnlyMemory<Rgba32> data, int components)
                {
                    Data = data;
                    Components = components;
                }
            }

            public readonly struct Transparency
            {
                public Rgb24 Tc8 { get; }
                public Rgb48 Tc16 { get; }

                public Transparency(Rgb24 tc, Rgb48 tc16)
                {
                    Tc8 = tc;
                    Tc16 = tc16;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rgb24
            {
                public byte R;
                public byte G;
                public byte B;

                public Rgb24(byte r, byte g, byte b)
                {
                    R = r;
                    G = g;
                    B = b;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rgba32
            {
                public Rgb24 Rgb;
                public byte A;

                public Rgba32(Rgb24 rgb, byte a)
                {
                    Rgb = rgb;
                    A = a;
                }

                public Rgba32(byte r, byte g, byte b, byte a) : this(new Rgb24(r, g, b), a)
                {
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rgb48
            {
                public ushort R;
                public ushort G;
                public ushort B;

                public Rgb48(ushort r, ushort g, ushort b)
                {
                    R = r;
                    G = g;
                    B = b;
                }
            }

            #endregion

            public static bool CheckSignature(ReadContext s)
            {
                for (int i = 0; i < 8; i++)
                    if (s.ReadByte() != Signature[i])
                        return false;
                return true;
            }

            public static bool Test(ReadContext s)
            {
                return CheckSignature(s);
            }

            public static bool Info(ReadContext s, out ReadState ri)
            {
                ri = new ReadState();
                if (!Load(s, ri, ScanMode.Header))
                    return false;
                return true;
            }

            public static bool Load(ReadContext s, ReadState ri, ScanMode scan)
            {
                if (!CheckSignature(s))
                {
                    if (scan != ScanMode.Type)
                        throw new StbImageReadException(ErrorCode.UnknownFormat);
                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                Rgba32[] paletteData = null;
                int paletteLength = 0;

                Rgb24 tc8 = default;
                Rgb48 tc16 = default;
                Header header = default;

                bool has_transparency = false;

                void InitializeReadState()
                {
                    ri.Components = header.RawComp;
                    if (header.PaletteComp != 0)
                    {
                        ri.OutComponents = header.PaletteComp;
                    }
                    else
                    {
                        if (has_transparency)
                            ri.Components++;
                        ri.OutComponents = ri.Components;
                    }

                    ri.Width = header.Width;
                    ri.Height = header.Height;
                    ri.Depth = header.Depth;
                    ri.OutDepth = header.Depth < 8 ? 8 : header.Depth;
                    ri.Orientation = ImageOrientation.TopLeftOrigin;

                    ri.StateReady();
                }

                var seenChunkTypes = new HashSet<ChunkType>();

                // TODO: add custom deflate algo support
                Stream deflateStream = null;

                // TODO: add state object and make this method static
                HandleChunkResult HandleChunk(ChunkHeader chunk, DataStream stream)
                {
                    if (seenChunkTypes.Count == 0)
                    {
                        if (chunk.Type != ChunkType.CgBI &&
                            chunk.Type != ChunkType.IHDR)
                            throw new StbImageReadException(ErrorCode.IHDRNotFirst);
                    }

                    try
                    {
                        switch (chunk.Type)
                        {
                            #region CgBI

                            case ChunkType.CgBI:
                            {
                                return HandleChunkResult.Finish;
                            }

                            #endregion

                            #region IHDR

                            case ChunkType.IHDR:
                            {
                                if (seenChunkTypes.Contains(ChunkType.IHDR))
                                    throw new StbImageReadException(ErrorCode.MultipleIHDR);

                                if (chunk.Length != 13)
                                    throw new StbImageReadException(ErrorCode.BadIHDRLength);

                                int width = s.ReadInt32BE();
                                if (width > (1 << 24))
                                    throw new StbImageReadException(ErrorCode.TooLarge);

                                int height = s.ReadInt32BE();
                                if (height > (1 << 24))
                                    throw new StbImageReadException(ErrorCode.TooLarge);

                                if ((width == 0) || (height == 0))
                                    throw new StbImageReadException(ErrorCode.EmptyImage);

                                byte depth = s.ReadByte();
                                if (depth != 1 &&
                                    depth != 2 &&
                                    depth != 4 &&
                                    depth != 8 &&
                                    depth != 16)
                                    throw new StbImageReadException(ErrorCode.UnsupportedBitDepth);

                                byte color = s.ReadByte();
                                if (color > 6 || (color == 3) && (header.Depth == 16))
                                    throw new StbImageReadException(ErrorCode.BadColorType);

                                if (color != 3 && (color & 1) != 0)
                                    throw new StbImageReadException(ErrorCode.BadColorType);

                                byte compression = s.ReadByte();
                                if (compression != 0)
                                    throw new StbImageReadException(ErrorCode.BadCompressionMethod);

                                byte filter = s.ReadByte();
                                if (filter != 0)
                                    throw new StbImageReadException(ErrorCode.BadFilterMethod);

                                byte interlace = s.ReadByte();
                                if (interlace > 1)
                                    throw new StbImageReadException(ErrorCode.BadInterlaceMethod);

                                bool hasCgbi = seenChunkTypes.Contains(ChunkType.CgBI);
                                header = new Header(
                                    width, height, depth, color, compression, filter, interlace, hasCgbi);

                                if (header.PaletteComp != 0)
                                {
                                    if (((1 << 30) / width / 4) < height)
                                        throw new StbImageReadException(ErrorCode.TooLarge);
                                }
                                else
                                {
                                    if (((1 << 30) / width / header.RawComp) < height)
                                        throw new StbImageReadException(ErrorCode.TooLarge);

                                    if (scan == ScanMode.Header)
                                        return HandleChunkResult.Header;
                                }

                                return HandleChunkResult.Finish;
                            }

                            #endregion

                            #region PLTE

                            case ChunkType.PLTE:
                            {
                                if (chunk.Length > 256 * 3)
                                    throw new StbImageReadException(ErrorCode.InvalidPLTE);

                                paletteLength = chunk.Length / 3;
                                if (paletteLength * 3 != chunk.Length)
                                    throw new StbImageReadException(ErrorCode.InvalidPLTE);

                                paletteData = new Rgba32[paletteLength];
                                for (int i = 0; i < paletteData.Length; i++)
                                {
                                    byte r = s.ReadByte();
                                    byte g = s.ReadByte();
                                    byte b = s.ReadByte();
                                    paletteData[i] = new Rgba32(r, g, b, 255);
                                }

                                // TODO: PaletteReady()
                                return HandleChunkResult.Finish;
                            }

                            #endregion

                            #region tRNS

                            case ChunkType.tRNS:
                            {
                                if (seenChunkTypes.Contains(ChunkType.IDAT))
                                    throw new StbImageReadException(ErrorCode.tRNSAfterIDAT);

                                if (header.PaletteComp != 0)
                                {
                                    if (paletteLength == 0)
                                        throw new StbImageReadException(ErrorCode.tRNSBeforePLTE);

                                    if (chunk.Length > paletteLength)
                                        throw new StbImageReadException(ErrorCode.BadtRNSLength);

                                    header.PaletteComp = 4;
                                    for (int i = 0; i < chunk.Length; i++)
                                        paletteData[i].A = s.ReadByte();
                                }
                                else
                                {
                                    if ((header.RawComp & 1) == 0)
                                        throw new StbImageReadException(ErrorCode.tRNSWithAlpha);

                                    if (chunk.Length != header.RawComp * 2)
                                        throw new StbImageReadException(ErrorCode.BadtRNSLength);

                                    if (header.Depth == 16)
                                    {
                                        if (header.RawComp == 1)
                                        {
                                            tc16 = new Rgb48(s.ReadUInt16BE(), 0, 0);
                                        }
                                        else if (header.RawComp == 3)
                                        {
                                            tc16 = new Rgb48(
                                                s.ReadUInt16BE(),
                                                s.ReadUInt16BE(),
                                                s.ReadUInt16BE());
                                        }
                                        else
                                        {
                                            throw new StbImageReadException(ErrorCode.BadComponentCount);
                                        }
                                    }
                                    else
                                    {
                                        byte ReadUInt16AsByte()
                                        {
                                            return (byte)((s.ReadUInt16BE() & 255) * DepthScaleTable[header.Depth]);
                                        }

                                        if (header.RawComp == 1)
                                        {
                                            tc8 = new Rgb24(ReadUInt16AsByte(), 0, 0);
                                        }
                                        else if (header.RawComp == 3)
                                        {
                                            tc8 = new Rgb24(
                                                ReadUInt16AsByte(),
                                                ReadUInt16AsByte(),
                                                ReadUInt16AsByte());
                                        }
                                        else
                                        {
                                            throw new StbImageReadException(ErrorCode.BadComponentCount);
                                        }
                                    }
                                    has_transparency = true;
                                }
                                return HandleChunkResult.Finish;
                            }

                            #endregion

                            #region IDAT

                            case ChunkType.IDAT:
                            {
                                if (header.PaletteComp != 0 && paletteLength == 0)
                                    throw new StbImageReadException(ErrorCode.NoPLTE);

                                if (!seenChunkTypes.Contains(ChunkType.IDAT))
                                {
                                    InitializeReadState();

                                    if (scan == ScanMode.Header)
                                        return HandleChunkResult.Header;

                                    // TODO: add support for custom decompressor streams
                                    deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
                                }
                                return HandleChunkResult.Include;
                            }

                            #endregion

                            #region IEND

                            case ChunkType.IEND:
                            {
                                if (scan == ScanMode.Header)
                                {
                                    InitializeReadState();
                                    return HandleChunkResult.Header;
                                }

                                if (!seenChunkTypes.Contains(ChunkType.IDAT))
                                    throw new StbImageReadException(ErrorCode.NoIDAT);

                                return HandleChunkResult.Include;
                            }

                            #endregion

                            default:
                            {
                                if (chunk.IsCritical)
                                    throw new StbImageReadException(ErrorCode.UnknownChunk);

                                return HandleChunkResult.Finish;
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

                    // CgBI (IPhone) streams dont have deflate header
                    // and we just skip it for normal streams.
                    if (!header.HasCgbi)
                    {
                        for (int i = 0; i < 2; i++)
                            if (dataStream.ReadByte() == -1)
                                throw new StbImageReadException(new EndOfStreamException());
                    }

                    var palette = header.PaletteComp != 0
                        ? new Palette(paletteData.AsMemory(0, paletteLength), header.PaletteComp)
                        : (Palette?)null;

                    var transparency = has_transparency
                        ? new Transparency(tc8, tc16)
                        : (Transparency?)null;

                    if (!CreateImage(s, ri, deflateStream, header, transparency, palette))
                        return false;

                    return true;
                }
            }

            public static bool CreateImage(
                ReadContext s, ReadState ri, Stream imageData,
                in Header header, in Transparency? transparency, in Palette? palette)
            {
                int width = header.Width;
                int height = header.Height;
                int depth = header.Depth;
                int rawComp = header.RawComp;
                int bytes_per_comp = depth == 16 ? 2 : 1;
                int outComp = header.RawComp + (transparency.HasValue ? 1 : 0);

                int row_bytes = width * outComp * bytes_per_comp;
                int img_width_bytes = (width * rawComp * depth + 7) / 8;
                int raw_width_bytes = img_width_bytes + 1;

                var previousRowBuffer = new byte[row_bytes];
                var rowBuffer = new byte[row_bytes];
                var data = new byte[raw_width_bytes];

                void SwapBuffers()
                {
                    var tmp = previousRowBuffer;
                    previousRowBuffer = rowBuffer;
                    rowBuffer = tmp;
                }

                if (header.Interlace == 0)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int read = imageData.Read(data, 0, data.Length);
                        if (read != data.Length)
                            throw new StbImageReadException(ErrorCode.NotEnoughPixels);

                        DefilterRow(previousRowBuffer, data, y, outComp, header, rowBuffer);

                        if (y > 0)
                        {
                            DecodeDefilteredRow(previousRowBuffer, outComp, header);
                            ProcessDefilteredRow(s, ri, previousRowBuffer, y - 1, outComp, 1, header, transparency, palette);
                        }
                        SwapBuffers();
                    }

                    DecodeDefilteredRow(previousRowBuffer, outComp, header);
                    ProcessDefilteredRow(s, ri, previousRowBuffer, height - 1, outComp, 1, header, transparency, palette);

                    return true;
                }

                //var ww = new System.Diagnostics.Stopwatch();
                for (int p = 0; p < 7; p++)
                {
                    //ww.Restart();
                    int orig_x = Interlace_xorig[p];
                    int orig_y = Interlace_yorig[p];
                    int spc_x = Interlace_xspc[p];
                    int spc_y = Interlace_yspc[p];

                    int w = (width - orig_x + spc_x - 1) / spc_x;
                    int h = (height - orig_y + spc_y - 1) / spc_y;
                    if (w != 0 && h != 0)
                    {
                        //Console.Write("Interlace step: " + p);

                        //if (!ProcessRow(s, ri, buffer, w, h, color))
                        //    return false;

                        // TODO: read row by row instead of buffering the whole file

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

            public static void DefilterRow(
                ReadOnlySpan<byte> previousRow,
                ReadOnlySpan<byte> filteredData,
                int y,
                int outComp,
                in Header header,
                Span<byte> rowDestination)
            {
                int width = header.Width;
                int depth = header.Depth;
                int rawComp = header.RawComp;
                int w = width;
                int bytes = depth == 16 ? 2 : 1;
                int output_bytes = outComp * bytes;
                int filter_bytes = rawComp * bytes;
                int img_width_bytes = ((width * rawComp * depth) + 7) / 8;

                int rawOff = 0;
                int curOff = 0;
                int priorOff = 0;

                var filter = (FilterType)filteredData[rawOff++];
                if ((int)filter > 4)
                    throw new StbImageReadException(ErrorCode.InvalidFilter);

                if (depth < 8)
                {
                    int o = width * outComp - img_width_bytes;
                    curOff += o;
                    priorOff += o;
                    filter_bytes = 1;
                    w = img_width_bytes;
                }

                if (y == 0)
                    filter = FirstRowFilter[(int)filter];

                int k;
                for (k = 0; k < filter_bytes; k++)
                {
                    var cur = rowDestination.Slice(curOff);
                    var rawslice = filteredData.Slice(rawOff);

                    switch (filter)
                    {
                        case FilterType.None:
                        case FilterType.Sub:
                        case FilterType.AverageFirst:
                        case FilterType.PaethFirst:
                            cur[k] = rawslice[k];
                            break;

                        case FilterType.Up:
                            cur[k] = (byte)((rawslice[k] + previousRow[priorOff + k]) & 255);
                            break;

                        case FilterType.Average:
                            cur[k] = (byte)((rawslice[k] + (previousRow[priorOff + k] >> 1)) & 255);
                            break;

                        case FilterType.Paeth:
                            cur[k] = (byte)(
                                (rawslice[k] + CRuntime.Paeth32(0, previousRow[priorOff + k], 0)) & 255);
                            break;
                    }
                }

                if (depth == 8)
                {
                    if (rawComp != outComp)
                        rowDestination[curOff + rawComp] = 255;
                    rawOff += rawComp;
                    curOff += outComp;
                    priorOff += outComp;
                }
                else if (depth == 16)
                {
                    if (rawComp != outComp)
                    {
                        rowDestination[curOff + filter_bytes] = 255;
                        rowDestination[curOff + filter_bytes + 1] = 255;
                    }
                    rawOff += filter_bytes;
                    curOff += output_bytes;
                    priorOff += output_bytes;
                }
                else
                {
                    rawOff += 1;
                    curOff += 1;
                    priorOff += 1;
                }

                if ((depth < 8) || (rawComp == outComp))
                {
                    int nk = (w - 1) * filter_bytes;
                    var raws = filteredData.Slice(rawOff, nk);

                    var cur = rowDestination.Slice(curOff);
                    var curf = rowDestination.Slice(curOff - filter_bytes);

                    switch (filter)
                    {
                        case FilterType.None:
                            raws.CopyTo(cur);
                            break;

                        case FilterType.Sub:
                            for (k = 0; k < raws.Length; k++)
                            {
                                cur[k] = (byte)((raws[k] + curf[k]) & 255);
                            }
                            break;

                        case FilterType.Up:
                        {
                            var prior = previousRow.Slice(priorOff);
                            for (k = 0; k < raws.Length; k++)
                            {
                                cur[k] = (byte)((raws[k] + prior[k]) & 255);
                            }
                            break;
                        }

                        case FilterType.Average:
                        {
                            var prior = previousRow.Slice(priorOff);
                            for (k = 0; k < raws.Length; k++)
                            {
                                cur[k] = (byte)(
                                    (raws[k] + ((prior[k] + curf[k]) >> 1)) & 255);
                            }
                            break;
                        }

                        case FilterType.Paeth:
                            // TODO: optimize/vectorize this
                            for (k = 0; k < raws.Length; k++)
                            {
                                int p = priorOff + k;
                                cur[k] = (byte)(
                                    (raws[k] + CRuntime.Paeth32(
                                        curf[k], previousRow[p], previousRow[p - filter_bytes])) & 255);
                            }
                            break;

                        case FilterType.AverageFirst:
                            for (k = 0; k < raws.Length; k++)
                            {
                                cur[k] = (byte)((raws[k] + (curf[k] >> 1)) & 255);
                            }
                            break;

                        case FilterType.PaethFirst:
                            for (k = 0; k < raws.Length; k++)
                            {
                                cur[k] = (byte)(
                                    (raws[k] + CRuntime.Paeth32(curf[k], 0, 0)) & 255);
                            }
                            break;
                    }
                    //rawOff += nk;
                }
                else
                {
                    int max = width - 1;
                    switch (filter)
                    {
                        case FilterType.None:
                        {
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes)
                            {
                                var raws = filteredData.Slice(rawOff, filter_bytes);
                                var cur = rowDestination.Slice(curOff);
                                raws.CopyTo(cur);
                                cur[filter_bytes] = 255;
                            }
                            //priorOff += output_bytes * max;
                            break;
                        }

                        case FilterType.Sub:
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes)
                            {
                                var cur = rowDestination.Slice(curOff);
                                var curo = rowDestination.Slice(curOff - output_bytes);
                                for (k = 0; k < filter_bytes; k++)
                                {
                                    cur[k] = (byte)((filteredData[rawOff + k] + curo[k]) & 255);
                                }
                                cur[filter_bytes] = 255;
                            }
                            //priorOff += output_bytes * max;
                            break;

                        case FilterType.Up:
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes, priorOff += output_bytes)
                            {
                                var cur = rowDestination.Slice(curOff);
                                var prior = previousRow.Slice(priorOff);
                                for (k = 0; k < filter_bytes; k++)
                                {
                                    cur[k] = (byte)((filteredData[rawOff + k] + prior[k]) & 255);
                                }
                                cur[filter_bytes] = 255;
                            }
                            break;

                        case FilterType.Average:
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes, priorOff += output_bytes)
                            {
                                var cur = rowDestination.Slice(curOff);
                                var curo = rowDestination.Slice(curOff - output_bytes);
                                var prior = previousRow.Slice(priorOff);
                                for (k = 0; k < filter_bytes; k++)
                                {
                                    cur[k] = (byte)(
                                        (filteredData[rawOff + k] + ((prior[k] + curo[k]) >> 1)) & 255);
                                }
                                cur[filter_bytes] = 255;
                            }
                            break;

                        case FilterType.Paeth:
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes, priorOff += output_bytes)
                            {
                                var cur = rowDestination.Slice(curOff);
                                var curo = rowDestination.Slice(curOff - output_bytes);
                                var prior = previousRow.Slice(priorOff);
                                var prioro = previousRow.Slice(priorOff - output_bytes);
                                for (k = 0; k < filter_bytes; k++)
                                {
                                    cur[k] = (byte)(
                                        (filteredData[rawOff + k] + CRuntime.Paeth32(
                                            curo[k], prior[k], prioro[k])) & 255);
                                }
                                cur[filter_bytes] = 255;
                            }
                            break;

                        case FilterType.AverageFirst:
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes)
                            {
                                var cur = rowDestination.Slice(curOff);
                                var curo = rowDestination.Slice(curOff - output_bytes);
                                for (k = 0; k < filter_bytes; k++)
                                {
                                    cur[k] = (byte)((filteredData[rawOff + k] + (curo[k] >> 1)) & 255);
                                }
                                cur[filter_bytes] = 255;
                            }
                            //priorOff += output_bytes * max;
                            break;

                        case FilterType.PaethFirst:
                            for (int i = 0; i < max; i++,
                                rawOff += filter_bytes, curOff += output_bytes)
                            {
                                var cur = rowDestination.Slice(curOff);
                                var curo = rowDestination.Slice(curOff - output_bytes);
                                for (k = 0; k < filter_bytes; k++)
                                {
                                    cur[k] = (byte)(
                                        (filteredData[rawOff + k] + CRuntime.Paeth32(curo[k], 0, 0)) & 255);
                                }
                                cur[filter_bytes] = 255;
                            }
                            //priorOff += output_bytes * max;
                            break;
                    }

                    if (depth == 16)
                    {
                        var cur = rowDestination.Slice(filter_bytes + 1);
                        for (int i = 0; i < cur.Length; i += output_bytes)
                            cur[i] = 255;
                    }
                }
            }

            public static void DecodeDefilteredRow(Span<byte> row, int outComp, in Header header)
            {
                int width = header.Width;
                int depth = header.Depth;
                int rawComp = header.RawComp;

                if (depth == 16)
                {
                    // convert bigendian to littleendian

                    var row16 = MemoryMarshal.Cast<byte, ushort>(row);

                    for (int i = 0; i < row16.Length; i++)
                        row16[i] = BinaryPrimitives.ReverseEndianness(row16[i]);

                }
                else if (depth < 8)
                {
                    int img_width_bytes = (width * rawComp * depth + 7) / 8;
                    var src = row.Slice(width * outComp - img_width_bytes);

                    // TODO: make loops forward loops

                    byte scale = (byte)((header.ColorType == 0) ? DepthScaleTable[depth] : 1);

                    int rowOff = 0;
                    int srcOff = 0;
                    int k;
                    if (depth == 4)
                    {
                        for (k = width * rawComp; k >= 2; k -= 2, srcOff++)
                        {
                            row[rowOff++] = (byte)(scale * (src[srcOff] >> 4));
                            row[rowOff++] = (byte)(scale * ((src[srcOff]) & 0x0f));
                        }
                        if (k > 0)
                            row[rowOff++] = (byte)(scale * (src[srcOff] >> 4));
                    }
                    else if (depth == 2)
                    {
                        for (k = width * rawComp; k >= 4; k -= 4, srcOff++)
                        {
                            row[rowOff++] = (byte)(scale * (src[srcOff] >> 6));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x03));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 2) & 0x03));
                            row[rowOff++] = (byte)(scale * ((src[srcOff]) & 0x03));
                        }
                        if (k > 0)
                            row[rowOff++] = (byte)(scale * (src[srcOff] >> 6));
                        if (k > 1)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x03));
                        if (k > 2)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 2) & 0x03));
                    }
                    else if (depth == 1)
                    {
                        for (k = width * rawComp; k >= 8; k -= 8, srcOff++)
                        {
                            row[rowOff++] = (byte)(scale * (src[srcOff] >> 7));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 6) & 0x01));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 5) & 0x01));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x01));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 3) & 0x01));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 2) & 0x01));
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 1) & 0x01));
                            row[rowOff++] = (byte)(scale * ((src[srcOff]) & 0x01));
                        }
                        if (k > 0)
                            row[rowOff++] = (byte)(scale * (src[srcOff] >> 7));
                        if (k > 1)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 6) & 0x01));
                        if (k > 2)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 5) & 0x01));
                        if (k > 3)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x01));
                        if (k > 4)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 3) & 0x01));
                        if (k > 5)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 2) & 0x01));
                        if (k > 6)
                            row[rowOff++] = (byte)(scale * ((src[srcOff] >> 1) & 0x01));
                    }

                    if (rawComp != outComp)
                    {
                        if (rawComp == 1)
                        {
                            for (int q = width - 1; q >= 0; --q)
                            {
                                row[q * 2 + 1] = 255;
                                row[q * 2 + 0] = row[q];
                            }
                        }
                        else
                        {
                            for (int q = width - 1; q >= 0; --q)
                            {
                                row[q * 4 + 3] = 255;
                                row[q * 4 + 2] = row[q * 3 + 2];
                                row[q * 4 + 1] = row[q * 3 + 1];
                                row[q * 4 + 0] = row[q * 3 + 0];
                            }
                        }
                    }
                }
            }

            public static void ProcessDefilteredRow(
                ReadContext s,
                ReadState ri,
                Span<byte> row,
                int y,
                int outComp,
                int outIncrement,
                in Header header,
                in Transparency? transparency,
                in Palette? palette)
            {
                if (transparency.HasValue)
                {
                    if (header.Depth == 16)
                        ComputeTransparency16(row, transparency.Value.Tc16, outComp);
                    else
                        ComputeTransparency8(row, transparency.Value.Tc8, outComp);
                }

                if (header.HasCgbi && s.DeIphoneFlag && outComp > 2)
                {
                    DeIphone(row, outComp, s.UnpremultiplyOnLoad);
                }

                if (palette.HasValue)
                {
                    var paletteData = palette.Value.Data.Span;
                    int comp = palette.Value.Components;

                    Span<byte> buffer = stackalloc byte[1024];
                    int left = header.Width;
                    int offset = 0;
                    while (left > 0)
                    {
                        int count = Math.Min(left, buffer.Length / comp);
                        var rowSlice = row.Slice(offset, count);
                        var bufferSlice = buffer.Slice(0, count * comp);

                        ExpandPalette(rowSlice, bufferSlice, comp, paletteData);
                        ri.OutputPixelLine(AddressingMajor.Row, y, offset, bufferSlice);

                        left -= count;
                        offset += count;
                    }
                }
                else
                {
                    ri.OutputPixelLine(AddressingMajor.Row, y, 0, row);
                }
            }

            public static void ComputeTransparency8(Span<byte> row, Rgb24 mask, int comp)
            {
                if (comp == 2)
                {
                    for (int i = 0; i < row.Length; i += 2)
                    {
                        row[i + 1] = row[i] == mask.R ? byte.MinValue : byte.MaxValue;
                    }
                }
                else if (comp == 4)
                {
                    for (int i = 0; i < row.Length; i += 4)
                    {
                        if (row[i + 0] == mask.R &&
                            row[i + 1] == mask.G &&
                            row[i + 2] == mask.B)
                            row[i + 3] = 0;
                    }
                }
                else
                {
                    throw new StbImageReadException(ErrorCode.BadComponentCount);
                }
            }

            public static void ComputeTransparency16(Span<byte> row, Rgb48 mask, int comp)
            {
                var row16 = MemoryMarshal.Cast<byte, ushort>(row);

                if (comp == 2)
                {
                    for (int i = 0; i < row16.Length; i += 2)
                    {
                        row16[i + 1] = row16[i] == mask.R ? ushort.MinValue : ushort.MaxValue;
                    }
                }
                else if (comp == 4)
                {
                    for (int i = 0; i < row16.Length; i += 4)
                    {
                        if (row16[i + 0] == mask.R &&
                            row16[i + 1] == mask.G &&
                            row16[i + 2] == mask.B)
                            row16[i + 3] = 0;
                    }
                }
                else
                {
                    throw new StbImageReadException(ErrorCode.BadComponentCount);
                }
            }

            public static void DeIphone(Span<byte> row, int outComp, bool unpremultiply)
            {
                if (outComp == 3)
                {
                    var rgb = MemoryMarshal.Cast<byte, Rgb24>(row);
                    for (int x = 0; x < rgb.Length; x++)
                    {
                        byte b = rgb[x].R;
                        rgb[x].R = rgb[x].B;
                        rgb[x].B = b;
                    }
                }
                else
                {
                    var rgba = MemoryMarshal.Cast<byte, Rgba32>(row);
                    if (unpremultiply)
                    {
                        for (int x = 0; x < rgba.Length; x++)
                        {
                            byte a = rgba[x].A;
                            byte b = rgba[x].Rgb.R;
                            if (a != 0)
                            {
                                byte half = (byte)(a / 2);
                                rgba[x].Rgb.R = (byte)((rgba[x].Rgb.B * 255 + half) / a);
                                rgba[x].Rgb.G = (byte)((rgba[x].Rgb.G * 255 + half) / a);
                                rgba[x].Rgb.B = (byte)((b * 255 + half) / a);
                            }
                            else
                            {
                                rgba[x].Rgb.R = rgba[x].Rgb.B;
                                rgba[x].Rgb.B = b;
                            }
                        }
                    }
                    else
                    {
                        for (int x = 0; x < rgba.Length; x++)
                        {
                            byte b = rgba[x].Rgb.R;
                            rgba[x].Rgb.R = rgba[x].Rgb.B;
                            rgba[x].Rgb.B = b;
                        }
                    }
                }
            }

            public static void ExpandPalette(
                ReadOnlySpan<byte> source, Span<byte> destination,
                int comp, ReadOnlySpan<Rgba32> palette)
            {
                if (comp == 3)
                {
                    var rgbDst = MemoryMarshal.Cast<byte, Rgb24>(destination);
                    for (int i = 0; i < rgbDst.Length; i++)
                    {
                        int n = source[i];
                        rgbDst[i] = palette[n].Rgb;
                    }
                }
                else if (comp == 4)
                {
                    var rgbaDst = MemoryMarshal.Cast<byte, Rgba32>(destination);
                    for (int i = 0; i < rgbaDst.Length; i++)
                    {
                        int n = source[i];
                        rgbaDst[i] = palette[n];
                    }
                }
                else
                {
                    throw new StbImageReadException(ErrorCode.BadPalette);
                }
            }

            public class DataStream : Stream
            {
                private HandleChunkDelegate _handleChunk;
                private int _chunkLeftToRead;

                public ReadContext Context { get; }
                public ChunkHeader LastChunkHeader { get; private set; }
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
                    // TODO: validate crc (and based on some config?)

                    // LastChunkHeader.IsCritical and take this into consideration

                    // maybe some enum?: CrcValidation { Ignore, Critical, Full } ???

                    int crc = Context.ReadInt32BE();
                }

                public override int Read(Span<byte> buffer)
                {
                    if (LastResult == HandleChunkResult.Header)
                        return 0;

                    Start:
                    if (_chunkLeftToRead >= 0)
                    {
                        int toRead = Math.Min(buffer.Length, _chunkLeftToRead);
                        Context.ReadBytes(buffer.Slice(0, toRead));

                        _chunkLeftToRead -= toRead;
                        if (_chunkLeftToRead == 0)
                        {
                            FinishChunk();
                            _chunkLeftToRead = -1;
                        }
                        return toRead;
                    }

                    Next:
                    LastChunkHeader = ChunkHeader.Read(Context);
                    long chunkDataEnd = Context.StreamPosition + LastChunkHeader.Length;

                    LastResult = _handleChunk.Invoke(LastChunkHeader, this);
                    switch (LastResult)
                    {
                        case HandleChunkResult.Header:
                            FinishChunk();
                            return 0;

                        case HandleChunkResult.Finish:
                            long chunkDataLeft = chunkDataEnd - Context.StreamPosition;
                            if (chunkDataLeft < 0)
                                throw new InvalidOperationException(
                                    "Handler read more data than the chunk contained.");

                            Context.Skip((int)chunkDataLeft);
                            FinishChunk();
                            goto Next;

                        case HandleChunkResult.Include:
                            _chunkLeftToRead = LastChunkHeader.Length;
                            goto Start;

                        default:
                            throw new InvalidOperationException("Undefined chunk handler result.");
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
                }
            }
        }
    }
}
