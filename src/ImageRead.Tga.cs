using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static unsafe class Tga
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
                public int palette_bits;
                public int x_origin;
                public int y_origin;
                public int bits_per_pixel;
                public int inverted;
            }

            public static int GetComponentCount(int bits_per_pixel, bool is_grey, out int bitsPerComp)
            {
                bitsPerComp = 8;

                switch (bits_per_pixel)
                {
                    case 8:
                        return 1;

                    case 15:
                    case 16:
                        if ((bits_per_pixel == 16) && is_grey)
                            return 2;

                        bitsPerComp = 16;
                        return 3;

                    case 24:
                    case 32:
                        return bits_per_pixel / 8;

                    default:
                        return 0;
                }
            }

            public static bool Test(ReadOnlySpan<byte> header)
            {
                return TestCore(header, out _);
            }

            public static bool Info(BinReader s, out ReadState state)
            {
                state = new ReadState();
                return ParseHeader(s, state, out _);
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

            public static bool ParseHeader(BinReader s, ReadState ri, out TgaInfo info)
            {
                if (s == null)
                    throw new ArgumentNullException(nameof(s));
                if (ri == null)
                    throw new ArgumentNullException(nameof(ri));

                Span<byte> tmp = stackalloc byte[HeaderSize];
                if (!s.TryReadBytes(tmp))
                {
                    info = default;
                    return false;
                }

                if (!TestCore(tmp, out info))
                    throw new StbImageReadException(ErrorCode.UnknownFormat);

                info.palette_start = s.ReadInt16LE();
                info.palette_len = s.ReadInt16LE();

                info.palette_bits = s.ReadByte();
                if (info.palette_bits != 8 &&
                    info.palette_bits != 15 &&
                    info.palette_bits != 16 &&
                    info.palette_bits != 24 &&
                    info.palette_bits != 32)
                    return false;

                info.x_origin = s.ReadInt16LE();
                info.y_origin = s.ReadInt16LE();

                s.Skip(9);

                ri.Width = s.ReadInt16LE();
                if (ri.Width < 1)
                    return false;

                ri.Height = s.ReadInt16LE();
                if (ri.Height < 1)
                    return false;

                info.bits_per_pixel = s.ReadByte();

                info.inverted = s.ReadByte();
                info.inverted = 1 - ((info.inverted >> 5) & 1);

                // use the number of bits from the palette if paletted
                if (info.palette_bits != 0)
                {
                    if (info.bits_per_pixel != 8 &&
                        info.bits_per_pixel != 16)
                        return false;

                    ri.Components = GetComponentCount(info.palette_bits, false, out ri.Depth);
                }
                else
                {
                    ri.Components = GetComponentCount(info.bits_per_pixel, info.image_type == 3, out ri.Depth);
                }

                if (ri.Components == 0)
                    throw new StbImageReadException(ErrorCode.BadComponentCount);

                ri.OutComponents = ri.Components;
                ri.OutDepth = ri.Depth;
                return true;
            }

            public static void ReadRgb16(BinReader reader, Span<byte> destination)
            {
                Debug.Assert(reader != null);

                const ushort fiveBitMask = 31;
                ushort px = (ushort)reader.ReadInt16LE();
                int r = (px >> 10) & fiveBitMask;
                int g = (px >> 5) & fiveBitMask;
                int b = px & fiveBitMask;
                destination[0] = (byte)(r * 255 / 31);
                destination[1] = (byte)(g * 255 / 31);
                destination[2] = (byte)(b * 255 / 31);
            }

            public static IMemoryHolder Load(BinReader s, ReadState ri, ArrayPool<byte>? pool = null)
            {
                var info = new TgaInfo();
                if (!ParseHeader(s, ri, ref info))
                    return null;

                byte[] _out_ = new byte[ri.Width * ri.Height * ri.OutComponents];
                
                Span<byte> raw_data = stackalloc byte[4];
                raw_data.Clear();

                s.Skip(info.offset);

                int pixelCount = ri.Width * ri.Height;
                byte tmp;
                int i;
                int j;
                if (info.colormap_type == 0 && !info.is_RLE && ri.OutDepth == 8)
                {
                    for (i = 0; i < ri.Height; ++i)
                    {
                        int row = info.inverted != 0 ? ri.Height - i - 1 : i;
                        byte* tga_row = _out_ + row * ri.Width * ri.OutComponents;
                        s.ReadBytes(new Span<byte>(tga_row, ri.Width * ri.OutComponents));
                    }
                }
                else
                {
                    byte* tga_palette = null;
                    if (info.colormap_type != 0)
                    {
                        s.Skip(info.palette_start);

                        tga_palette = (byte*)MAllocMad2(info.palette_len, ri.OutComponents, 0);
                        if (tga_palette == null)
                        {
                            CRuntime.Free(_out_);
                            s.Error(ErrorCode.OutOfMemory);
                            return null;
                        }

                        if (ri.Depth == 16)
                        {
                            byte* pal_entry = tga_palette;
                            for (i = 0; i < info.palette_len; ++i)
                            {
                                ReadRgb16(s, new Span<byte>(pal_entry, ri.OutComponents));
                                pal_entry += ri.OutComponents;
                            }
                        }
                        else
                        {
                            // TODO: something is missing here...
                            throw new NotImplementedException();

                            s.ReadBytes(new Span<byte>(tga_palette, info.palette_len * ri.OutComponents));

                            CRuntime.Free(_out_);
                            CRuntime.Free(tga_palette);

                            throw new StbImageReadException(ErrorCode.BadPalette);
                        }
                    }

                    int RLE_count = 0;
                    int RLE_repeating = 0;
                    bool read_next_pixel = true;

                    for (i = 0; i < pixelCount; ++i)
                    {
                        if (info.is_RLE)
                        {
                            if (RLE_count == 0)
                            {
                                int RLE_cmd = s.ReadByte();
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
                                int pal_idx = (info.bits_per_pixel == 8) ? s.ReadByte() : s.ReadInt16LE();
                                if (pal_idx >= info.palette_len)
                                    pal_idx = 0;

                                pal_idx *= ri.OutComponents;
                                for (j = 0; j < ri.OutComponents; ++j)
                                    raw_data[j] = tga_palette[pal_idx + j];
                            }
                            else if (ri.OutDepth == 16)
                                ReadRgb16(s, raw_data);
                            else
                                for (j = 0; j < ri.OutComponents; ++j)
                                    raw_data[j] = s.ReadByte();

                            read_next_pixel = false;
                        }

                        for (j = 0; j < ri.OutComponents; ++j)
                            _out_[i * ri.OutComponents + j] = raw_data[j];

                        RLE_count--;
                    }

                    if (info.inverted != 0)
                    {
                        for (j = 0; (j * 2) < ri.Height; ++j)
                        {
                            int index1 = j * ri.Width * ri.OutComponents;
                            int index2 = (ri.Height - 1 - j) * ri.Width * ri.OutComponents;

                            for (i = ri.Width * ri.OutComponents; i > 0; --i)
                            {
                                tmp = _out_[index1];
                                _out_[index1] = _out_[index2];
                                _out_[index2] = tmp;
                                index1++;
                                index2++;
                            }
                        }
                    }

                    if (tga_palette != null)
                        CRuntime.Free(tga_palette);
                }

                // swap to RGB - if the source data was RGB16, it already is in the right order
                if (ri.OutComponents >= 3 && ri.OutDepth == 8)
                {
                    byte* tga_pixel = _out_;
                    for (i = 0; i < pixelCount; ++i)
                    {
                        tmp = tga_pixel[0];
                        tga_pixel[0] = tga_pixel[2];
                        tga_pixel[2] = tmp;
                        tga_pixel += ri.OutComponents;
                    }
                }

                IMemoryHolder result = new HGlobalMemoryHolder(
                    _out_, (ri.OutComponents * pixelCount * ri.OutDepth + 7) / 8);

                var errorCode = ConvertFormat(result, ref ri, out var convertedResult);
                if (errorCode != ErrorCode.Ok)
                    return null;
                return convertedResult;
            }
        }
    }
}
