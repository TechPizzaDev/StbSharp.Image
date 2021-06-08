using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StbSharp.ImageRead
{
    public static unsafe class Gif
    {
        public const int HeaderSize = 6;

        [StructLayout(LayoutKind.Sequential)]
        public struct GifLzw
        {
            public short prefix;
            public byte first;
            public byte suffix;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rgba32
        {
            public byte R;
            public byte G;
            public byte B;
            public byte A;
        }

        public class GifState : IDisposable
        {
            public ArrayPool<byte>? Pool { get; }

            private byte[]? _store;

            public ref Rgba32 GlobalPalette => ref Unsafe.As<byte, Rgba32>(ref _store![0]);
            public ref Rgba32 LocalPalette => ref Unsafe.As<byte, Rgba32>(ref _store![256 * sizeof(Rgba32)]);
            public ref Rgba32 CurrentPalette => ref Unsafe.As<byte, Rgba32>(ref _store![256 * sizeof(Rgba32) * colorTableIndex]);
            public ref GifLzw Codes => ref Unsafe.As<byte, GifLzw>(ref _store![256 * sizeof(Rgba32) * 2]);

            public Span<Rgba32> GlobalPaletteSpan => MemoryMarshal.CreateSpan(ref GlobalPalette, GlobalPaletteSize);
            public Span<Rgba32> LocalPaletteSpan => MemoryMarshal.CreateSpan(ref LocalPalette, LocalPaletteSize);
            public Span<GifLzw> CodesSpan => MemoryMarshal.CreateSpan(ref Codes, 8192);

            public byte* _out_;
            public byte* history;
            public int flags;
            public int bgindex;
            public int ratio;
            public int transparentIndex;
            public int graphicControlFlags;
            public int delay;
            public int colorTableIndex;
            public int parse;
            public int step;
            public int localFlags;
            public int start_x;
            public int start_y;
            public int max_x;
            public int max_y;
            public int cur_x;
            public int cur_y;
            public int line_size;

            public int DisposeMethod => (graphicControlFlags & 0b11100) >> 2;
            public int GlobalPaletteSize => 2 << (flags & 0b111);
            public int LocalPaletteSize => 2 << (localFlags & 0b111);

            public GifState(ArrayPool<byte>? pool)
            {
                Pool = pool;
                if (Pool != null)
                {
                    int size = (256 * sizeof(Rgba32) * 2) + (8192 * sizeof(GifLzw));
                    _store = Pool.Rent(size);
                }
            }

            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (Pool != null)
                    {
                        Pool.Return(_store);
                        _store = null;
                    }
                }

                if (_out_ != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_out_);
                    _out_ = null;
                }
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        public static bool Test(ReadOnlySpan<byte> header)
        {
            if (header.Length < HeaderSize)
                return false;

            if ((header[0] != 'G') ||
                (header[1] != 'I') ||
                (header[2] != 'F') ||
                (header[3] != '8'))
                return false;

            byte version = header[4];
            if ((version != '7') && (version != '9'))
                return false;

            if (header[5] != 'a')
                return false;

            return true;
        }

        public static bool ParseHeader(ImageBinReader s, ReadState ri, GifState g)
        {
            Span<byte> tmp = stackalloc byte[HeaderSize];
            if (!s.TryReadBytes(tmp))
                return false;

            if (!Test(tmp))
                throw new StbImageReadException(ErrorCode.UnknownFormat);

            ri.Width = s.ReadUInt16LE();
            ri.Height = s.ReadUInt16LE();
            ri.Components = 4;
            ri.Depth = 8;

            g.flags = s.ReadByte();
            g.bgindex = s.ReadByte();
            g.ratio = s.ReadByte();
            g.transparentIndex = -1;

            if ((g.flags & 0x80) != 0)
                ReadColorTable(s, g.GlobalPaletteSpan, -1);

            return true;
        }

        public static void OutCode(GifState g, ushort code)
        {
            Debug.Assert(code < 8192);

            ref GifLzw lcode = ref Unsafe.Add(ref g.Codes, code);
            if (lcode.prefix >= 0)
                OutCode(g, (ushort)lcode.prefix);

            if (g.cur_y >= g.max_y)
                return;

            uint index = (uint)(g.cur_x + g.cur_y);
            g.history[index] = 1;

            ref Rgba32 color = ref Unsafe.Add(ref g.CurrentPalette, lcode.suffix);
            if (color.A >= 128)
            {
                Rgba32* p = (Rgba32*)(g._out_ + index * 4);
                *p = color;
            }

            g.cur_x++;
            if (g.cur_x >= g.max_x)
            {
                g.cur_x = g.start_x;
                g.cur_y += g.step;
                while ((g.cur_y >= g.max_y) && (g.parse > 0))
                {
                    g.step = (1 << g.parse) * g.line_size;
                    g.cur_y = g.start_y + (g.step >> 1);
                    --g.parse;
                }
            }
        }

        public static byte[]? ProcessRaster(ImageBinReader s, GifState g, ReadState ri)
        {
            byte lzw_cs = s.ReadByte();
            if (lzw_cs > 12)
            {
                // TODO:
                throw new StbImageReadException();
            }

            int clear = 1 << lzw_cs;
            uint first = 1;
            int codesize = lzw_cs + 1;
            int codemask = (1 << codesize) - 1;
            int valid_bits = 0;

            ref GifLzw codes = ref g.Codes;
            for (int init_code = 0; init_code < clear; init_code++)
            {
                ref GifLzw initCode = ref Unsafe.Add(ref codes, init_code);
                initCode.prefix = -1;
                initCode.first = (byte)init_code;
                initCode.suffix = (byte)init_code;
            }

            int avail = clear + 2;
            int oldcode = -1;
            int len = 0;
            int bits = 0;
            for (; ; )
            {
                if (valid_bits < codesize)
                {
                    if (len == 0)
                    {
                        len = s.ReadByte();
                        if (len == 0)
                        {
                            // TODO:
                            byte[] bigresult = new byte[ri.Width * ri.Height * ri.OutComponents];
                            Marshal.Copy((IntPtr)g._out_, bigresult, 0, bigresult.Length);
                            return bigresult;
                        }
                    }

                    len--;
                    bits |= s.ReadByte() << valid_bits;
                    valid_bits += 8;
                }
                else
                {
                    int code = bits & codemask;
                    bits >>= codesize;
                    valid_bits -= codesize;
                    if (code == clear)
                    {
                        codesize = lzw_cs + 1;
                        codemask = (1 << codesize) - 1;
                        avail = clear + 2;
                        oldcode = -1;
                        first = 0;
                    }
                    else if (code == (clear + 1))
                    {
                        s.Skip(len);
                        while ((len = s.ReadByte()) > 0)
                            s.Skip(len);

                        // TODO:
                        byte[] bigresult = new byte[ri.Width * ri.Height * ri.OutComponents];
                        Marshal.Copy((IntPtr)g._out_, bigresult, 0, bigresult.Length);
                        return bigresult;
                    }
                    else if (code <= avail)
                    {
                        if (first != 0)
                            throw new StbImageReadException(ErrorCode.NoClearCode);

                        if (oldcode >= 0)
                        {
                            ref GifLzw p = ref Unsafe.Add(ref codes, avail++);
                            if (avail > 8192)
                                throw new StbImageReadException(ErrorCode.TooManyCodes);

                            p.prefix = (short)oldcode;
                            p.first = Unsafe.Add(ref codes, oldcode).first;
                            p.suffix = (code == avail) ? p.first : Unsafe.Add(ref codes, code).first;
                        }
                        else if (code == avail)
                        {
                            throw new StbImageReadException(ErrorCode.IllegalCodeInRaster);
                        }

                        OutCode(g, (ushort)code);
                        if (((avail & codemask) == 0) && (avail <= 0x0FFF))
                        {
                            codesize++;
                            codemask = (1 << codesize) - 1;
                        }

                        oldcode = code;
                    }
                    else
                    {
                        throw new StbImageReadException(ErrorCode.IllegalCodeInRaster);
                    }
                }
            }
        }

        public static void ReadColorTable(
            ImageBinReader s, Span<Rgba32> palette, int transparencyIndex)
        {
            for (int i = 0; i < palette.Length; i++)
            {
                ref Rgba32 color = ref palette[i];
                color.R = s.ReadByte();
                color.G = s.ReadByte();
                color.B = s.ReadByte();
                color.A = transparencyIndex == i ? (byte)0 : (byte)255;
            }
        }

        public static byte[]? LoadNext(
            ImageBinReader s, GifState g, ReadState ri, byte* last_non_disposable)
        {
            bool first_frame = false;
            int pi = 0;
            int pcount = 0;

            if (g._out_ == null)
            {
                if (!ParseHeader(s, ri, g))
                    return null;

                ri.OutComponents = ri.Components;
                ri.OutDepth = ri.Depth;

                // TODO:
                //if (AreValidMad3Sizes(ri.OutComponents, ri.Width, ri.Height, 0) == 0)
                //{
                //    throw new StbImageReadException(ErrorCode.TooLarge);
                //}

                pcount = ri.Width * ri.Height;
                g._out_ = (byte*)Marshal.AllocHGlobal(ri.OutComponents * pcount);
                g.history = (byte*)Marshal.AllocHGlobal(pcount);

                if ((g._out_ == null) || (g.history == null))
                {
                    throw new StbImageReadException(ErrorCode.OutOfMemory);
                }

                new Span<byte>(g._out_, ri.OutComponents * pcount).Clear();
                new Span<byte>(g.history, pcount).Clear();
                first_frame = true;
            }
            else
            {
                pcount = ri.Width * ri.Height;

                int dispose = g.DisposeMethod;
                if ((dispose == 3) && (last_non_disposable == null))
                    dispose = 2;

                if (dispose == 3)
                {
                    for (pi = 0; pi < pcount; ++pi)
                    {
                        if (g.history[pi] != 0)
                        {
                            Unsafe.CopyBlock(
                                ref g._out_[pi * ri.OutComponents],
                                ref last_non_disposable[pi * ri.OutComponents],
                                (uint)ri.OutComponents);
                        }
                    }
                }
                else if (dispose == 2)
                {
                    for (pi = 0; pi < pcount; ++pi)
                    {
                        if (g.history[pi] != 0)
                        {
                            Unsafe.CopyBlock(
                                ref g._out_[pi * ri.OutComponents],
                                ref Unsafe.As<Rgba32, byte>(ref Unsafe.Add(ref g.GlobalPalette, g.bgindex)),
                                (uint)ri.OutComponents);
                        }
                    }
                }
            }

            new Span<byte>(g.history, pcount).Clear();
            for (; ; )
            {
                int tag = s.ReadByte();
                switch (tag)
                {
                    case 0x2C:
                    {
                        int x = s.ReadUInt16LE();
                        int y = s.ReadUInt16LE();
                        int w = s.ReadUInt16LE();
                        int h = s.ReadUInt16LE();
                        if (((x + w) > ri.Width) || ((y + h) > ri.Height))
                            throw new StbImageReadException(ErrorCode.BadImageDescriptor);

                        g.line_size = ri.Width;
                        g.start_x = x;
                        g.start_y = y * g.line_size;
                        g.max_x = g.start_x + w;
                        g.max_y = g.start_y + h * g.line_size;
                        g.cur_x = g.start_x;
                        g.cur_y = g.start_y;
                        if (w == 0)
                            g.cur_y = g.max_y;

                        g.localFlags = s.ReadByte();
                        if ((g.localFlags & 0b1000000) != 0)
                        {
                            g.step = 8 * g.line_size;
                            g.parse = 3;
                        }
                        else
                        {
                            g.step = g.line_size;
                            g.parse = 0;
                        }

                        if ((g.localFlags & 0b10000000) != 0)
                        {
                            ReadColorTable(
                                s,
                                g.LocalPaletteSpan,
                                (g.graphicControlFlags & 0b1) != 0 ? g.transparentIndex : -1);

                            g.colorTableIndex = 1;
                        }
                        else if ((g.flags & 0b10000000) != 0)
                        {
                            g.colorTableIndex = 0;
                        }
                        else
                        {
                            throw new StbImageReadException(ErrorCode.NoColorTable);
                        }

                        byte[]? o = ProcessRaster(s, g, ri);
                        if (o == null)
                            return null;

                        pcount = ri.Width * ri.Height;
                        if (first_frame && (g.bgindex > 0) && (g.graphicControlFlags & 0b1) == 0)
                        {
                            for (pi = 0; pi < pcount; ++pi)
                            {
                                if (g.history[pi] == 0)
                                {
                                    ref Rgba32 color = ref Unsafe.Add(ref g.GlobalPalette, g.bgindex);
                                    color.A = 255;
                                    Unsafe.CopyBlock(
                                        ref g._out_[pi * ri.OutComponents],
                                        ref Unsafe.As<Rgba32, byte>(ref color),
                                        (uint)ri.OutComponents);
                                }
                            }
                        }
                        return o;
                    }

                    case 0x21:
                    {
                        int block_len = 0;
                        int ext = s.ReadByte();
                        if (ext == 0xF9)
                        {
                            block_len = s.ReadByte();
                            if (block_len == 4)
                            {
                                g.graphicControlFlags = s.ReadByte();
                                g.delay = 10 * s.ReadUInt16LE();
                                if (g.transparentIndex >= 0)
                                    Unsafe.Add(ref g.GlobalPalette, g.transparentIndex).A = 255;

                                if ((g.graphicControlFlags & 0b1) != 0)
                                {
                                    g.transparentIndex = s.ReadByte();
                                    if (g.transparentIndex >= 0)
                                        Unsafe.Add(ref g.GlobalPalette, g.transparentIndex).A = 0;
                                }
                                else
                                {
                                    s.Skip(1);
                                    g.transparentIndex = -1;
                                }
                            }
                            else
                            {
                                s.Skip(block_len);
                                break;
                            }
                        }
                        while ((block_len = s.ReadByte()) != 0)
                            s.Skip(block_len);
                        break;
                    }

                    case 0x3B:
                        // Trailer
                        return null;

                    default:
                        throw new StbImageReadException(ErrorCode.UnknownCode);
                }
            }
        }

        public static byte[]? LoadMain(
            ImageBinReader s, out List<int> delays, out int layers, ReadState ri, ArrayPool<byte>? pool = null)
        {
            delays = new List<int>();
            layers = 0;

            pool ??= ArrayPool<byte>.Shared;
            using (var g = new GifState(pool))
            {
                byte* _out_ = null;
                byte* last_non_disposable = null;
                int fullStride = 0;

                byte[]? u;
                do
                {
                    u = LoadNext(s, g, ri, last_non_disposable);
                    if (u == null)
                        break;

                    delays.Add(g.delay);
                    layers = delays.Count;

                    fullStride = ri.Width * ri.Height * 4;

                    _out_ = _out_ != null
                        ? (byte*)Marshal.ReAllocHGlobal((IntPtr)_out_, (IntPtr)(layers * fullStride))
                        : (byte*)Marshal.AllocHGlobal(layers * fullStride);

                    byte* curOut = _out_ + ((layers - 1) * fullStride);
                    var dstSpan = new Span<byte>(curOut, fullStride);
                    u.CopyTo(dstSpan);

                    int disposem = g.DisposeMethod;
                    if (disposem == 0 || disposem == 1)
                        last_non_disposable = curOut;
                }
                while (u != null);

                byte[] bigresult = new byte[layers * fullStride];
                Marshal.Copy((IntPtr)_out_, bigresult, 0, bigresult.Length);
                return bigresult;
            }
        }

        public static byte[]? Load(ImageBinReader s, ReadState ri, ArrayPool<byte>? pool = default)
        {
            pool ??= ArrayPool<byte>.Shared;
            using (var g = new GifState(pool))
            {
                byte[]? u = LoadNext(s, g, ri, null);
                return u;
            }
        }

        public static bool Info(ImageBinReader s, out ReadState ri, ArrayPool<byte>? pool = default)
        {
            pool ??= ArrayPool<byte>.Shared;
            using (var g = new GifState(pool))
            {
                ri = new ReadState();
                return ParseHeader(s, ri, g);
            }
        }
    }
}