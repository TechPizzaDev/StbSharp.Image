using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace StbSharp.ImageRead
{
    [SkipLocalsInit]
    public static class Jpeg
    {
        // TODO: optimize YCbCr with intrinsics

        #region Constants

        public const byte NoneMarker = 0xff;

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

        public delegate Memory<byte> ResamplerMethod(
            Memory<byte> destination,
            Memory<byte> inputNear,
            Memory<byte> inputFar,
            int w,
            int hs);

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
            public Span<ushort> Code => MemoryMarshal.Cast<byte, ushort>(MCode.Span);
            public Span<byte> Values => MValues.Span;
            public Span<byte> Size => MSize.Span;
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

                Memory<byte> m = _buffer.AsMemory(0, Size);
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

        [InlineArray(Huffman.FastLength)]
        public struct FastAc
        {
            private short _e0;
        }

        [InlineArray(64)]
        public struct Dequant
        {
            private ushort _e0;
        }

        public class JpegState : IDisposable
        {
            public const int CompCount = 4;

            private bool _isDisposed;

            public ImageBinReader Reader { get; }
            public ReadState State { get; }
            public ArrayPool<byte> BytePool { get; }

            public readonly Huffman[] huff_dc = new Huffman[CompCount];
            public readonly Huffman[] huff_ac = new Huffman[CompCount];
            public readonly FastAc[] fast_ac = new FastAc[CompCount];

            public readonly Dequant[] dequant = new Dequant[CompCount];

            // sizes for components, interleaved MCUs
            public int img_h_max, img_v_max;
            public int img_mcu_x, img_mcu_y;
            public int img_mcu_w, img_mcu_h;

            // definition of jpeg image component
            public ImageComponent[] components = new ImageComponent[CompCount];

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

            public bool SkipInvalidMarkerLength = true;
            public bool SkipInvalidMarker = true;

            public JpegState(ImageBinReader reader, ReadState readState, ArrayPool<byte>? arrayPool)
            {
                Reader = reader ?? throw new ArgumentNullException(nameof(reader));
                State = readState ?? throw new ArgumentNullException(nameof(readState));
                BytePool = arrayPool ?? ArrayPool<byte>.Shared;

                for (int i = 0; i < CompCount; i++)
                {
                    huff_ac[i] = new Huffman(BytePool);
                    huff_dc[i] = new Huffman(BytePool);
                }

                for (int i = 0; i < components.Length; ++i)
                    components[i] = new ImageComponent();
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

        /// <summary>
        /// In each scan, we'll have scan_n components, and the order
        /// of the components is specified by order[].
        /// </summary>
        /// <param name="marker">The marker value to check.</param>
        /// <returns>Whether <paramref name="marker"/> is a restart marker.</returns>
        public static bool IsRestart(byte marker)
        {
            return marker >= 0xd0 && marker <= 0xd7;
        }

        private static void BuildHuffman(Huffman h, Span<int> count)
        {
            Span<byte> Size = h.Size;

            int k = 0;
            for (int i = 0; i < 16; ++i)
            {
                for (int j = 0; j < count[i]; ++j)
                    Size[k++] = (byte)(i + 1);
            }

            Span<int> Delta = h.Delta;
            Span<ushort> Code = h.Code;

            int code = 0;
            Size[k] = 0;
            k = 0;
            for (int j = 1; j <= 16; ++j)
            {
                Delta[j] = k - code;
                if (Size[k] == j)
                {
                    while (Size[k] == j)
                        Code[k++] = (ushort)code++;

                    if ((code - 1) >= (1 << j))
                        StbImageReadException.Throw(ErrorCode.BadCodeLengths);
                }

                h.Maxcode[j] = (uint)(code << (16 - j));
                code <<= 1;
            }
            h.Maxcode[17] = 0xffffffff;

            Span<byte> Fast = h.Fast;
            Fast.Fill(255);

            for (int i = 0; i < k; ++i)
            {
                int s = Size[i];
                if (s <= 9)
                {
                    int c = Code[i] << (9 - s);
                    int m = 1 << (9 - s);
                    for (int j = 0; j < m; ++j)
                        Fast[c + j] = (byte)i;
                }
            }
        }

        private static void BuildFastAc(Span<short> fastAc, Huffman h)
        {
            Span<byte> Fast = h.Fast[..fastAc.Length];

            fastAc.Clear();
            for (int i = 0; i < Fast.Length; ++i)
            {
                byte fast = Fast[i];
                if (fast >= 255)
                {
                    continue;
                }

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

        /// <exception cref="System.IO.EndOfStreamException"/>
        private static void GrowBufferUnsafe(JpegState state)
        {
            if (state.nomore)
                return;

            do
            {
                byte b = state.Reader.ReadByte();
                if (b == NoneMarker)
                {
                    byte c;
                    do
                    {
                        c = state.Reader.ReadByte(); // consume fill bytes
                    }
                    while (c == NoneMarker);

                    if (c != 0)
                    {
                        state.marker = c;
                        state.nomore = true;
                        return;
                    }
                }

                state.code_buffer |= (uint)(b << (24 - state.code_bits));
                state.code_bits += 8;
            }
            while (state.code_bits <= 24);
        }

        private static int HuffmanDecode(JpegState state, Huffman h)
        {
            if (state.code_bits < 16)
                GrowBufferUnsafe(state);

            int c = (int)((state.code_buffer >> (32 - 9)) & (Huffman.FastLength - 1));
            int k = h.Fast[c];
            if (k < 255)
            {
                int s = h.Size[k];
                if (s > state.code_bits)
                    return -1;

                state.code_buffer <<= s;
                state.code_bits -= s;
                return h.Values[k];
            }

            uint tmp = state.code_buffer >> 16;
            for (k = 9 + 1; ; ++k)
            {
                if (tmp < h.Maxcode[k])
                    break;
            }

            if (k == 17)
            {
                state.code_bits -= 16;
                return -1;
            }

            if (k > state.code_bits)
                return -1;

            c = (int)((state.code_buffer >> (32 - k)) & BMask[k]) + h.Delta[k];
            state.code_bits -= k;
            state.code_buffer <<= k;
            return h.Values[c];
        }

        private static int ExtendReceive(JpegState state, int n)
        {
            if (state.code_bits < n)
                GrowBufferUnsafe(state);

            int sgn = (int)state.code_buffer >> 31;
            uint k = BitOperations.RotateLeft(state.code_buffer, n);
            uint mask = BMask[n];
            state.code_buffer = k & ~mask;
            k &= mask;
            state.code_bits -= n;
            return (int)(k + (JBias[n] & ~sgn));
        }

        private static int ReadBits(JpegState state, int n)
        {
            if (state.code_bits < n)
                GrowBufferUnsafe(state);

            uint k = BitOperations.RotateLeft(state.code_buffer, n);
            uint mask = BMask[n];
            state.code_buffer = k & ~mask;
            k &= mask;
            state.code_bits -= n;
            return (int)k;
        }

        private static bool ReadBit(JpegState state)
        {
            if (state.code_bits < 1)
                GrowBufferUnsafe(state);

            uint k = state.code_buffer;
            state.code_buffer <<= 1;
            state.code_bits--;
            return (k & 0x80000000) != 0;
        }

        private static void DecodeBlock(
            JpegState state, Span<short> data, Huffman hdc, Huffman hac,
            ReadOnlySpan<short> fac, int b, ReadOnlySpan<ushort> dequant)
        {
            if (state.code_bits < 16)
                GrowBufferUnsafe(state);

            int t = HuffmanDecode(state, hdc);
            if (t < 0)
                StbImageReadException.Throw(ErrorCode.BadHuffmanCode);

            data.Clear();

            int diff = t != 0 ? ExtendReceive(state, t) : 0;
            int dc = state.components[b].dc_pred + diff;
            state.components[b].dc_pred = dc;
            data[0] = (short)(dc * dequant[0]);

            ReadOnlySpan<byte> deZigZag = DeZigZag;
            int k = 1;
            do
            {
                if (state.code_bits < 16)
                    GrowBufferUnsafe(state);

                int c = (int)((state.code_buffer >> (32 - 9)) & (Huffman.FastLength - 1));
                int r = fac[c];
                int s;
                if (r != 0)
                {
                    k += (r >> 4) & 15;
                    s = r & 15;
                    state.code_buffer <<= s;
                    state.code_bits -= s;
                    int zig = deZigZag[k++];
                    data[zig] = (short)((r >> 8) * dequant[zig]);
                }
                else
                {
                    int rs = HuffmanDecode(state, hac);
                    if (rs < 0)
                        StbImageReadException.Throw(ErrorCode.BadHuffmanCode);

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
                        int zig = deZigZag[k++];
                        int value = ExtendReceive(state, s);
                        data[zig] = (short)(value * dequant[zig]);
                    }
                }
            }
            while (k < 64);
        }

        private static void DecodeBlockProgressiveDc(
            JpegState state, Span<short> data, Huffman hdc, int b)
        {
            if (state.spec_end != 0)
                StbImageReadException.Throw(ErrorCode.CantMergeDcAndAc);

            if (state.code_bits < 16)
                GrowBufferUnsafe(state);

            if (state.succ_high == 0)
            {
                data.Clear();
                int t = HuffmanDecode(state, hdc);
                int diff = t != 0 ? ExtendReceive(state, t) : 0;
                int dc = state.components[b].dc_pred + diff;
                state.components[b].dc_pred = dc;
                data[0] = (short)(dc << state.succ_low);
            }
            else
            {
                if (ReadBit(state))
                    data[0] += (short)(1 << state.succ_low);
            }
        }

        private static void DecodeBlockProggressiveAc(
            JpegState state, Span<short> data, Huffman hac, ReadOnlySpan<short> fac)
        {
            if (state.spec_start == 0)
                StbImageReadException.Throw(ErrorCode.CantMergeDcAndAc);

            if (state.succ_high == 0)
            {
                if (state.eob_run != 0)
                {
                    --state.eob_run;
                    return;
                }

                DecodeBlockProgAcSuccZero(state, data, hac, fac);
            }
            else
            {
                DecodeBlockProgAcSuccHigh(state, data, hac);
            }
        }

        private static void DecodeBlockProgAcSuccZero(
            JpegState state, Span<short> data, Huffman hac, ReadOnlySpan<short> fac)
        {
            ReadOnlySpan<byte> deZigZag = DeZigZag;

            int shift = state.succ_low;
            int k = state.spec_start;
            do
            {
                if (state.code_bits < 16)
                    GrowBufferUnsafe(state);

                int c = (int)((state.code_buffer >> (32 - 9)) & ((1 << 9) - 1));
                int r = fac[c];

                if (r != 0)
                {
                    k += (r >> 4) & 15;
                    int s = r & 15;
                    state.code_buffer <<= s;
                    state.code_bits -= s;
                    data[deZigZag[k++]] = (short)((r >> 8) << shift);
                }
                else
                {
                    int rs = HuffmanDecode(state, hac);
                    if (rs < 0)
                        StbImageReadException.Throw(ErrorCode.BadHuffmanCode);

                    int s = rs & 15;
                    r = rs >> 4;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            state.eob_run = 1 << r;
                            if (r != 0)
                                state.eob_run += ReadBits(state, r);
                            --state.eob_run;
                            break;
                        }

                        k += 16;
                    }
                    else
                    {
                        k += r;
                        int extended = ExtendReceive(state, s);
                        data[deZigZag[k++]] = (short)(extended << shift);
                    }
                }
            }
            while (k <= state.spec_end);
        }

        private static void DecodeBlockProgAcSuccHigh(JpegState state, Span<short> data, Huffman hac)
        {
            short bit = (short)(1 << state.succ_low);
            if (state.eob_run != 0)
            {
                state.eob_run--;
                DecodeBlockProgAcSuccHighEob(state, data, hac, bit);
            }
            else
            {
                DecodeBlockProgAcSuccHighLast(state, data, hac, bit);
            }
        }


        private static void DecodeBlockProgAcSuccHighEob(JpegState state, Span<short> data, Huffman hac, short bit)
        {
            ReadOnlySpan<byte> deZigZag = DeZigZag;

            int offset = state.spec_start;
            while (offset <= state.spec_end)
            {
                ref short p = ref data[deZigZag[offset++]];
                if (p != 0)
                {
                    if (ReadBit(state))
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

        private static void DecodeBlockProgAcSuccHighLast(JpegState state, Span<short> data, Huffman hac, short bit)
        {
            ReadOnlySpan<byte> deZigZag = DeZigZag;

            int k = state.spec_start;
            do
            {
                int rs = HuffmanDecode(state, hac);
                if (rs < 0)
                    StbImageReadException.Throw(ErrorCode.BadHuffmanCode);

                int s = rs & 15;
                int r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        state.eob_run = (1 << r) - 1;
                        if (r != 0)
                            state.eob_run += ReadBits(state, r);
                        r = 64;
                    }
                }
                else
                {
                    if (s != 1)
                        StbImageReadException.Throw(ErrorCode.BadHuffmanCode);

                    if (ReadBit(state))
                        s = bit;
                    else
                        s = -bit;
                }

                while (k <= state.spec_end)
                {
                    ref short p = ref data[deZigZag[k++]];
                    if (p != 0)
                    {
                        if (ReadBit(state))
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
            }
            while (k <= state.spec_end);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void IdctBlock(Span<byte> dst, int dstStride, ReadOnlySpan<short> data)
        {
            // sse2 integer IDCT. not the fastest possible implementation but it
            // produces bit-identical results to the generic C version so it's
            // fully "transparent".

            // out(0) = c0[even]*x + c0[odd]*y   (c0, x, y 16-bit, out 32-bit)
            // out(1) = c1[even]*x + c1[odd]*y
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void dct_rot(
                Vector128<short> x, Vector128<short> y,
                Vector128<short> c0, Vector128<short> c1,
                out Vector128<int> out0l, out Vector128<int> out0h,
                out Vector128<int> out1l, out Vector128<int> out1h)
            {
                Vector128<short> c0_l = V128Helper.UnpackLow(x, y);
                Vector128<short> c0_h = V128Helper.UnpackHigh(x, y);

                out0l = V128Helper.MultiplyAddAdjacent(c0_l, c0);
                out0h = V128Helper.MultiplyAddAdjacent(c0_h, c0);
                out1l = V128Helper.MultiplyAddAdjacent(c0_l, c1);
                out1h = V128Helper.MultiplyAddAdjacent(c0_h, c1);
            }

            // butterfly a/b, add bias, then shift by "s" and pack
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void dct_bfly32o(
                Vector128<int> aLow, Vector128<int> aHigh,
                Vector128<int> bLow, Vector128<int> bHigh,
                Vector128<int> bias,
                [ConstantExpected] byte s,
                out Vector128<short> out0, out Vector128<short> out1)
            {
                Vector128<int> abiased_l = aLow + bias;
                Vector128<int> abiased_h = aHigh + bias;

                Vector128<int> sum_l = abiased_l + bLow;
                Vector128<int> sum_h = abiased_h + bHigh;

                Vector128<int> dif_l = abiased_l - bLow;
                Vector128<int> dif_h = abiased_h - bHigh;

                out0 = V128Helper.PackSignedSaturate(sum_l >> s, sum_h >> s);
                out1 = V128Helper.PackSignedSaturate(dif_l >> s, dif_h >> s);
            }

            // 8-bit interleave step (for transposes)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void dct_interleave8(ref Vector128<byte> a, ref Vector128<byte> b)
            {
                Vector128<byte> tmp = a;
                a = V128Helper.UnpackLow(a, b);
                b = V128Helper.UnpackHigh(tmp, b);
            }

            // 16-bit interleave step (for transposes)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void dct_interleave16(ref Vector128<short> a, ref Vector128<short> b)
            {
                Vector128<short> tmp = a;
                a = V128Helper.UnpackLow(a, b);
                b = V128Helper.UnpackHigh(tmp, b);
            }

            Vector128<short> rot0_0 = Vector128.Create(2217, -5350, 2217, -5350, 2217, -5350, 2217, -5350);
            Vector128<short> rot0_1 = Vector128.Create(5352, 2217, 5352, 2217, 5352, 2217, 5352, 2217);
            Vector128<short> rot1_0 = Vector128.Create(1131, 4816, 1131, 4816, 1131, 4816, 1131, 4816);
            Vector128<short> rot1_1 = Vector128.Create(4816, -5681, 4816, -5681, 4816, -5681, 4816, -5681);
            Vector128<short> rot2_0 = Vector128.Create(-6811, -8034, -6811, -8034, -6811, -8034, -6811, -8034);
            Vector128<short> rot2_1 = Vector128.Create(-8034, 4552, -8034, 4552, -8034, 4552, -8034, 4552);
            Vector128<short> rot3_0 = Vector128.Create(6813, -1597, 6813, -1597, 6813, -1597, 6813, -1597);
            Vector128<short> rot3_1 = Vector128.Create(-1597, 4552, -1597, 4552, -1597, 4552, -1597, 4552);

            // rounding biases in column/row passes, see stbi__idct_block for explanation.
            Vector128<int> bias_0 = Vector128.Create(512);
            Vector128<int> bias_1 = Vector128.Create(65536 + (128 << 17));

            // This is loaded to match our regular (generic) integer IDCT exactly.
            ReadOnlySpan<short> datas = data.Slice(0, 64);
            Vector128<short> row0 = Vector128.Create(datas.Slice(0 * 8));
            Vector128<short> row1 = Vector128.Create(datas.Slice(1 * 8));
            Vector128<short> row2 = Vector128.Create(datas.Slice(2 * 8));
            Vector128<short> row3 = Vector128.Create(datas.Slice(3 * 8));
            Vector128<short> row4 = Vector128.Create(datas.Slice(4 * 8));
            Vector128<short> row5 = Vector128.Create(datas.Slice(5 * 8));
            Vector128<short> row6 = Vector128.Create(datas.Slice(6 * 8));
            Vector128<short> row7 = Vector128.Create(datas.Slice(7 * 8));

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            void dct_pass(Vector128<int> bias, [ConstantExpected] byte shift)
            {
                // even part
                dct_rot(
                    row2, row6, rot0_0, rot0_1,
                    out Vector128<int> t2e_l, out Vector128<int> t2e_h,
                    out Vector128<int> t3e_l, out Vector128<int> t3e_h);

                Vector128<short> sum04 = row0 + row4;
                Vector128<short> dif04 = row0 - row4;

                Vector128<int> t0e_l = V128Helper.UnpackLow(Vector128<short>.Zero, sum04).AsInt32() >> 4;
                Vector128<int> t0e_h = V128Helper.UnpackHigh(Vector128<short>.Zero, sum04).AsInt32() >> 4;

                Vector128<int> t1e_l = V128Helper.UnpackLow(Vector128<short>.Zero, dif04).AsInt32() >> 4;
                Vector128<int> t1e_h = V128Helper.UnpackHigh(Vector128<short>.Zero, dif04).AsInt32() >> 4;

                Vector128<int> x0_l = t0e_l + t3e_l;
                Vector128<int> x3_l = t0e_l - t3e_l;
                Vector128<int> x1_l = t1e_l + t2e_l;
                Vector128<int> x2_l = t1e_l - t2e_l;

                Vector128<int> x0_h = t0e_h + t3e_h;
                Vector128<int> x3_h = t0e_h - t3e_h;
                Vector128<int> x1_h = t1e_h + t2e_h;
                Vector128<int> x2_h = t1e_h - t2e_h;

                // odd part
                dct_rot(
                    row7, row3, rot2_0, rot2_1,
                    out Vector128<int> y0o_l, out Vector128<int> y0o_h,
                    out Vector128<int> y2o_l, out Vector128<int> y2o_h);
                dct_rot(
                    row5, row1, rot3_0, rot3_1,
                    out Vector128<int> y1o_l, out Vector128<int> y1o_h,
                    out Vector128<int> y3o_l, out Vector128<int> y3o_h);

                Vector128<short> sum17 = row1 + row7;
                Vector128<short> sum35 = row3 + row5;
                dct_rot(
                    sum17, sum35, rot1_0, rot1_1,
                    out Vector128<int> y4o_l, out Vector128<int> y4o_h,
                    out Vector128<int> y5o_l, out Vector128<int> y5o_h);

                Vector128<int> x4_l = y0o_l + y4o_l;
                Vector128<int> x5_l = y1o_l + y5o_l;
                Vector128<int> x6_l = y2o_l + y5o_l;
                Vector128<int> x7_l = y3o_l + y4o_l;

                Vector128<int> x4_h = y0o_h + y4o_h;
                Vector128<int> x5_h = y1o_h + y5o_h;
                Vector128<int> x6_h = y2o_h + y5o_h;
                Vector128<int> x7_h = y3o_h + y4o_h;

                dct_bfly32o(x0_l, x0_h, x7_l, x7_h, bias, shift, out row0, out row7);
                dct_bfly32o(x1_l, x1_h, x6_l, x6_h, bias, shift, out row1, out row6);
                dct_bfly32o(x2_l, x2_h, x5_l, x5_h, bias, shift, out row2, out row5);
                dct_bfly32o(x3_l, x3_h, x4_l, x4_h, bias, shift, out row3, out row4);
            }

            // column pass
            dct_pass(bias_0, 10);

            {
                // 16bit 8x8 transpose pass 1
                dct_interleave16(ref row0, ref row4);
                dct_interleave16(ref row1, ref row5);
                dct_interleave16(ref row2, ref row6);
                dct_interleave16(ref row3, ref row7);

                // transpose pass 2
                dct_interleave16(ref row0, ref row2);
                dct_interleave16(ref row1, ref row3);
                dct_interleave16(ref row4, ref row6);
                dct_interleave16(ref row5, ref row7);

                // transpose pass 3
                dct_interleave16(ref row0, ref row1);
                dct_interleave16(ref row2, ref row3);
                dct_interleave16(ref row4, ref row5);
                dct_interleave16(ref row6, ref row7);
            }

            // row pass
            dct_pass(bias_1, 17);

            {
                // pack
                Vector128<byte> p0 = V128Helper.PackUnsignedSaturate(row0, row1); // a0a1a2a3...a7b0b1b2b3...b7
                Vector128<byte> p1 = V128Helper.PackUnsignedSaturate(row2, row3);
                Vector128<byte> p2 = V128Helper.PackUnsignedSaturate(row4, row5);
                Vector128<byte> p3 = V128Helper.PackUnsignedSaturate(row6, row7);

                // 8bit 8x8 transpose pass 1
                dct_interleave8(ref p0, ref p2); // a0e0a1e1...
                dct_interleave8(ref p1, ref p3); // c0g0c1g1...

                // transpose pass 2
                dct_interleave8(ref p0, ref p1); // a0c0e0g0...
                dct_interleave8(ref p2, ref p3); // b0d0f0h0...

                // transpose pass 3
                dct_interleave8(ref p0, ref p2); // a0b0c0d0...
                dct_interleave8(ref p1, ref p3); // a4b4c4d4...

                // store
                Span<byte> dsts = dst[..(dstStride * 7 + 8)];
                p0.GetLower().CopyTo(dsts);
                p0.GetUpper().CopyTo(dsts.Slice(dstStride * 1));
                p2.GetLower().CopyTo(dsts.Slice(dstStride * 2));
                p2.GetUpper().CopyTo(dsts.Slice(dstStride * 3));
                p1.GetLower().CopyTo(dsts.Slice(dstStride * 4));
                p1.GetUpper().CopyTo(dsts.Slice(dstStride * 5));
                p3.GetLower().CopyTo(dsts.Slice(dstStride * 6));
                p3.GetUpper().CopyTo(dsts.Slice(dstStride * 7));
            }
        }

        /// <summary>
        /// If there's a pending marker from the entropy stream, return it,
        /// otherwise fetch a new marker from the stream. If there's no marker, 
        /// return <see cref="NoneMarker"/>, which is never a valid marker value.
        /// </summary>
        private static byte ReadMarker(JpegState state)
        {
            byte x = state.marker;
            if (x != NoneMarker)
            {
                state.marker = NoneMarker;
                return x;
            }

            x = state.Reader.ReadByte();
            if (x != NoneMarker)
                return NoneMarker;

            while (x == NoneMarker)
                x = state.Reader.ReadByte(); // consume repeated fill bytes

            return x;
        }

        /// <summary>
        /// Reset the entropy decoder and the dc prediction after a restart interval.
        /// </summary>
        private static void Reset(JpegState state)
        {
            state.code_bits = 0;
            state.code_buffer = 0;
            state.nomore = false;
            for (int i = 0; i < state.components.Length; i++)
                state.components[i].dc_pred = 0;
            state.marker = NoneMarker;
            state.todo = state.restart_interval != 0 ? state.restart_interval : 0x7fffffff;
            state.eob_run = 0;

            // no more than 1<<31 MCUs if no restart_interal? that's plenty safe,
            // since we don't even allow 1<<30 pixels
        }

        private static bool ParseEntropyCodedData(JpegState state)
        {
            Reset(state);

            if (!state.progressive)
            {
                Span<short> data = stackalloc short[64];

                if (state.scan_n == 1)
                {
                    // non-interleaved data, we just need to process one block at a time,
                    // in trivial scanline order
                    // number of blocks to do just depends on how many actual "pixels" this
                    // component has, independent of interleaved MCU blocking and such

                    int n = state.order[0];
                    ref ImageComponent component = ref state.components[n];
                    int w = (component.x + 7) / 8;
                    int h = (component.y + 7) / 8;

                    int ha = component.ha;
                    Huffman hdc = state.huff_dc[component.hd];
                    Huffman hac = state.huff_ac[ha];
                    ReadOnlySpan<short> fastAc = state.fast_ac[ha];
                    ReadOnlySpan<ushort> dequant = state.dequant[component.tq];

                    Span<byte> componentData = component.data.Span;

                    for (int j = 0; j < h; ++j)
                    {
                        for (int i = 0; i < w; ++i)
                        {
                            DecodeBlock(
                                state,
                                data,
                                hdc,
                                hac,
                                fastAc,
                                n,
                                dequant);

                            IdctBlock(
                                componentData.Slice(component.w2 * j * 8 + i * 8),
                                component.w2,
                                data);

                            // every data block is an MCU, so countdown the restart interval
                            if (--state.todo <= 0)
                            {
                                if (state.code_bits < 24)
                                    GrowBufferUnsafe(state);

                                // if it's NOT a restart, then just bail, so we get corrupt data
                                // rather than no data
                                if (!IsRestart(state.marker))
                                    return true;

                                Reset(state);
                            }
                        }
                    }
                }
                else
                {
                    // interleaved

                    for (int j = 0; j < state.img_mcu_y; ++j)
                    {
                        for (int i = 0; i < state.img_mcu_x; ++i)
                        {
                            // scan an interleaved mcu... process scan_n components in order

                            for (int k = 0; k < state.scan_n; ++k)
                            {
                                // scan out an mcu's worth of this component; that's just determined
                                // by the basic H and V specified for the component

                                int n = state.order[k];
                                ref ImageComponent component = ref state.components[n];

                                int ha = component.ha;
                                Huffman hdc = state.huff_dc[component.hd];
                                Huffman hac = state.huff_ac[ha];
                                ReadOnlySpan<short> fastAc = state.fast_ac[ha];
                                ReadOnlySpan<ushort> dequant = state.dequant[component.tq];

                                Span<byte> componentData = component.data.Span;

                                for (int y = 0; y < component.v; ++y)
                                {
                                    for (int x = 0; x < component.h; ++x)
                                    {
                                        int bx = (i * component.h + x) * 8;
                                        int by = (j * component.v + y) * 8;

                                        DecodeBlock(
                                            state,
                                            data,
                                            hdc,
                                            hac,
                                            fastAc,
                                            n,
                                            dequant);

                                        IdctBlock(
                                            componentData.Slice(component.w2 * by + bx),
                                            component.w2,
                                            data);
                                    }
                                }
                            }

                            // after all interleaved components, that's an interleaved MCU,
                            // so now count down the restart interval
                            if (--state.todo <= 0)
                            {
                                if (state.code_bits < 24)
                                    GrowBufferUnsafe(state);

                                if (!IsRestart(state.marker))
                                    return true;
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
                    // non-interleaved data, we just need to process one block at a time,
                    // in trivial scanline order
                    // number of blocks to do just depends on how many actual "pixels" this
                    // component has, independent of interleaved MCU blocking and such

                    int n = state.order[0];
                    ref ImageComponent component = ref state.components[n];
                    int w = (component.x + 7) / 8;
                    int h = (component.y + 7) / 8;
                    Span<short> coeff16 = MemoryMarshal.Cast<byte, short>(component.coeff.Span);

                    int ha = component.ha;
                    Huffman hdc = state.huff_dc[component.hd];
                    Huffman hac = state.huff_ac[ha];
                    ReadOnlySpan<short> fastAc = state.fast_ac[ha];

                    for (int j = 0; j < h; ++j)
                    {
                        for (int i = 0; i < w; ++i)
                        {
                            Span<short> data = coeff16.Slice(
                                64 * (i + j * component.coeff_w),
                                64);

                            if (state.spec_start == 0)
                                DecodeBlockProgressiveDc(state, data, hdc, n);
                            else
                                DecodeBlockProggressiveAc(state, data, hac, fastAc);

                            // every data block is an MCU, so countdown the restart interval
                            if (--state.todo <= 0)
                            {
                                if (state.code_bits < 24)
                                    GrowBufferUnsafe(state);

                                if (!IsRestart(state.marker))
                                    return true;
                                Reset(state);
                            }
                        }
                    }
                }
                else
                {
                    // interleaved

                    for (int j = 0; j < state.img_mcu_y; ++j)
                    {
                        for (int i = 0; i < state.img_mcu_x; ++i)
                        {
                            // scan an interleaved mcu... process scan_n components in order

                            for (int k = 0; k < state.scan_n; ++k)
                            {
                                // scan out an mcu's worth of this component; that's just determined
                                // by the basic H and V specified for the component

                                int n = state.order[k];
                                ImageComponent component = state.components[n];
                                Span<short> coeff16 = MemoryMarshal.Cast<byte, short>(component.coeff.Span);

                                Huffman hdc = state.huff_dc[component.hd];

                                for (int y = 0; y < component.v; ++y)
                                {
                                    for (int x = 0; x < component.h; ++x)
                                    {
                                        int x2 = i * component.h + x;
                                        int y2 = j * component.v + y;
                                        Span<short> data = coeff16.Slice(
                                            64 * (x2 + y2 * component.coeff_w),
                                            64);

                                        DecodeBlockProgressiveDc(state, data, hdc, n);
                                    }
                                }
                            }

                            // after all interleaved components, that's an interleaved MCU,
                            // so now count down the restart interval
                            if (--state.todo <= 0)
                            {
                                if (state.code_bits < 24)
                                    GrowBufferUnsafe(state);

                                if (!IsRestart(state.marker))
                                    return true;
                                Reset(state);
                            }
                        }
                    }
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Dequantize(Span<short> data, ReadOnlySpan<ushort> dequant)
        {
            data = data[..64];
            dequant = dequant[..64];

            (Vector512.Create<short>(data) * Vector512.Create(dequant).AsInt16()).CopyTo(data);
            (Vector512.Create<short>(data.Slice(32)) * Vector512.Create(dequant.Slice(32)).AsInt16()).CopyTo(data);
        }

        public static void FinishProgresive(JpegState z)
        {
            Debug.Assert(z.progressive);

            for (int n = 0; n < z.State.Components; ++n)
            {
                ref ImageComponent component = ref z.components[n];
                int w = (component.x + 7) / 8;
                int h = (component.y + 7) / 8;

                Span<short> coeff_data = MemoryMarshal.Cast<byte, short>(component.coeff.Span);
                Span<byte> componentData = component.data.Span;

                for (int j = 0; j < h; ++j)
                {
                    for (int i = 0; i < w; i++)
                    {
                        Span<short> data = coeff_data.Slice(64 * (i + j * component.coeff_w), 64);

                        Dequantize(data, z.dequant[component.tq]);

                        IdctBlock(
                            componentData.Slice(component.w2 * j * 8 + i * 8),
                            component.w2,
                            data);
                    }
                }
            }
        }

        private static bool ProcessMarker(JpegState z, byte m)
        {
            ArgumentNullException.ThrowIfNull(z);

            ImageBinReader s = z.Reader;
            int length;
            switch (m)
            {
                case NoneMarker:
                    StbImageReadException.Throw(ErrorCode.MarkerExpected);
                    return false;

                case 0xDD:
                    if (s.ReadInt16BE() != 4)
                        StbImageReadException.Throw(ErrorCode.BadDRILength);

                    z.restart_interval = s.ReadInt16BE();
                    return true;

                case 0xDB:
                    length = s.ReadInt16BE() - 2;
                    while (length > 0)
                    {
                        int q = s.ReadByte();
                        int p = q >> 4;
                        if ((p != 0) && (p != 1))
                            StbImageReadException.Throw(ErrorCode.BadDQTType);

                        int t = q & 15;
                        if (t > 3)
                            StbImageReadException.Throw(ErrorCode.BadDQTTable);

                        bool sixteen = p != 0;
                        for (int i = 0; i < 64; i++)
                        {
                            z.dequant[t][DeZigZag[i]] = (ushort)(sixteen
                                ? s.ReadInt16BE()
                                : s.ReadByte());
                        }
                        length -= sixteen ? 129 : 65;
                    }
                    return length == 0;

                case 0xC4:
                    Span<int> sizes = stackalloc int[16];
                    length = s.ReadInt16BE() - 2;
                    while (length > 0)
                    {
                        int q = s.ReadByte();
                        int tc = q >> 4;
                        int th = q & 15;
                        if ((tc > 1) || (th > 3))
                            StbImageReadException.Throw(ErrorCode.BadDHTHeader);

                        int n = 0;
                        for (int i = 0; i < sizes.Length; ++i)
                        {
                            sizes[i] = s.ReadByte();
                            n += sizes[i];
                        }

                        Huffman[] huff;

                        length -= 17;
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
                        length -= n;
                    }
                    return length == 0;
            }

            if (((m >= 0xE0) && (m <= 0xEF)) || (m == 0xFE))
            {
                length = s.ReadInt16BE() - 2;
                if (length < 0)
                {
                    if (z.SkipInvalidMarkerLength)
                        return true;

                    if (m == 0xFE)
                        StbImageReadException.Throw(ErrorCode.BadCOMLength);
                    else
                        StbImageReadException.Throw(ErrorCode.BadAPPLength);
                }

                if ((m == 0xE0) && (length >= 5))
                {
                    int ok = 1;
                    int i;
                    for (i = 0; i < 5; ++i)
                    {
                        if (s.ReadByte() != JfifTag[i])
                            ok = 0;
                    }

                    length -= 5;
                    if (ok != 0)
                        z.jfif = 1;
                }
                else if ((m == 0xEE) && (length >= 12))
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

                    length -= 6;
                    if (ok)
                    {
                        s.ReadByte();
                        s.ReadInt16BE();
                        s.ReadInt16BE();
                        z.app14_color_transform = s.ReadByte();
                        length -= 6;
                    }
                }

                s.Skip(length);
                return true;
            }

            if (!z.SkipInvalidMarker)
            {
                StbImageReadException.Throw(ErrorCode.UnknownMarker);
            }
            return true;
        }

        public static bool ProcessScanHeader(JpegState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            ImageBinReader redaer = state.Reader;

            int Ls = redaer.ReadInt16BE();
            state.scan_n = redaer.ReadByte();
            if ((state.scan_n < 1) || (state.scan_n > 4) || (state.scan_n > state.State.Components))
                StbImageReadException.Throw(ErrorCode.BadSOSComponentCount);

            if (Ls != 6 + 2 * state.scan_n)
                StbImageReadException.Throw(ErrorCode.BadSOSLength);

            for (int i = 0; i < state.scan_n; ++i)
            {
                int id = redaer.ReadByte();
                int q = redaer.ReadByte();

                int which;
                for (which = 0; which < state.State.Components; ++which)
                {
                    if (state.components[which].id == id)
                        break;
                }

                if (which == state.State.Components)
                    return false;

                state.components[which].hd = q >> 4;
                if (state.components[which].hd > 3)
                    StbImageReadException.Throw(ErrorCode.BadDCHuffman);

                state.components[which].ha = q & 15;
                if (state.components[which].ha > 3)
                    StbImageReadException.Throw(ErrorCode.BadACHuffman);

                state.order[i] = which;
            }

            {
                state.spec_start = redaer.ReadByte();
                state.spec_end = redaer.ReadByte();
                int aa = redaer.ReadByte();
                state.succ_high = aa >> 4;
                state.succ_low = aa & 15;

                if (state.progressive)
                {
                    if ((state.spec_start > 63) ||
                        (state.spec_end > 63) ||
                        (state.spec_start > state.spec_end) ||
                        (state.succ_high > 13) ||
                        (state.succ_low > 13))
                        StbImageReadException.Throw(ErrorCode.BadSOS);
                }
                else
                {
                    if (state.spec_start != 0)
                        StbImageReadException.Throw(ErrorCode.BadSOS);

                    if ((state.succ_high != 0) || (state.succ_low != 0))
                        StbImageReadException.Throw(ErrorCode.BadSOS);

                    state.spec_end = 63;
                }
            }

            return true;
        }

        public static void FreeComponents(JpegState state, int ncomp)
        {
            ArgumentNullException.ThrowIfNull(state);

            for (int i = 0; i < ncomp; i++)
            {
                ref ImageComponent comp = ref state.components[i];

                if (comp.raw_data != null)
                {
                    state.BytePool.Return(comp.raw_data);
                    comp.raw_data = null;
                    comp.data = null;
                }

                if (comp.raw_coeff != null)
                {
                    state.BytePool.Return(comp.raw_coeff);
                    comp.raw_coeff = null;
                    comp.coeff = null;
                }

                if (comp.raw_linebuf != null)
                {
                    state.BytePool.Return(comp.raw_linebuf);
                    comp.raw_linebuf = null;
                    comp.linebuf = null;
                }
            }
        }

        public static void ProcessFrameHeader(JpegState z, ScanMode scan)
        {
            ArgumentNullException.ThrowIfNull(z);

            ImageBinReader s = z.Reader;

            int Lf = s.ReadInt16BE();
            if (Lf < 11)
                StbImageReadException.Throw(ErrorCode.BadSOFLength);

            int p = s.ReadByte();
            if (p != 8)
                StbImageReadException.Throw(ErrorCode.UnsupportedBitDepth);

            z.State.Height = s.ReadUInt16BE();
            if (z.State.Height == 0)
                StbImageReadException.Throw(ErrorCode.ZeroHeight);

            z.State.Width = s.ReadUInt16BE();
            if (z.State.Width == 0)
                StbImageReadException.Throw(ErrorCode.ZeroWidth);

            z.State.Components = s.ReadByte();
            if ((z.State.Components != 1) &&
                (z.State.Components != 3) &&
                (z.State.Components != 4))
                StbImageReadException.Throw(ErrorCode.BadComponentCount);

            for (int i = 0; i < z.State.Components; ++i)
            {
                z.components[i].data = null;
                z.components[i].linebuf = null;
            }

            if (Lf != 8 + 3 * z.State.Components)
                StbImageReadException.Throw(ErrorCode.BadSOFLength);

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
                    StbImageReadException.Throw(ErrorCode.BadH);

                comp.v = q & 15;
                if ((comp.v == 0) || (comp.v > 4))
                    StbImageReadException.Throw(ErrorCode.BadV);

                comp.tq = s.ReadByte();
                if (comp.tq > 3)
                    StbImageReadException.Throw(ErrorCode.BadTQ);
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
            ArgumentNullException.ThrowIfNull(z);

            z.jfif = 0;
            z.app14_color_transform = -1;
            z.marker = NoneMarker; // initialize cached marker to empty

            byte m = ReadMarker(z);
            if (m != 0xd8)
                StbImageReadException.Throw(ErrorCode.NoSOI);

            m = ReadMarker(z);
            while (m is not (0xc0 or 0xc1 or 0xc2))
            {
                if (!ProcessMarker(z, m))
                    return false;

                do
                {
                    // some files have extra padding after their blocks, so ok, we'll scan
                    m = ReadMarker(z);
                }
                while (m == NoneMarker);
            }

            z.progressive = m == 0xc2;

            ProcessFrameHeader(z, scan);

            z.State.Depth = 8;

            z.State.OutComponents = z.State.Components >= 3 ? 3 : 1;
            z.State.OutDepth = z.State.Depth;

            z.State.StateReady();

            return true;
        }

        /// <summary>
        /// Decode image to YCbCr format.
        /// </summary>
        private static bool ParseData(JpegState state)
        {
            FreeComponents(state, 4);
            state.restart_interval = 0;

            if (!ParseHeader(state, (int)ScanMode.Load))
                return false;

            ImageBinReader s = state.Reader;
            byte m = ReadMarker(state);
            while (m != 0xd9)
            {
                if (m == 0xda)
                {
                    if (!ProcessScanHeader(state))
                        return false;

                    if (!ParseEntropyCodedData(state))
                        return false;

                    if (state.marker == NoneMarker)
                    {
                        // handle 0s at the end of image data from IP Kamera 9060
                        while (s.ReadByte() != NoneMarker) { }

                        state.marker = s.ReadByte();
                        // if we reach eof without hitting a marker, 
                        // ReadMarker() below will fail and we'll eventually return 0
                    }
                }
                else if (m == 0xdc)
                {
                    int Ld = s.ReadInt16BE();
                    int NL = s.ReadInt16BE();

                    if (Ld != 4)
                        StbImageReadException.Throw(ErrorCode.BadDNLLength);

                    if (NL != state.State.Height)
                        StbImageReadException.Throw(ErrorCode.BadDNLHeight);
                }
                else
                {
                    if (!ProcessMarker(state, m))
                        return false;
                }

                m = ReadMarker(state);
            }

            if (state.progressive)
                FinishProgresive(state);

            state.is_rgb = state.State.Components == 3 && (state.rgb == 3 || (state.app14_color_transform == 0 && state.jfif == 0));
            state.decode_n = (state.State.Components < 3 && !state.is_rgb) ? 1 : state.State.Components;

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
            Span<byte> o = dst.Span;
            Span<byte> n = inNear.Span;
            Span<byte> f = inFar.Span;

            for (int i = 0; i < w; ++i)
            {
                o[i] = (byte)((3 * n[i] + f[i] + 2) >> 2);
            }

            return dst;
        }

        public static Memory<byte> ResampleRowH2(
            Memory<byte> dst, Memory<byte> inNear, Memory<byte> inFar, int w, int hs)
        {
            Span<byte> o = dst.Span;
            Span<byte> input = inNear.Span;
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static Memory<byte> ResampleRowHV2(
            Memory<byte> destination, Memory<byte> inputNear, Memory<byte> inputFar, int w, int hs)
        {
            Span<byte> dst = destination.Span;
            Span<byte> near = inputNear.Span;
            Span<byte> far = inputFar.Span;

            // need to generate 2x2 samples for every one in input
            if (w == 1)
            {
                dst[0] = dst[1] = (byte)((3 * near[0] + far[0] + 2) / 4);
                return destination;
            }

            int i = 0;
            int t1 = 3 * near[0] + far[0];

            // Intrinsics process groups of 8 pixels for as long as they can.
            // Note they can't handle the last pixel in a row in this loop
            // because they need to handle the filter boundary conditions.
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<short> bias = Vector128.Create((short)8);

                for (; i < ((w - 1) & ~7); i += 8)
                {
                    // load and perform the vertical filtering pass
                    // this uses 3*x + y = 4*x + (y - x)
                    Vector128<byte> farb = Vector128.CreateScalar(MemoryMarshal.Read<long>(far.Slice(i))).AsByte();
                    Vector128<byte> nearb = Vector128.CreateScalar(MemoryMarshal.Read<long>(near.Slice(i))).AsByte();
                    Vector128<short> farw = V128Helper.UnpackLow(farb, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> nearw = V128Helper.UnpackLow(nearb, Vector128<byte>.Zero).AsInt16();
                    Vector128<short> diff = farw - nearw;
                    Vector128<short> nears = nearw << 2;
                    Vector128<short> curr = nears + diff; // current row

                    // horizontal filter works the same based on shifted vers of current
                    // row. "prev" is current row shifted right by 1 pixel; we need to
                    // insert the previous pixel value (from t1).
                    // "next" is current row shifted left by 1 pixel, with first pixel
                    // of next block of 8 pixels added in.
                    Vector128<short> prv0 = V128Helper.ShiftBytesLeft(curr.AsByte(), 2).AsInt16();
                    Vector128<short> nxt0 = V128Helper.ShiftBytesRight(curr.AsByte(), 2).AsInt16();
                    Vector128<short> prev = prv0.WithElement(0, (short)t1);
                    Vector128<short> next = nxt0.WithElement(7, (short)(3 * near[i + 8] + far[i + 8]));

                    // horizontal filter, polyphase implementation since it's convenient:
                    // even pixels = 3*cur + prev = cur*4 + (prev - cur)
                    // odd  pixels = 3*cur + next = cur*4 + (next - cur)
                    // note the shared term.
                    Vector128<short> curs = curr << 2;
                    Vector128<short> prvd = prev - curr;
                    Vector128<short> nxtd = next - curr;
                    Vector128<short> curb = curs + bias;
                    Vector128<short> even = prvd + curb;
                    Vector128<short> odd = nxtd + curb;

                    // interleave even and odd pixels, then undo scaling.
                    Vector128<short> int0 = V128Helper.UnpackLow(even, odd);
                    Vector128<short> int1 = V128Helper.UnpackHigh(even, odd);
                    Vector128<short> de0 = int0 >>> 4;
                    Vector128<short> de1 = int1 >>> 4;

                    // pack and write output
                    Vector128<byte> outv = V128Helper.PackUnsignedSaturate(de0, de1);
                    outv.CopyTo(dst.Slice(i * 2));

                    // "previous" value for next iter
                    t1 = 3 * near[i + 7] + far[i + 7];
                }

                int t0 = t1;
                t1 = 3 * near[i] + far[i];
                dst[i * 2] = (byte)((3 * t1 + t0 + 8) / 16);
            }
            else
            {
                dst[0] = (byte)((t1 + 2) / 4);
            }

            for (i++; i < w; i++)
            {
                int t0 = t1;
                t1 = 3 * near[i] + far[i];
                dst[i * 2 - 1] = (byte)((3 * t0 + t1 + 8) / 16);
                dst[i * 2] = (byte)((3 * t1 + t0 + 8) / 16);
            }
            dst[w * 2 - 1] = (byte)((t1 + 2) / 4);

            return destination;
        }

        public static Memory<byte> ResampleRowGeneric(
            Memory<byte> destination,
            Memory<byte> inputNear,
            Memory<byte> inputFar,
            int w,
            int hs)
        {
            Span<byte> dst = destination.Span;
            Span<byte> inNear = inputNear.Span;

            for (int i = 0; i < w; i++)
            {
                byte near = inNear[i];
                dst.Slice(i * hs, hs).Fill(near);
            }

            return destination;
        }

        #endregion

        private const short YCrFactor = (int)(1.40200f * 4096f + 0.5f);
        private const short YCgFactor = -(int)(0.71414f * 4096f + 0.5f);
        private const short YCgbFactor = -(int)(0.34414f * 4096f + 0.5f);
        private const short YBFactor = (int)(1.77200f * 4096f + 0.5f);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void YCbCrToRGB(
            Span<byte> dst, Span<byte> y, Span<byte> pcb, Span<byte> pcr)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                // this is a fairly straightforward implementation and not super-optimized.
                Vector128<short> cr_const0 = Vector128.Create(YCrFactor);
                Vector128<short> cr_const1 = Vector128.Create(YCgFactor);
                Vector128<short> cb_const0 = Vector128.Create(YCgbFactor);
                Vector128<short> cb_const1 = Vector128.Create(YBFactor);

                // Check if we have space for 28 elements,
                // as we write 16 bytes (12 significant) at a time.
                while (dst.Length >= 28)
                {
                    // load
                    Vector128<byte> y_bytes = Vector128.CreateScalar(MemoryMarshal.Read<long>(y)).AsByte();
                    Vector128<byte> cr_bytes = Vector128.CreateScalar(MemoryMarshal.Read<long>(pcr)).AsByte();
                    Vector128<byte> cb_bytes = Vector128.CreateScalar(MemoryMarshal.Read<long>(pcb)).AsByte();
                    Vector128<byte> signflip = Vector128.Create((sbyte)-128).AsByte();
                    Vector128<byte> cr_biased = cr_bytes ^ signflip;
                    Vector128<byte> cb_biased = cb_bytes ^ signflip;

                    // unpack to short (and left-shift cr, cb by 8)
                    Vector128<byte> y_bias = Vector128.Create((byte)128);
                    Vector128<short> yw = V128Helper.UnpackLow(y_bias, y_bytes).AsInt16();
                    Vector128<short> crw = V128Helper.UnpackLow(Vector128<byte>.Zero, cr_biased).AsInt16();
                    Vector128<short> cbw = V128Helper.UnpackLow(Vector128<byte>.Zero, cb_biased).AsInt16();

                    // color transform
                    Vector128<short> yws = yw >>> 4;
                    Vector128<short> cr0 = V128Helper.MultiplyHigh(cr_const0, crw);
                    Vector128<short> cb0 = V128Helper.MultiplyHigh(cb_const0, cbw);
                    Vector128<short> cr1 = V128Helper.MultiplyHigh(crw, cr_const1);
                    Vector128<short> cb1 = V128Helper.MultiplyHigh(cbw, cb_const1);
                    Vector128<short> rws = cr0 + yws;
                    Vector128<short> gwt = cb0 + yws;
                    Vector128<short> bws = yws + cb1;
                    Vector128<short> gws = gwt + cr1;

                    // descale
                    Vector128<short> rw = rws >> 4;
                    Vector128<short> bw = bws >> 4;
                    Vector128<short> gw = gws >> 4;

                    // back to byte, set up for transpose
                    Vector128<byte> brb = V128Helper.PackUnsignedSaturate(rw, bw);
                    Vector128<byte> gxb = V128Helper.PackUnsignedSaturate(gw, Vector128<short>.Zero);

                    // transpose to interleave channels
                    Vector128<short> t0 = V128Helper.UnpackLow(brb, gxb).AsInt16();
                    Vector128<short> t1 = V128Helper.UnpackHigh(brb, gxb).AsInt16();
                    Vector128<byte> o0 = V128Helper.UnpackLow(t0, t1).AsByte();
                    Vector128<byte> o1 = V128Helper.UnpackHigh(t0, t1).AsByte();

                    Vector128<byte> shuffleMask = Vector128.Create(
                        (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 3, 7, 11, 15);
                    o0 = Vector128.Shuffle(o0, shuffleMask);
                    o1 = Vector128.Shuffle(o1, shuffleMask);

                    o0.CopyTo(dst); // Write 16 bytes (12 significant)
                    o1.CopyTo(dst.Slice(12)); // Write 16 bytes (12 significant), offset 12 bytes after the first

                    dst = dst.Slice(24); // Increment by 24 as we only wrote 12+12 significant
                    y = y.Slice(8);
                    pcr = pcr.Slice(8);
                    pcb = pcb.Slice(8);
                }
            }

            int x = 0;
            while (dst.Length >= 3)
            {
                int y_fixed = (y[x] << 20) + (1 << 19);
                int cr = pcr[x] - 128;
                int cb = pcb[x] - 128;

                int r = y_fixed + cr * (YCrFactor << 8);
                int g = y_fixed + cr * (YCgFactor << 8) + ((cb * (YCgbFactor << 8)) & unchecked((int)0xffff0000));
                int b = y_fixed + cb * (YBFactor << 8);

                dst[0] = byte.CreateSaturating(r >> 20);
                dst[1] = byte.CreateSaturating(g >> 20);
                dst[2] = byte.CreateSaturating(b >> 20);

                dst = dst.Slice(3);
                x += 1;
            }
        }

        public static void Cleanup(JpegState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            FreeComponents(state, state.State.Components);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Blinn8x8(byte x, byte y)
        {
            uint t = (uint)(x * y + 128);
            return (byte)((t + (t >> 8)) >> 8);
        }

        public static void LoadImage(JpegState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            try
            {
                if (ParseData(state))
                    ProcessData(state);
            }
            finally
            {
                Cleanup(state);
            }
        }

        private static void ProcessData(JpegState state)
        {
            ResampleData[] res_comp = new ResampleData[state.decode_n];
            for (int k = 0; k < res_comp.Length; k++)
            {
                ref ImageComponent comp = ref state.components[k];
                int lineBufLen = state.State.Width + 3;
                comp.raw_linebuf = state.BytePool.Rent(lineBufLen);
                comp.linebuf = comp.raw_linebuf.AsMemory(0, lineBufLen);

                ref ResampleData r = ref res_comp[k];
                r.hs = state.img_h_max / comp.h;
                r.vs = state.img_v_max / comp.v;
                r.ystep = r.vs >> 1;
                r.w_lores = (state.State.Width + r.hs - 1) / r.hs;
                r.ypos = 0;
                r.line0 = r.line1 = comp.data;

                if ((r.hs == 1) && (r.vs == 1))
                    r.Resample = ResampleRow1;
                else if ((r.hs == 1) && (r.vs == 2))
                    r.Resample = ResampleRowV2;
                else if ((r.hs == 2) && (r.vs == 1))
                    r.Resample = ResampleRowH2;
                else if ((r.hs == 2) && (r.vs == 2))
                    r.Resample = ResampleRowHV2;
                else
                    r.Resample = ResampleRowGeneric;
            }

            Memory<byte>[] coutput = new Memory<byte>[JpegState.CompCount];
            int outStride = state.State.OutComponents * state.State.Width;
            byte[] pooledRowBuffer = state.BytePool.Rent(outStride);
            Span<byte> rowBuffer = pooledRowBuffer.AsSpan(0, outStride);
            try
            {
                for (int j = 0; j < state.State.Height; ++j)
                {
                    for (int k = 0; k < res_comp.Length; ++k)
                    {
                        ref ResampleData r = ref res_comp[k];
                        ImageComponent component = state.components[k];
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

                    Span<byte> co0 = coutput[0].Span;
                    Span<byte> co1 = coutput[1].Span;
                    Span<byte> co2 = coutput[2].Span;
                    Span<byte> co3 = coutput[3].Span;

                    // TODO: validate/improve rowBuffer slicing
                    if (state.State.Components == 3)
                    {
                        if (state.is_rgb)
                        {
                            for (int i = 0, x = 0; i < rowBuffer.Length; i += 3, x++)
                            {
                                rowBuffer[0 + i] = co0[x];
                                rowBuffer[1 + i] = co1[x];
                                rowBuffer[2 + i] = co2[x];
                            }
                        }
                        else
                        {
                            YCbCrToRGB(rowBuffer, co0, co1, co2);
                        }
                    }
                    else if (state.State.Components == 4)
                    {
                        if (state.app14_color_transform == 0)
                        {
                            for (int i = 0, x = 0; i < rowBuffer.Length; i += 3, x++)
                            {
                                byte m = co3[i];
                                rowBuffer[2 + i] = Blinn8x8(co2[x], m);
                                rowBuffer[1 + i] = Blinn8x8(co1[x], m);
                                rowBuffer[0 + i] = Blinn8x8(co0[x], m);
                            }
                        }
                        else if (state.app14_color_transform == 2)
                        {
                            YCbCrToRGB(rowBuffer, co0, co1, co2);

                            for (int i = 0; i < rowBuffer.Length; i += 3)
                            {
                                byte m = co3[i];
                                rowBuffer[0 + i] = Blinn8x8((byte)(255 - rowBuffer[0 + i]), m);
                                rowBuffer[1 + i] = Blinn8x8((byte)(255 - rowBuffer[1 + i]), m);
                                rowBuffer[2 + i] = Blinn8x8((byte)(255 - rowBuffer[2 + i]), m);
                            }
                        }
                        else
                        {
                            YCbCrToRGB(rowBuffer, co0, co1, co2);
                        }
                    }
                    else
                    {
                        state.State.OutputPixelLine(AddressingMajor.Row, j, 0, co0.Slice(0, outStride));
                        continue;
                    }

                    state.State.OutputPixelLine(AddressingMajor.Row, j, 0, rowBuffer);
                }
            }
            finally
            {
                state.BytePool.Return(pooledRowBuffer);
            }
        }

        public static void Load(
            ImageBinReader reader, ReadState state, ArrayPool<byte>? arrayPool = null)
        {
            using (var j = new JpegState(reader, state, arrayPool))
                LoadImage(j);
        }

        public static void InfoCore(JpegState state)
        {
            if (!ParseHeader(state, ScanMode.Header))
                StbImageReadException.Throw(ErrorCode.Undefined);
        }

        public static void Info(
            ImageBinReader reader, out ReadState state, ArrayPool<byte>? arrayPool = null)
        {
            state = new ReadState();

            using (var j = new JpegState(reader, state, arrayPool))
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
