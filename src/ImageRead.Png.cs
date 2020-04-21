using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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

            private static FilterType[] FirstRowFilter =
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

                public readonly int Length;
                public readonly uint Type;

                public PngChunkHeader(uint length, uint type)
                {
                    if (length > int.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(length));

                    Length = (int)length;
                    Type = type;
                }

                public static PngChunkHeader Read(ReadContext s)
                {
                    uint length = s.ReadUInt32BE();
                    uint type = s.ReadUInt32BE();
                    return new PngChunkHeader(length, type);
                }
            }

            public static bool CheckSignature(ReadContext s)
            {
                for (int i = 0; i < 8; i++)
                    if (s.ReadByte() != Signature[i])
                        return false;
                return true;
            }

            public struct Header
            {
                public int Width { get; }
                public int Height { get; }
                public int Depth { get; }
                public int ColorType { get; }
                public int compression { get; }
                public int filter { get; }
                public int interlace { get; }

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
                    int compression, int filter, int interlace)
                {
                    Width = width;
                    Height = height;
                    Depth = depth;
                    this.ColorType = color;
                    this.compression = compression;
                    this.filter = filter;
                    this.interlace = interlace;

                    PaletteComp = color == 3 ? 3 : 0;
                }
            }

            public static bool CreateImage(
                ReadContext s, ReadState ri, Stream imageData,
                in Header header, in Transparency? transparency, in Palette? palette)
            {
                int width = header.Width;
                int height = header.Height;
                int bytes_per_comp = ri.OutDepth == 16 ? 2 : 1;
                int bytes_per_pixel = ri.OutComponents * bytes_per_comp;
                int raw_out_comp = header.RawComp + (transparency.HasValue ? 1 : 0);

                int stride = width * bytes_per_pixel;

                if (header.interlace == 0)
                {
                    var a = new stbi__png();
                    try
                    {
                        var ms = new MemoryStream();
                        imageData.CopyTo(ms);

                        var rawdata = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                        CreateImageRaw(
                            s, a, header.RawComp, raw_out_comp, width, height, header.Depth, header.ColorType, rawdata);

                        if (transparency.HasValue)
                        {
                            var ppp = new Span<byte>(a._out_, height * stride);
                            if (header.Depth == 16)
                                ComputeTransparency16(ppp, transparency.Value.Tc16, raw_out_comp);
                            else
                                ComputeTransparency8(ppp, transparency.Value.Tc8, raw_out_comp);
                        }

                        //if ((((is_iphone) != 0) && ((stbi__de_iphone_flag) != 0)) && (raw_out_comp > (2)))
                        //    stbi__de_iphone(z);

                        if (palette.HasValue)
                        {
                            var resultrow = new byte[stride].AsSpan();
                            var indexrows = new Span<byte>(a._out_, height * width);
                            var paletteData = palette.Value.Data.Span;
                            int comp = palette.Value.Components;
                            for (int y = 0; y < height; y++)
                            {
                                var indexrow = indexrows.Slice(y * width, width);
                                ExpandPalette(indexrow, resultrow, comp, paletteData);
                                ri.OutputPixelLine(AddressingMajor.Row, y, 0, resultrow);
                            }
                        }
                        else
                        {
                            var rows = new Span<byte>(a._out_, height * stride);
                            for (int y = 0; y < height; y++)
                            {
                                var row = rows.Slice(y * stride, stride);
                                ri.OutputPixelLine(AddressingMajor.Row, y, 0, row);
                            }
                        }

                        return true;
                    }
                    finally
                    {
                        CRuntime.Free(a._out_);
                    }
                    /*
                    bool ProcessFiltered(Span<byte> filtered, int row)
                    {
                        if (!ProcessFilteredRow(
                            s, ri, width, row, color,
                            transparency, palette, filtered, palettizedRow))
                            return false;
                    
                        var result = palettizedRow ?? filtered;
                        ri.OutputLine(AddressingMajor.Row, row, 0, result);
                        return true;
                    }
                    
                    for (int y = 0; y < height; y++)
                    {
                        if (data.Read(buffer) != buffer.Length)
                        {
                            s.Error(ErrorCode.EndOfStream);
                            return false;
                        }
                    
                        if (!ProcessFilter(s, ri, width, y, buffer, secondRow, firstRow))
                            return false;
                    
                        // swap
                        var first = firstRow;
                        var second = secondRow;
                        var third = thirdRow;
                        firstRow = third;
                        secondRow = first;
                        thirdRow = second;
                    
                        if (y < 2)
                            continue;
                    
                        if (!ProcessFiltered(third, y - 2))
                            return false;
                    }
                    
                    return ProcessFiltered(secondRow, height - 2) && ProcessFiltered(firstRow, height - 1);
                    */
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
                            throw new StbImageReadException(new EndOfStreamException());

                        _chunkLeftToRead -= toRead;
                        if (_chunkLeftToRead == 0)
                        {
                            FinishChunk();
                            _chunkLeftToRead = -1;
                        }
                        return toRead;
                    }

                    if (LastResult == HandleChunkResult.Header)
                        return 0;

                    Next:
                    var chunkHeader = PngChunkHeader.Read(Context);
                    LastResult = _handleChunk.Invoke(chunkHeader, this);

                    switch (LastResult)
                    {
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

                Rgba32[] paletteData = null;
                int paletteLength = 0;

                Rgb24 tc8 = default;
                Rgb48 tc16 = default;
                Header header = default;

                bool has_transparency = false;
                bool is_iphone = false;

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

                var seenChunkTypes = new HashSet<uint>();

                // TODO: add custom deflate algo support
                Stream deflateStream = null;

                // TODO: add state object and make this method static
                HandleChunkResult HandleChunk(PngChunkHeader chunk, DataStream stream)
                {
                    // TODO: handle iphone (CgBI) chunk
                    if (seenChunkTypes.Count == 0 && chunk.Type != PngChunkHeader.IHDR)
                        throw new StbImageReadException(ErrorCode.IHDRNotFirst);

                    try
                    {
                        switch (chunk.Type)
                        {
                            #region IHDR

                            case PngChunkHeader.IHDR:
                            {
                                if (seenChunkTypes.Count > 0)
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

                                header = new Header(
                                    width, height, depth, color, compression, filter, interlace);

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
                                return HandleChunkResult.Manual;
                            }

                            #endregion

                            #region tRNS

                            case PngChunkHeader.tRNS:
                            {
                                if (seenChunkTypes.Contains(PngChunkHeader.IDAT))
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
                                return HandleChunkResult.Manual;
                            }

                            #endregion

                            #region IDAT

                            case PngChunkHeader.IDAT:
                            {
                                if (header.PaletteComp != 0 && paletteLength == 0)
                                    throw new StbImageReadException(ErrorCode.NoPLTE);

                                if (!seenChunkTypes.Contains(PngChunkHeader.IDAT))
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

                            case PngChunkHeader.IEND:
                            {
                                if (scan == ScanMode.Header)
                                {
                                    InitializeReadState();
                                    return HandleChunkResult.Header;
                                }

                                if (!seenChunkTypes.Contains(PngChunkHeader.IDAT))
                                    throw new StbImageReadException(ErrorCode.NoIDAT);

                                return HandleChunkResult.Include;
                            }

                            #endregion

                            default:
                            {
                                // check if the chunk is critical 
                                if ((chunk.Type & (1 << 29)) == 0)
                                    throw new StbImageReadException(ErrorCode.UnknownChunk);

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

                    // CgBI (IPhone) streams dont have deflate header
                    // and we just skip it for normal streams.
                    if (!is_iphone)
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

                    // TODO: FIXME:
                    //if (is_iphone && s.DeIphoneFlag && ri.OutComponents > 2)
                    //    DeIphone(ref z, ref ri);

                    return true;
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

            public static void CreateImageRaw(
                ReadContext s, stbi__png a, int raw_comp, int out_comp, int width, int height, int depth, int color,
                ReadOnlySpan<byte> raw)
            {
                int bytes = depth == 16 ? 2 : 1;
                int output_bytes = out_comp * bytes;
                int stride = width * output_bytes;
                int filter_bytes = raw_comp * bytes;
                int w = width;
                a._out_ = (byte*)stbi__malloc_mad3(width, height, output_bytes, 0);
                if (a._out_ == null)
                    throw new Exception();
                if (stbi__mad3sizes_valid(raw_comp, width, depth, 7) == 0)
                    throw new Exception();

                int img_width_bytes = ((width * raw_comp * depth) + 7) / 8;
                int raw_width_bytes = (img_width_bytes + 1);
                int img_len = raw_width_bytes * height;
                if (raw.Length < img_len)
                    throw new StbImageReadException(ErrorCode.NotEnoughPixels);

                int k = 0;
                int rawOff = 0;
                for (int j = 0; j < height; ++j)
                {
                    var filter = (FilterType)raw[rawOff++];
                    if ((int)filter > 4)
                        throw new StbImageReadException(ErrorCode.InvalidFilter);

                    int curOff = 0;
                    int priorOff = 0;

                    if (depth < 8)
                    {
                        int o = width * out_comp - img_width_bytes;
                        curOff += o;
                        priorOff += o;
                        filter_bytes = 1;
                        w = img_width_bytes;
                    }

                    var curr = new Span<byte>(a._out_ + stride * j, stride);
                    Span<byte> priorr;

                    if (j == 0)
                    {
                        filter = FirstRowFilter[(int)filter];
                        priorr = Span<byte>.Empty;
                    }
                    else
                    {
                        priorr = new Span<byte>(a._out_ + stride * (j - 1), stride);
                    }

                    for (k = 0; k < filter_bytes; k++)
                    {
                        var cur = curr.Slice(curOff);
                        var rawslice = raw.Slice(rawOff);

                        switch (filter)
                        {
                            case FilterType.None:
                            case FilterType.Sub:
                            case FilterType.AverageFirst:
                            case FilterType.PaethFirst:
                                cur[k] = rawslice[k];
                                break;

                            case FilterType.Up:
                                cur[k] = (byte)((rawslice[k] + priorr[priorOff + k]) & 255);
                                break;

                            case FilterType.Average:
                                cur[k] = (byte)((rawslice[k] + (priorr[priorOff + k] >> 1)) & 255);
                                break;

                            case FilterType.Paeth:
                                cur[k] = (byte)((rawslice[k] + CRuntime.Paeth32(0, priorr[priorOff + k], 0)) & 255);
                                break;
                        }
                    }

                    if (depth == 8)
                    {
                        if (raw_comp != out_comp)
                            curr[curOff + raw_comp] = 255;
                        rawOff += raw_comp;
                        curOff += out_comp;
                        priorOff += out_comp;
                    }
                    else if (depth == 16)
                    {
                        if (raw_comp != out_comp)
                        {
                            curr[curOff + filter_bytes] = 255;
                            curr[curOff + filter_bytes + 1] = 255;
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

                    if ((depth < 8) || (raw_comp == out_comp))
                    {
                        int nk = (w - 1) * filter_bytes;
                        var raws = raw.Slice(rawOff, nk);

                        var cur = curr.Slice(curOff);
                        var curf = curr.Slice(curOff - filter_bytes);

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
                                var prior = priorr.Slice(priorOff);
                                for (k = 0; k < raws.Length; k++)
                                {
                                    cur[k] = (byte)((raws[k] + prior[k]) & 255);
                                }
                                break;
                            }

                            case FilterType.Average:
                            {
                                var prior = priorr.Slice(priorOff);
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
                                            curf[k], priorr[p], priorr[p - filter_bytes])) & 255);
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
                        rawOff += nk;
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
                                    var raws = raw.Slice(rawOff, filter_bytes);
                                    var cur = curr.Slice(curOff);
                                    raws.CopyTo(cur);
                                    cur[filter_bytes] = 255;
                                }
                                priorOff += output_bytes * max;
                                break;
                            }

                            case FilterType.Sub:
                                for (int i = 0; i < max; i++,
                                    rawOff += filter_bytes, curOff += output_bytes)
                                {
                                    var cur = curr.Slice(curOff);
                                    var curo = curr.Slice(curOff - output_bytes);
                                    for (k = 0; k < filter_bytes; k++)
                                    {
                                        cur[k] = (byte)((raw[rawOff + k] + curo[k]) & 255);
                                    }
                                    cur[filter_bytes] = 255;
                                }
                                priorOff += output_bytes * max;
                                break;

                            case FilterType.Up:
                                for (int i = 0; i < max; i++,
                                    rawOff += filter_bytes, curOff += output_bytes, priorOff += output_bytes)
                                {
                                    var cur = curr.Slice(curOff);
                                    var prior = priorr.Slice(priorOff);
                                    for (k = 0; k < filter_bytes; k++)
                                    {
                                        cur[k] = (byte)((raw[rawOff + k] + prior[k]) & 255);
                                    }
                                    cur[filter_bytes] = 255;
                                }
                                break;

                            case FilterType.Average:
                                for (int i = 0; i < max; i++,
                                    rawOff += filter_bytes, curOff += output_bytes, priorOff += output_bytes)
                                {
                                    var cur = curr.Slice(curOff);
                                    var curo = curr.Slice(curOff - output_bytes);
                                    var prior = priorr.Slice(priorOff);
                                    for (k = 0; k < filter_bytes; k++)
                                    {
                                        cur[k] = (byte)(
                                            (raw[rawOff + k] + ((prior[k] + curo[k]) >> 1)) & 255);
                                    }
                                    cur[filter_bytes] = 255;
                                }
                                break;

                            case FilterType.Paeth:
                                for (int i = 0; i < max; i++,
                                    rawOff += filter_bytes, curOff += output_bytes, priorOff += output_bytes)
                                {
                                    var cur = curr.Slice(curOff);
                                    var curo = curr.Slice(curOff - output_bytes);
                                    var prior = priorr.Slice(priorOff);
                                    var prioro = priorr.Slice(priorOff - output_bytes);
                                    for (k = 0; k < filter_bytes; k++)
                                    {
                                        cur[k] = (byte)(
                                            (raw[rawOff + k] + CRuntime.Paeth32(
                                                curo[k], prior[k], prioro[k])) & 255);
                                    }
                                    cur[filter_bytes] = 255;
                                }
                                break;

                            case FilterType.AverageFirst:
                                for (int i = 0; i < max; i++,
                                    rawOff += filter_bytes, curOff += output_bytes)
                                {
                                    var cur = curr.Slice(curOff);
                                    var curo = curr.Slice(curOff - output_bytes);
                                    for (k = 0; k < filter_bytes; k++)
                                    {
                                        cur[k] = (byte)((raw[rawOff + k] + (curo[k] >> 1)) & 255);
                                    }
                                    cur[filter_bytes] = 255;
                                }
                                priorOff += output_bytes * max;
                                break;

                            case FilterType.PaethFirst:
                                for (int i = 0; i < max; i++,
                                    rawOff += filter_bytes, curOff += output_bytes)
                                {
                                    var cur = curr.Slice(curOff);
                                    var curo = curr.Slice(curOff - output_bytes);
                                    for (k = 0; k < filter_bytes; k++)
                                    {
                                        cur[k] = (byte)(
                                            (raw[rawOff + k] + CRuntime.Paeth32(curo[k], 0, 0)) & 255);
                                    }
                                    cur[filter_bytes] = 255;
                                }
                                priorOff += output_bytes * max;
                                break;
                        }

                        if (depth == 16)
                        {
                            var cur = curr.Slice(filter_bytes + 1);
                            for (int i = 0; i < cur.Length; i += output_bytes)
                                cur[i] = 255;
                        }
                    }
                }



                // post
                if (depth == 16)
                {
                    // bigendian to littleendian

                    ushort* cur16 = (ushort*)a._out_;
                    ushort* target = cur16;
                    for (int j = 0; j < height; j++)
                    {
                        target += width * out_comp;
                        for (; cur16 < target; cur16++)
                        {
                            *cur16 = BinaryPrimitives.ReverseEndianness(*cur16);
                        }
                    }
                }
                else if (depth < 8)
                {
                    for (int j = 0; j < height; ++j)
                    {
                        var curr = new Span<byte>(a._out_ + stride * j, stride);
                        int curOff = 0;

                        byte* _in_ = a._out_ + stride * j + width * out_comp - img_width_bytes;

                        byte scale = (byte)((color == 0) ? DepthScaleTable[depth] : 1);

                        // TODO: make loops forward loops

                        if (depth == 4)
                        {
                            for (k = width * raw_comp; k >= 2; k -= 2, ++_in_)
                            {
                                curr[curOff++] = (byte)(scale * (*_in_ >> 4));
                                curr[curOff++] = (byte)(scale * ((*_in_) & 0x0f));
                            }
                            if (k > 0)
                                curr[curOff++] = (byte)(scale * (*_in_ >> 4));
                        }
                        else if (depth == 2)
                        {
                            for (k = width * raw_comp; k >= 4; k -= 4, ++_in_)
                            {
                                curr[curOff++] = (byte)(scale * (*_in_ >> 6));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 4) & 0x03));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 2) & 0x03));
                                curr[curOff++] = (byte)(scale * ((*_in_) & 0x03));
                            }
                            if (k > 0)
                                curr[curOff++] = (byte)(scale * (*_in_ >> 6));
                            if (k > 1)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 4) & 0x03));
                            if (k > 2)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 2) & 0x03));
                        }
                        else if (depth == 1)
                        {
                            for (k = width * raw_comp; k >= 8; k -= 8, ++_in_)
                            {
                                curr[curOff++] = (byte)(scale * (*_in_ >> 7));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 6) & 0x01));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 5) & 0x01));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 4) & 0x01));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 3) & 0x01));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 2) & 0x01));
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 1) & 0x01));
                                curr[curOff++] = (byte)(scale * ((*_in_) & 0x01));
                            }
                            if (k > 0)
                                curr[curOff++] = (byte)(scale * (*_in_ >> 7));
                            if (k > 1)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 6) & 0x01));
                            if (k > 2)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 5) & 0x01));
                            if (k > 3)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 4) & 0x01));
                            if (k > 4)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 3) & 0x01));
                            if (k > 5)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 2) & 0x01));
                            if (k > 6)
                                curr[curOff++] = (byte)(scale * ((*_in_ >> 1) & 0x01));
                        }

                        if (raw_comp != out_comp)
                        {
                            int q = 0;
                            if (raw_comp == 1)
                            {
                                for (q = width - 1; q >= 0; --q)
                                {
                                    curr[q * 2 + 1] = 255;
                                    curr[q * 2 + 0] = curr[q];
                                }
                            }
                            else
                            {
                                for (q = width - 1; q >= 0; --q)
                                {
                                    curr[q * 4 + 3] = 255;
                                    curr[q * 4 + 2] = curr[q * 3 + 2];
                                    curr[q * 4 + 1] = curr[q * 3 + 1];
                                    curr[q * 4 + 0] = curr[q * 3 + 0];
                                }
                            }
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

            public class stbi__png
            {
                public byte* _out_;
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
        }
    }
}
