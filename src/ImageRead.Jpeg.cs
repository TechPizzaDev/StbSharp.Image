using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static class Jpeg
        {
            #region Constants

            public const int HeaderSize = 2;

            private static uint[] BMask =
            {
                0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535
            };

            private static int[] JBias =
            {
                0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047, -4095, -8191, -16383, -32767
            };

            private static ReadOnlySpan<byte> DeZigZag => new byte[]
            {
                0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40,
                48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29,
                22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54,
                47, 55, 62, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63
            };

            #endregion

            public struct Idct
            {
                public int t0, t1, t2, t3, p1, p2, p3, p4, p5, x0, x1, x2, x3;
            }

            public delegate void IdctBlockKernel(
                Span<byte> output, int out_stride, Span<short> data);

            public delegate void YCbCrToRgbKernel(
                Span<byte> output, Span<byte> y, Span<byte> pcb, Span<byte> pcr);

            public delegate Memory<byte> ResamplerMethod(
                Memory<byte> a, Memory<byte> b, Memory<byte> c, int d, int e);

            [StructLayout(LayoutKind.Sequential)]
            public struct ImageComponent
            {
                public int id;
                public int h, v;
                public int tq;
                public int hd, ha;
                public int dc_pred;

                public int x, y, w2, h2;
                public byte[] raw_data;
                public Memory<byte> data;

                public byte[] raw_linebuf;
                public Memory<byte> linebuf;

                public short[] raw_coeff;
                public Memory<short> coeff; // progressive only
                public int coeff_w, coeff_h; // number of 8x8 coefficient blocks
            }

            public class Huffman
            {
                public const int FastLength = 512;
                public const int CodeLength = 256;
                public const int ValuesLength = 256;
                public const int SizeLength = 257;
                public const int MaxcodeLength = 18;
                public const int DeltaLength = 17;

                // TODO: arraypool
                private byte[] _buffer;

                public Memory<byte> MFast { get; }
                public Memory<byte> MCode { get; }
                public Memory<byte> MValues { get; }
                public Memory<byte> MSize { get; }
                public Memory<byte> MMaxcode { get; }
                public Memory<byte> MDelta { get; }

                public Span<byte> Fast => MFast.Span;
                public Span<ushort> Code => MemoryMarshal.Cast<byte, ushort>(MCode.Span);
                public Span<byte> Values => MValues.Span;
                public Span<byte> Size => MSize.Span;
                public Span<uint> Maxcode => MemoryMarshal.Cast<byte, uint>(MMaxcode.Span);
                public Span<int> Delta => MemoryMarshal.Cast<byte, int>(MDelta.Span);

                public Huffman()
                {
                    _buffer = new byte[
                        FastLength * sizeof(byte) +
                        CodeLength * sizeof(ushort) +
                        ValuesLength * sizeof(byte) +
                        SizeLength * sizeof(byte) +
                        MaxcodeLength * sizeof(uint) +
                        DeltaLength * sizeof(int)];

                    var m = _buffer.AsMemory();
                    int o = 0;

                    MFast = m.Slice(o, FastLength * sizeof(byte));
                    o += MFast.Length;

                    MCode = m.Slice(o, CodeLength * sizeof(ushort));
                    o += MCode.Length;

                    MValues = m.Slice(o, ValuesLength * sizeof(byte));
                    o += MValues.Length;

                    MSize = m.Slice(o, SizeLength * sizeof(byte));
                    o += MSize.Length;

                    MMaxcode = m.Slice(o, MaxcodeLength * sizeof(uint));
                    o += MMaxcode.Length;

                    MDelta = m.Slice(o, DeltaLength * sizeof(int));
                }
            }

            public class JpegState
            {
                public BinReader s;
                public readonly Huffman[] huff_dc = new Huffman[4];
                public readonly Huffman[] huff_ac = new Huffman[4];
                public readonly ushort[][] dequant;

                public readonly short[][] fast_ac;
                public bool[] bitbuffer = new bool[64];

                // sizes for components, interleaved MCUs
                public int img_h_max, img_v_max;
                public int img_mcu_x, img_mcu_y;
                public int img_mcu_w, img_mcu_h;

                // definition of jpeg image component
                public ImageComponent[] components = new ImageComponent[4];

                public uint code_buffer; // jpeg entropy-coded buffer
                public int code_bits; // number of valid bits
                public byte marker; // marker seen while filling entropy buffer
                public bool nomore; // flag if we saw a marker so must stop

                public bool progressive;
                public int spec_start;
                public int spec_end;
                public int succ_high;
                public int succ_low;
                public int eob_run;
                public int jfif;
                public int app14_color_transform; // Adobe APP14 tag
                public int rgb;

                public int scan_n;
                public int[] order = new int[4];
                public int restart_interval, todo;

                public IdctBlockKernel idct_block_kernel;
                public YCbCrToRgbKernel YCbCr_to_RGB_kernel;
                public ResamplerMethod resample_row_hv_2_kernel;

                public int decode_n;
                public bool is_rgb;
                public ReadState ri;

                public JpegState(BinReader reader, ReadState readState)
                {
                    s = reader;
                    ri = readState;

                    idct_block_kernel = IdctBlock;
                    YCbCr_to_RGB_kernel = YCbCrToRGB;
                    resample_row_hv_2_kernel = ResampleRowHV2;

                    for (var i = 0; i < 4; ++i)
                    {
                        huff_ac[i] = new Huffman();
                        huff_dc[i] = new Huffman();
                    }

                    for (var i = 0; i < components.Length; ++i)
                        components[i] = new ImageComponent();

                    fast_ac = new short[4][];
                    for (var i = 0; i < fast_ac.Length; ++i)
                        fast_ac[i] = new short[Huffman.FastLength];

                    dequant = new ushort[4][];
                    for (var i = 0; i < dequant.Length; ++i)
                        dequant[i] = new ushort[64];
                }
            };

            public class ResampleData
            {
                public ResamplerMethod Resample;
                public Memory<byte> line0;
                public Memory<byte> line1;
                public int hs;
                public int vs;
                public int w_lores;
                public int ystep;
                public int ypos;
            }

            public static void BuildHuffman(Huffman h, Span<int> count)
            {
                int i;
                int j;
                int k = 0;
                for (i = 0; i < 16; ++i)
                {
                    for (j = 0; j < count[i]; ++j)
                        h.Size[k++] = (byte)(i + 1);
                }

                int code = 0;
                h.Size[k] = 0;
                k = 0;
                for (j = 1; j <= 16; ++j)
                {
                    h.Delta[j] = k - code;
                    if (h.Size[k] == j)
                    {
                        while (h.Size[k] == j)
                            h.Code[k++] = (ushort)code++;

                        if ((code - 1) >= (1 << j))
                            throw new StbImageReadException(ErrorCode.BadCodeLengths);
                    }

                    h.Maxcode[j] = (uint)(code << (16 - j));
                    code <<= 1;
                }

                h.Maxcode[j] = 0xffffffff;

                for (i = 0; i < Huffman.FastLength; ++i)
                    h.Fast[i] = 255;

                for (i = 0; i < k; ++i)
                {
                    int s = h.Size[i];
                    if (s <= 9)
                    {
                        int c = h.Code[i] << (9 - s);
                        int m = 1 << (9 - s);
                        for (j = 0; j < m; ++j)
                            h.Fast[c + j] = (byte)i;
                    }
                }
            }

            public static void BuildFastAc(short[] fast_ac, Huffman h)
            {
                int i;
                for (i = 0; i < (1 << 9); ++i)
                {
                    byte fast = h.Fast[i];
                    fast_ac[i] = 0;
                    if (fast < 255)
                    {
                        int rs = h.Values[fast];
                        int run = (rs >> 4) & 15;
                        int magbits = rs & 15;
                        int len = h.Size[fast];
                        if ((magbits != 0) && (len + magbits <= 9))
                        {
                            int k = ((i << len) & ((1 << 9) - 1)) >> (9 - magbits);
                            int m = 1 << (magbits - 1);
                            if (k < m)
                                k += (int)((~0U << magbits) + 1);
                            if ((k >= (-128)) && (k <= 127))
                                fast_ac[i] = (short)((k << 8) + (run << 4) + len + magbits);
                        }
                    }
                }
            }

            public static async ValueTask GrowBufferUnsafe(JpegState j)
            {
                if (j.nomore)
                    return;

                do
                {
                    int b = await j.s.ReadByte();
                    if (b == 0xff)
                    {
                        int c = await j.s.ReadByte();
                        while (c == 0xff)
                            c = await j.s.ReadByte();

                        if (c != 0)
                        {
                            j.marker = (byte)c;
                            j.nomore = true;
                            return;
                        }
                    }

                    j.code_buffer |= (uint)(b << (24 - j.code_bits));
                    j.code_bits += 8;
                } while (j.code_bits <= 24);
            }

            public static async ValueTask<int> HuffmanDecode(JpegState j, Huffman h)
            {
                if (j.code_bits < 16)
                    await GrowBufferUnsafe(j);

                int c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                int k = h.Fast[c];
                if (k < 255)
                {
                    int s = h.Size[k];
                    if (s > j.code_bits)
                        return -1;
                    j.code_buffer <<= s;
                    j.code_bits -= s;
                    return h.Values[k];
                }

                uint tmp = j.code_buffer >> 16;
                for (k = 9 + 1; ; ++k)
                {
                    if (tmp < h.Maxcode[k])
                        break;
                }

                if (k == 17)
                {
                    j.code_bits -= 16;
                    return -1;
                }

                if (k > j.code_bits)
                    return -1;
                c = (int)(((j.code_buffer >> (32 - k)) & BMask[k]) + h.Delta[k]);
                j.code_bits -= k;
                j.code_buffer <<= k;
                return h.Values[c];
            }

            public static async ValueTask<int> ExtendReceive(JpegState j, int n)
            {
                if (j.code_bits < n)
                    await GrowBufferUnsafe(j);

                int sgn = (int)j.code_buffer >> 31;
                uint k = CRuntime.RotateBits(j.code_buffer, n);
                uint mask = BMask[n];
                j.code_buffer = k & ~mask;
                k &= mask;
                j.code_bits -= n;
                return (int)(k + (JBias[n] & ~sgn));
            }

            public static async ValueTask<int> ReadBits(JpegState j, int n)
            {
                if (j.code_bits < n)
                    await GrowBufferUnsafe(j);

                uint k = CRuntime.RotateBits(j.code_buffer, n);
                uint mask = BMask[n];
                j.code_buffer = k & ~mask;
                k &= mask;
                j.code_bits -= n;
                return (int)k;
            }

            public static async ValueTask<bool> ReadBit(JpegState j)
            {
                if (j.code_bits < 1)
                    await GrowBufferUnsafe(j);

                uint k = j.code_buffer;
                j.code_buffer <<= 1;
                j.code_bits--;
                return (k & 0x80000000) != 0;
            }

            public static async ValueTask DecodeBlock(
                JpegState j, Memory<short> data, Huffman hdc, Huffman hac,
                short[] fac, int b, ushort[] dequant)
            {
                if (j.code_bits < 16)
                    await GrowBufferUnsafe(j);

                int t = await HuffmanDecode(j, hdc);
                if (t < 0)
                    throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                data.Span.Clear();
                int diff = t != 0 ? await ExtendReceive(j, t) : 0;
                int dc = j.components[b].dc_pred + diff;
                j.components[b].dc_pred = dc;
                data.Span[0] = (short)(dc * dequant[0]);

                int k = 1;
                do
                {
                    if (j.code_bits < 16)
                        await GrowBufferUnsafe(j);

                    int c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                    int r = fac[c];
                    int s;
                    if (r != 0)
                    {
                        k += (r >> 4) & 15;
                        s = r & 15;
                        j.code_buffer <<= s;
                        j.code_bits -= s;
                        var zig = DeZigZag[k++];
                        data.Span[zig] = (short)((r >> 8) * dequant[zig]);
                    }
                    else
                    {
                        int rs = await HuffmanDecode(j, hac);
                        if (rs < 0)
                            throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                        s = rs & 15;
                        r = rs >> 4;
                        if (s == 0)
                        {
                            if (rs != 0xf0)
                                break;
                            k += 16;
                        }
                        else
                        {
                            k += r;
                            var zig = DeZigZag[k++];
                            var value = await ExtendReceive(j, s);
                            data.Span[zig] = (short)(value * dequant[zig]);
                        }
                    }
                } while (k < 64);
            }

            public static async ValueTask DecodeBlockProgressiveDc(
                JpegState j, Memory<short> data, Huffman hdc, int b)
            {
                if (j.spec_end != 0)
                    throw new StbImageReadException(ErrorCode.CantMergeDcAndAc);

                if (j.code_bits < 16)
                    await GrowBufferUnsafe(j);

                int t;
                int diff;
                int dc;
                if (j.succ_high == 0)
                {
                    data.Span.Clear();
                    t = await HuffmanDecode(j, hdc);
                    diff = t != 0 ? await ExtendReceive(j, t) : 0;
                    dc = j.components[b].dc_pred + diff;
                    j.components[b].dc_pred = dc;
                    data.Span[0] = (short)(dc << j.succ_low);
                }
                else
                {
                    if (await ReadBit(j))
                        data.Span[0] += (short)(1 << j.succ_low);
                }
            }

            public static async ValueTask DecodeBlockProggressiveAc(
                JpegState j, Memory<short> data, Huffman hac, short[] fac)
            {
                if (j.spec_start == 0)
                    throw new StbImageReadException(ErrorCode.CantMergeDcAndAc);

                if (j.succ_high == 0)
                {
                    int shift = j.succ_low;
                    if (j.eob_run != 0)
                    {
                        --j.eob_run;
                        return;
                    }

                    int k = j.spec_start;
                    do
                    {
                        if (j.code_bits < 16)
                            await GrowBufferUnsafe(j);

                        int c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                        int r = fac[c];
                        int s;
                        if (r != 0)
                        {
                            k += (r >> 4) & 15;
                            s = r & 15;
                            j.code_buffer <<= s;
                            j.code_bits -= s;
                            var zig = DeZigZag[k++];
                            data.Span[zig] = (short)((r >> 8) << shift);
                        }
                        else
                        {
                            int rs = await HuffmanDecode(j, hac);
                            if (rs < 0)
                                throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                            s = rs & 15;
                            r = rs >> 4;
                            if (s == 0)
                            {
                                if (r < 15)
                                {
                                    j.eob_run = 1 << r;
                                    if (r != 0)
                                        j.eob_run += await ReadBits(j, r);
                                    --j.eob_run;
                                    break;
                                }

                                k += 16;
                            }
                            else
                            {
                                k += r;
                                var zig = DeZigZag[k++];
                                int extended = await ExtendReceive(j, s);
                                data.Span[zig] = (short)(extended << shift);
                            }
                        }
                    } while (k <= j.spec_end);
                }
                else
                {
                    short bit = (short)(1 << j.succ_low);
                    if (j.eob_run != 0)
                    {
                        j.eob_run--;

                        static int CountBits(ReadOnlySpan<short> data, int offset, int end)
                        {
                            int count = 0;
                            var deZigZag = DeZigZag;
                            while (offset <= end)
                            {
                                if (data[deZigZag[offset++]] != 0)
                                    count++;
                            }
                            return count;
                        }

                        static void DecodeAssignBit(
                            bool[] bits, Span<short> data, short bit, int offset, int end)
                        {
                            int bitOffset = 0;
                            var deZigZag = DeZigZag;

                            while (offset <= end)
                            {
                                ref short p = ref data[deZigZag[offset++]];
                                if (p != 0)
                                {
                                    if (bits[bitOffset++])
                                    {
                                        if ((p & bit) == 0)
                                        {
                                            if (p > 0)
                                                p += bit;
                                            else
                                                p -= bit;
                                        }
                                    }
                                }
                            }
                        }

                        int bitCount = CountBits(data.Span, j.spec_start, j.spec_end);
                        for (int i = 0; i < bitCount; i++)
                            j.bitbuffer[i] = await ReadBit(j);

                        DecodeAssignBit(j.bitbuffer, data.Span, bit, j.spec_start, j.spec_end);
                    }
                    else
                    {
                        int k = j.spec_start;
                        do
                        {
                            int rs = await HuffmanDecode(j, hac);
                            if (rs < 0)
                                throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                            int s = rs & 15;
                            int r = rs >> 4;
                            if (s == 0)
                            {
                                if (r < 15)
                                {
                                    j.eob_run = (1 << r) - 1;
                                    if (r != 0)
                                        j.eob_run += await ReadBits(j, r);
                                    r = 64;
                                }
                            }
                            else
                            {
                                if (s != 1)
                                    throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                                if (await ReadBit(j))
                                    s = bit;
                                else
                                    s = -bit;
                            }

                            static int CountBits(ReadOnlySpan<short> data, int r, int offset, int end)
                            {
                                int count = 0;
                                var deZigZag = DeZigZag;
                                while (offset <= end)
                                {
                                    if (data[deZigZag[offset++]] != 0)
                                    {
                                        count++;
                                    }
                                    else
                                    {
                                        if (r == 0)
                                            break;
                                        r--;
                                    }
                                }
                                return count;
                            }

                            static void DecodeDecrementAssignBit(
                                bool[] bits, Span<short> data, short s, int r, short bit,
                                ref int offset, int end)
                            {
                                int bitOffset = 0;
                                var deZigZag = DeZigZag;

                                while (offset <= end)
                                {
                                    ref short p = ref data[deZigZag[offset++]];
                                    if (p != 0)
                                    {
                                        if (bits[bitOffset++])
                                        {
                                            if ((p & bit) == 0)
                                            {
                                                if (p > 0)
                                                    p += bit;
                                                else
                                                    p -= bit;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (r == 0)
                                        {
                                            p = s;
                                            break;
                                        }
                                        r--;
                                    }
                                }
                            }

                            int bitCount = CountBits(data.Span, r, k, j.spec_end);
                            for (int i = 0; i < bitCount; i++)
                                j.bitbuffer[i] = await ReadBit(j);

                            DecodeDecrementAssignBit(j.bitbuffer, data.Span, (short)s, r, bit, ref k, j.spec_end);

                        } while (k <= j.spec_end);
                    }
                }
            }

            public static byte Clamp(int x)
            {
                if (x < 0)
                    return 0;
                if (x > 255)
                    return 255;
                return (byte)x;
            }

            public static void Idct1D(
                int s0, int s1, int s2, int s3, int s4, int s5, int s6, int s7, out Idct idct)
            {
                idct.p2 = s2;
                idct.p3 = s6;
                idct.p1 = (idct.p2 + idct.p3) * (int)(0.5411961f * 4096 + 0.5);
                idct.t2 = idct.p1 + idct.p3 * (int)((-1.847759065f) * 4096 + 0.5);
                idct.t3 = idct.p1 + idct.p2 * (int)(0.765366865f * 4096 + 0.5);
                idct.p2 = s0;
                idct.p3 = s4;
                idct.t0 = (idct.p2 + idct.p3) * 4096;
                idct.t1 = (idct.p2 - idct.p3) * 4096;
                idct.x0 = idct.t0 + idct.t3;
                idct.x3 = idct.t0 - idct.t3;
                idct.x1 = idct.t1 + idct.t2;
                idct.x2 = idct.t1 - idct.t2;
                idct.t0 = s7;
                idct.t1 = s5;
                idct.t2 = s3;
                idct.t3 = s1;
                idct.p3 = idct.t0 + idct.t2;
                idct.p4 = idct.t1 + idct.t3;
                idct.p1 = idct.t0 + idct.t3;
                idct.p2 = idct.t1 + idct.t2;
                idct.p5 = (idct.p3 + idct.p4) * (int)(1.175875602f * 4096 + 0.5);
                idct.t0 *= (int)(0.298631336f * 4096 + 0.5);
                idct.t1 *= (int)(2.053119869f * 4096 + 0.5);
                idct.t2 *= (int)(3.072711026f * 4096 + 0.5);
                idct.t3 *= (int)(1.501321110f * 4096 + 0.5);
                idct.p1 = idct.p5 + idct.p1 * (int)((-0.899976223f) * 4096 + 0.5);
                idct.p2 = idct.p5 + idct.p2 * (int)((-2.562915447f) * 4096 + 0.5);
                idct.p3 *= (int)((-1.961570560f) * 4096 + 0.5);
                idct.p4 *= (int)((-0.390180644f) * 4096 + 0.5);
                idct.t3 += idct.p1 + idct.p4;
                idct.t2 += idct.p2 + idct.p3;
                idct.t1 += idct.p2 + idct.p4;
                idct.t0 += idct.p1 + idct.p3;
            }

            public static void IdctBlock(Span<byte> _out_, int out_stride, Span<short> data)
            {
                Span<int> val = stackalloc int[64];

                for (int i = 0; i < 8; ++i)
                {
                    var v = val.Slice(i);
                    var d = data.Slice(i);

                    if ((d[8] == 0) &&
                        (d[16] == 0) &&
                        (d[24] == 0) &&
                        (d[32] == 0) &&
                        (d[40] == 0) &&
                        (d[48] == 0) &&
                        (d[56] == 0))
                    {
                        int dcterm = d[0] << 2;
                        v[0] = v[8] = v[16] = v[24] = v[32] = v[40] = v[48] = v[56] = dcterm;
                    }
                    else
                    {
                        Idct1D(d[0], d[8], d[16], d[24], d[32], d[40], d[48], d[56], out var idct);

                        idct.x0 += 512;
                        idct.x1 += 512;
                        idct.x2 += 512;
                        idct.x3 += 512;

                        v[0] = (idct.x0 + idct.t3) >> 10;
                        v[8] = (idct.x1 + idct.t2) >> 10;
                        v[16] = (idct.x2 + idct.t1) >> 10;
                        v[24] = (idct.x3 + idct.t0) >> 10;
                        v[32] = (idct.x3 - idct.t0) >> 10;
                        v[40] = (idct.x2 - idct.t1) >> 10;
                        v[48] = (idct.x1 - idct.t2) >> 10;
                        v[56] = (idct.x0 - idct.t3) >> 10;
                    }
                }

                for (int i = 0; i < 8; ++i)
                {
                    var o = _out_.Slice(i * out_stride);
                    var v = val.Slice(i * 8);

                    Idct1D(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], out var idct);

                    idct.x0 += 65536 + (128 << 17);
                    idct.x1 += 65536 + (128 << 17);
                    idct.x2 += 65536 + (128 << 17);
                    idct.x3 += 65536 + (128 << 17);

                    o[0] = Clamp((idct.x0 + idct.t3) >> 17);
                    o[1] = Clamp((idct.x1 + idct.t2) >> 17);
                    o[2] = Clamp((idct.x2 + idct.t1) >> 17);
                    o[3] = Clamp((idct.x3 + idct.t0) >> 17);
                    o[4] = Clamp((idct.x3 - idct.t0) >> 17);
                    o[5] = Clamp((idct.x2 - idct.t1) >> 17);
                    o[6] = Clamp((idct.x1 - idct.t2) >> 17);
                    o[7] = Clamp((idct.x0 - idct.t3) >> 17);
                }
            }

            public static async ValueTask<byte> ReadMarker(JpegState j)
            {
                byte x;
                if (j.marker != 0xff)
                {
                    x = j.marker;
                    j.marker = 0xff;
                    return x;
                }

                x = await j.s.ReadByte();
                if (x != 0xff)
                    return 0xff;

                while (x == 0xff)
                    x = await j.s.ReadByte();

                return x;
            }

            public static void Reset(JpegState j)
            {
                j.code_bits = 0;
                j.code_buffer = 0;
                j.nomore = false;
                for (int i = 0; i < j.components.Length; i++)
                    j.components[i].dc_pred = 0;
                j.marker = 0xff;
                j.todo = j.restart_interval != 0 ? j.restart_interval : 0x7fffffff;
                j.eob_run = 0;
            }

            public static async ValueTask<bool> ParseEntropyCodedData(JpegState z)
            {
                Reset(z);
                if (!z.progressive)
                {
                    if (z.scan_n == 1)
                    {
                        var data = new short[64].AsMemory();
                        int n = z.order[0];
                        int w = (z.components[n].x + 7) / 8;
                        int h = (z.components[n].y + 7) / 8;
                        for (int j = 0; j < h; ++j)
                        {
                            for (int i = 0; i < w; ++i)
                            {
                                int ha = z.components[n].ha;

                                await DecodeBlock(
                                    z, data, z.huff_dc[z.components[n].hd], z.huff_ac[ha],
                                    z.fast_ac[ha], n, z.dequant[z.components[n].tq]);

                                z.idct_block_kernel(
                                    z.components[n].data.Slice(z.components[n].w2 * j * 8 + i * 8).Span,
                                    z.components[n].w2,
                                    data.Span);

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        await GrowBufferUnsafe(z);

                                    if (!((z.marker >= 0xd0) && (z.marker <= 0xd7)))
                                        return true;

                                    Reset(z);
                                }
                            }
                        }
                        return true;
                    }
                    else
                    {
                        int k;
                        int x;
                        int y;
                        var data = new short[64].AsMemory();
                        for (int j = 0; j < z.img_mcu_y; ++j)
                        {
                            for (int i = 0; i < z.img_mcu_x; ++i)
                            {
                                for (k = 0; k < z.scan_n; ++k)
                                {
                                    int n = z.order[k];
                                    for (y = 0; y < z.components[n].v; ++y)
                                    {
                                        for (x = 0; x < z.components[n].h; ++x)
                                        {
                                            int x2 = (i * z.components[n].h + x) * 8;
                                            int y2 = (j * z.components[n].v + y) * 8;
                                            int ha = z.components[n].ha;

                                            await DecodeBlock(z, data,
                                                z.huff_dc[z.components[n].hd],
                                                z.huff_ac[ha], z.fast_ac[ha], n,
                                                z.dequant[z.components[n].tq]);

                                            z.idct_block_kernel(
                                                z.components[n].data.Slice(z.components[n].w2 * y2 + x2).Span,
                                                z.components[n].w2,
                                                data.Span);
                                        }
                                    }
                                }

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        await GrowBufferUnsafe(z);

                                    if (!((z.marker >= 0xd0) && (z.marker <= 0xd7)))
                                        return true;
                                    Reset(z);
                                }
                            }
                        }

                        return true;
                    }
                }
                else
                {
                    if (z.scan_n == 1)
                    {
                        int n = z.order[0];
                        int w = (z.components[n].x + 7) / 8;
                        int h = (z.components[n].y + 7) / 8;
                        for (int j = 0; j < h; ++j)
                        {
                            for (int i = 0; i < w; ++i)
                            {
                                var data = z.components[n].coeff.Slice(64 * (i + j * z.components[n].coeff_w), 64);

                                if (z.spec_start == 0)
                                {
                                    await DecodeBlockProgressiveDc(z, data, z.huff_dc[z.components[n].hd], n);
                                }
                                else
                                {
                                    int ha = z.components[n].ha;
                                    await DecodeBlockProggressiveAc(z, data, z.huff_ac[ha], z.fast_ac[ha]);
                                }

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        await GrowBufferUnsafe(z);

                                    if (!((z.marker >= 0xd0) && (z.marker <= 0xd7)))
                                        return true;
                                    Reset(z);
                                }
                            }
                        }
                        return true;
                    }
                    else
                    {
                        int k;
                        int x;
                        int y;
                        for (int j = 0; j < z.img_mcu_y; ++j)
                        {
                            for (int i = 0; i < z.img_mcu_x; ++i)
                            {
                                for (k = 0; k < z.scan_n; ++k)
                                {
                                    int n = z.order[k];
                                    for (y = 0; y < z.components[n].v; ++y)
                                    {
                                        for (x = 0; x < z.components[n].h; ++x)
                                        {
                                            int x2 = i * z.components[n].h + x;
                                            int y2 = j * z.components[n].v + y;
                                            var data = z.components[n].coeff.Slice(64 * (x2 + y2 * z.components[n].coeff_w), 64);
                                            await DecodeBlockProgressiveDc(z, data, z.huff_dc[z.components[n].hd], n);
                                        }
                                    }
                                }

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        await GrowBufferUnsafe(z);

                                    if (!((z.marker >= 0xd0) && (z.marker <= 0xd7)))
                                        return true;
                                    Reset(z);
                                }
                            }
                        }
                        return true;
                    }
                }

            }

            public static void Dequantize(Span<short> data, ushort[] dequant)
            {
                for (int i = 0; i < data.Length; ++i)
                    data[i] *= (short)dequant[i];
            }

            public static void Finish(JpegState z)
            {
                if (!z.progressive)
                    return;

                int i;
                int j;
                for (int n = 0; n < z.ri.Components; ++n)
                {
                    int w = (z.components[n].x + 7) / 8;
                    int h = (z.components[n].y + 7) / 8;
                    for (j = 0; j < h; ++j)
                    {
                        for (i = 0; i < w; ++i)
                        {
                            var data = z.components[n].coeff.Slice(64 * (i + j * z.components[n].coeff_w), 64);
                            Dequantize(data.Span, z.dequant[z.components[n].tq]);

                            z.idct_block_kernel(
                                z.components[n].data.Slice(z.components[n].w2 * j * 8 + i * 8).Span,
                                z.components[n].w2,
                                data.Span);
                        }
                    }
                }
            }

            public static async ValueTask<bool> ProcessMarker(JpegState z, int m)
            {
                var s = z.s;
                int L;
                switch (m)
                {
                    case 0xff:
                        throw new StbImageReadException(ErrorCode.ExpectedMarker);

                    case 0xDD:
                        if (await s.ReadInt16BE() != 4)
                            throw new StbImageReadException(ErrorCode.BadDRILength);

                        z.restart_interval = await s.ReadInt16BE();
                        return true;

                    case 0xDB:
                        L = await s.ReadInt16BE() - 2;
                        while (L > 0)
                        {
                            int q = await s.ReadByte();
                            int p = q >> 4;
                            if ((p != 0) && (p != 1))
                                throw new StbImageReadException(ErrorCode.BadDQTType);

                            int t = q & 15;
                            if (t > 3)
                                throw new StbImageReadException(ErrorCode.BadDQTTable);

                            int sixteen = (p != 0) ? 1 : 0;

                            for (int i = 0; i < 64; ++i)
                            {
                                z.dequant[t][DeZigZag[i]] = (ushort)(sixteen != 0
                                    ? await s.ReadInt16BE()
                                    : await s.ReadByte());
                            }
                            L -= sixteen != 0 ? 129 : 65;
                        }
                        return L == 0;

                    case 0xC4:
                        var sizes = new int[16];
                        L = await s.ReadInt16BE() - 2;
                        while (L > 0)
                        {
                            int q = await s.ReadByte();
                            int tc = q >> 4;
                            int th = q & 15;
                            if ((tc > 1) || (th > 3))
                                throw new StbImageReadException(ErrorCode.BadDHTHeader);

                            int n = 0;
                            for (int i = 0; i < sizes.Length; ++i)
                            {
                                sizes[i] = await s.ReadByte();
                                n += sizes[i];
                            }

                            Huffman[] huff;

                            L -= 17;
                            if (tc == 0)
                            {
                                BuildHuffman(z.huff_dc[th], sizes);
                                huff = z.huff_dc;
                            }
                            else
                            {
                                BuildHuffman(z.huff_ac[th], sizes);
                                huff = z.huff_ac;
                            }

                            await s.ReadBytes(huff[th].MValues.Slice(0, n));

                            if (tc != 0)
                                BuildFastAc(z.fast_ac[th], z.huff_ac[th]);
                            L -= n;
                        }
                        return L == 0;
                }

                if (((m >= 0xE0) && (m <= 0xEF)) || (m == 0xFE))
                {
                    L = await s.ReadInt16BE();
                    if (L < 2)
                    {
                        if (m == 0xFE)
                            throw new StbImageReadException(ErrorCode.BadCOMLength);
                        else
                            throw new StbImageReadException(ErrorCode.BadAPPLength);
                    }

                    L -= 2;
                    if ((m == 0xE0) && (L >= 5))
                    {
                        var tag = new byte[5];
                        tag[0] = (byte)'J';
                        tag[1] = (byte)'F';
                        tag[2] = (byte)'I';
                        tag[3] = (byte)'F';
                        tag[4] = (byte)'\0';

                        int ok = 1;
                        int i;
                        for (i = 0; i < 5; ++i)
                        {
                            if (await s.ReadByte() != tag[i])
                                ok = 0;
                        }

                        L -= 5;
                        if (ok != 0)
                            z.jfif = 1;
                    }
                    else if ((m == 0xEE) && (L >= 12))
                    {
                        var tag = new byte[6];
                        tag[0] = (byte)'A';
                        tag[1] = (byte)'d';
                        tag[2] = (byte)'o';
                        tag[3] = (byte)'b';
                        tag[4] = (byte)'e';
                        tag[5] = (byte)'\0';

                        bool ok = true;
                        for (int i = 0; i < 6; ++i)
                        {
                            if (await s.ReadByte() != tag[i])
                            {
                                ok = false;
                                break;
                            }
                        }

                        L -= 6;
                        if (ok)
                        {
                            await s.ReadByte();
                            await s.ReadInt16BE();
                            await s.ReadInt16BE();
                            z.app14_color_transform = await s.ReadByte();
                            L -= 6;
                        }
                    }

                    await s.Skip(L);
                    return true;
                }

                throw new StbImageReadException(ErrorCode.UnknownMarker);
            }

            public static async ValueTask<bool> ProcessScanHeader(JpegState z)
            {
                var s = z.s;
                int Ls = await s.ReadInt16BE();
                z.scan_n = await s.ReadByte();
                if ((z.scan_n < 1) || (z.scan_n > 4) || (z.scan_n > z.ri.Components))
                    throw new StbImageReadException(ErrorCode.BadSOSComponentCount);

                if (Ls != 6 + 2 * z.scan_n)
                    throw new StbImageReadException(ErrorCode.BadSOSLength);

                for (int i = 0; i < z.scan_n; ++i)
                {
                    int id = await s.ReadByte();
                    int q = await s.ReadByte();
                    int which;
                    for (which = 0; which < z.ri.Components; ++which)
                    {
                        if (z.components[which].id == id)
                            break;
                    }

                    if (which == z.ri.Components)
                        return false;

                    z.components[which].hd = q >> 4;
                    if (z.components[which].hd > 3)
                        throw new StbImageReadException(ErrorCode.BadDCHuffman);

                    z.components[which].ha = q & 15;
                    if (z.components[which].ha > 3)
                        throw new StbImageReadException(ErrorCode.BadACHuffman);

                    z.order[i] = which;
                }

                {
                    int aa;
                    z.spec_start = await s.ReadByte();
                    z.spec_end = await s.ReadByte();
                    aa = await s.ReadByte();
                    z.succ_high = aa >> 4;
                    z.succ_low = aa & 15;
                    if (z.progressive)
                    {
                        if ((z.spec_start > 63) ||
                            (z.spec_end > 63) ||
                            (z.spec_start > z.spec_end) ||
                            (z.succ_high > 13) ||
                            (z.succ_low > 13))
                            throw new StbImageReadException(ErrorCode.BadSOS);
                    }
                    else
                    {
                        if (z.spec_start != 0)
                            throw new StbImageReadException(ErrorCode.BadSOS);

                        if ((z.succ_high != 0) || (z.succ_low != 0))
                            throw new StbImageReadException(ErrorCode.BadSOS);

                        z.spec_end = 63;
                    }
                }

                return true;
            }

            public static void FreeComponents(JpegState z, int ncomp)
            {
                for (int i = 0; i < ncomp; i++)
                {
                    if (z.components[i].raw_data != null)
                    {
                        z.components[i].raw_data = null;
                        z.components[i].data = null;
                    }

                    if (z.components[i].raw_coeff != null)
                    {
                        z.components[i].raw_coeff = null;
                        z.components[i].coeff = null;
                    }

                    if (z.components[i].raw_linebuf != null)
                    {
                        z.components[i].raw_linebuf = null;
                        z.components[i].linebuf = null;
                    }
                }
            }

            public static async ValueTask ProcessFrameHeader(JpegState z, ScanMode scan)
            {
                var s = z.s;
                int Lf;
                int p;
                int i;
                int q;
                int h_max = 1;
                int v_max = 1;
                Lf = await s.ReadInt16BE();
                if (Lf < 11)
                    throw new StbImageReadException(ErrorCode.BadSOFLength);

                p = await s.ReadByte();
                if (p != 8)
                    throw new StbImageReadException(ErrorCode.UnsupportedBitDepth);

                z.ri.Height = await s.ReadInt16BE();
                if (z.ri.Height == 0)
                    throw new StbImageReadException(ErrorCode.ZeroHeight);

                z.ri.Width = await s.ReadInt16BE();
                if (z.ri.Width == 0)
                    throw new StbImageReadException(ErrorCode.ZeroWidth);

                z.ri.Components = await s.ReadByte();
                if ((z.ri.Components != 1) &&
                    (z.ri.Components != 3) &&
                    (z.ri.Components != 4))
                    throw new StbImageReadException(ErrorCode.BadComponentCount);

                for (i = 0; i < z.ri.Components; ++i)
                {
                    z.components[i].data = null;
                    z.components[i].linebuf = null;
                }

                if (Lf != 8 + 3 * z.ri.Components)
                    throw new StbImageReadException(ErrorCode.BadSOFLength);

                z.rgb = 0;

                var rgb = new byte[3];
                rgb[0] = (byte)'R';
                rgb[1] = (byte)'G';
                rgb[2] = (byte)'B';

                for (i = 0; i < z.ri.Components; i++)
                {
                    z.components[i].id = await s.ReadByte();
                    if ((z.ri.Components == 3) && (z.components[i].id == rgb[i]))
                        ++z.rgb;
                    q = await s.ReadByte();
                    z.components[i].h = q >> 4;
                    if ((z.components[i].h == 0) || (z.components[i].h > 4))
                        throw new StbImageReadException(ErrorCode.BadH);

                    z.components[i].v = q & 15;
                    if ((z.components[i].v == 0) || (z.components[i].v > 4))
                        throw new StbImageReadException(ErrorCode.BadV);

                    z.components[i].tq = await s.ReadByte();
                    if (z.components[i].tq > 3)
                        throw new StbImageReadException(ErrorCode.BadTQ);
                }

                if (scan != ScanMode.Load)
                    return;

                for (i = 0; i < z.ri.Components; ++i)
                {
                    if (z.components[i].h > h_max)
                        h_max = z.components[i].h;
                    if (z.components[i].v > v_max)
                        v_max = z.components[i].v;
                }

                z.img_h_max = h_max;
                z.img_v_max = v_max;
                z.img_mcu_w = h_max * 8;
                z.img_mcu_h = v_max * 8;
                z.img_mcu_x = (z.ri.Width + z.img_mcu_w - 1) / z.img_mcu_w;
                z.img_mcu_y = (z.ri.Height + z.img_mcu_h - 1) / z.img_mcu_h;
                for (i = 0; i < z.ri.Components; ++i)
                {
                    z.components[i].x = (z.ri.Width * z.components[i].h + h_max - 1) / h_max;
                    z.components[i].y = (z.ri.Height * z.components[i].v + v_max - 1) / v_max;
                    z.components[i].w2 = z.img_mcu_x * z.components[i].h * 8;
                    z.components[i].h2 = z.img_mcu_y * z.components[i].v * 8;

                    z.components[i].raw_coeff = null;
                    z.components[i].coeff = null;

                    z.components[i].raw_linebuf = null;
                    z.components[i].linebuf = null;

                    z.components[i].raw_data = new byte[z.components[i].w2 * z.components[i].h2];
                    z.components[i].data = z.components[i].raw_data.AsMemory();

                    if (z.progressive)
                    {
                        z.components[i].coeff_w = z.components[i].w2 / 8;
                        z.components[i].coeff_h = z.components[i].h2 / 8;

                        z.components[i].raw_coeff = new short[z.components[i].w2 * z.components[i].h2];
                        z.components[i].coeff = z.components[i].raw_coeff.AsMemory();
                    }
                }
            }

            public static async ValueTask<bool> ParseHeader(JpegState z, ScanMode scan)
            {
                z.jfif = 0;
                z.app14_color_transform = -1;
                z.marker = 0xff;

                int m = await ReadMarker(z);
                if (!(m == 0xd8))
                    throw new StbImageReadException(ErrorCode.NoSOI);

                m = await ReadMarker(z);
                while (!((m == 0xc0) || (m == 0xc1) || (m == 0xc2)))
                {
                    if (!await ProcessMarker(z, m))
                        return false;

                    do
                    {
                        m = await ReadMarker(z);
                    }
                    while (m == 0xff);
                }

                z.progressive = m == 0xc2;
                await ProcessFrameHeader(z, scan);

                return true;
            }

            public static async ValueTask<bool> ParseData(JpegState j)
            {
                for (int i = 0; i < 4; i++)
                {
                    j.components[i].raw_data = null;
                    j.components[i].raw_coeff = null;
                }

                j.restart_interval = 0;

                if (!await ParseHeader(j, (int)ScanMode.Load))
                    return false;

                var s = j.s;
                int m = await ReadMarker(j);
                while (!(m == 0xd9))
                {
                    if (m == 0xda)
                    {
                        if (!await ProcessScanHeader(j))
                            return false;

                        if (!await ParseEntropyCodedData(j))
                            return false;

                        if (j.marker == 0xff)
                        {
                            while (true)
                            {
                                if (await s.ReadByte() == 0xff)
                                {
                                    j.marker = await s.ReadByte();
                                    break;
                                }
                            }
                        }
                    }
                    else if (m == 0xdc)
                    {
                        int Ld = await s.ReadInt16BE();
                        uint NL = (uint)await s.ReadInt16BE();
                        if (Ld != 4)
                            throw new StbImageReadException(ErrorCode.BadDNLLength);

                        if (NL != j.ri.Height)
                            throw new StbImageReadException(ErrorCode.BadDNLHeight);
                    }
                    else
                    {
                        if (!await ProcessMarker(j, m))
                            return false;
                    }

                    m = await ReadMarker(j);
                }

                if (j.progressive)
                    Finish(j);

                j.ri.Depth = 8;

                j.is_rgb = j.ri.Components == 3 && (j.rgb == 3 || (j.app14_color_transform == 0 && j.jfif == 0));
                j.decode_n = (j.ri.Components < 3 && !j.is_rgb) ? 1 : j.ri.Components;

                return true;
            }

            public static Memory<byte> ResampleRow1(
                Memory<byte> _out_, Memory<byte> in_near, Memory<byte> in_far, int w, int hs)
            {
                return in_near;
            }

            public static Memory<byte> ResampleRowV2(
                Memory<byte> _out_, Memory<byte> in_near, Memory<byte> in_far, int w, int hs)
            {
                var o = _out_.Span;
                var n = in_near.Span;
                var f = in_far.Span;

                for (int i = 0; i < w; ++i)
                {
                    o[i] = (byte)((3 * n[i] + f[i] + 2) >> 2);
                }

                return _out_;
            }

            public static Memory<byte> ResampleRowH2(
                Memory<byte> _out_, Memory<byte> in_near, Memory<byte> in_far, int w, int hs)
            {
                var o = _out_.Span;
                var input = in_near.Span;
                if (w == 1)
                {
                    o[0] = o[1] = input[0];
                    return _out_;
                }

                o[0] = input[0];
                o[1] = (byte)((input[0] * 3 + input[1] + 2) >> 2);
                int i;
                for (i = 1; i < (w - 1); ++i)
                {
                    int n = 3 * input[i] + 2;
                    o[i * 2 + 0] = (byte)((n + input[i - 1]) >> 2);
                    o[i * 2 + 1] = (byte)((n + input[i + 1]) >> 2);
                }

                o[i * 2 + 0] = (byte)((input[w - 2] * 3 + input[w - 1] + 2) >> 2);
                o[i * 2 + 1] = input[w - 1];

                return _out_;
            }

            public static Memory<byte> ResampleRowHV2(
                Memory<byte> _out_, Memory<byte> in_near, Memory<byte> in_far, int w, int hs)
            {
                var o = _out_.Span;
                var n = in_near.Span;
                var f = in_far.Span;

                if (w == 1)
                {
                    o[0] = o[1] = (byte)((3 * n[0] + f[0] + 2) >> 2);
                    return _out_;
                }

                int t1 = 3 * n[0] + f[0];
                o[0] = (byte)((t1 + 2) >> 2);
                for (int i = 1; i < w; ++i)
                {
                    int t0 = t1;
                    t1 = 3 * n[i] + n[i];
                    o[i * 2 - 1] = (byte)((3 * t0 + t1 + 8) >> 4);
                    o[i * 2] = (byte)((3 * t1 + t0 + 8) >> 4);
                }

                o[w * 2 - 1] = (byte)((t1 + 2) >> 2);
                return _out_;
            }

            public static Memory<byte> ResampleRowGeneric(
                Memory<byte> _out_, Memory<byte> in_near, Memory<byte> in_far, int w, int hs)
            {
                var o = _out_.Span;
                var n = in_near.Span;

                for (int i = 0; i < w; ++i)
                {
                    var oslice = o.Slice(i * hs);
                    byte near = n[i];

                    for (int j = 0; j < hs; ++j)
                        oslice[j] = near;
                }

                return _out_;
            }

            public static void YCbCrToRGB(
                Span<byte> _out_, Span<byte> y, Span<byte> pcb, Span<byte> pcr)
            {
                for (int i = 0, x = 0; i < _out_.Length; i += 3, x++)
                {
                    int y_fixed = (y[x] << 20) + (1 << 19);
                    int cr = pcr[x] - 128;
                    int cb = pcb[x] - 128;

                    int r = y_fixed + cr * (((int)(1.40200f * 4096.0f + 0.5f)) << 8);
                    int g = (int)(y_fixed + (cr * -(((int)(0.71414f * 4096.0f + 0.5f)) << 8)) +
                         ((cb * -(((int)(0.34414f * 4096.0f + 0.5f)) << 8)) & 0xffff0000));
                    int b = y_fixed + cb * (((int)(1.77200f * 4096.0f + 0.5f)) << 8);

                    r >>= 20;
                    g >>= 20;
                    b >>= 20;

                    if (((uint)r) > 255)
                        r = r < 0 ? 0 : 255;

                    if (((uint)g) > 255)
                        g = g < 0 ? 0 : 255;

                    if (((uint)b) > 255)
                        b = b < 0 ? 0 : 255;

                    _out_[i + 0] = (byte)r;
                    _out_[i + 1] = (byte)g;
                    _out_[i + 2] = (byte)b;
                }
            }

            public static void Cleanup(JpegState j)
            {
                FreeComponents(j, j.ri.Components);
            }

            public static byte Blinn8x8(byte x, byte y)
            {
                uint t = (uint)(x * y + 128);
                return (byte)((t + (t >> 8)) >> 8);
            }

            public static byte ComputeY8(byte r, byte g, byte b)
            {
                return (byte)(((r * 77) + (g * 150) + (29 * b)) >> 8);
            }

            public static async Task LoadImage(JpegState z)
            {
                if (!await ParseData(z))
                {
                    Cleanup(z);
                    return;
                }

                z.ri.OutComponents = z.ri.Components >= 3 ? 3 : 1;
                z.ri.OutDepth = z.ri.Depth;

                z.ri.StateReady();

                var coutput = new Memory<byte>[4];
                var res_comp = new ResampleData[4];

                for (int i = 0; i < res_comp.Length; i++)
                    res_comp[i] = new ResampleData();

                for (int k = 0; k < z.decode_n; k++)
                {
                    z.components[k].raw_linebuf = new byte[z.ri.Width + 3];
                    z.components[k].linebuf = z.components[k].raw_linebuf.AsMemory();

                    ResampleData r = res_comp[k];
                    r.hs = z.img_h_max / z.components[k].h;
                    r.vs = z.img_v_max / z.components[k].v;
                    r.ystep = r.vs >> 1;
                    r.w_lores = (z.ri.Width + r.hs - 1) / r.hs;
                    r.ypos = 0;
                    r.line0 = r.line1 = z.components[k].data;

                    if ((r.hs == 1) && (r.vs == 1))
                        r.Resample = ResampleRow1;
                    else if ((r.hs == 1) && (r.vs == 2))
                        r.Resample = ResampleRowV2;
                    else if ((r.hs == 2) && (r.vs == 1))
                        r.Resample = ResampleRowH2;
                    else if ((r.hs == 2) && (r.vs == 2))
                        r.Resample = z.resample_row_hv_2_kernel;
                    else
                        r.Resample = ResampleRowGeneric;
                }

                int stride = z.ri.OutComponents * z.ri.Width;
                var output = new byte[stride * z.ri.Height].AsMemory();

                for (int j = 0; j < z.ri.Height; ++j)
                {
                    var _out_ = output.Slice(stride * j, stride);

                    for (int k = 0; k < z.decode_n; ++k)
                    {
                        ResampleData r = res_comp[k];
                        bool y_bot = r.ystep >= (r.vs >> 1);

                        coutput[k] = r.Resample(
                            z.components[k].linebuf,
                            y_bot ? r.line1 : r.line0,
                            y_bot ? r.line0 : r.line1,
                            r.w_lores,
                            r.hs);

                        if ((++r.ystep) >= r.vs)
                        {
                            r.ystep = 0;
                            r.line0 = r.line1;
                            if ((++r.ypos) < z.components[k].y)
                                r.line1 = r.line1.Slice(z.components[k].w2);
                        }
                    }

                    if (z.ri.OutComponents >= 3)
                    {
                        var y = coutput[0];
                        if (z.ri.Components == 3)
                        {
                            if (z.is_rgb)
                            {
                                for (int i = 0; i < z.ri.Width; ++i)
                                {
                                    _out_.Span[0] = y.Span[i];
                                    _out_.Span[1] = coutput[1].Span[i];
                                    _out_.Span[2] = coutput[2].Span[i];
                                    _out_.Span[3] = 255;
                                    _out_ = _out_.Slice(z.ri.OutComponents);
                                }
                            }
                            else
                            {
                                z.YCbCr_to_RGB_kernel(
                                    _out_.Span, y.Span, coutput[1].Span, coutput[2].Span);
                            }
                        }
                        else if (z.ri.Components == 4)
                        {
                            if (z.app14_color_transform == 0)
                            {
                                for (int i = 0; i < z.ri.Width; ++i)
                                {
                                    byte m = coutput[3].Span[i];
                                    _out_.Span[0] = Blinn8x8(coutput[0].Span[i], m);
                                    _out_.Span[1] = Blinn8x8(coutput[1].Span[i], m);
                                    _out_.Span[2] = Blinn8x8(coutput[2].Span[i], m);
                                    _out_.Span[3] = 255;
                                    _out_ = _out_.Slice(z.ri.OutComponents);
                                }
                            }
                            else if (z.app14_color_transform == 2)
                            {
                                z.YCbCr_to_RGB_kernel(
                                    _out_.Span, y.Span, coutput[1].Span, coutput[2].Span);

                                for (int i = 0; i < z.ri.Width; ++i)
                                {
                                    byte m = coutput[3].Span[i];
                                    _out_.Span[0] = Blinn8x8((byte)(255 - _out_.Span[0]), m);
                                    _out_.Span[1] = Blinn8x8((byte)(255 - _out_.Span[1]), m);
                                    _out_.Span[2] = Blinn8x8((byte)(255 - _out_.Span[2]), m);
                                    _out_ = _out_.Slice(z.ri.OutComponents);
                                }
                            }
                            else
                            {
                                z.YCbCr_to_RGB_kernel(
                                    _out_.Span, y.Span, coutput[1].Span, coutput[2].Span);
                            }
                        }
                        else
                            for (int i = 0; i < z.ri.Width; ++i)
                            {
                                _out_.Span[0] = _out_.Span[1] = _out_.Span[2] = y.Span[i];
                                _out_.Span[3] = 255;
                                _out_ = _out_.Slice(z.ri.OutComponents);
                            }
                    }
                    else
                    {
                        if (z.is_rgb)
                        {
                            if (z.ri.OutComponents == 1)
                            {
                                for (int i = 0; i < z.ri.Width; ++i)
                                    _out_.Span[i] = ComputeY8(coutput[0].Span[i], coutput[1].Span[i], coutput[2].Span[i]);
                            }
                            else
                            {
                                for (int i = 0; i < z.ri.Width; ++i)
                                {
                                    _out_.Span[0] = ComputeY8(coutput[0].Span[i], coutput[1].Span[i], coutput[2].Span[i]);
                                    _out_.Span[1] = 255;
                                    _out_ = _out_.Slice(2);
                                }
                            }
                        }
                        else if ((z.ri.Components == 4) && (z.app14_color_transform == 0))
                        {
                            for (int i = 0; i < z.ri.Width; ++i)
                            {
                                byte m = coutput[3].Span[i];
                                byte r = Blinn8x8(coutput[0].Span[i], m);
                                byte g = Blinn8x8(coutput[1].Span[i], m);
                                byte b = Blinn8x8(coutput[2].Span[i], m);
                                _out_.Span[0] = ComputeY8(r, g, b);
                                _out_.Span[1] = 255;
                                _out_ = _out_.Slice(z.ri.OutComponents);
                            }
                        }
                        else if ((z.ri.Components == 4) && (z.app14_color_transform == 2))
                        {
                            for (int i = 0; i < z.ri.Width; ++i)
                            {
                                _out_.Span[0] = Blinn8x8((byte)(255 - coutput[0].Span[i]), coutput[3].Span[i]);
                                _out_.Span[1] = 255;
                                _out_ = _out_.Slice(z.ri.OutComponents);
                            }
                        }
                        else
                        {
                            var y = coutput[0];
                            if (z.ri.OutComponents == 1)
                            {
                                for (int i = 0; i < z.ri.Width; ++i)
                                {
                                    _out_.Span[0] = y.Span[i];
                                    _out_ = _out_.Slice(1);
                                }
                            }
                            else
                            {
                                for (int i = 0; i < z.ri.Width; ++i)
                                {
                                    _out_.Span[0] = y.Span[i];
                                    _out_.Span[1] = 255;
                                    _out_ = _out_.Slice(2);
                                }
                            }
                        }
                    }

                    z.ri.OutputPixelLine(AddressingMajor.Row, j, 0, _out_.Span);
                }

                Cleanup(z);
            }

            public static async Task Load(BinReader s, ReadState ri)
            {
                var j = new JpegState(s, ri);
                await LoadImage(j);
            }

            public static async ValueTask InfoCore(JpegState j)
            {
                if (!await ParseHeader(j, ScanMode.Header))
                    throw new StbImageReadException(ErrorCode.Undefined);
            }

            public static ValueTask Info(BinReader s, out ReadState ri)
            {
                ri = new ReadState();
                var j = new JpegState(s, ri);
                return InfoCore(j);
            }

            public static bool Test(ReadOnlySpan<byte> header)
            {
                //var j = new Context(s, new ReadState());
                //return ParseHeader(j, ScanMode.Type);

                if (header.Length < HeaderSize)
                    return false;

                // TODO: improve test

                return header[0] == 0xff // 255
                    && header[1] == 0xd8; // 216
            }
        }
    }
}
