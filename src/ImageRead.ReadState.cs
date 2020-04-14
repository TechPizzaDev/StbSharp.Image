using System;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public delegate void StateReadyDelegate(ReadState state);

        public delegate void OutputLineDelegate(
            ReadState state, AddressingMajor addressMajor, int line, int start, ReadOnlySpan<byte> pixels);

        public delegate void OutputInterleavedDelegate(
            ReadState state, AddressingMajor addressMajor, int line, int start, int spacing, ReadOnlySpan<byte> pixels);

        public delegate void OutputPixelDelegate(
            ReadState state, int x, int y, ReadOnlySpan<byte> pixel);

        public class ReadState
        {
            public StateReadyDelegate StateReadyCallback;

            public OutputLineDelegate OutputLineCallback;
            public OutputInterleavedDelegate OutputInterleavedCallback;
            public OutputPixelDelegate OutputPixelCallback;

            public int Width;
            public int Height;
            public int Depth;
            public int Components;

            public bool Progressive;
            public ImageOrientation Orientation;

            public int OutDepth;
            public int OutComponents;

            public ReadState()
            {
            }

            public void StateReady()
            {
                StateReadyCallback?.Invoke(this);
            }

            public void OutputLine(
                AddressingMajor addressMajor, int line, int start, ReadOnlySpan<byte> pixels)
            {
                OutputLineCallback?.Invoke(this, addressMajor, line, start, pixels);
            }

            public void OutputInterleaved(
                AddressingMajor addressMajor, int line, int start, int spacing, ReadOnlySpan<byte> pixels)
            {
                OutputInterleavedCallback?.Invoke(this, addressMajor, line, start, spacing, pixels);
            }

            public void OutputPixel(int x, int y, ReadOnlySpan<byte> pixels)
            {
                OutputPixelCallback?.Invoke(this, x, y, pixels);
            }
        }
    }
}