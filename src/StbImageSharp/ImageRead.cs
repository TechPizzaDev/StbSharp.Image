using System;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public static string LastError;

        public delegate int ReadCallback(ReadContext context, Span<byte> data);
        public delegate int SkipCallback(ReadContext context, int n);

        public delegate void BufferReadyCallback(in ReadState readState, IMemoryHolder buffer);
        public delegate void ReadProgressCallback(double progress, Rect? rect);

        public enum ScanMode
        {
            Load = 0,
            Type = 1,
            Header = 2
        }

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

        private static int Error(string str)
        {
            LastError = str;
            return 0;
        }
    }
}