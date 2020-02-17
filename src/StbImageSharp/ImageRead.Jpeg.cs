using System;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static unsafe class Jpeg
        {
            #region Constants

            private static uint[] BMask =
            {
                0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535
            };

            private static int[] JBias =
            {
                0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047, -4095, -8191, -16383, -32767
            };

            private static byte[] DeZigZag =
            {
                0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40,
                48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29,
                22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54,
                47, 55, 62, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63
            };

            #endregion

            public delegate void IdctBlockKernel(byte* output, int out_stride, short* data);

            public delegate void YCbCrToRgbKernel(
                byte* output, byte* y, byte* pcb, byte* pcr, int count, int step);

            public delegate byte* ResamplerMethod(byte* a, byte* b, byte* c, int d, int e);

            private static readonly IdctBlockKernel _cached__idct_block = IdctBlock;
            private static readonly YCbCrToRgbKernel _cached__YCbCr_to_RGB_row = YCbCrToRGB;
            private static readonly ResamplerMethod _cached__resample_row_hv_2 = ResampleRowHV2;

            [StructLayout(LayoutKind.Sequential)]
            public struct ImageComponent
            {
                public int id;
                public int h, v;
                public int tq;
                public int hd, ha;
                public int dc_pred;

                public int x, y, w2, h2;
                public byte* data;
                public void* raw_data;
                public void* raw_coeff;
                public byte* linebuf;

                public short* coeff; // progressive only
                public int coeff_w, coeff_h; // number of 8x8 coefficient blocks
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Huffman
            {
                public const int FastLength = 1 << 9;

                public fixed byte fast[FastLength];
                public fixed ushort code[256];
                public fixed byte values[256];
                public fixed byte size[257];
                public fixed uint maxcode[18];
                public fixed int delta[17];
            }

            public class Context
            {
                public ReadContext s;
                public readonly Huffman[] huff_dc = new Huffman[4];
                public readonly Huffman[] huff_ac = new Huffman[4];
                public readonly ushort[][] dequant;

                public readonly short[][] fast_ac;

                // sizes for components, interleaved MCUs
                public int img_h_max, img_v_max;
                public int img_mcu_x, img_mcu_y;
                public int img_mcu_w, img_mcu_h;

                // definition of jpeg image component
                public ImageComponent[] components = new ImageComponent[4];

                public uint code_buffer; // jpeg entropy-coded buffer
                public int code_bits; // number of valid bits
                public byte marker; // marker seen while filling entropy buffer
                public int nomore; // flag if we saw a marker so must stop

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

                public Context(ReadContext context, ReadState readState)
                {
                    s = context;
                    ri = readState;

                    idct_block_kernel = _cached__idct_block;
                    YCbCr_to_RGB_kernel = _cached__YCbCr_to_RGB_row;
                    resample_row_hv_2_kernel = _cached__resample_row_hv_2;

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

            [StructLayout(LayoutKind.Sequential)]
            public struct ResampleData
            {
                public ResamplerMethod Resample;
                public byte* line0;
                public byte* line1;
                public int hs;
                public int vs;
                public int w_lores;
                public int ystep;
                public int ypos;
            }

            public static int BuildHuffman(ref Huffman h, int* count)
            {
                int i;
                int j;
                int k = 0;
                int code = 0;
                for (i = 0; i < 16; ++i)
                {
                    for (j = 0; j < count[i]; ++j)
                        h.size[k++] = (byte)(i + 1);
                }

                h.size[k] = 0;
                k = 0;
                for (j = 1; j <= 16; ++j)
                {
                    h.delta[j] = k - code;
                    if (h.size[k] == j)
                    {
                        while (h.size[k] == j)
                        {
                            h.code[k++] = (ushort)code++;
                        }

                        if ((code - 1) >= (1 << j))
                            return Error("bad code lengths");
                    }

                    h.maxcode[j] = (uint)(code << (16 - j));
                    code <<= 1;
                }

                h.maxcode[j] = 0xffffffff;
                for (i = 0; i < Huffman.FastLength; ++i)
                {
                    h.fast[i] = 255;
                }
                for (i = 0; i < k; ++i)
                {
                    int s = h.size[i];
                    if (s <= 9)
                    {
                        int c = h.code[i] << (9 - s);
                        int m = 1 << (9 - s);
                        for (j = 0; j < m; ++j)
                        {
                            h.fast[c + j] = (byte)i;
                        }
                    }
                }

                return 1;
            }

            public static void BuildFastAc(short[] fast_ac, ref Huffman h)
            {
                int i;
                for (i = 0; i < (1 << 9); ++i)
                {
                    byte fast = h.fast[i];
                    fast_ac[i] = 0;
                    if (fast < 255)
                    {
                        int rs = h.values[fast];
                        int run = (rs >> 4) & 15;
                        int magbits = rs & 15;
                        int len = h.size[fast];
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

            public static void GrowBufferUnsafe(Context j)
            {
                do
                {
                    int b = j.nomore != 0 ? 0 : j.s.ReadByte();
                    if (b == 0xff)
                    {
                        int c = j.s.ReadByte();
                        while (c == 0xff)
                            c = j.s.ReadByte();

                        if (c != 0)
                        {
                            j.marker = (byte)c;
                            j.nomore = 1;
                            return;
                        }
                    }

                    j.code_buffer |= (uint)(b << (24 - j.code_bits));
                    j.code_bits += 8;
                } while (j.code_bits <= 24);
            }

            public static int HuffmanDecode(Context j, ref Huffman h)
            {
                if (j.code_bits < 16)
                    GrowBufferUnsafe(j);

                int c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                int k = h.fast[c];
                if (k < 255)
                {
                    int s = h.size[k];
                    if (s > j.code_bits)
                        return -1;
                    j.code_buffer <<= s;
                    j.code_bits -= s;
                    return h.values[k];
                }

                uint tmp = j.code_buffer >> 16;
                for (k = 9 + 1; ; ++k)
                {
                    if (tmp < h.maxcode[k])
                        break;
                }

                if (k == 17)
                {
                    j.code_bits -= 16;
                    return -1;
                }

                if (k > j.code_bits)
                    return -1;
                c = (int)(((j.code_buffer >> (32 - k)) & BMask[k]) + h.delta[k]);
                j.code_bits -= k;
                j.code_buffer <<= k;
                return h.values[c];
            }

            public static int ExtendReceive(Context j, int n)
            {
                uint k;
                int sgn;
                if (j.code_bits < n)
                    GrowBufferUnsafe(j);
                sgn = (int)j.code_buffer >> 31;
                k = CRuntime.RotateBits(j.code_buffer, n);
                j.code_buffer = k & ~BMask[n];
                k &= BMask[n];
                j.code_bits -= n;
                return (int)(k + (JBias[n] & ~sgn));
            }

            public static int ReadBits(Context j, int n)
            {
                uint k;
                if (j.code_bits < n)
                    GrowBufferUnsafe(j);
                k = CRuntime.RotateBits(j.code_buffer, n);
                j.code_buffer = k & ~BMask[n];
                k &= BMask[n];
                j.code_bits -= n;
                return (int)k;
            }

            public static int ReadBit(Context j)
            {
                uint k;
                if (j.code_bits < 1)
                    GrowBufferUnsafe(j);
                k = j.code_buffer;
                j.code_buffer <<= 1;
                --j.code_bits;
                return (int)(k & 0x80000000);
            }

            public static int DecodeBlock(
                Context j, short* data, ref Huffman hdc, ref Huffman hac,
                short[] fac, int b, ushort[] dequant)
            {
                int diff;
                int dc;
                int k;
                int t;
                if (j.code_bits < 16)
                    GrowBufferUnsafe(j);
                t = HuffmanDecode(j, ref hdc);
                if (t < 0)
                    return Error("bad huffman code");
                CRuntime.MemSet(data, 0, 64 * sizeof(short));
                diff = t != 0 ? ExtendReceive(j, t) : 0;
                dc = j.components[b].dc_pred + diff;
                j.components[b].dc_pred = dc;
                data[0] = (short)(dc * dequant[0]);
                k = 1;
                do
                {
                    uint zig;
                    int c;
                    int r;
                    int s;
                    if (j.code_bits < 16)
                        GrowBufferUnsafe(j);
                    c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                    r = fac[c];
                    if (r != 0)
                    {
                        k += (r >> 4) & 15;
                        s = r & 15;
                        j.code_buffer <<= s;
                        j.code_bits -= s;
                        zig = DeZigZag[k++];
                        data[zig] = (short)((r >> 8) * dequant[zig]);
                    }
                    else
                    {
                        int rs = HuffmanDecode(j, ref hac);
                        if (rs < 0)
                            return Error("bad huffman code");
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
                            zig = DeZigZag[k++];
                            data[zig] = (short)(ExtendReceive(j, s) * dequant[zig]);
                        }
                    }
                } while (k < 64);

                return 1;
            }

            public static int DecodeBlockProgressiveDc(
                Context j, short* data, ref Huffman hdc, int b)
            {
                int diff;
                int dc;
                int t;
                if (j.spec_end != 0)
                    return Error("can't merge dc and ac");
                if (j.code_bits < 16)
                    GrowBufferUnsafe(j);
                if (j.succ_high == 0)
                {
                    CRuntime.MemSet(data, 0, 64 * sizeof(short));
                    t = HuffmanDecode(j, ref hdc);
                    diff = t != 0 ? ExtendReceive(j, t) : 0;
                    dc = j.components[b].dc_pred + diff;
                    j.components[b].dc_pred = dc;
                    data[0] = (short)(dc << j.succ_low);
                }
                else
                {
                    if (ReadBit(j) != 0)
                        data[0] += (short)(1 << j.succ_low);
                }

                return 1;
            }

            public static int DecodeBlockProggressiveAc(
                Context j, short* data, ref Huffman hac, short[] fac)
            {
                int k;
                if (j.spec_start == 0)
                    return Error("can't merge dc and ac");
                if (j.succ_high == 0)
                {
                    int shift = j.succ_low;
                    if (j.eob_run != 0)
                    {
                        --j.eob_run;
                        return 1;
                    }

                    k = j.spec_start;
                    do
                    {
                        uint zig;
                        int c;
                        int r;
                        int s;
                        if (j.code_bits < 16)
                            GrowBufferUnsafe(j);
                        c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                        r = fac[c];
                        if (r != 0)
                        {
                            k += (r >> 4) & 15;
                            s = r & 15;
                            j.code_buffer <<= s;
                            j.code_bits -= s;
                            zig = DeZigZag[k++];
                            data[zig] = (short)((r >> 8) << shift);
                        }
                        else
                        {
                            int rs = HuffmanDecode(j, ref hac);
                            if (rs < 0)
                                return Error("bad huffman code");
                            s = rs & 15;
                            r = rs >> 4;
                            if (s == 0)
                            {
                                if (r < 15)
                                {
                                    j.eob_run = 1 << r;
                                    if (r != 0)
                                        j.eob_run += ReadBits(j, r);
                                    --j.eob_run;
                                    break;
                                }

                                k += 16;
                            }
                            else
                            {
                                k += r;
                                zig = DeZigZag[k++];
                                data[zig] = (short)(ExtendReceive(j, s) << shift);
                            }
                        }
                    } while (k <= j.spec_end);
                }
                else
                {
                    short bit = (short)(1 << j.succ_low);
                    if (j.eob_run != 0)
                    {
                        --j.eob_run;
                        for (k = j.spec_start; k <= j.spec_end; ++k)
                        {
                            short* p = &data[DeZigZag[k]];
                            if (*p != 0)
                                if (ReadBit(j) != 0)
                                    if ((*p & bit) == 0)
                                    {
                                        if ((*p) > 0)
                                            *p += bit;
                                        else
                                            *p -= bit;
                                    }
                        }
                    }
                    else
                    {
                        k = j.spec_start;
                        do
                        {
                            int r;
                            int s;
                            int rs = HuffmanDecode(j, ref hac);
                            if (rs < 0)
                                return Error("bad huffman code");
                            s = rs & 15;
                            r = rs >> 4;
                            if (s == 0)
                            {
                                if (r < 15)
                                {
                                    j.eob_run = (1 << r) - 1;
                                    if (r != 0)
                                        j.eob_run += ReadBits(j, r);
                                    r = 64;
                                }
                                else
                                {
                                }
                            }
                            else
                            {
                                if (s != 1)
                                    return Error("bad huffman code");
                                if (ReadBit(j) != 0)
                                    s = bit;
                                else
                                    s = -bit;
                            }

                            while (k <= j.spec_end)
                            {
                                short* p = &data[DeZigZag[k++]];
                                if (*p != 0)
                                {
                                    if (ReadBit(j) != 0)
                                        if ((*p & bit) == 0)
                                        {
                                            if ((*p) > 0)
                                                *p += bit;
                                            else
                                                *p -= bit;
                                        }
                                }
                                else
                                {
                                    if (r == 0)
                                    {
                                        *p = (short)s;
                                        break;
                                    }

                                    --r;
                                }
                            }
                        } while (k <= j.spec_end);
                    }
                }

                return 1;
            }

            public static byte Clamp(int x)
            {
                if (((uint)x) > 255)
                {
                    if (x < 0)
                        return 0;
                    if (x > 255)
                        return 255;
                }
                return (byte)x;
            }

            public static void IdctBlock(byte* _out_, int out_stride, short* data)
            {
                int i;
                int* val = stackalloc int[64];
                int* v = val;
                byte* o;
                short* d = data;
                for (i = 0; i < 8; ++i, ++d, ++v)
                {
                    if ((d[8] == 0) && (d[16] == 0) && (d[24] == 0) && (d[32] == 0) &&
                          (d[40] == 0) &&
                         (d[48] == 0) && (d[56] == 0))
                    {
                        int dcterm = d[0] << 2;
                        v[0] =

                            v[8] =
                                v[16] = v[24] =
                                    v[32] = v[40] = v[48] = v[56] = dcterm;
                    }
                    else
                    {
                        int t0;
                        int t1;
                        int t2;
                        int t3;
                        int p1;
                        int p2;
                        int p3;
                        int p4;
                        int p5;
                        int x0;
                        int x1;
                        int x2;
                        int x3;

                        p2 = d[16];
                        p3 = d[48];
                        p1 = (p2 + p3) * ((int)(0.5411961f * 4096 + 0.5));
                        t2 = p1 + p3 * ((int)((-1.847759065f) * 4096 + 0.5));
                        t3 = p1 + p2 * ((int)(0.765366865f * 4096 + 0.5));
                        p2 = d[0];
                        p3 = d[32];
                        t0 = (p2 + p3) << 12;
                        t1 = (p2 - p3) << 12;
                        x0 = t0 + t3;
                        x3 = t0 - t3;
                        x1 = t1 + t2;
                        x2 = t1 - t2;
                        t0 = d[56];
                        t1 = d[40];
                        t2 = d[24];
                        t3 = d[8];
                        p3 = t0 + t2;
                        p4 = t1 + t3;
                        p1 = t0 + t3;
                        p2 = t1 + t2;
                        p5 = (p3 + p4) * ((int)(1.175875602f * 4096 + 0.5));
                        t0 = t0 * ((int)(0.298631336f * 4096 + 0.5));
                        t1 = t1 * ((int)(2.053119869f * 4096 + 0.5));
                        t2 = t2 * ((int)(3.072711026f * 4096 + 0.5));
                        t3 = t3 * ((int)(1.501321110f * 4096 + 0.5));
                        p1 = p5 + p1 * ((int)((-0.899976223f) * 4096 + 0.5));
                        p2 = p5 + p2 * ((int)((-2.562915447f) * 4096 + 0.5));
                        p3 = p3 * ((int)((-1.961570560f) * 4096 + 0.5));
                        p4 = p4 * ((int)((-0.390180644f) * 4096 + 0.5));
                        t3 += p1 + p4;
                        t2 += p2 + p3;
                        t1 += p2 + p4;
                        t0 += p1 + p3;
                        x0 += 512;
                        x1 += 512;
                        x2 += 512;
                        x3 += 512;

                        v[0] = (x0 + t3) >> 10;
                        v[8] = (x1 + t2) >> 10;
                        v[16] = (x2 + t1) >> 10;
                        v[24] = (x3 + t0) >> 10;
                        v[32] = (x3 - t0) >> 10;
                        v[40] = (x2 - t1) >> 10;
                        v[48] = (x1 - t2) >> 10;
                        v[56] = (x0 - t3) >> 10;
                    }
                }

                for (i = 0, v = val, o = _out_; i < 8; ++i, v += 8, o += out_stride)
                {
                    int t0;
                    int t1;
                    int t2;
                    int t3;
                    int p1;
                    int p2;
                    int p3;
                    int p4;
                    int p5;
                    int x0;
                    int x1;
                    int x2;
                    int x3;

                    p2 = v[2];
                    p3 = v[6];
                    p1 = (p2 + p3) * ((int)(0.5411961f * 4096 + 0.5));
                    t2 = p1 + p3 * ((int)((-1.847759065f) * 4096 + 0.5));
                    t3 = p1 + p2 * ((int)(0.765366865f * 4096 + 0.5));
                    p2 = v[0];
                    p3 = v[4];
                    t0 = (p2 + p3) << 12;
                    t1 = (p2 - p3) << 12;
                    x0 = t0 + t3;
                    x3 = t0 - t3;
                    x1 = t1 + t2;
                    x2 = t1 - t2;
                    t0 = v[7];
                    t1 = v[5];
                    t2 = v[3];
                    t3 = v[1];
                    p3 = t0 + t2;
                    p4 = t1 + t3;
                    p1 = t0 + t3;
                    p2 = t1 + t2;
                    p5 = (p3 + p4) * ((int)(1.175875602f * 4096 + 0.5));
                    t0 = t0 * ((int)(0.298631336f * 4096 + 0.5));
                    t1 = t1 * ((int)(2.053119869f * 4096 + 0.5));
                    t2 = t2 * ((int)(3.072711026f * 4096 + 0.5));
                    t3 = t3 * ((int)(1.501321110f * 4096 + 0.5));
                    p1 = p5 + p1 * ((int)((-0.899976223f) * 4096 + 0.5));
                    p2 = p5 + p2 * ((int)((-2.562915447f) * 4096 + 0.5));
                    p3 = p3 * ((int)((-1.961570560f) * 4096 + 0.5));
                    p4 = p4 * ((int)((-0.390180644f) * 4096 + 0.5));
                    t3 += p1 + p4;
                    t2 += p2 + p3;
                    t1 += p2 + p4;
                    t0 += p1 + p3;
                    x0 += 65536 + (128 << 17);
                    x1 += 65536 + (128 << 17);
                    x2 += 65536 + (128 << 17);
                    x3 += 65536 + (128 << 17);

                    o[0] = Clamp((x0 + t3) >> 17);
                    o[1] = Clamp((x1 + t2) >> 17);
                    o[2] = Clamp((x2 + t1) >> 17);
                    o[3] = Clamp((x3 + t0) >> 17);
                    o[4] = Clamp((x3 - t0) >> 17);
                    o[5] = Clamp((x2 - t1) >> 17);
                    o[6] = Clamp((x1 - t2) >> 17);
                    o[7] = Clamp((x0 - t3) >> 17);
                }
            }

            public static byte ReadMarker(Context j)
            {
                byte x;
                if (j.marker != 0xff)
                {
                    x = j.marker;
                    j.marker = 0xff;
                    return x;
                }

                x = j.s.ReadByte();
                if (x != 0xff)
                    return 0xff;

                while (x == 0xff)
                    x = j.s.ReadByte();

                return x;
            }

            public static void Reset(Context j)
            {
                j.code_bits = 0;
                j.code_buffer = 0;
                j.nomore = 0;
                for (int i = 0; i < j.components.Length; i++)
                    j.components[i].dc_pred = 0;
                j.marker = 0xff;
                j.todo = j.restart_interval != 0 ? j.restart_interval : 0x7fffffff;
                j.eob_run = 0;
            }

            public static bool ParseEntropyCodedData(Context z)
            {
                Reset(z);
                if (!z.progressive)
                {
                    if (z.scan_n == 1)
                    {
                        short* data = stackalloc short[64];
                        int n = z.order[0];
                        int w = (z.components[n].x + 7) >> 3;
                        int h = (z.components[n].y + 7) >> 3;
                        for (int j = 0; j < h; ++j)
                        {
                            for (int i = 0; i < w; ++i)
                            {
                                int ha = z.components[n].ha;
                                if (DecodeBlock(
                                        z, data, ref z.huff_dc[z.components[n].hd], ref z.huff_ac[ha],
                                        z.fast_ac[ha], n, z.dequant[z.components[n].tq]) == 0)
                                    return false;

                                z.idct_block_kernel(
                                    z.components[n].data + z.components[n].w2 * j * 8 + i * 8,
                                    z.components[n].w2, data);

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        GrowBufferUnsafe(z);
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
                        short* data = stackalloc short[64];
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
                                            if (
                                                DecodeBlock(z, data,
                                                    ref z.huff_dc[z.components[n].hd],
                                                    ref z.huff_ac[ha], z.fast_ac[ha], n,
                                                    z.dequant[z.components[n].tq]) == 0)
                                                return false;
                                            z.idct_block_kernel(z.components[n].data + z.components[n].w2 * y2 + x2,
                                                z.components[n].w2, data);
                                        }
                                    }
                                }

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        GrowBufferUnsafe(z);
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
                        int w = (z.components[n].x + 7) >> 3;
                        int h = (z.components[n].y + 7) >> 3;
                        for (int j = 0; j < h; ++j)
                        {
                            for (int i = 0; i < w; ++i)
                            {
                                short* data = z.components[n].coeff + 64 * (i + j * z.components[n].coeff_w);
                                if (z.spec_start == 0)
                                {
                                    if (DecodeBlockProgressiveDc(z, data,
                                            ref z.huff_dc[z.components[n].hd], n) == 0)
                                        return false;
                                }
                                else
                                {
                                    int ha = z.components[n].ha;
                                    if (DecodeBlockProggressiveAc(
                                        z, data, ref z.huff_ac[ha], z.fast_ac[ha]) == 0)
                                        return false;
                                }

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        GrowBufferUnsafe(z);
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
                                            short* data = z.components[n].coeff + 64 * (x2 + y2 * z.components[n].coeff_w);
                                            if (DecodeBlockProgressiveDc(
                                                z, data, ref z.huff_dc[z.components[n].hd], n) == 0)
                                                return false;
                                        }
                                    }
                                }

                                if (--z.todo <= 0)
                                {
                                    if (z.code_bits < 24)
                                        GrowBufferUnsafe(z);
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

            public static void Dequantize(short* data, ushort[] dequant)
            {
                int i;
                for (i = 0; i < 64; ++i)
                {
                    data[i] *= (short)dequant[i];
                }
            }

            public static void Finish(Context z)
            {
                if (!z.progressive)
                    return;

                int i;
                int j;
                for (int n = 0; n < z.ri.Components; ++n)
                {
                    int w = (z.components[n].x + 7) >> 3;
                    int h = (z.components[n].y + 7) >> 3;
                    for (j = 0; j < h; ++j)
                    {
                        for (i = 0; i < w; ++i)
                        {
                            short* data = z.components[n].coeff + 64 * (i + j * z.components[n].coeff_w);
                            Dequantize(data, z.dequant[z.components[n].tq]);
                            z.idct_block_kernel(z.components[n].data + z.components[n].w2 * j * 8 + i * 8,
                                z.components[n].w2, data);
                        }
                    }
                }
            }

            public static bool ProcessMarker(Context z, int m)
            {
                ReadContext s = z.s;
                int L;
                switch (m)
                {
                    case 0xff:
                        Error("expected marker");
                        return false;

                    case 0xDD:
                        if (s.ReadInt16BE() != 4)
                        {
                            Error("bad DRI len");
                            return false;
                        }
                        z.restart_interval = s.ReadInt16BE();
                        return true;

                    case 0xDB:
                        L = s.ReadInt16BE() - 2;
                        while (L > 0)
                        {
                            int q = s.ReadByte();
                            int p = q >> 4;
                            int sixteen = (p != 0) ? 1 : 0;
                            int t = q & 15;
                            int i;
                            if ((p != 0) && (p != 1))
                            {
                                Error("bad DQT type");
                                return false;
                            }
                            if (t > 3)
                            {
                                Error("bad DQT table");
                                return false;
                            }
                            for (i = 0; i < 64; ++i)
                            {
                                z.dequant[t][DeZigZag[i]] =
                                    (ushort)(sixteen != 0 ? s.ReadInt16BE() : s.ReadByte());
                            }
                            L -= sixteen != 0 ? 129 : 65;
                        }
                        return L == 0;

                    case 0xC4:
                        L = s.ReadInt16BE() - 2;
                        while (L > 0)
                        {
                            int* sizes = stackalloc int[16];
                            int i;
                            int n = 0;
                            int q = s.ReadByte();
                            int tc = q >> 4;
                            int th = q & 15;
                            if ((tc > 1) || (th > 3))
                            {
                                Error("bad DHT header");
                                return false;
                            }
                            for (i = 0; i < 16; ++i)
                            {
                                sizes[i] = s.ReadByte();
                                n += sizes[i];
                            }

                            Huffman[] huff;

                            L -= 17;
                            if (tc == 0)
                            {
                                if (BuildHuffman(ref z.huff_dc[th], sizes) == 0)
                                    return false;
                                huff = z.huff_dc;
                            }
                            else
                            {
                                if (BuildHuffman(ref z.huff_ac[th], sizes) == 0)
                                    return false;
                                huff = z.huff_ac;
                            }

                            for (i = 0; i < n; ++i)
                                huff[th].values[i] = s.ReadByte();

                            if (tc != 0)
                                BuildFastAc(z.fast_ac[th], ref z.huff_ac[th]);
                            L -= n;
                        }
                        return L == 0;
                }

                if (((m >= 0xE0) && (m <= 0xEF)) || (m == 0xFE))
                {
                    L = s.ReadInt16BE();
                    if (L < 2)
                    {
                        if (m == 0xFE)
                            Error("bad COM len");
                        else
                            Error("bad APP len");
                        return false;
                    }

                    L -= 2;
                    if ((m == 0xE0) && (L >= 5))
                    {
                        Span<byte> tag = stackalloc byte[5];
                        tag[0] = (byte)'J';
                        tag[1] = (byte)'F';
                        tag[2] = (byte)'I';
                        tag[3] = (byte)'F';
                        tag[4] = (byte)'\0';

                        int ok = 1;
                        int i;
                        for (i = 0; i < 5; ++i)
                        {
                            if (s.ReadByte() != tag[i])
                                ok = 0;
                        }

                        L -= 5;
                        if (ok != 0)
                            z.jfif = 1;
                    }
                    else if ((m == 0xEE) && (L >= 12))
                    {
                        Span<byte> tag = stackalloc byte[6];
                        tag[0] = (byte)'A';
                        tag[1] = (byte)'d';
                        tag[2] = (byte)'o';
                        tag[3] = (byte)'b';
                        tag[4] = (byte)'e';
                        tag[5] = (byte)'\0';

                        bool ok = true;
                        for (int i = 0; i < 6; ++i)
                        {
                            if (s.ReadByte() != tag[i])
                            {
                                ok = false;
                                break;
                            }
                        }

                        L -= 6;
                        if (ok)
                        {
                            s.ReadByte();
                            s.ReadInt16BE();
                            s.ReadInt16BE();
                            z.app14_color_transform = s.ReadByte();
                            L -= 6;
                        }
                    }

                    s.Skip(L);
                    return true;
                }

                Error("unknown marker");
                return false;
            }

            public static bool ProcessScanHeader(Context z)
            {
                var s = z.s;
                int Ls = s.ReadInt16BE();
                z.scan_n = s.ReadByte();
                if ((z.scan_n < 1) || (z.scan_n > 4) || (z.scan_n > z.ri.Components))
                {
                    Error("bad SOS component count");
                    return false;
                }
                if (Ls != 6 + 2 * z.scan_n)
                {
                    Error("bad SOS len");
                    return false;
                }

                for (int i = 0; i < z.scan_n; ++i)
                {
                    int id = s.ReadByte();
                    int which;
                    int q = s.ReadByte();
                    for (which = 0; which < z.ri.Components; ++which)
                    {
                        if (z.components[which].id == id)
                            break;
                    }

                    if (which == z.ri.Components)
                        return false;
                    z.components[which].hd = q >> 4;
                    if (z.components[which].hd > 3)
                    {
                        Error("bad DC huff");
                        return false;
                    }

                    z.components[which].ha = q & 15;
                    if (z.components[which].ha > 3)
                    {
                        Error("bad AC huff");
                        return false;
                    }
                    z.order[i] = which;
                }

                {
                    int aa;
                    z.spec_start = s.ReadByte();
                    z.spec_end = s.ReadByte();
                    aa = s.ReadByte();
                    z.succ_high = aa >> 4;
                    z.succ_low = aa & 15;
                    if (z.progressive)
                    {
                        if ((z.spec_start > 63) || (z.spec_end > 63) || (z.spec_start > z.spec_end) ||
                            (z.succ_high > 13) || (z.succ_low > 13))
                        {
                            Error("bad SOS");
                            return false;
                        }
                    }
                    else
                    {
                        if (z.spec_start != 0)
                        {
                            Error("bad SOS");
                            return false;
                        }
                        if ((z.succ_high != 0) || (z.succ_low != 0))
                        {
                            Error("bad SOS");
                            return false;
                        }
                        z.spec_end = 63;
                    }
                }

                return true;
            }

            public static void FreeComponents(Context z, int ncomp)
            {
                for (int i = 0; i < ncomp; ++i)
                {
                    if (z.components[i].raw_data != null)
                    {
                        CRuntime.Free(z.components[i].raw_data);
                        z.components[i].raw_data = null;
                        z.components[i].data = null;
                    }

                    if (z.components[i].raw_coeff != null)
                    {
                        CRuntime.Free(z.components[i].raw_coeff);
                        z.components[i].raw_coeff = null;
                        z.components[i].coeff = null;
                    }

                    if (z.components[i].linebuf != null)
                    {
                        CRuntime.Free(z.components[i].linebuf);
                        z.components[i].linebuf = null;
                    }
                }
            }

            public static bool ProcessFrameHeader(Context z, ScanMode scan)
            {
                ReadContext s = z.s;
                int Lf;
                int p;
                int i;
                int q;
                int h_max = 1;
                int v_max = 1;
                Lf = s.ReadInt16BE();
                if (Lf < 11)
                {
                    Error("bad SOF len");
                    return false;
                }
                p = s.ReadByte();
                if (p != 8)
                {
                    Error("only 8-bit");
                    return false;
                }
                z.ri.Height = s.ReadInt16BE();
                if (z.ri.Height == 0)
                {
                    Error("no header height");
                    return false;
                }
                z.ri.Width = s.ReadInt16BE();
                if (z.ri.Width == 0)
                {
                    Error("0 width");
                    return false;
                }
                z.ri.Components = s.ReadByte();
                if ((z.ri.Components != 1) &&
                    (z.ri.Components != 3) &&
                    (z.ri.Components != 4))
                {
                    Error("bad component count");
                    return false;
                }
                for (i = 0; i < z.ri.Components; ++i)
                {
                    z.components[i].data = null;
                    z.components[i].linebuf = null;
                }

                if (Lf != 8 + 3 * z.ri.Components)
                {
                    Error("bad SOF len");
                    return false;
                }
                z.rgb = 0;

                byte* rgb = stackalloc byte[3];
                rgb[0] = (byte)'R';
                rgb[1] = (byte)'G';
                rgb[2] = (byte)'B';
                for (i = 0; i < z.ri.Components; ++i)
                {
                    z.components[i].id = s.ReadByte();
                    if ((z.ri.Components == 3) && (z.components[i].id == rgb[i]))
                        ++z.rgb;
                    q = s.ReadByte();
                    z.components[i].h = q >> 4;
                    if ((z.components[i].h == 0) || (z.components[i].h > 4))
                    {
                        Error("bad H");
                        return false;
                    }
                    z.components[i].v = q & 15;
                    if ((z.components[i].v == 0) || (z.components[i].v > 4))
                    {
                        Error("bad V");
                        return false;
                    }
                    z.components[i].tq = s.ReadByte();
                    if (z.components[i].tq > 3)
                    {
                        Error("bad TQ");
                        return false;
                    }
                }

                if (scan != ScanMode.Load)
                    return true;

                if (AreValidMad3Sizes(z.ri.Width, z.ri.Height, z.ri.Components, 0) == 0)
                {
                    Error("too large");
                    return false;
                }

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
                    z.components[i].coeff = null;
                    z.components[i].raw_coeff = null;
                    z.components[i].linebuf = null;
                    z.components[i].raw_data = MAllocMad2(
                        z.components[i].w2, z.components[i].h2, 15);

                    if (z.components[i].raw_data == null)
                    {
                        FreeComponents(z, i + 1);
                        Error("outofmem");
                        return false;
                    }

                    z.components[i].data = (byte*)(((long)z.components[i].raw_data + 15) & ~15);
                    if (z.progressive)
                    {
                        z.components[i].coeff_w = z.components[i].w2 / 8;
                        z.components[i].coeff_h = z.components[i].h2 / 8;
                        z.components[i].raw_coeff = MAllocMad3(
                            z.components[i].w2, z.components[i].h2, 2, 15);

                        if (z.components[i].raw_coeff == null)
                        {
                            FreeComponents(z, i + 1);
                            Error("outofmem");
                            return false;
                        }
                        z.components[i].coeff = (short*)(((long)z.components[i].raw_coeff + 15) & ~15);
                    }
                }

                return true;
            }

            public static bool ParseHeader(Context z, ScanMode scan)
            {
                z.jfif = 0;
                z.app14_color_transform = -1;
                z.marker = 0xff;

                int m = ReadMarker(z);
                if (!(m == 0xd8))
                {
                    if (scan == ScanMode.Load)
                        Error("no SOI");
                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                m = ReadMarker(z);
                while (!((m == 0xc0) || (m == 0xc1) || (m == 0xc2)))
                {
                    if (!ProcessMarker(z, m))
                        return false;

                    m = ReadMarker(z);
                    while (m == 0xff)
                    {
                        if (z.s.IsAtEndOfStream())
                        {
                            Error("no SOF");
                            return false;
                        }
                        m = ReadMarker(z);
                    }
                }

                z.progressive = m == 0xc2;
                if (!ProcessFrameHeader(z, scan))
                    return false;
                return true;
            }

            public static bool Load(Context j)
            {
                for (int i = 0; i < 4; i++)
                {
                    j.components[i].raw_data = null;
                    j.components[i].raw_coeff = null;
                }

                j.restart_interval = 0;
                if (!ParseHeader(j, (int)ScanMode.Load))
                    return false;

                var s = j.s;
                int m = ReadMarker(j);
                while (!(m == 0xd9))
                {
                    if (m == 0xda)
                    {
                        if (!ProcessScanHeader(j))
                            return false;
                        if (!ParseEntropyCodedData(j))
                            return false;

                        if (j.marker == 0xff)
                        {
                            while (!s.IsAtEndOfStream())
                            {
                                int x = s.ReadByte();
                                if (x == 255)
                                {
                                    j.marker = s.ReadByte();
                                    break;
                                }
                            }
                        }
                    }
                    else if (m == 0xdc)
                    {
                        int Ld = s.ReadInt16BE();
                        uint NL = (uint)s.ReadInt16BE();
                        if (Ld != 4)
                            Error("bad DNL len");
                        if (NL != j.ri.Height)
                            Error("bad DNL height");
                    }
                    else
                    {
                        if (!ProcessMarker(j, m))
                            return false;
                    }

                    m = ReadMarker(j);
                }

                if (j.progressive)
                    Finish(j);

                j.ri.OutComponents = j.ri.RequestedComponents ?? (j.ri.Components >= 3 ? 3 : 1);
                j.is_rgb = j.ri.Components == 3 && (j.rgb == 3 || (j.app14_color_transform == 0 && j.jfif == 0));
                j.decode_n = (j.ri.Components == 3 && j.ri.OutComponents < 3 && !j.is_rgb) ? 1 : j.ri.Components;

                return true;
            }

            public static byte* ResampleRow1(byte* _out_, byte* in_near, byte* in_far, int w, int hs)
            {
                return in_near;
            }

            public static byte* ResampleRowV2(byte* _out_, byte* in_near, byte* in_far, int w, int hs)
            {
                int i;
                for (i = 0; i < w; ++i)
                {
                    _out_[i] = (byte)((3 * in_near[i] + in_far[i] + 2) >> 2);
                }

                return _out_;
            }

            public static byte* ResampleRowH2(byte* _out_, byte* in_near, byte* in_far, int w, int hs)
            {
                int i;
                byte* input = in_near;
                if (w == 1)
                {
                    _out_[0] = _out_[1] = input[0];
                    return _out_;
                }

                _out_[0] = input[0];
                _out_[1] = (byte)((input[0] * 3 + input[1] + 2) >> 2);
                for (i = 1; i < (w - 1); ++i)
                {
                    int n = 3 * input[i] + 2;
                    _out_[i * 2 + 0] = (byte)((n + input[i - 1]) >> 2);
                    _out_[i * 2 + 1] = (byte)((n + input[i + 1]) >> 2);
                }

                _out_[i * 2 + 0] = (byte)((input[w - 2] * 3 + input[w - 1] + 2) >> 2);
                _out_[i * 2 + 1] = input[w - 1];
                return _out_;
            }

            public static byte* ResampleRowHV2(byte* _out_, byte* in_near, byte* in_far, int w, int hs)
            {
                int i;
                int t0;
                int t1;
                if (w == 1)
                {
                    _out_[0] = _out_[1] = (byte)((3 * in_near[0] + in_far[0] + 2) >> 2);
                    return _out_;
                }

                t1 = 3 * in_near[0] + in_far[0];
                _out_[0] = (byte)((t1 + 2) >> 2);
                for (i = 1; i < w; ++i)
                {
                    t0 = t1;
                    t1 = 3 * in_near[i] + in_far[i];
                    _out_[i * 2 - 1] = (byte)((3 * t0 + t1 + 8) >> 4);
                    _out_[i * 2] = (byte)((3 * t1 + t0 + 8) >> 4);
                }

                _out_[w * 2 - 1] = (byte)((t1 + 2) >> 2);
                return _out_;
            }

            public static byte* ResampleRowGeneric(byte* _out_, byte* in_near, byte* in_far, int w, int hs)
            {
                int i;
                int j;
                for (i = 0; i < w; ++i)
                {
                    for (j = 0; j < hs; ++j)
                    {
                        _out_[i * hs + j] = in_near[i];
                    }
                }

                return _out_;
            }

            public static void YCbCrToRGB(byte* _out_, byte* y, byte* pcb, byte* pcr, int count, int step)
            {
                for (int i = 0; i < count; ++i)
                {
                    int y_fixed = (y[i] << 20) + (1 << 19);
                    int cr = pcr[i] - 128;
                    int cb = pcb[i] - 128;

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

                    _out_[0] = (byte)r;
                    _out_[1] = (byte)g;
                    _out_[2] = (byte)b;
                    _out_[3] = 255;
                    _out_ += step;
                }
            }

            public static void Cleanup(Context j)
            {
                FreeComponents(j, j.ri.Components);
            }

            public static byte Blinn8x8(byte x, byte y)
            {
                uint t = (uint)(x * y + 128);
                return (byte)((t + (t >> 8)) >> 8);
            }

            public static IMemoryResult LoadImage(Context z)
            {
                if (z.ri.RequestedComponents < 0 || z.ri.RequestedComponents > 4)
                {
                    Error("bad req_comp");
                    return null;
                }

                if (!Load(z))
                {
                    Cleanup(z);
                    return null;
                }

                int k;
                uint i;
                byte* output;
                byte** coutput = stackalloc byte*[4];
                var res_comp = new ResampleData[4];

                for (k = 0; k < z.decode_n; k++)
                {
                    z.components[k].linebuf = (byte*)CRuntime.MAlloc(z.ri.Width + 3);
                    if (z.components[k].linebuf == null)
                    {
                        Cleanup(z);
                        Error("outofmem");
                        return null;
                    }

                    ref ResampleData r = ref res_comp[k];
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

                output = (byte*)MAllocMad3(z.ri.OutComponents, z.ri.Width, z.ri.Height, 1);
                if (output == null)
                {
                    Cleanup(z);
                    Error("outofmem");
                    return null;
                }

                for (int j = 0; j < z.ri.Height; ++j)
                {
                    byte* _out_ = output + z.ri.OutComponents * z.ri.Width * j;
                    for (k = 0; k < z.decode_n; ++k)
                    {
                        ref ResampleData r = ref res_comp[k];
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
                                r.line1 += z.components[k].w2;
                        }
                    }

                    if (z.ri.OutComponents >= 3)
                    {
                        byte* y = coutput[0];
                        if (z.ri.Components == 3)
                        {
                            if (z.is_rgb)
                            {
                                for (i = 0; i < z.ri.Width; ++i)
                                {
                                    _out_[0] = y[i];
                                    _out_[1] = coutput[1][i];
                                    _out_[2] = coutput[2][i];
                                    _out_[3] = 255;
                                    _out_ += z.ri.OutComponents;
                                }
                            }
                            else
                            {
                                z.YCbCr_to_RGB_kernel(_out_, y, coutput[1], coutput[2], z.ri.Width, z.ri.OutComponents);
                            }
                        }
                        else if (z.ri.Components == 4)
                        {
                            if (z.app14_color_transform == 0)
                            {
                                for (i = 0; i < z.ri.Width; ++i)
                                {
                                    byte m = coutput[3][i];
                                    _out_[0] = Blinn8x8(coutput[0][i], m);
                                    _out_[1] = Blinn8x8(coutput[1][i], m);
                                    _out_[2] = Blinn8x8(coutput[2][i], m);
                                    _out_[3] = 255;
                                    _out_ += z.ri.OutComponents;
                                }
                            }
                            else if (z.app14_color_transform == 2)
                            {
                                z.YCbCr_to_RGB_kernel(_out_, y, coutput[1], coutput[2], z.ri.Width, z.ri.OutComponents);
                                for (i = 0; i < z.ri.Width; ++i)
                                {
                                    byte m = coutput[3][i];
                                    _out_[0] = Blinn8x8((byte)(255 - _out_[0]), m);
                                    _out_[1] = Blinn8x8((byte)(255 - _out_[1]), m);
                                    _out_[2] = Blinn8x8((byte)(255 - _out_[2]), m);
                                    _out_ += z.ri.OutComponents;
                                }
                            }
                            else
                            {
                                z.YCbCr_to_RGB_kernel(_out_, y, coutput[1], coutput[2], z.ri.Width, z.ri.OutComponents);
                            }
                        }
                        else
                            for (i = 0; i < z.ri.Width; ++i)
                            {
                                _out_[0] = _out_[1] = _out_[2] = y[i];
                                _out_[3] = 255;
                                _out_ += z.ri.OutComponents;
                            }
                    }
                    else
                    {
                        i = 0;
                        if (z.is_rgb)
                        {
                            if (z.ri.OutComponents == 1)
                            {
                                for (; i < z.ri.Width; ++i)
                                    *_out_++ = ComputeY8(coutput[0][i], coutput[1][i], coutput[2][i]);
                            }
                            else
                            {
                                for (; i < z.ri.Width; ++i)
                                {
                                    _out_[0] = ComputeY8(coutput[0][i], coutput[1][i], coutput[2][i]);
                                    _out_[1] = 255;
                                    _out_ += 2;
                                }
                            }
                        }
                        else if ((z.ri.Components == 4) && (z.app14_color_transform == 0))
                        {
                            for (; i < z.ri.Width; ++i)
                            {
                                byte m = coutput[3][i];
                                byte r = Blinn8x8(coutput[0][i], m);
                                byte g = Blinn8x8(coutput[1][i], m);
                                byte b = Blinn8x8(coutput[2][i], m);
                                _out_[0] = ComputeY8(r, g, b);
                                _out_[1] = 255;
                                _out_ += z.ri.OutComponents;
                            }
                        }
                        else if ((z.ri.Components == 4) && (z.app14_color_transform == 2))
                        {
                            for (; i < z.ri.Width; ++i)
                            {
                                _out_[0] = Blinn8x8((byte)(255 - coutput[0][i]), coutput[3][i]);
                                _out_[1] = 255;
                                _out_ += z.ri.OutComponents;
                            }
                        }
                        else
                        {
                            byte* y = coutput[0];
                            i = 0;
                            if (z.ri.OutComponents == 1)
                            {
                                for (; i < z.ri.Width; ++i)
                                    _out_[i] = y[i];
                            }
                            else
                            {
                                for (; i < z.ri.Width; ++i)
                                {
                                    *_out_++ = y[i];
                                    *_out_++ = 255;
                                }
                            }
                        }
                    }
                }

                Cleanup(z);

                return new HGlobalMemoryResult(output, z.ri.OutComponents * z.ri.Width * z.ri.Height);
            }

            public static IMemoryResult LoadImage(ReadContext s, ref ReadState ri)
            {
                var j = new Context(s, ri);
                var result = LoadImage(j);
                ri = j.ri;
                return result;
            }

            public static bool Test(ReadContext s)
            {
                var j = new Context(s, new ReadState());
                bool r = ParseHeader(j, ScanMode.Type);
                s.Rewind();
                return r;
            }

            public static bool InfoCore(Context j)
            {
                if (!ParseHeader(j, ScanMode.Header))
                {
                    j.s.Rewind();
                    return false;
                }
                return true;
            }

            public static bool Info(ReadContext s, out ReadState ri)
            {
                var j = new Context(s, new ReadState());
                var result = InfoCore(j);
                ri = j.ri;
                return result;
            }
        }
    }
}
