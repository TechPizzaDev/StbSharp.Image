using System;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static unsafe partial class StbImage
	{
		public static string LastError;
        public static int stbi__vertically_flip_on_load;

        public const int STBI__ZFAST_BITS = 9;

		public delegate int ReadCallback(ReadContext context, Span<byte> data);
		public delegate int SkipCallback(ReadContext context, int n);
        public delegate void ReadProgressCallback(double progress, Rect? rect);

		public delegate void IdctBlockKernel(byte* output, int out_stride, short* data);

		public delegate void YCbCrToRgbKernel(
			byte* output, byte* y, byte* pcb, byte* pcr, int count, int step);

		public delegate byte* ResamplerMethod(byte* a, byte* b, byte* c, int d, int e);

        private static readonly IdctBlockKernel _cached__idct_block = stbi__idct_block;
        private static readonly YCbCrToRgbKernel _cached__YCbCr_to_RGB_row = stbi__YCbCr_to_RGB_row;
        private static readonly ResamplerMethod _cached__resample_row_hv_2 = stbi__resample_row_hv_2;

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

            public JpegContext(ReadContext ctx)
			{
                s = ctx;

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

				dequant = new ushort [4][];
				for (var i = 0; i < dequant.Length; ++i)
					dequant[i] = new ushort[64];
			}
		};

		public struct stbi__resample
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

        public struct GifContext
        {
            public int w;
            public int h;
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

            public static GifContext Create()
            {
                var g = new GifContext();
                try
                {
                    g.codes = (GifLzw*)CRuntime.malloc(8192 * sizeof(GifLzw));
                    g.pal = (byte*)CRuntime.malloc(256 * 4 * sizeof(byte));
                    g.lpal = (byte*)CRuntime.malloc(256 * 4 * sizeof(byte));
                    return g;
                }
                catch
                {
                    g.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (pal != null)
                {
                    CRuntime.free(pal);
                    pal = null;
                }

                if (lpal != null)
                {
                    CRuntime.free(lpal);
                    lpal = null;
                }

                if (codes != null)
                {
                    CRuntime.free(codes);
                    codes = null;
                }
            }
        }

		private static int stbi__err(string str)
		{
			LastError = str;
			return 0;
		}
	}
}