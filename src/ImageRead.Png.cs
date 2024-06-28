using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StbSharp.ImageRead
{
    [SkipLocalsInit]
    public static class Png
    {
        public const int HeaderSize = 8;

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

        public delegate HandleChunkResult HandleChunkDelegate(PngChunkHeader chunkHeader, ChunkStream stream);

        #region Constants

        public static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

        public static ReadOnlySpan<byte> DepthScaleTable => [0, 0xff, 0x55, 0, 0x11, 0, 0, 0, 0x01];

        public static ReadOnlySpan<byte> InterlaceOriginX => [0, 4, 0, 2, 0, 1, 0,];
        public static ReadOnlySpan<byte> InterlaceOriginY => [0, 0, 4, 0, 2, 0, 1];
        public static ReadOnlySpan<byte> InterlaceSpacingX => [8, 8, 4, 4, 2, 2, 1];
        public static ReadOnlySpan<byte> InterlaceSpacingY => [8, 8, 8, 4, 4, 2, 2];

        private static ReadOnlySpan<FilterType> FirstRowFilter =>
        [
            FilterType.None,
            FilterType.Sub,
            FilterType.None,
            FilterType.AverageFirst,
            FilterType.PaethFirst
        ];

        #endregion

        #region Structs

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

            public int PaletteComponents { get; set; }

            public readonly int Components
            {
                get
                {
                    if (PaletteComponents != 0)
                        return 1;
                    return ((ColorType & 2) != 0 ? 3 : 1) + ((ColorType & 4) != 0 ? 1 : 0);
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

                PaletteComponents = color == 3 ? 3 : 0;
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

        #endregion

        public static PngChunkHeader ReadChunkHeader(ImageBinReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            uint length = reader.ReadUInt32BE();
            uint type = reader.ReadUInt32BE();
            return new PngChunkHeader(length, (PngChunkType)type);
        }

        public static bool Test(ReadOnlySpan<byte> header)
        {
            return Signature.SequenceEqual(header);
        }

        public static void Info(ImageBinReader reader, out ReadState state)
        {
            state = new ReadState();
            Load(reader, state, ScanMode.Header);
        }

        public static void Load(
            ImageBinReader reader,
            ReadState state,
            ScanMode scan,
            ArrayPool<byte>? bytePool = null,
            ZlibHelper.DeflateDecompressorFactory? deflateDecompressorFactory = null)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(state);

            Span<byte> tmp = stackalloc byte[HeaderSize];
            if (!reader.TryReadBytes(tmp))
                throw new StbImageReadException(ErrorCode.UnknownHeader);

            if (!Test(tmp))
                throw new StbImageReadException(ErrorCode.UnknownFormat);

            Header header = default;

            Rgba32[]? paletteData = null;
            int paletteLength = 0;

            bool has_transparency = false;
            Rgb24 tc8 = default;
            Rgb48 tc16 = default;

            void InitializeReadState()
            {
                state.Components = header.Components;
                if (header.PaletteComponents != 0)
                {
                    state.OutComponents = header.PaletteComponents;
                }
                else
                {
                    if (has_transparency)
                        state.Components++;
                    state.OutComponents = state.Components;
                }

                state.Width = header.Width;
                state.Height = header.Height;
                state.Depth = header.Depth;
                state.OutDepth = header.Depth < 8 ? 8 : header.Depth;
                state.Orientation = ImageOrientation.TopLeftOrigin;

                state.StateReady();
            }

            var seenChunkTypes = new HashSet<PngChunkType>();
            Stream? decompressedStream = null;

            // TODO: add state object and make this method static?
            HandleChunkResult HandleChunk(PngChunkHeader chunk, ChunkStream stream)
            {
                if (seenChunkTypes.Count == 0)
                {
                    if (chunk.Type != PngChunkType.CgBI &&
                        chunk.Type != PngChunkType.IHDR)
                        throw new StbImageReadException(ErrorCode.IHDRNotFirst);
                }

                try
                {
                    switch (chunk.Type)
                    {
                        #region CgBI

                        case PngChunkType.CgBI:
                        {
                            return HandleChunkResult.Finish;
                        }

                        #endregion

                        #region IHDR

                        case PngChunkType.IHDR:
                        {
                            if (seenChunkTypes.Contains(PngChunkType.IHDR))
                                throw new StbImageReadException(ErrorCode.MultipleIHDR);

                            if (chunk.Length != 13)
                                throw new StbImageReadException(ErrorCode.BadIHDRLength);

                            int width = reader.ReadInt32BE();
                            if (width > (1 << 24))
                                throw new StbImageReadException(ErrorCode.TooLarge);

                            int height = reader.ReadInt32BE();
                            if (height > (1 << 24))
                                throw new StbImageReadException(ErrorCode.TooLarge);

                            if ((width == 0) || (height == 0))
                                throw new StbImageReadException(ErrorCode.EmptyImage);

                            byte depth = reader.ReadByte();
                            if (depth != 1 &&
                                depth != 2 &&
                                depth != 4 &&
                                depth != 8 &&
                                depth != 16)
                                throw new StbImageReadException(ErrorCode.UnsupportedBitDepth);

                            byte color = reader.ReadByte();
                            if (color > 6 || (color == 3) && (header.Depth == 16))
                                throw new StbImageReadException(ErrorCode.BadColorType);

                            if (color != 3 && (color & 1) != 0)
                                throw new StbImageReadException(ErrorCode.BadColorType);

                            byte compression = reader.ReadByte();
                            if (compression != 0)
                                throw new StbImageReadException(ErrorCode.BadCompressionMethod);

                            byte filter = reader.ReadByte();
                            if (filter != 0)
                                throw new StbImageReadException(ErrorCode.BadFilterMethod);

                            byte interlace = reader.ReadByte();
                            if (interlace > 1)
                                throw new StbImageReadException(ErrorCode.BadInterlaceMethod);

                            bool hasCgbi = seenChunkTypes.Contains(PngChunkType.CgBI);
                            header = new Header(
                                width, height, depth, color, compression, filter, interlace, hasCgbi);

                            if (header.PaletteComponents != 0)
                            {
                                if (((1 << 30) / width / 4) < height)
                                    throw new StbImageReadException(ErrorCode.TooLarge);
                            }
                            else
                            {
                                if (((1 << 30) / width / header.Components) < height)
                                    throw new StbImageReadException(ErrorCode.TooLarge);

                                if (scan == ScanMode.Header)
                                    return HandleChunkResult.Header;
                            }

                            return HandleChunkResult.Finish;
                        }

                        #endregion

                        #region PLTE

                        case PngChunkType.PLTE:
                        {
                            if (chunk.Length > 256 * 3)
                                throw new StbImageReadException(ErrorCode.InvalidPLTE);

                            paletteLength = chunk.Length / 3;
                            if (paletteLength * 3 != chunk.Length)
                                throw new StbImageReadException(ErrorCode.InvalidPLTE);

                            paletteData = new Rgba32[paletteLength];
                            for (int i = 0; i < paletteData.Length; i++)
                            {
                                byte r = reader.ReadByte();
                                byte g = reader.ReadByte();
                                byte b = reader.ReadByte();
                                paletteData[i] = new Rgba32(r, g, b, 255);
                            }

                            // TODO: PaletteReady()
                            return HandleChunkResult.Finish;
                        }

                        #endregion

                        #region tRNS

                        case PngChunkType.tRNS:
                        {
                            if (seenChunkTypes.Contains(PngChunkType.IDAT))
                                throw new StbImageReadException(ErrorCode.tRNSAfterIDAT);

                            if (header.PaletteComponents != 0)
                            {
                                if (paletteLength == 0)
                                    throw new StbImageReadException(ErrorCode.tRNSBeforePLTE);

                                if (chunk.Length > paletteLength)
                                    throw new StbImageReadException(ErrorCode.BadtRNSLength);

                                header.PaletteComponents = 4;
                                for (int i = 0; i < chunk.Length; i++)
                                    paletteData![i].A = reader.ReadByte();
                            }
                            else
                            {
                                if ((header.Components & 1) == 0)
                                    throw new StbImageReadException(ErrorCode.tRNSWithAlpha);

                                if (chunk.Length != header.Components * 2)
                                    throw new StbImageReadException(ErrorCode.BadtRNSLength);

                                if (header.Depth == 16)
                                {
                                    if (header.Components == 1)
                                    {
                                        tc16 = new Rgb48(reader.ReadUInt16BE(), 0, 0);
                                    }
                                    else if (header.Components == 3)
                                    {
                                        tc16 = new Rgb48(
                                            reader.ReadUInt16BE(),
                                            reader.ReadUInt16BE(),
                                            reader.ReadUInt16BE());
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
                                        var int16 = reader.ReadUInt16BE();
                                        return (byte)((int16 & 255) * DepthScaleTable[header.Depth]);
                                    }

                                    if (header.Components == 1)
                                    {
                                        tc8 = new Rgb24(ReadUInt16AsByte(), 0, 0);
                                    }
                                    else if (header.Components == 3)
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

                        case PngChunkType.IDAT:
                        {
                            if (header.PaletteComponents != 0 && paletteLength == 0)
                                throw new StbImageReadException(ErrorCode.NoPLTE);

                            if (!seenChunkTypes.Contains(PngChunkType.IDAT))
                            {
                                InitializeReadState();

                                if (scan == ScanMode.Header)
                                    return HandleChunkResult.Header;

                                decompressedStream = ZlibHelper.CreateDecompressor(
                                    stream, leaveOpen: true, deflateDecompressorFactory);
                            }
                            return HandleChunkResult.Include;
                        }

                        #endregion

                        #region IEND

                        case PngChunkType.IEND:
                        {
                            if (scan == ScanMode.Header)
                            {
                                InitializeReadState();
                                return HandleChunkResult.Header;
                            }

                            if (!seenChunkTypes.Contains(PngChunkType.IDAT))
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

            // TODO: Consider another stream class that contains this method
            //       so we don't have to use a delegate. It's not such a big deal though.
            using (var chunkStream = new ChunkStream(reader, HandleChunk))
            {
                chunkStream.Read(Span<byte>.Empty);

                if (chunkStream.LastResult == HandleChunkResult.Header)
                {
                    if (scan != ScanMode.Header)
                        throw new InvalidOperationException();
                    return;
                }
                else if (chunkStream.LastResult != HandleChunkResult.Include)
                {
                    return;
                }

                // CgBI (IPhone) streams dont have deflate header
                // and we just skip it for normal streams.
                if (!header.HasCgbi)
                {
                    for (int i = 0; i < 2; i++)
                        if (chunkStream.ReadByte() == -1)
                            throw new EndOfStreamException();
                }

                var palette = header.PaletteComponents != 0
                    ? new Palette(paletteData.AsMemory(0, paletteLength), header.PaletteComponents)
                    : (Palette?)null;

                var transparency = has_transparency
                    ? new Transparency(tc8, tc16)
                    : (Transparency?)null;

                CreateImage(state, decompressedStream!, bytePool, header, transparency, palette);
            }
        }

        public static void ProcessDefilteredRow(
            ReadState state,
            Span<byte> data,
            int width, int row, int outComp,
            int originX, int spacingX,
            in Header header,
            in Transparency? transparency,
            in Palette? palette)
        {
            DecodeDefilteredRow(data, width, outComp, header);

            PostDefilteredRow(
                state, data,
                width, row, outComp,
                originX, spacingX,
                header, transparency, palette);
        }

        public static void DecodeImage(
            ReadState state, Stream filteredData,
            Span<byte> filteredDataBuffer,
            Span<byte> previousRowBuffer,
            Span<byte> currentRowBuffer,
            int width, int height, int comp,
            int originX, int originY,
            int spacingX, int spacingY,
            in Header header,
            in Transparency? transparency,
            in Palette? palette)
        {
            ArgumentNullException.ThrowIfNull(filteredData);

            for (int y = 0; y < height; y++)
            {
                if (filteredData.ReadAtLeast(filteredDataBuffer, filteredDataBuffer.Length, false) != filteredDataBuffer.Length)
                    throw new StbImageReadException(ErrorCode.BadCompression);

                DefilterRow(
                    previousRowBuffer, filteredDataBuffer, currentRowBuffer,
                    width, y, comp, header);

                if (y > 0)
                {
                    int row = originY + (y - 1) * spacingY;
                    ProcessDefilteredRow(
                        state, previousRowBuffer,
                        width, row, comp,
                        originX, spacingX,
                        header, transparency, palette);
                }

                // Swap buffers.
                var nextRowBuffer = previousRowBuffer;
                previousRowBuffer = currentRowBuffer;
                currentRowBuffer = nextRowBuffer;
            }

            int lastRow = originY + (height - 1) * spacingY;
            ProcessDefilteredRow(
                state, previousRowBuffer,
                width, lastRow, comp,
                originX, spacingX,
                header, transparency, palette);
        }

        public static void CreateImage(
            ReadState state, Stream decompressedStream, ArrayPool<byte>? bytePool,
            in Header header, in Transparency? transparency, in Palette? palette)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(decompressedStream);
            bytePool ??= ArrayPool<byte>.Shared;

            int depth = header.Depth;
            int srcComp = header.Components;
            int bytes_per_comp = depth == 16 ? 2 : 1;
            int comp = header.Components + (transparency.HasValue ? 1 : 0);

            int width = header.Width;
            int height = header.Height;
            int row_bytes = width * comp * bytes_per_comp;
            int img_width_bytes = (width * srcComp * depth + 7) / 8;
            int raw_width_bytes = img_width_bytes + 1;

            var filteredDataBuffer = bytePool.Rent(raw_width_bytes);
            var previousRowBuffer = bytePool.Rent(row_bytes);
            var currentRowBuffer = bytePool.Rent(row_bytes);

            var filteredData = filteredDataBuffer.AsSpan(0, raw_width_bytes);
            var previousRow = previousRowBuffer.AsSpan(0, row_bytes);
            var currentRow = currentRowBuffer.AsSpan(0, row_bytes);

            try
            {
                if (header.Interlace == 0)
                {
                    DecodeImage(
                        state, decompressedStream,
                        filteredData, previousRow, currentRow,
                        width, height, comp,
                        originX: 0, originY: 0, spacingX: 1, spacingY: 1,
                        header, transparency, palette);
                }
                else if (header.Interlace == 1)
                {
                    for (int p = 0; p < 7; p++)
                    {
                        int originX = InterlaceOriginX[p];
                        int originY = InterlaceOriginY[p];
                        int spacingX = InterlaceSpacingX[p];
                        int spacingY = InterlaceSpacingY[p];

                        int interlace_width = (width - originX + spacingX - 1) / spacingX;
                        int interlace_height = (height - originY + spacingY - 1) / spacingY;

                        if (interlace_width != 0 && interlace_height != 0)
                        {
                            int interlace_row_bytes = interlace_width * comp * bytes_per_comp;
                            int interlace_img_width_bytes = (interlace_width * srcComp * depth + 7) / 8;
                            int interlace_full_width_bytes = 1 + interlace_img_width_bytes;

                            var filteredDataSlice = filteredData.Slice(0, interlace_full_width_bytes);
                            var previousRowSlice = previousRow.Slice(0, interlace_row_bytes);
                            var currentRowSlice = currentRow.Slice(0, interlace_row_bytes);
                            DecodeImage(
                                state, decompressedStream,
                                filteredDataSlice, previousRowSlice, currentRowSlice,
                                interlace_width, interlace_height, comp,
                                originX, originY, spacingX, spacingY,
                                header, transparency, palette);
                        }
                    }
                }
                else
                {
                    throw new StbImageReadException(ErrorCode.BadInterlaceMethod);
                }
            }
            finally
            {
                bytePool.Return(filteredDataBuffer);
                bytePool.Return(previousRowBuffer);
                bytePool.Return(currentRowBuffer);
            }
        }

        public static void DefilterRow(
            ReadOnlySpan<byte> prevFiltRow,
            ReadOnlySpan<byte> currentFilteredRow,
            Span<byte> destination,
            int width,
            int y,
            int dstComp,
            in Header header)
        {
            int rawOff = 0;
            int curOff = 0;
            int priorOff = 0;

            var filter = (FilterType)currentFilteredRow[rawOff++];
            if ((int)filter > 4)
                throw new StbImageReadException(ErrorCode.InvalidFilter);

            if (y == 0)
                filter = FirstRowFilter[(int)filter];

            int w = width;
            int depth = header.Depth;
            int srcComp = header.Components;
            int byte_depth = depth == 16 ? 2 : 1;
            int dst_bpc = dstComp * byte_depth;
            int filt_bpc = srcComp * byte_depth;
            int img_width_bytes = ((width * srcComp * depth) + 7) / 8;

            if (depth < 8)
            {
                int o = width * dstComp - img_width_bytes;
                curOff += o;
                priorOff += o;
                filt_bpc = 1;
                w = img_width_bytes;
            }

            destination.Clear();

            for (int i = 0; i < filt_bpc; i++)
            {
                var cur = destination[curOff..];
                var rawslice = currentFilteredRow[rawOff..];

                switch (filter)
                {
                    case FilterType.None:
                    case FilterType.Sub:
                    case FilterType.AverageFirst:
                    case FilterType.PaethFirst:
                        cur[i] = rawslice[i];
                        break;

                    case FilterType.Up:
                        cur[i] = (byte)(rawslice[i] + prevFiltRow[priorOff + i]);
                        break;

                    case FilterType.Average:
                        cur[i] = (byte)(rawslice[i] + (prevFiltRow[priorOff + i] / 2));
                        break;

                    case FilterType.Paeth:
                        cur[i] = (byte)
                            (rawslice[i] + MathHelper.Paeth(0, prevFiltRow[priorOff + i], 0));
                        break;
                }
            }

            if (depth == 8)
            {
                if (srcComp != dstComp)
                    destination[curOff + srcComp] = 255;
                rawOff += srcComp;
                curOff += dstComp;
                priorOff += dstComp;
            }
            else if (depth == 16)
            {
                if (srcComp != dstComp)
                {
                    destination[curOff + filt_bpc] = 255;
                    destination[curOff + filt_bpc + 1] = 255;
                }
                rawOff += filt_bpc;
                curOff += dst_bpc;
                priorOff += dst_bpc;
            }
            else
            {
                rawOff += 1;
                curOff += 1;
                priorOff += 1;
            }

            // TODO: Vectorize this;
            // Can be tricky/impossible as it needs to process values dependent on previous value

            if ((depth < 8) || (srcComp == dstComp))
            {
                int nk = (w - 1) * filt_bpc;
                var filt = currentFilteredRow.Slice(rawOff, nk);
                var dst = destination.Slice(curOff, filt.Length);
                var ndst = destination.Slice(curOff - filt_bpc, filt.Length);

                int k;
                switch (filter)
                {
                    case FilterType.None:
                        filt.CopyTo(dst);
                        break;

                    case FilterType.Sub:
                        for (k = 0; k < filt.Length; k++)
                            dst[k] = (byte)(filt[k] + ndst[k]);
                        break;

                    case FilterType.Up:
                    {
                        var prior = prevFiltRow.Slice(priorOff, filt.Length);
                        k = 0;
                        if (Vector.IsHardwareAccelerated)
                        {
                            while (filt.Length - k >= Vector<byte>.Count)
                            {
                                var v_filtered = new Vector<byte>(filt[k..]);
                                var v_prior = new Vector<byte>(prior[k..]);
                                Vector.Add(v_filtered, v_prior).CopyTo(dst[k..]);
                                k += Vector<byte>.Count;
                            }
                        }
                        for (; k < filt.Length; k++)
                        {
                            dst[k] = (byte)(filt[k] + prior[k]);
                        }
                        break;
                    }

                    case FilterType.Average:
                    {
                        var prior = prevFiltRow.Slice(priorOff, filt.Length);
                        for (k = 0; k < filt.Length; k++)
                            dst[k] = (byte)(filt[k] + ((prior[k] + ndst[k]) / 2));
                        break;
                    }

                    case FilterType.Paeth:
                        var bSpan = prevFiltRow.Slice(priorOff, filt.Length);
                        var cSpan = prevFiltRow.Slice(priorOff - filt_bpc, filt.Length);

                        for (k = 0; k < filt.Length; k++)
                        {
                            dst[k] = (byte)(filt[k] + MathHelper.Paeth(ndst[k], bSpan[k], cSpan[k]));
                        }
                        break;

                    case FilterType.AverageFirst:
                        for (k = 0; k < filt.Length; k++)
                        {
                            dst[k] = (byte)(filt[k] + (ndst[k] / 2));
                        }
                        break;

                    case FilterType.PaethFirst:
                        for (k = 0; k < filt.Length; k++)
                        {
                            dst[k] = (byte)(filt[k] + MathHelper.Paeth(ndst[k], 0, 0));
                        }
                        break;
                }
            }
            else
            {
                int max = width - 1;
                int k;
                switch (filter)
                {
                    case FilterType.None:
                    {
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc)
                        {
                            var raws = currentFilteredRow.Slice(rawOff, filt_bpc);
                            var cur = destination[curOff..];
                            raws.CopyTo(cur);
                            cur[filt_bpc] = 255;
                        }
                        break;
                    }

                    case FilterType.Sub:
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc)
                        {
                            var cur = destination[curOff..];
                            var curo = destination[(curOff - dst_bpc)..];
                            for (k = 0; k < filt_bpc; k++)
                            {
                                cur[k] = (byte)(currentFilteredRow[rawOff + k] + curo[k]);
                            }
                            cur[filt_bpc] = 255;
                        }
                        break;

                    case FilterType.Up:
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc, priorOff += dst_bpc)
                        {
                            var cur = destination[curOff..];
                            var prior = prevFiltRow[priorOff..];
                            for (k = 0; k < filt_bpc; k++)
                            {
                                cur[k] = (byte)(currentFilteredRow[rawOff + k] + prior[k]);
                            }
                            cur[filt_bpc] = 255;
                        }
                        break;

                    case FilterType.Average:
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc, priorOff += dst_bpc)
                        {
                            var cur = destination[curOff..];
                            var curo = destination[(curOff - dst_bpc)..];
                            var prior = prevFiltRow[priorOff..];
                            for (k = 0; k < filt_bpc; k++)
                            {
                                cur[k] = (byte)
                                    (currentFilteredRow[rawOff + k] + ((prior[k] + curo[k]) >> 1));
                            }
                            cur[filt_bpc] = 255;
                        }
                        break;

                    case FilterType.Paeth:
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc, priorOff += dst_bpc)
                        {
                            var cur = destination[curOff..];
                            var curo = destination[(curOff - dst_bpc)..];
                            var prior = prevFiltRow[priorOff..];
                            var prioro = prevFiltRow[(priorOff - dst_bpc)..];
                            for (k = 0; k < filt_bpc; k++)
                            {
                                cur[k] = (byte)
                                    (currentFilteredRow[rawOff + k] + MathHelper.Paeth(
                                        curo[k], prior[k], prioro[k]));
                            }
                            cur[filt_bpc] = 255;
                        }
                        break;

                    case FilterType.AverageFirst:
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc)
                        {
                            var cur = destination[curOff..];
                            var curo = destination[(curOff - dst_bpc)..];
                            for (k = 0; k < filt_bpc; k++)
                            {
                                cur[k] = (byte)(currentFilteredRow[rawOff + k] + (curo[k] / 2));
                            }
                            cur[filt_bpc] = 255;
                        }
                        break;

                    case FilterType.PaethFirst:
                        for (int i = 0; i < max; i++,
                            rawOff += filt_bpc, curOff += dst_bpc)
                        {
                            var cur = destination[curOff..];
                            var curo = destination[(curOff - dst_bpc)..];
                            for (k = 0; k < filt_bpc; k++)
                            {
                                cur[k] = (byte)
                                    (currentFilteredRow[rawOff + k] + MathHelper.Paeth(curo[k], 0, 0));
                            }
                            cur[filt_bpc] = 255;
                        }
                        break;
                }

                if (depth == 16)
                {
                    var cur = destination[(filt_bpc + 1)..];
                    for (int i = 0; i < cur.Length; i += dst_bpc)
                        cur[i] = 255;
                }
            }
        }

        public static void DecodeDefilteredRow(Span<byte> row, int width, int dstComp, in Header header)
        {
            int depth = header.Depth;
            int srcComp = header.Components;

            if (depth == 16)
            {
                var row16 = MemoryMarshal.Cast<byte, ushort>(row);

                for (int i = 0; i < row16.Length; i++)
                    row16[i] = BinaryPrimitives.ReverseEndianness(row16[i]);
            }
            else if (depth < 8)
            {
                int img_width_bytes = (width * srcComp * depth + 7) / 8;
                var src = row[(width * dstComp - img_width_bytes)..];

                // TODO: make loops forward loops

                byte scale = (byte)(header.ColorType == 0 ? DepthScaleTable[depth] : 1);

                int rowOff = 0;
                int srcOff = 0;
                int k;
                if (depth == 4)
                {
                    for (k = width * srcComp; k >= 2; k -= 2, srcOff++)
                    {
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x0f));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 0) & 0x0f));
                    }

                    if (k > 0)
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x0f));
                }
                else if (depth == 2)
                {
                    for (k = width * srcComp; k >= 4; k -= 4, srcOff++)
                    {
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 6) & 0x03));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x03));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 2) & 0x03));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 0) & 0x03));
                    }

                    for (int i = 0; i < k; i++)
                    {
                        row[rowOff++] = (byte)(scale * (src[srcOff] >> (6 - i * 2) & 0x03));
                    }
                }
                else if (depth == 1)
                {
                    for (k = width * srcComp; k >= 8; k -= 8, srcOff++)
                    {
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 7) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 6) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 5) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 4) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 3) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 2) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 1) & 0x01));
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> 0) & 0x01));
                    }

                    for (int i = 0; i < k; i++)
                    {
                        row[rowOff++] = (byte)(scale * ((src[srcOff] >> (7 - i)) & 0x01));
                    }
                }

                if (srcComp != dstComp)
                {
                    if (srcComp == 1)
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

        public static void PostDefilteredRow(
            ReadState state,
            Span<byte> data,
            int width, int row, int outComp,
            int originX, int spacingX,
            in Header header,
            in Transparency? transparency,
            in Palette? palette)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (transparency.HasValue)
            {
                if (header.Depth == 16)
                    ComputeTransparency16(data, transparency.Value.Tc16, outComp);
                else
                    ComputeTransparency8(data, transparency.Value.Tc8, outComp);
            }

            if (header.HasCgbi && state.DeIphoneFlag && outComp > 2)
            {
                DeIphone(data, outComp, state.UnpremultiplyOnLoad);
            }

            if (palette.HasValue)
            {
                var paletteData = palette.Value.Data.Span;
                int comp = palette.Value.Components;

                Span<byte> buffer = stackalloc byte[4096];
                int bufferCapacity = buffer.Length / comp;

                int offset = 0;
                while (offset < width)
                {
                    int count = Math.Min(width - offset, bufferCapacity);
                    var rowSlice = data.Slice(offset, count);
                    var bufferSlice = buffer.Slice(0, count * comp);

                    ExpandPalette(rowSlice, bufferSlice, comp, paletteData);

                    int start = originX + offset;
                    state.OutputPixelLine(AddressingMajor.Row, row, start, spacingX, bufferSlice);

                    offset += count;
                }
            }
            else
            {
                state.OutputPixelLine(AddressingMajor.Row, row, originX, spacingX, data);
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

        [SkipLocalsInit]
        public class ChunkStream : Stream
        {
            private HandleChunkDelegate _handleChunk;
            private int _chunkLeftToRead;
            private int _validationCrc;

            public ImageBinReader Reader { get; }
            public PngChunkHeader LastChunkHeader { get; private set; }
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

            public ChunkStream(ImageBinReader reader, HandleChunkDelegate handleChunk)
            {
                Reader = reader ?? throw new ArgumentNullException(nameof(reader));
                _handleChunk = handleChunk ?? throw new ArgumentNullException(nameof(handleChunk));

                _chunkLeftToRead = -1;
            }

            private void FinishChunk()
            {
                // TODO: validate crc (and based on some config?)

                // take LastChunkHeader.IsCritical into consideration

                // maybe some enum?: CrcValidation { Ignore, Critical, Full } ???

                int crc = Reader.ReadInt32BE();
            }

            public override int Read(Span<byte> buffer)
            {
                if (LastResult == HandleChunkResult.Header)
                    return 0;

                Start:
                if (_chunkLeftToRead >= 0)
                {
                    int toRead = Math.Min(buffer.Length, _chunkLeftToRead);
                    Reader.ReadBytes(buffer.Slice(0, toRead));

                    _chunkLeftToRead -= toRead;
                    if (_chunkLeftToRead == 0)
                    {
                        FinishChunk();
                        _chunkLeftToRead = -1;
                    }
                    return toRead;
                }

                Next:
                LastChunkHeader = ReadChunkHeader(Reader);
                long chunkDataEnd = Reader.Position + LastChunkHeader.Length;

                LastResult = _handleChunk.Invoke(LastChunkHeader, this);
                switch (LastResult)
                {
                    case HandleChunkResult.Header:
                        FinishChunk();
                        return 0;

                    case HandleChunkResult.Finish:
                        long chunkDataLeft = chunkDataEnd - Reader.Position;
                        if (chunkDataLeft < 0)
                            throw new InvalidOperationException(
                                "Handler read more data than the chunk contained.");

                        Reader.Skip((int)chunkDataLeft);
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

            public override int ReadByte()
            {
                Span<byte> tmp = stackalloc byte[1];
                if (Read(tmp) == 0)
                    return -1;
                return tmp[0];
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
