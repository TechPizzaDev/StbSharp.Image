﻿using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

            private static ReadOnlySpan<byte> RGB_Sequence => new byte[]
            {
                (byte)'R',
                (byte)'G',
                (byte)'B',
            };

            private static ReadOnlySpan<byte> JfifTag => new byte[] {
                (byte)'J',
                (byte)'F',
                (byte)'I',
                (byte)'F',
                (byte)'\0'
            };

            private static ReadOnlySpan<byte> AdobeTag => new byte[] {
                (byte)'A',
                (byte)'d',
                (byte)'o',
                (byte)'b',
                (byte)'e',
                (byte)'\0'
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
                Span<byte> dst, int dstStride, ReadOnlySpan<short> data);

            public delegate void YCbCrToRgbKernel(
                Span<byte> dst, Span<byte> y, Span<byte> pcb, Span<byte> pcr);

            public delegate Memory<byte> ResamplerMethod(
                Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs);

            [StructLayout(LayoutKind.Sequential)]
            public struct ImageComponent
            {
                public int id;
                public int h, v;
                public int tq;
                public int hd, ha;
                public int dc_pred;

                public int x, y, w2, h2;
                public byte[]? raw_data;
                public Memory<byte> data;

                public byte[]? raw_linebuf;
                public Memory<byte> linebuf;

                public byte[]? raw_coeff;
                public Memory<byte> coeff; // progressive only
                public int coeff_w, coeff_h; // number of 8x8 coefficient blocks
            }

            public class Huffman : IDisposable
            {
                public const int FastLength = 512;
                public const int CodeLength = 256;
                public const int ValuesLength = 256;
                public const int SizeLength = 257;
                public const int MaxcodeLength = 18;
                public const int DeltaLength = 17;

                private ArrayPool<byte> _pool;
                private byte[]? _buffer;
                private bool _isDisposed;

                public Memory<byte> MFast { get; private set; }
                public Memory<byte> MCode { get; private set; }
                public Memory<byte> MValues { get; private set; }
                public Memory<byte> MSize { get; private set; }
                public Memory<byte> MMaxcode { get; private set; }
                public Memory<byte> MDelta { get; private set; }

                public Span<byte> Fast => MFast.Span;
                
                [CLSCompliant(false)]
                public Span<ushort> Code => MemoryMarshal.Cast<byte, ushort>(MCode.Span);

                public Span<byte> Values => MValues.Span;
                public Span<byte> Size => MSize.Span;

                [CLSCompliant(false)]
                public Span<uint> Maxcode => MemoryMarshal.Cast<byte, uint>(MMaxcode.Span);

                public Span<int> Delta => MemoryMarshal.Cast<byte, int>(MDelta.Span);

                public Huffman(ArrayPool<byte> pool)
                {
                    _pool = pool ?? throw new ArgumentNullException(nameof(pool));

                    const int Size =
                        FastLength * sizeof(byte) +
                        CodeLength * sizeof(ushort) +
                        ValuesLength * sizeof(byte) +
                        SizeLength * sizeof(byte) +
                        MaxcodeLength * sizeof(uint) +
                        DeltaLength * sizeof(int);

                    _buffer = _pool.Rent(Size);

                    var m = _buffer.AsMemory(0, Size);
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

                protected virtual void Dispose(bool disposing)
                {
                    if (!_isDisposed)
                    {
                        MFast = default;
                        MCode = default;
                        MValues = default;
                        MSize = default;
                        MMaxcode = default;
                        MDelta = default;

                        if (_buffer != null)
                        {
                            _pool.Return(_buffer);
                            _buffer = null;
                        }

                        _isDisposed = true;
                    }
                }

                public void Dispose()
                {
                    Dispose(disposing: true);
                    GC.SuppressFinalize(this);
                }
            }

            public class JpegState : IDisposable
            {
                public const int CompCount = 4;

                private bool _isDisposed;

                public BinReader Reader { get; }
                public ReadState State { get; }
                public ArrayPool<byte> BytePool { get; }

                public readonly Huffman[] huff_dc = new Huffman[CompCount];
                public readonly Huffman[] huff_ac = new Huffman[CompCount];
                public readonly short[][] fast_ac = new short[CompCount][];

                [CLSCompliant(false)]
                public readonly ushort[][] dequant = new ushort[CompCount][];

                // sizes for components, interleaved MCUs
                public int img_h_max, img_v_max;
                public int img_mcu_x, img_mcu_y;
                public int img_mcu_w, img_mcu_h;

                // definition of jpeg image component
                public ImageComponent[] components = new ImageComponent[CompCount];

                [CLSCompliant(false)]
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
                public int decode_n;
                public bool is_rgb;
                public int restart_interval;
                public int todo;
                public int[] order = new int[CompCount];

                public IdctBlockKernel idct_block_kernel;
                public YCbCrToRgbKernel YCbCr_to_RGB_kernel;
                public ResamplerMethod resample_row_hv_2_kernel;

                public JpegState(BinReader reader, ReadState readState, ArrayPool<byte>? arrayPool)
                {
                    Reader = reader ?? throw new ArgumentNullException(nameof(reader));
                    State = readState ?? throw new ArgumentNullException(nameof(readState));
                    BytePool = arrayPool ?? ArrayPool<byte>.Shared;

                    idct_block_kernel = IdctBlock;
                    YCbCr_to_RGB_kernel = YCbCrToRGB;
                    resample_row_hv_2_kernel = ResampleRowHV2;

                    for (int i = 0; i < CompCount; i++)
                    {
                        huff_ac[i] = new Huffman(BytePool);
                        huff_dc[i] = new Huffman(BytePool);
                    }

                    for (var i = 0; i < components.Length; ++i)
                        components[i] = new ImageComponent();

                    for (var i = 0; i < fast_ac.Length; ++i)
                        fast_ac[i] = new short[Huffman.FastLength];

                    for (var i = 0; i < dequant.Length; ++i)
                        dequant[i] = new ushort[64];
                }

                protected virtual void Dispose(bool disposing)
                {
                    if (!_isDisposed)
                    {
                        if (disposing)
                        {
                            for (int i = 0; i < CompCount; i++)
                            {
                                huff_ac[i]?.Dispose();
                                huff_ac[i] = null!;

                                huff_dc[i]?.Dispose();
                                huff_dc[i] = null!;
                            }
                        }

                        _isDisposed = true;
                    }
                }

                public void Dispose()
                {
                    Dispose(disposing: true);
                    GC.SuppressFinalize(this);
                }
            }

            public struct ResampleData
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

            public static bool IsRestart(byte x)
            {
                return x >= 0xd0 && x <= 0xd7;
            }

            public static void AssertIsRestart(byte x)
            {
                if (!IsRestart(x))
                    throw new StbImageReadException(ErrorCode.NoResetMarker);
            }

            public static void BuildHuffman(Huffman h, Span<int> count)
            {
                Debug.Assert(h != null);
                var Size = h.Size;

                int i;
                int j;
                int k = 0;
                for (i = 0; i < 16; ++i)
                {
                    for (j = 0; j < count[i]; ++j)
                        Size[k++] = (byte)(i + 1);
                }

                var Delta = h.Delta;
                var Code = h.Code;

                int code = 0;
                Size[k] = 0;
                k = 0;
                for (j = 1; j <= 16; ++j)
                {
                    Delta[j] = k - code;
                    if (Size[k] == j)
                    {
                        while (Size[k] == j)
                            Code[k++] = (ushort)code++;

                        if ((code - 1) >= (1 << j))
                            throw new StbImageReadException(ErrorCode.BadCodeLengths);
                    }

                    h.Maxcode[j] = (uint)(code << (16 - j));
                    code <<= 1;
                }

                h.Maxcode[j] = 0xffffffff;

                var Fast = h.Fast;
                Fast.Fill(255);

                for (i = 0; i < k; ++i)
                {
                    int s = Size[i];
                    if (s <= 9)
                    {
                        int c = Code[i] << (9 - s);
                        int m = 1 << (9 - s);
                        for (j = 0; j < m; ++j)
                            Fast[c + j] = (byte)i;
                    }
                }
            }

            public static void BuildFastAc(Span<short> fastAc, Huffman h)
            {
                Debug.Assert(h != null);
                var Fast = h.Fast;

                fastAc.Clear();
                for (int i = 0; i < Fast.Length; ++i)
                {
                    byte fast = Fast[i];
                    if (fast < 255)
                    {
                        int rs = h.Values[fast];
                        int run = (rs >> 4) & 15;
                        int magbits = rs & 15;
                        int len = h.Size[fast];
                        if ((magbits != 0) && (len + magbits <= 9))
                        {
                            int k = ((i << len) & (Huffman.FastLength - 1)) >> (9 - magbits);
                            int m = 1 << (magbits - 1);
                            if (k < m)
                                k += (int)((~0U << magbits) + 1);
                            if ((k >= (-128)) && (k <= 127))
                                fastAc[i] = (short)((k << 8) + (run << 4) + len + magbits);
                        }
                    }
                }
            }

            public static void GrowBufferUnsafe(JpegState j)
            {
                Debug.Assert(j != null);

                if (j.nomore)
                    return;

                do
                {
                    int b = j.Reader.ReadByte();
                    if (b == 0xff)
                    {
                        int c = j.Reader.ReadByte();
                        while (c == 0xff)
                            c = j.Reader.ReadByte();

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

            // TODO: pull up Span gets and pass spans instead of Huffman
            public static int HuffmanDecode(JpegState j, Huffman h)
            {
                Debug.Assert(j != null);
                Debug.Assert(h != null);

                if (j.code_bits < 16)
                    GrowBufferUnsafe(j);

                var Fast = h.Fast;
                int c = (int)((j.code_buffer >> (32 - 9)) & (Fast.Length - 1));
                int k = Fast[c];
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
                var Maxcode = h.Maxcode;
                for (k = 9 + 1; ; ++k)
                {
                    if (tmp < Maxcode[k])
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

            public static int ExtendReceive(JpegState j, int n)
            {
                Debug.Assert(j != null);

                if (j.code_bits < n)
                    GrowBufferUnsafe(j);

                int sgn = (int)j.code_buffer >> 31;
                uint k = MathHelper.RotateBits(j.code_buffer, n);
                uint mask = BMask[n];
                j.code_buffer = k & ~mask;
                k &= mask;
                j.code_bits -= n;
                return (int)(k + (JBias[n] & ~sgn));
            }

            public static int ReadBits(JpegState j, int n)
            {
                Debug.Assert(j != null);

                if (j.code_bits < n)
                    GrowBufferUnsafe(j);

                uint k = MathHelper.RotateBits(j.code_buffer, n);
                uint mask = BMask[n];
                j.code_buffer = k & ~mask;
                k &= mask;
                j.code_bits -= n;
                return (int)k;
            }

            public static bool ReadBit(JpegState j)
            {
                Debug.Assert(j != null);

                if (j.code_bits < 1)
                    GrowBufferUnsafe(j);

                uint k = j.code_buffer;
                j.code_buffer <<= 1;
                j.code_bits--;
                return (k & 0x80000000) != 0;
            }

            [CLSCompliant(false)]
            public static void DecodeBlock(
                    JpegState j, Span<short> data, Huffman hdc, Huffman hac,
                    ReadOnlySpan<short> fac, int b, ReadOnlySpan<ushort> dequant)
            {
                Debug.Assert(j != null);

                if (j.code_bits < 16)
                    GrowBufferUnsafe(j);

                int t = HuffmanDecode(j, hdc);
                if (t < 0)
                    throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                data.Clear();

                int diff = t != 0 ? ExtendReceive(j, t) : 0;
                int dc = j.components[b].dc_pred + diff;
                j.components[b].dc_pred = dc;
                data[0] = (short)(dc * dequant[0]);

                var deZigZag = DeZigZag;
                int k = 1;
                do
                {
                    if (j.code_bits < 16)
                        GrowBufferUnsafe(j);

                    int c = (int)((j.code_buffer >> (32 - 9)) & (Huffman.FastLength - 1));
                    int r = fac[c];
                    int s;
                    if (r != 0)
                    {
                        k += (r >> 4) & 15;
                        s = r & 15;
                        j.code_buffer <<= s;
                        j.code_bits -= s;
                        var zig = deZigZag[k++];
                        data[zig] = (short)((r >> 8) * dequant[zig]);
                    }
                    else
                    {
                        int rs = HuffmanDecode(j, hac);
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
                            var zig = deZigZag[k++];
                            var value = ExtendReceive(j, s);
                            data[zig] = (short)(value * dequant[zig]);
                        }
                    }
                } while (k < 64);
            }

            public static Span<short> ByteToInt16(Memory<byte> bytes)
            {
                return MemoryMarshal.Cast<byte, short>(bytes.Span);
            }

            public static void DecodeBlockProgressiveDc(
                JpegState j, Span<short> data, Huffman hdc, int b)
            {
                Debug.Assert(j != null);

                if (j.spec_end != 0)
                    throw new StbImageReadException(ErrorCode.CantMergeDcAndAc);

                if (j.code_bits < 16)
                    GrowBufferUnsafe(j);

                if (j.succ_high == 0)
                {
                    data.Clear();
                    int t = HuffmanDecode(j, hdc);
                    int diff = t != 0 ? ExtendReceive(j, t) : 0;
                    int dc = j.components[b].dc_pred + diff;
                    j.components[b].dc_pred = dc;
                    data[0] = (short)(dc << j.succ_low);
                }
                else
                {
                    if (ReadBit(j))
                        data[0] += (short)(1 << j.succ_low);
                }
            }

            public static void DecodeBlockProggressiveAc(
                JpegState j, Span<short> data, Huffman hac, short[] fac)
            {
                Debug.Assert(j != null);
                Debug.Assert(fac != null);

                if (j.spec_start == 0)
                    throw new StbImageReadException(ErrorCode.CantMergeDcAndAc);

                var deZigZag = DeZigZag;
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
                            GrowBufferUnsafe(j);

                        int c = (int)((j.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                        int r = fac[c];
                        int s;
                        if (r != 0)
                        {
                            k += (r >> 4) & 15;
                            s = r & 15;
                            j.code_buffer <<= s;
                            j.code_bits -= s;
                            var zig = deZigZag[k++];
                            data[zig] = (short)((r >> 8) << shift);
                        }
                        else
                        {
                            int rs = HuffmanDecode(j, hac);
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
                                        j.eob_run += ReadBits(j, r);
                                    --j.eob_run;
                                    break;
                                }

                                k += 16;
                            }
                            else
                            {
                                k += r;
                                var zig = deZigZag[k++];
                                int extended = ExtendReceive(j, s);
                                data[zig] = (short)(extended << shift);
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

                        int offset = j.spec_start;
                        while (offset <= j.spec_end)
                        {
                            ref short p = ref data[deZigZag[offset++]];
                            if (p != 0)
                            {
                                if (ReadBit(j))
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
                    else
                    {
                        int k = j.spec_start;
                        do
                        {
                            int rs = HuffmanDecode(j, hac);
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
                                        j.eob_run += ReadBits(j, r);
                                    r = 64;
                                }
                            }
                            else
                            {
                                if (s != 1)
                                    throw new StbImageReadException(ErrorCode.BadHuffmanCode);

                                if (ReadBit(j))
                                    s = bit;
                                else
                                    s = -bit;
                            }

                            while (k <= j.spec_end)
                            {
                                ref short p = ref data[deZigZag[k++]];
                                if (p != 0)
                                {
                                    if (ReadBit(j))
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
                                        p = (short)s;
                                        break;
                                    }
                                    r--;
                                }
                            }

                        } while (k <= j.spec_end);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static byte Clamp(int x)
            {
                if (x < 0)
                    return 0;
                if (x > 255)
                    return 255;
                return (byte)x;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void CreateIdctVectors(Idct idct, out Vector128<int> low, out Vector128<int> high)
            {
                low = Sse2.ShiftRightArithmetic(
                    Sse2.Add(
                        Vector128.Create(idct.x0, idct.x1, idct.x2, idct.x3),
                        Vector128.Create(idct.t3, idct.t2, idct.t1, idct.t0)),
                    17);

                high = Sse2.ShiftRightArithmetic(
                    Sse2.Subtract(
                        Vector128.Create(idct.x3, idct.x2, idct.x1, idct.x0),
                        Vector128.Create(idct.t0, idct.t1, idct.t2, idct.t3)),
                    17);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void CalcIdct(Span<int> v, out Idct idct)
            {
                Idct1D(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], out idct);

                idct.x0 += 65536 + (128 << 17);
                idct.x1 += 65536 + (128 << 17);
                idct.x2 += 65536 + (128 << 17);
                idct.x3 += 65536 + (128 << 17);
            }

            public static void IdctBlock(Span<byte> dst, int dstStride, ReadOnlySpan<short> data)
            {
                Span<int> val = stackalloc int[64];

                for (int i = 0; i < val.Length / 8; ++i)
                {
                    var d = data.Slice(i);
                    var v = val.Slice(i);

                    if ((d[56] == 0) &&
                        (d[08] == 0) &&
                        (d[16] == 0) &&
                        (d[24] == 0) &&
                        (d[32] == 0) &&
                        (d[40] == 0) &&
                        (d[48] == 0))
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

                        v[56] = (idct.x0 - idct.t3) >> 10;
                        v[0] = (idct.x0 + idct.t3) >> 10;
                        v[8] = (idct.x1 + idct.t2) >> 10;
                        v[16] = (idct.x2 + idct.t1) >> 10;
                        v[24] = (idct.x3 + idct.t0) >> 10;
                        v[32] = (idct.x3 - idct.t0) >> 10;
                        v[40] = (idct.x2 - idct.t1) >> 10;
                        v[48] = (idct.x1 - idct.t2) >> 10;
                    }
                }

                for (int i = 0; i < val.Length / 8; i++)
                {
                    CalcIdct(val.Slice(i * 8), out var idct);

                    var o = dst.Slice(i * dstStride);
                    o[7] = Clamp((idct.x0 - idct.t3) >> 17);
                    o[0] = Clamp((idct.x0 + idct.t3) >> 17);
                    o[1] = Clamp((idct.x1 + idct.t2) >> 17);
                    o[2] = Clamp((idct.x2 + idct.t1) >> 17);
                    o[3] = Clamp((idct.x3 + idct.t0) >> 17);
                    o[4] = Clamp((idct.x3 - idct.t0) >> 17);
                    o[5] = Clamp((idct.x2 - idct.t1) >> 17);
                    o[6] = Clamp((idct.x1 - idct.t2) >> 17);
                }
            }

            public static byte ReadMarker(JpegState j)
            {
                Debug.Assert(j != null);

                byte x;
                if (j.marker != 0xff)
                {
                    x = j.marker;
                    j.marker = 0xff;
                    return x;
                }

                x = j.Reader.ReadByte();
                if (x != 0xff)
                    return 0xff;

                while (x == 0xff)
                    x = j.Reader.ReadByte();

                return x;
            }

            public static void Reset(JpegState j)
            {
                Debug.Assert(j != null);

                j.code_bits = 0;
                j.code_buffer = 0;
                j.nomore = false;
                for (int i = 0; i < j.components.Length; i++)
                    j.components[i].dc_pred = 0;
                j.marker = 0xff;
                j.todo = j.restart_interval != 0 ? j.restart_interval : 0x7fffffff;
                j.eob_run = 0;
            }

            public static void ParseEntropyCodedData(JpegState state)
            {
                Debug.Assert(state != null);

                Reset(state);
                if (!state.progressive)
                {
                    Span<short> data = stackalloc short[64];

                    if (state.scan_n == 1)
                    {
                        int n = state.order[0];
                        var component = state.components[n];
                        int w = (component.x + 7) / 8;
                        int h = (component.y + 7) / 8;
                        var cdata = component.data.Span;

                        for (int j = 0; j < h; ++j)
                        {
                            for (int i = 0; i < w; ++i)
                            {
                                int ha = component.ha;

                                DecodeBlock(
                                    state, data, state.huff_dc[component.hd], state.huff_ac[ha],
                                    state.fast_ac[ha], n, state.dequant[component.tq]);

                                state.idct_block_kernel(
                                    cdata.Slice(component.w2 * j * 8 + i * 8),
                                    component.w2,
                                    data);

                                if (--state.todo <= 0)
                                {
                                    if (state.code_bits < 24)
                                        GrowBufferUnsafe(state);

                                    AssertIsRestart(state.marker);
                                    Reset(state);
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int j = 0; j < state.img_mcu_y; ++j)
                        {
                            for (int i = 0; i < state.img_mcu_x; ++i)
                            {
                                for (int k = 0; k < state.scan_n; ++k)
                                {
                                    int n = state.order[k];
                                    var component = state.components[n];
                                    var cdata = component.data.Span;

                                    for (int y = 0; y < component.v; ++y)
                                    {
                                        for (int x = 0; x < component.h; ++x)
                                        {
                                            int x2 = (i * component.h + x) * 8;
                                            int y2 = (j * component.v + y) * 8;
                                            int ha = component.ha;

                                            DecodeBlock(
                                                state, data,
                                                state.huff_dc[component.hd],
                                                state.huff_ac[ha], state.fast_ac[ha], n,
                                                state.dequant[component.tq]);

                                            state.idct_block_kernel(
                                                cdata.Slice(component.w2 * y2 + x2),
                                                component.w2,
                                                data);
                                        }
                                    }
                                }

                                if (--state.todo <= 0)
                                {
                                    if (state.code_bits < 24)
                                        GrowBufferUnsafe(state);

                                    AssertIsRestart(state.marker);
                                    Reset(state);
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (state.scan_n == 1)
                    {
                        int n = state.order[0];
                        var component = state.components[n];
                        int w = (component.x + 7) / 8;
                        int h = (component.y + 7) / 8;
                        var coeff16 = MemoryMarshal.Cast<byte, short>(component.coeff.Span);

                        for (int j = 0; j < h; ++j)
                        {
                            for (int i = 0; i < w; ++i)
                            {
                                var data = coeff16.Slice(
                                    64 * (i + j * component.coeff_w),
                                    64);

                                if (state.spec_start == 0)
                                {
                                    DecodeBlockProgressiveDc(state, data, state.huff_dc[component.hd], n);
                                }
                                else
                                {
                                    int ha = component.ha;
                                    DecodeBlockProggressiveAc(state, data, state.huff_ac[ha], state.fast_ac[ha]);
                                }

                                if (--state.todo <= 0)
                                {
                                    if (state.code_bits < 24)
                                        GrowBufferUnsafe(state);

                                    AssertIsRestart(state.marker);
                                    Reset(state);
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int j = 0; j < state.img_mcu_y; ++j)
                        {
                            for (int i = 0; i < state.img_mcu_x; ++i)
                            {
                                for (int k = 0; k < state.scan_n; ++k)
                                {
                                    int n = state.order[k];
                                    var component = state.components[n];
                                    var coeff16 = MemoryMarshal.Cast<byte, short>(component.coeff.Span);

                                    for (int y = 0; y < component.v; ++y)
                                    {
                                        for (int x = 0; x < component.h; ++x)
                                        {
                                            int x2 = i * component.h + x;
                                            int y2 = j * component.v + y;
                                            var data = coeff16.Slice(
                                                64 * (x2 + y2 * component.coeff_w),
                                                64);

                                            DecodeBlockProgressiveDc(state, data, state.huff_dc[component.hd], n);
                                        }
                                    }
                                }

                                if (--state.todo <= 0)
                                {
                                    if (state.code_bits < 24)
                                        GrowBufferUnsafe(state);

                                    AssertIsRestart(state.marker);
                                    Reset(state);
                                }
                            }
                        }
                    }
                }
            }

            [CLSCompliant(false)]
            public static void Dequantize(Span<short> data, ReadOnlySpan<ushort> dequant)
            {
                if (data.Length > dequant.Length)
                    throw new ArgumentException(
                        "Not enough elements for the given data.", nameof(dequant));

                for (int i = 0; i < data.Length; i++)
                    data[i] *= (short)dequant[i];
            }

            public static void Finish(JpegState z)
            {
                Debug.Assert(z != null);

                if (!z.progressive)
                    return;

                for (int n = 0; n < z.State.Components; ++n)
                {
                    var component = z.components[n];
                    int w = (component.x + 7) / 8;
                    int h = (component.y + 7) / 8;

                    for (int j = 0; j < h; ++j)
                    {
                        for (int i = 0; i < w; i++)
                        {
                            var data16 = ByteToInt16(component.coeff);
                            var data = data16.Slice(64 * (i + j * component.coeff_w), 64);

                            Dequantize(data, z.dequant[component.tq]);

                            z.idct_block_kernel(
                                component.data.Span.Slice(component.w2 * j * 8 + i * 8),
                                component.w2,
                                data);
                        }
                    }
                }
            }

            public static bool ProcessMarker(JpegState z, int m)
            {
                Debug.Assert(z != null);

                var s = z.Reader;
                int L;
                switch (m)
                {
                    case 0xff:
                        throw new StbImageReadException(ErrorCode.ExpectedMarker);

                    case 0xDD:
                        if (s.ReadInt16BE() != 4)
                            throw new StbImageReadException(ErrorCode.BadDRILength);

                        z.restart_interval = s.ReadInt16BE();
                        return true;

                    case 0xDB:
                        L = s.ReadInt16BE() - 2;
                        while (L > 0)
                        {
                            int q = s.ReadByte();
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
                                    ? s.ReadInt16BE()
                                    : s.ReadByte());
                            }
                            L -= sixteen != 0 ? 129 : 65;
                        }
                        return L == 0;

                    case 0xC4:
                        Span<int> sizes = stackalloc int[16];
                        L = s.ReadInt16BE() - 2;
                        while (L > 0)
                        {
                            int q = s.ReadByte();
                            int tc = q >> 4;
                            int th = q & 15;
                            if ((tc > 1) || (th > 3))
                                throw new StbImageReadException(ErrorCode.BadDHTHeader);

                            int n = 0;
                            for (int i = 0; i < sizes.Length; ++i)
                            {
                                sizes[i] = s.ReadByte();
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

                            s.ReadBytes(huff[th].Values.Slice(0, n));

                            if (tc != 0)
                                BuildFastAc(z.fast_ac[th], z.huff_ac[th]);
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
                            throw new StbImageReadException(ErrorCode.BadCOMLength);
                        else
                            throw new StbImageReadException(ErrorCode.BadAPPLength);
                    }

                    L -= 2;
                    if ((m == 0xE0) && (L >= 5))
                    {
                        int ok = 1;
                        int i;
                        for (i = 0; i < 5; ++i)
                        {
                            if (s.ReadByte() != JfifTag[i])
                                ok = 0;
                        }

                        L -= 5;
                        if (ok != 0)
                            z.jfif = 1;
                    }
                    else if ((m == 0xEE) && (L >= 12))
                    {
                        bool ok = true;
                        for (int i = 0; i < 6; ++i)
                        {
                            if (s.ReadByte() != AdobeTag[i])
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

                throw new StbImageReadException(ErrorCode.UnknownMarker);
            }

            public static bool ProcessScanHeader(JpegState z)
            {
                if (z == null)
                    throw new ArgumentNullException(nameof(z));

                var s = z.Reader;
                int Ls = s.ReadInt16BE();
                z.scan_n = s.ReadByte();
                if ((z.scan_n < 1) || (z.scan_n > 4) || (z.scan_n > z.State.Components))
                    throw new StbImageReadException(ErrorCode.BadSOSComponentCount);

                if (Ls != 6 + 2 * z.scan_n)
                    throw new StbImageReadException(ErrorCode.BadSOSLength);

                for (int i = 0; i < z.scan_n; ++i)
                {
                    int id = s.ReadByte();
                    int q = s.ReadByte();

                    int which;
                    for (which = 0; which < z.State.Components; ++which)
                    {
                        if (z.components[which].id == id)
                            break;
                    }

                    if (which == z.State.Components)
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
                    z.spec_start = s.ReadByte();
                    z.spec_end = s.ReadByte();
                    int aa = s.ReadByte();
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
                Debug.Assert(z != null);

                for (int i = 0; i < ncomp; i++)
                {
                    ref ImageComponent comp = ref z.components[i];

                    if (comp.raw_data != null)
                    {
                        z.BytePool.Return(comp.raw_data);
                        comp.raw_data = null;
                        comp.data = null;
                    }

                    if (comp.raw_coeff != null)
                    {
                        z.BytePool.Return(comp.raw_coeff);
                        comp.raw_coeff = null;
                        comp.coeff = null;
                    }

                    if (comp.raw_linebuf != null)
                    {
                        z.BytePool.Return(comp.raw_linebuf);
                        comp.raw_linebuf = null;
                        comp.linebuf = null;
                    }
                }
            }

            public static void ProcessFrameHeader(JpegState z, ScanMode scan)
            {
                Debug.Assert(z != null);

                var s = z.Reader;

                int Lf = s.ReadInt16BE();
                if (Lf < 11)
                    throw new StbImageReadException(ErrorCode.BadSOFLength);

                int p = s.ReadByte();
                if (p != 8)
                    throw new StbImageReadException(ErrorCode.UnsupportedBitDepth);

                z.State.Height = s.ReadInt16BE();
                if (z.State.Height == 0)
                    throw new StbImageReadException(ErrorCode.ZeroHeight);

                z.State.Width = s.ReadInt16BE();
                if (z.State.Width == 0)
                    throw new StbImageReadException(ErrorCode.ZeroWidth);

                z.State.Components = s.ReadByte();
                if ((z.State.Components != 1) &&
                    (z.State.Components != 3) &&
                    (z.State.Components != 4))
                    throw new StbImageReadException(ErrorCode.BadComponentCount);

                for (int i = 0; i < z.State.Components; ++i)
                {
                    z.components[i].data = null;
                    z.components[i].linebuf = null;
                }

                if (Lf != 8 + 3 * z.State.Components)
                    throw new StbImageReadException(ErrorCode.BadSOFLength);

                z.rgb = 0;

                for (int i = 0; i < z.State.Components; i++)
                {
                    ref ImageComponent comp = ref z.components[i];
                    comp.id = s.ReadByte();
                    if ((z.State.Components == 3) && (comp.id == RGB_Sequence[i]))
                        z.rgb++;

                    int q = s.ReadByte();
                    comp.h = q >> 4;
                    if ((comp.h == 0) || (comp.h > 4))
                        throw new StbImageReadException(ErrorCode.BadH);

                    comp.v = q & 15;
                    if ((comp.v == 0) || (comp.v > 4))
                        throw new StbImageReadException(ErrorCode.BadV);

                    comp.tq = s.ReadByte();
                    if (comp.tq > 3)
                        throw new StbImageReadException(ErrorCode.BadTQ);
                }

                if (scan != ScanMode.Load)
                    return;

                int h_max = 1;
                int v_max = 1;
                for (int i = 0; i < z.State.Components; ++i)
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
                z.img_mcu_x = (z.State.Width + z.img_mcu_w - 1) / z.img_mcu_w;
                z.img_mcu_y = (z.State.Height + z.img_mcu_h - 1) / z.img_mcu_h;

                FreeComponents(z, z.State.Components);

                for (int i = 0; i < z.State.Components; i++)
                {
                    ref ImageComponent comp = ref z.components[i];
                    comp.x = (z.State.Width * comp.h + h_max - 1) / h_max;
                    comp.y = (z.State.Height * comp.v + v_max - 1) / v_max;
                    comp.w2 = z.img_mcu_x * comp.h * 8;
                    comp.h2 = z.img_mcu_y * comp.v * 8;

                    int elementCount = comp.w2 * comp.h2;

                    comp.raw_data = z.BytePool.Rent(elementCount);
                    comp.data = comp.raw_data.AsMemory(0, elementCount);

                    if (z.progressive)
                    {
                        comp.coeff_w = comp.w2 / 8;
                        comp.coeff_h = comp.h2 / 8;

                        int coeffBytes = elementCount * sizeof(short);
                        comp.raw_coeff = z.BytePool.Rent(coeffBytes);
                        comp.coeff = comp.raw_coeff.AsMemory(0, coeffBytes);
                    }
                }
            }

            public static bool ParseHeader(JpegState z, ScanMode scan)
            {
                Debug.Assert(z != null);

                z.jfif = 0;
                z.app14_color_transform = -1;
                z.marker = 0xff;

                int m = ReadMarker(z);
                if (!(m == 0xd8))
                    throw new StbImageReadException(ErrorCode.NoSOI);

                m = ReadMarker(z);
                while (!((m == 0xc0) || (m == 0xc1) || (m == 0xc2)))
                {
                    if (!ProcessMarker(z, m))
                        return false;

                    do
                    {
                        m = ReadMarker(z);
                    }
                    while (m == 0xff);
                }

                z.progressive = m == 0xc2;

                ProcessFrameHeader(z, scan);

                z.State.Depth = 8;

                z.State.OutComponents = z.State.Components >= 3 ? 3 : 1;
                z.State.OutDepth = z.State.Depth;

                z.State.StateReady();

                return true;
            }

            public static bool ParseData(JpegState j)
            {
                Debug.Assert(j != null);

                FreeComponents(j, 4);
                j.restart_interval = 0;

                if (!ParseHeader(j, (int)ScanMode.Load))
                    return false;

                var s = j.Reader;
                int m = ReadMarker(j);
                while (!(m == 0xd9))
                {
                    if (m == 0xda)
                    {
                        if (!ProcessScanHeader(j))
                            return false;

                        ParseEntropyCodedData(j);

                        if (j.marker == 0xff)
                        {
                            while (s.ReadByte() != 0xff)
                                ;
                            j.marker = s.ReadByte();
                        }
                    }
                    else if (m == 0xdc)
                    {
                        int Ld = s.ReadInt16BE();
                        int NL = s.ReadInt16BE();

                        if (Ld != 4)
                            throw new StbImageReadException(ErrorCode.BadDNLLength);

                        if (NL != j.State.Height)
                            throw new StbImageReadException(ErrorCode.BadDNLHeight);
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

                j.is_rgb = j.State.Components == 3 && (j.rgb == 3 || (j.app14_color_transform == 0 && j.jfif == 0));
                j.decode_n = (j.State.Components < 3 && !j.is_rgb) ? 1 : j.State.Components;

                return true;
            }

            #region Row Resamplers

            public static Memory<byte> ResampleRow1(
                Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs)
            {
                return inNear;
            }

            public static Memory<byte> ResampleRowV2(
                Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs)
            {
                var o = dst.Span;
                var n = inNear.Span;
                var f = inFar.Span;

                for (int i = 0; i < w; ++i)
                {
                    o[i] = (byte)((3 * n[i] + f[i] + 2) >> 2);
                }

                return dst;
            }

            public static Memory<byte> ResampleRowH2(
                Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs)
            {
                var o = dst.Span;
                var input = inNear.Span;
                if (w == 1)
                {
                    o[0] = o[1] = input[0];
                    return dst;
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

                return dst;
            }

            public static Memory<byte> ResampleRowHV2(
                Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs)
            {
                var o = dst.Span;
                var n = inNear.Span;
                var f = inFar.Span;

                if (w == 1)
                {
                    o[0] = o[1] = (byte)((3 * n[0] + f[0] + 2) >> 2);
                    return dst;
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
                return dst;
            }

            public static Memory<byte> ResampleRowGeneric(
                Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs)
            {
                var o = dst.Span;
                var n = inNear.Span;

                for (int i = 0; i < w; ++i)
                {
                    var oslice = o.Slice(i * hs);
                    byte near = n[i];

                    for (int j = 0; j < hs; ++j)
                        oslice[j] = near;
                }

                return dst;
            }

            #endregion

            public static void YCbCrToRGB(
                Span<byte> dst, Span<byte> y, Span<byte> pcb, Span<byte> pcr)
            {
                int i = 0;
                int x = 0;

                const int crFactor = ((int)(1.40200f * 4096.0f + 0.5f)) << 8;
                const int cgFactor = -(((int)(0.71414f * 4096.0f + 0.5f)) << 8);
                const int cgbFactor = -(((int)(0.34414f * 4096.0f + 0.5f)) << 8);
                const int bFactor = ((int)(1.77200f * 4096.0f + 0.5f)) << 8;

                //if (Sse2.IsSupported)
                //{
                //    for (; i < dst.Length; i += 3, x++)
                //    {
                //
                //    }
                //}

                for (; i < dst.Length; i += 3, x++)
                {
                    int y_fixed = (y[x] << 20) + (1 << 19);
                    int cr = pcr[x] - 128;
                    int cb = pcb[x] - 128;

                    int r = y_fixed + cr * crFactor;
                    int g = y_fixed + cr * cgFactor + (int)((cb * cgbFactor) & 0xffff0000);
                    int b = y_fixed + cb * bFactor;

                    r >>= 20;
                    g >>= 20;
                    b >>= 20;

                    if (((uint)r) > 255)
                        r = r < 0 ? 0 : 255;

                    if (((uint)g) > 255)
                        g = g < 0 ? 0 : 255;

                    if (((uint)b) > 255)
                        b = b < 0 ? 0 : 255;

                    dst[i + 2] = (byte)b;
                    dst[i + 1] = (byte)g;
                    dst[i + 0] = (byte)r;
                }
            }

            public static void Cleanup(JpegState j)
            {
                Debug.Assert(j != null);

                FreeComponents(j, j.State.Components);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static byte Blinn8x8(byte x, byte y)
            {
                uint t = (uint)(x * y + 128);
                return (byte)((t + (t >> 8)) >> 8);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static byte ComputeY8(byte r, byte g, byte b)
            {
                return (byte)(((r * 77) + (g * 150) + (29 * b)) >> 8);
            }

            public static void LoadImage(JpegState z)
            {
                if (z == null)
                    throw new ArgumentNullException(nameof(z));

                try
                {
                    if (ParseData(z))
                        ProcessData(z);
                }
                finally
                {
                    Cleanup(z);
                }
            }

            public static void ProcessData(JpegState z)
            {
                if (z == null)
                    throw new ArgumentNullException(nameof(z));

                var res_comp = new ResampleData[z.decode_n];
                for (int k = 0; k < res_comp.Length; k++)
                {
                    ref ImageComponent comp = ref z.components[k];
                    int lineBufLen = z.State.Width + 3;
                    comp.raw_linebuf = z.BytePool.Rent(lineBufLen);
                    comp.linebuf = comp.raw_linebuf.AsMemory(0, lineBufLen);

                    ref ResampleData r = ref res_comp[k];
                    r.hs = z.img_h_max / comp.h;
                    r.vs = z.img_v_max / comp.v;
                    r.ystep = r.vs >> 1;
                    r.w_lores = (z.State.Width + r.hs - 1) / r.hs;
                    r.ypos = 0;
                    r.line0 = r.line1 = comp.data;

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

                var coutput = new Memory<byte>[JpegState.CompCount];
                int outStride = z.State.OutComponents * z.State.Width;
                var pooledRowBuffer = z.BytePool.Rent(outStride);
                var rowBuffer = pooledRowBuffer.AsSpan(0, outStride);
                try
                {
                    for (int j = 0; j < z.State.Height; ++j)
                    {
                        for (int k = 0; k < res_comp.Length; ++k)
                        {
                            ref ResampleData r = ref res_comp[k];
                            ImageComponent component = z.components[k];
                            bool y_bot = r.ystep >= (r.vs >> 1);

                            coutput[k] = r.Resample(
                                component.linebuf,
                                y_bot ? r.line1 : r.line0,
                                y_bot ? r.line0 : r.line1,
                                r.w_lores,
                                r.hs);

                            if ((++r.ystep) >= r.vs)
                            {
                                r.ystep = 0;
                                r.line0 = r.line1;
                                if ((++r.ypos) < component.y)
                                    r.line1 = r.line1.Slice(component.w2);
                            }
                        }

                        var co0 = coutput[0].Span;
                        var co1 = coutput[1].Span;
                        var co2 = coutput[2].Span;
                        var co3 = coutput[3].Span;

                        // TODO: validate/improve rowBuffer slicing
                        if (z.State.Components == 3)
                        {
                            if (z.is_rgb)
                            {
                                for (int i = 0, x = 0; i < rowBuffer.Length; i += 3, x++)
                                {
                                    rowBuffer[2 + i] = co2[x];
                                    rowBuffer[1 + i] = co1[x];
                                    rowBuffer[0 + i] = co0[x];
                                }
                            }
                            else
                            {
                                z.YCbCr_to_RGB_kernel(rowBuffer, co0, co1, co2);
                            }
                        }
                        else if (z.State.Components == 4)
                        {
                            if (z.app14_color_transform == 0)
                            {
                                for (int i = 0, x = 0; i < rowBuffer.Length; i += 3, x++)
                                {
                                    byte m = co3[i];
                                    rowBuffer[2 + i] = Blinn8x8(co2[x], m);
                                    rowBuffer[1 + i] = Blinn8x8(co1[x], m);
                                    rowBuffer[0 + i] = Blinn8x8(co0[x], m);
                                }
                            }
                            else if (z.app14_color_transform == 2)
                            {
                                z.YCbCr_to_RGB_kernel(rowBuffer, co0, co1, co2);

                                for (int i = 0; i < rowBuffer.Length; i += 3)
                                {
                                    byte m = co3[i];
                                    rowBuffer[2 + i] = Blinn8x8((byte)(255 - rowBuffer[2 + i]), m);
                                    rowBuffer[1 + i] = Blinn8x8((byte)(255 - rowBuffer[1 + i]), m);
                                    rowBuffer[0 + i] = Blinn8x8((byte)(255 - rowBuffer[0 + i]), m);
                                }
                            }
                            else
                            {
                                z.YCbCr_to_RGB_kernel(rowBuffer, co0, co1, co2);
                            }
                        }
                        else
                        {
                            z.State.OutputPixelLine(AddressingMajor.Row, j, 0, co0.Slice(0, outStride));
                            continue;
                        }

                        z.State.OutputPixelLine(AddressingMajor.Row, j, 0, rowBuffer);
                    }
                }
                finally
                {
                    z.BytePool.Return(pooledRowBuffer);
                }
            }

            public static void Load(BinReader s, ReadState ri, ArrayPool<byte>? arrayPool = null)
            {
                using (var j = new JpegState(s, ri, arrayPool))
                    LoadImage(j);
            }

            public static void InfoCore(JpegState j)
            {
                if (!ParseHeader(j, ScanMode.Header))
                    throw new StbImageReadException(ErrorCode.Undefined);
            }

            public static void Info(BinReader s, out ReadState ri, ArrayPool<byte>? arrayPool = null)
            {
                ri = new ReadState();
                using (var j = new JpegState(s, ri, arrayPool))
                    InfoCore(j);
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
