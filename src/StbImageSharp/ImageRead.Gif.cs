using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static partial class ImageRead
    {
        public static unsafe class Gif
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct GifLzw
            {
                public short prefix;
                public byte first;
                public byte suffix;
            }

            public class Context : IDisposable
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

                public Context(bool allocatePalette)
                {
                    if (allocatePalette)
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
                }

                #region IDisposable

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

                ~Context()
                {
                    Dispose(false);
                }

                #endregion
            }

            public static bool Test(ReadContext s, Context g)
            {
                var ri = new ReadState();
                bool r = ParseHeader(s, g, ref ri, ScanMode.Type);
                s.Rewind();
                return r;
            }

            public static bool Test(ReadContext s)
            {
                using (var g = new Context(false))
                    return Test(s, g);
            }

            public static bool ParseHeader(
                ReadContext s, Context g, ref ReadState ri, ScanMode scan)
            {
                byte version;
                if ((s.ReadByte() != 'G') ||
                    (s.ReadByte() != 'I') ||
                    (s.ReadByte() != 'F') ||
                    (s.ReadByte() != '8'))
                {
                    Error("not GIF");
                    return false;
                }

                version = s.ReadByte();
                if ((version != '7') && (version != '9'))
                {
                    Error("not GIF");
                    return false;
                }
                if (s.ReadByte() != 'a')
                {
                    Error("not GIF");
                    return false;
                }

                if (scan == ScanMode.Type)
                    return true;

                ri.Width = s.ReadInt16LE();
                ri.Height = s.ReadInt16LE();
                ri.Components = 4;

                g.flags = s.ReadByte();
                g.bgindex = s.ReadByte();
                g.ratio = s.ReadByte();
                g.transparent = -1;

                if ((g.flags & 0x80) != 0)
                    ParseColortable(s, ref ri, g.pal, 2 << (g.flags & 7), -1);

                return true;
            }

            public static void OutCode(Context g, ushort code)
            {
                if (g.codes[code].prefix >= 0)
                    OutCode(g, (ushort)g.codes[code].prefix);

                if (g.cur_y >= g.max_y)
                    return;

                byte* p = &g._out_[g.cur_x + g.cur_y];
                byte* c = &g.color_table[g.codes[code].suffix * 4];
                if (c[3] >= 128)
                {
                    p[0] = c[2];
                    p[1] = c[1];
                    p[2] = c[0];
                    p[3] = c[3];
                }

                g.cur_x += 4;
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

            public static IMemoryHolder ProcessRaster(
                ReadContext s, ref Context g, ref ReadState ri)
            {
                byte lzw_cs = s.ReadByte();
                if (lzw_cs > 12)
                    return null;

                int clear = 1 << lzw_cs;
                uint first = 1;
                int codesize = lzw_cs + 1;
                int codemask = (1 << codesize) - 1;
                int bits = 0;
                int valid_bits = 0;
                for (int init_code = 0; init_code < clear; init_code++)
                {
                    g.codes[init_code].prefix = -1;
                    g.codes[init_code].first = (byte)init_code;
                    g.codes[init_code].suffix = (byte)init_code;
                }

                GifLzw* p;
                int avail = clear + 2;
                int oldcode = -1;
                int len = 0;
                for (; ; )
                {
                    if (valid_bits < codesize)
                    {
                        if (len == 0)
                        {
                            len = s.ReadByte();
                            if (len == 0)
                                return new HGlobalMemoryHolder(g._out_, ri.Width * ri.Height * ri.OutComponents);
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

                            return new HGlobalMemoryHolder(g._out_, ri.Width * ri.Height * ri.OutComponents);
                        }
                        else if (code <= avail)
                        {
                            if (first != 0)
                                Error("no clear code");
                            if (oldcode >= 0)
                            {
                                p = g.codes + avail++;
                                if (avail > 4096)
                                    Error("too many codes");

                                p->prefix = (short)oldcode;
                                p->first = g.codes[oldcode].first;
                                p->suffix = (code == avail) ? p->first : g.codes[code].first;
                            }
                            else if (code == avail)
                            {
                                Error("illegal code in raster");
                                return null;
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
                            Error("illegal code in raster");
                            return null;
                        }
                    }
                }
            }

            public static void FillBackground(
                ref Context g, ref ReadState ri, int x0, int y0, int x1, int y1)
            {
                byte* c = g.pal + g.bgindex;
                for (int y = y0; y < y1; y += ri.OutComponents * ri.Width)
                {
                    for (int x = x0; x < x1; x += ri.OutComponents)
                    {
                        byte* p = &g._out_[y + x];
                        p[0] = c[2];
                        p[1] = c[1];
                        p[2] = c[0];
                        p[3] = 0;
                    }
                }
            }

            public static void ParseColortable(
                ReadContext s, ref ReadState ri, byte* pal, int num_entries, int transp)
            {
                for (int i = 0; i < num_entries; ++i)
                {
                    pal[i * ri.OutComponents + 3] = (byte)(transp == i ? 0 : 255);
                    pal[i * ri.OutComponents + 2] = s.ReadByte();
                    pal[i * ri.OutComponents + 1] = s.ReadByte();
                    pal[i * ri.OutComponents] = s.ReadByte();
                }
            }

            public static IMemoryHolder LoadNext(
                ReadContext s, Context g, ref ReadState ri, byte* two_back)
            {
                int dispose = 0;
                int first_frame = 0;
                int pi = 0;
                int pcount = 0;

                if (g._out_ == null)
                {
                    if (!ParseHeader(s, g, ref ri, ScanMode.Load))
                        return null;

                    ri.OutComponents = ri.Components;

                    if (AreValidMad3Sizes(ri.OutComponents, ri.Width, ri.Height, 0) == 0)
                    {
                        Error("too large");
                        return null;
                    }

                    pcount = ri.Width * ri.Height;
                    g._out_ = (byte*)CRuntime.MAlloc(ri.OutComponents * pcount);
                    g.background = (byte*)CRuntime.MAlloc(ri.OutComponents * pcount);
                    g.history = (byte*)CRuntime.MAlloc(pcount);
                    if ((g._out_ == null) || (g.background == null) || (g.history == null))
                    {
                        Error("outofmem");
                        return null;
                    }

                    CRuntime.MemSet(g._out_, 0, ri.OutComponents * pcount);
                    CRuntime.MemSet(g.background, 0, ri.OutComponents * pcount);
                    CRuntime.MemSet(g.history, 0, pcount);
                    first_frame = 1;
                }
                else
                {
                    dispose = (g.eflags & 0x1C) >> 2;
                    pcount = ri.Width * ri.Height;
                    if ((dispose == 3) && (two_back == null))
                        dispose = 2;

                    if (dispose == 3)
                    {
                        for (pi = 0; pi < pcount; ++pi)
                        {
                            if (g.history[pi] != 0)
                                CRuntime.MemCopy(
                                    &g._out_[pi * ri.OutComponents], &two_back[pi * ri.OutComponents], ri.OutComponents);
                        }
                    }
                    else if (dispose == 2)
                    {
                        for (pi = 0; pi < pcount; ++pi)
                        {
                            if (g.history[pi] != 0)
                                CRuntime.MemCopy(
                                    &g._out_[pi * ri.OutComponents], &g.background[pi * ri.OutComponents], ri.OutComponents);
                        }
                    }
                    CRuntime.MemCopy(g.background, g._out_, ri.OutComponents * pcount);
                }

                CRuntime.MemSet(g.history, 0, pcount);
                for (; ; )
                {
                    int tag = s.ReadByte();
                    switch (tag)
                    {
                        case 0x2C:
                        {
                            int x = s.ReadInt16LE();
                            int y = s.ReadInt16LE();
                            int w = s.ReadInt16LE();
                            int h = s.ReadInt16LE();
                            if (((x + w) > ri.Width) || ((y + h) > ri.Height))
                            {
                                Error("bad image descriptor");
                                return null;
                            }

                            g.line_size = ri.Width * ri.OutComponents;
                            g.start_x = x * ri.OutComponents;
                            g.start_y = y * g.line_size;
                            g.max_x = g.start_x + w * ri.OutComponents;
                            g.max_y = g.start_y + h * g.line_size;
                            g.cur_x = g.start_x;
                            g.cur_y = g.start_y;
                            if (w == 0)
                                g.cur_y = g.max_y;
                            g.lflags = s.ReadByte();

                            if ((g.lflags & 0x40) != 0)
                            {
                                g.step = 8 * g.line_size;
                                g.parse = 3;
                            }
                            else
                            {
                                g.step = g.line_size;
                                g.parse = 0;
                            }
                            if ((g.lflags & 0x80) != 0)
                            {
                                ParseColortable(
                                    s,
                                    ref ri,
                                    g.lpal,
                                    2 << (g.lflags & 7),
                                    (g.eflags & 0x01) != 0 ? g.transparent : -1);

                                g.color_table = g.lpal;
                            }
                            else if ((g.flags & 0x80) != 0)
                            {
                                g.color_table = g.pal;
                            }
                            else
                            {
                                Error("missing color table");
                                return null;
                            }

                            var o = ProcessRaster(s, ref g, ref ri);
                            if (o == null)
                                return null;

                            pcount = ri.Width * ri.Height;
                            if ((first_frame != 0) && (g.bgindex > 0))
                            {
                                for (pi = 0; pi < pcount; ++pi)
                                {
                                    if (g.history[pi] == 0)
                                    {
                                        g.pal[g.bgindex * ri.OutComponents + 3] = 255;
                                        CRuntime.MemCopy(
                                            &g._out_[pi * ri.OutComponents], &g.pal[g.bgindex], ri.OutComponents);
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
                                    g.eflags = s.ReadByte();
                                    g.delay = 10 * s.ReadInt16LE();
                                    if (g.transparent >= 0)
                                        g.pal[g.transparent * ri.OutComponents + 3] = 255;

                                    if ((g.eflags & 0x01) != 0)
                                    {
                                        g.transparent = s.ReadByte();
                                        if (g.transparent >= 0)
                                            g.pal[g.transparent * ri.OutComponents + 3] = 0;
                                    }
                                    else
                                    {
                                        s.Skip(1);
                                        g.transparent = -1;
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
                            return null;

                        default:
                            Error("unknown code");
                            return null;
                    }
                }
            }

            public static IMemoryHolder LoadMain(
                ReadContext s, out List<int> delays, out int layers, ref ReadState ri)
            {
                layers = 0;
                delays = null;

                using (var g = new Context(true))
                {
                    if (!Test(s, g))
                        return null;

                    IMemoryHolder u;
                    byte* _out_ = null;
                    byte* two_back = null;
                    int stride = 0;
                    delays = new List<int>();

                    do
                    {
                        u = LoadNext(s, g, ref ri, two_back);
                        if (u == null)
                            break;

                        delays.Add(g.delay);
                        layers = delays.Count;

                        stride = ri.Width * ri.Height * 4;

                        _out_ = _out_ != null
                            ? (byte*)CRuntime.ReAlloc(_out_, layers * stride)
                            : (byte*)CRuntime.MAlloc(layers * stride);

                        var dstSpan = new Span<byte>(_out_ + ((layers - 1) * stride), stride);
                        u.Span.CopyTo(dstSpan);
                        
                        if (layers >= 2)
                            two_back = _out_ - 2 * stride;
                    }
                    while (u != null);

                    CRuntime.Free(g._out_);
                    CRuntime.Free(g.history);
                    CRuntime.Free(g.background);

                    IMemoryHolder result = new HGlobalMemoryHolder(_out_, layers * stride);
                    result = ConvertFormat(result, ref ri);
                    return result;
                }
            }

            public static IMemoryHolder Load(ReadContext s, ref ReadState ri)
            {
                using (var g = new Context(true))
                {
                    IMemoryHolder u = LoadNext(s, g, ref ri, null);
                    if (u != null)
                    {
                        u = ConvertFormat(u, ref ri);
                    }
                    else
                    {
                        CRuntime.Free(g._out_);
                        g._out_ = null;
                    }
                    return u;
                }
            }

            public static bool Info(ReadContext s, out ReadState ri)
            {
                using (var g = new Context(true))
                {
                    ri = new ReadState();
                    if (!ParseHeader(s, g, ref ri, ScanMode.Header))
                    {
                        s.Rewind();
                        return false;
                    }
                    return true;
                }
            }
        }
    }
}