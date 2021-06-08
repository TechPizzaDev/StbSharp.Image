using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace StbSharp.ImageRead
{
    [SkipLocalsInit]
    public static class Tga
    {
        public const int HeaderSize = 3;

        public struct TgaInfo
        {
            public int offset;
            public int colormap_type;
            public int image_type;
            public bool is_RLE;
            public int palette_start;
            public int palette_len;
            public int palette_bpp;
            public int x_origin;
            public int y_origin;
            public int bits_per_pixel;
            public int inverted;
        }

        public static int GetComponentCount(int bitsPerPixel, bool isGray, out int bitsPerComp)
        {
            bitsPerComp = 8;

            switch (bitsPerPixel)
            {
                case 8:
                    return 1;

                case 15:
                case 16:
                    if ((bitsPerPixel == 16) && isGray)
                        return 2;

                    bitsPerComp = 16;
                    return 3;

                case 24:
                case 32:
                    return bitsPerPixel / 8;

                default:
                    return 0;
            }
        }

        public static bool Test(ReadOnlySpan<byte> header)
        {
            return TestCore(header, out _);
        }

        public static TgaInfo Info(ImageBinReader reader, out ReadState state)
        {
            state = new ReadState();
            var header = ParseHeader(reader, state);
            return header;
        }

        public static bool TestCore(ReadOnlySpan<byte> header, out TgaInfo info)
        {
            info = default;

            if (header.Length < HeaderSize)
                return false;

            info.offset = header[0];

            info.colormap_type = header[1];
            if (info.colormap_type > 1)
                return false;

            info.image_type = header[2];
            if (info.image_type >= 8)
            {
                info.image_type -= 8;
                info.is_RLE = true;
            }

            if (info.colormap_type == 1)
            {
                if (info.image_type != 1 &&
                    info.image_type != 9)
                    return false;
            }
            else
            {
                if (info.image_type != 2 &&
                    info.image_type != 3)
                    return false;
            }

            return true;
        }

        public static TgaInfo ParseHeader(ImageBinReader reader, ReadState state)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            Span<byte> tmp = stackalloc byte[HeaderSize];
            if (!reader.TryReadBytes(tmp))
                throw new EndOfStreamException();

            bool test = TestCore(tmp, out var info);

            if (info.colormap_type == 1)
            {
                if (info.image_type != 1 &&
                    info.image_type != 9)
                    throw new StbImageReadException(ErrorCode.BadImageType);

                info.palette_start = reader.ReadInt16LE();
                info.palette_len = reader.ReadInt16LE();
                info.palette_bpp = reader.ReadByte();

                if (info.palette_bpp != 8 &&
                    info.palette_bpp != 15 &&
                    info.palette_bpp != 16 &&
                    info.palette_bpp != 24 &&
                    info.palette_bpp != 32)
                    throw new StbImageReadException(ErrorCode.BadPalette);
            }
            else
            {
                if (info.image_type != 2 &&
                    info.image_type != 3 &&
                    info.image_type != 10 &&
                    info.image_type != 11)
                    throw new StbImageReadException(ErrorCode.BadImageType);

                reader.Skip(5);
                // 16bit: Color Map Origin
                // 16bit: Color Map Length
                // 8bit:  Color Map Entry Size
            }

            Debug.Assert(test); // Prior checks should throw if test was unsucessful.

            info.x_origin = reader.ReadInt16LE();
            info.y_origin = reader.ReadInt16LE();

            state.Width = reader.ReadUInt16LE();
            if (state.Width < 1)
                throw new StbImageReadException(ErrorCode.ZeroWidth);

            state.Height = reader.ReadUInt16LE();
            if (state.Height < 1)
                throw new StbImageReadException(ErrorCode.ZeroHeight);

            info.bits_per_pixel = reader.ReadByte();

            info.inverted = reader.ReadByte();
            info.inverted = 1 - ((info.inverted >> 5) & 1);

            // use the number of bits from the palette if paletted
            if (info.palette_bpp != 0)
            {
                if (info.bits_per_pixel != 8 &&
                    info.bits_per_pixel != 16)
                    throw new StbImageReadException(ErrorCode.BadBitsPerPixel);

                state.Components = GetComponentCount(
                    info.palette_bpp, false, out state.Depth);
            }
            else
            {
                state.Components = GetComponentCount(
                    info.bits_per_pixel, info.image_type == 3, out state.Depth);
            }

            if (state.Components == 0)
                throw new StbImageReadException(ErrorCode.BadComponentCount);

            state.OutComponents = state.Components;
            state.OutDepth = state.Depth;

            state.Orientation = 
                ImageOrientation.LeftToRight | 
                (info.inverted != 0 ? ImageOrientation.BottomToTop : ImageOrientation.TopToBottom);

            state.StateReady();
            return info;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadRgb16(ImageBinReader reader, Span<byte> destination)
        {
            Debug.Assert(reader != null);

            const ushort fiveBitMask = 31;
            var px = (ushort)reader.ReadInt16LE();
            int b = px & fiveBitMask;
            int g = (px >> 5) & fiveBitMask;
            int r = (px >> 10) & fiveBitMask;

            destination[2] = (byte)(b * 255 / 31);
            destination[1] = (byte)(g * 255 / 31);
            destination[0] = (byte)(r * 255 / 31);
        }

        /// <summary>
        /// Swap to RGB - if the source data was RGB16, it already is in the right order.
        /// </summary>
        public static void SwapComponentOrder(Span<byte> data, int components, int depth)
        {
            if (components >= 3 && depth == 8)
            {
                for (int i = 0; i < data.Length; i += components)
                {
                    byte tmp = data[i];
                    data[i] = data[i + 2];
                    data[i + 2] = tmp;
                }
            }
        }

        public static TgaInfo Load(
            ImageBinReader reader, ReadState state, ArrayPool<byte>? bytePool = null)
        {
            var info = ParseHeader(reader, state);

            reader.Skip(info.offset);

            bytePool ??= ArrayPool<byte>.Shared;

            int lineBufferLength = (state.Width * state.Components * state.Depth + 7) / 8;
            byte[] lineBuffer = bytePool.Rent(lineBufferLength);
            try
            {
                Span<byte> line = lineBuffer.AsSpan(0, lineBufferLength);

                if (info.colormap_type == 0 && !info.is_RLE && state.Depth == 8)
                {
                    for (int y = 0; y < state.Height; ++y)
                    {
                        reader.ReadBytes(line);
                        SwapComponentOrder(line, state.Components, state.Depth);

                        int row = info.inverted != 0 ? state.Height - y - 1 : y;
                        state.OutputPixelLine(AddressingMajor.Row, row, 0, line);
                    }
                }
                else
                {
                    Memory<byte> paletteBuffer = default;
                    Span<byte> palette = default;

                    if (info.colormap_type != 0)
                    {
                        reader.Skip(info.palette_start);

                        paletteBuffer = new byte[info.palette_len * state.Components];
                        palette = paletteBuffer.Span;

                        if (state.Depth == 16)
                        {
                            for (int i = 0; i < palette.Length; i += state.Components)
                                ReadRgb16(reader, palette[i..]);

                            Debug.Assert(!palette.IsEmpty);
                        }
                        else
                        {
                            reader.ReadBytes(palette);
                        }
                    }

                    int RLE_count = 0;
                    int RLE_repeating = 0;
                    bool read_next_pixel = true;
                    Span<byte> tmp = stackalloc byte[4];

                    for (int y = 0; y < state.Height; y++)
                    {
                        Span<byte> sline = line;

                        for (int x = 0; x < state.Width; x++)
                        {
                            if (info.is_RLE)
                            {
                                if (RLE_count == 0)
                                {
                                    int RLE_cmd = reader.ReadByte();
                                    RLE_count = 1 + (RLE_cmd & 127);
                                    RLE_repeating = RLE_cmd >> 7;
                                    read_next_pixel = true;
                                }
                                else if (RLE_repeating == 0)
                                    read_next_pixel = true;
                            }
                            else
                                read_next_pixel = true;

                            if (read_next_pixel)
                            {
                                if (info.colormap_type != 0)
                                {
                                    int pal_idx = (info.bits_per_pixel == 8)
                                        ? reader.ReadByte()
                                        : reader.ReadInt16LE();

                                    if (pal_idx >= info.palette_len)
                                        pal_idx = 0;

                                    pal_idx *= state.Components;
                                    for (int j = 0; j < state.Components; j++)
                                        tmp[j] = palette[pal_idx + j];
                                }
                                else if (state.Depth == 16)
                                    ReadRgb16(reader, tmp);
                                else
                                    for (int j = 0; j < state.Components; j++)
                                        tmp[j] = reader.ReadByte();

                                read_next_pixel = false;
                            }

                            for (int j = 0; j < state.Components; j++)
                                sline[j] = tmp[j];
                            sline = sline[state.Components..];

                            RLE_count--;
                        }

                        SwapComponentOrder(line, state.Components, state.Depth);
                        int row = info.inverted != 0 ? state.Height - y - 1 : y;
                        state.OutputPixelLine(AddressingMajor.Row, row, 0, line);
                    }
                }

                return info;
            }
            finally
            {
                bytePool.Return(lineBuffer);
            }
        }
    }
}
