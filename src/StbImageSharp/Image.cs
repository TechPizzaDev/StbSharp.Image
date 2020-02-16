using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace StbSharp
{
    public static unsafe partial class StbImage
    {
        public static string LastError;

        public delegate int ReadCallback(ReadContext context, Span<byte> data);
        public delegate int SkipCallback(ReadContext context, int n);

        public delegate void BufferReadyCallback(int width, int height, void* buffer);
        public delegate void ReadProgressCallback(double progress, Rect? rect);

        public class ReadContext : IDisposable
        {
            public readonly Stream Stream;
            public readonly byte[] ReadBuffer;
            public readonly CancellationToken CancellationToken;

            public readonly ReadCallback Read;
            public readonly SkipCallback Skip;
            public bool ReadFromCallbacks;

            public readonly int DataLength;
            public byte* DataOriginalStart { get; private set; }
            public readonly byte* DataOriginalEnd;
            public byte* DataStart;
            public byte* Data;
            public byte* DataEnd;

            public bool vertically_flip_on_load = false;
            public bool unpremultiply_on_load = true;
            public bool de_iphone_flag = true;

            public ReadContext(byte* data, int len, CancellationToken cancellationToken)
            {
                ReadFromCallbacks = false;
                Read = null;
                Skip = null;
                CancellationToken = cancellationToken;

                DataLength = len;
                DataStart = null;
                DataOriginalStart = data;
                Data = DataOriginalStart;
                DataEnd = DataOriginalEnd = data + len;
            }

            public ReadContext(
                Stream stream, byte[] readBuffer, CancellationToken cancellationToken,
                ReadCallback read, SkipCallback skip)
            {
                Stream = stream;
                ReadBuffer = readBuffer;
                CancellationToken = cancellationToken;

                Read = read;
                Skip = skip;
                ReadFromCallbacks = true;

                DataLength = 256;
                DataOriginalStart = (byte*)CRuntime.MAlloc(DataLength);

                DataStart = DataOriginalStart;
                stbi__refill_buffer(this);
                DataOriginalEnd = DataEnd;
            }


            protected virtual void Dispose(bool disposing)
            {
                CRuntime.Free(DataOriginalStart);
                DataOriginalStart = null;
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            ~ReadContext()
            {
                Dispose(false);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ReadState
        {
            public readonly int? RequestedComponents;
            public readonly int? RequestedDepth;

            public readonly BufferReadyCallback BufferReady;
            public readonly ReadProgressCallback Progress;

            public int Width;
            public int Height;
            public int Depth;
            public int Components;

            public int OutDepth;
            public int OutComponents;

            public ReadState(
                int? requestedComponents,
                int? requestedDepth,
                BufferReadyCallback onBufferReady = null,
                ReadProgressCallback onProgress = null) : this()
            {
                RequestedComponents = requestedComponents;
                RequestedDepth = requestedDepth;
                BufferReady = onBufferReady;
                Progress = onProgress;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Rect
        {
            public readonly int X;
            public readonly int Y;
            public readonly int W;
            public readonly int H;

            public Rect(int x, int y, int w, int h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }
        }

        private static int stbi__err(string str)
        {
            LastError = str;
            return 0;
        }

        public delegate void IdctBlockKernel(byte* output, int out_stride, short* data);

        public delegate void YCbCrToRgbKernel(
            byte* output, byte* y, byte* pcb, byte* pcr, int count, int step);

        public delegate byte* ResamplerMethod(byte* a, byte* b, byte* c, int d, int e);

        private static readonly IdctBlockKernel _cached__idct_block = stbi__idct_block;
        private static readonly YCbCrToRgbKernel _cached__YCbCr_to_RGB_row = stbi__YCbCr_to_RGB_row;
        private static readonly ResamplerMethod _cached__resample_row_hv_2 = stbi__resample_row_hv_2;

        [StructLayout(LayoutKind.Sequential)]
        public struct JpegComponent
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

        public class JpegContext
        {
            public const int STBI__ZFAST_BITS = 9;

            public ReadContext s;
            public readonly stbi__huffman[] huff_dc = new stbi__huffman[4];
            public readonly stbi__huffman[] huff_ac = new stbi__huffman[4];
            public readonly ushort[][] dequant;

            public readonly short[][] fast_ac;

            // sizes for components, interleaved MCUs
            public int img_h_max, img_v_max;
            public int img_mcu_x, img_mcu_y;
            public int img_mcu_w, img_mcu_h;

            // definition of jpeg image component
            public JpegComponent[] img_comp = new JpegComponent[4];

            public uint code_buffer; // jpeg entropy-coded buffer
            public int code_bits; // number of valid bits
            public byte marker; // marker seen while filling entropy buffer
            public int nomore; // flag if we saw a marker so must stop

            public int progressive;
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

            public JpegContext(ReadContext context, ReadState readState)
            {
                s = context;
                ri = readState;

                idct_block_kernel = _cached__idct_block;
                YCbCr_to_RGB_kernel = _cached__YCbCr_to_RGB_row;
                resample_row_hv_2_kernel = _cached__resample_row_hv_2;

                for (var i = 0; i < 4; ++i)
                {
                    huff_ac[i] = new stbi__huffman();
                    huff_dc[i] = new stbi__huffman();
                }

                for (var i = 0; i < img_comp.Length; ++i)
                    img_comp[i] = new JpegComponent();

                fast_ac = new short[4][];
                for (var i = 0; i < fast_ac.Length; ++i)
                    fast_ac[i] = new short[1 << STBI__ZFAST_BITS];

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

        [StructLayout(LayoutKind.Sequential)]
        public struct GifLzw
        {
            public short prefix;
            public byte first;
            public byte suffix;
        }

        public class GifContext : IDisposable
        {
            public byte* _out_;
            public byte* background;
            public byte* history;
            public int flags;
            public int bgindex;
            public int ratio;
            public int transparent;
            public int eflags;
            public int delay;
            public byte* pal;
            public byte* lpal;
            public GifLzw* codes;
            public byte* color_table;
            public int parse;
            public int step;
            public int lflags;
            public int start_x;
            public int start_y;
            public int max_x;
            public int max_y;
            public int cur_x;
            public int cur_y;
            public int line_size;

            public GifContext()
            {
                try
                {
                    pal = (byte*)CRuntime.MAlloc(256 * 4 * sizeof(byte));
                    lpal = (byte*)CRuntime.MAlloc(256 * 4 * sizeof(byte));
                    codes = (GifLzw*)CRuntime.MAlloc(8192 * sizeof(GifLzw));
                }
                catch
                {
                    Dispose();
                }
            }

            protected virtual void Dispose(bool disposing)
            {
                CRuntime.Free(pal);
                pal = null;

                CRuntime.Free(lpal);
                lpal = null;

                CRuntime.Free(codes);
                codes = null;
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            ~GifContext()
            {
                Dispose(false);
            }
        }
    }
}