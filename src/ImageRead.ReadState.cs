using System;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public delegate void StateReadyCallback(in ReadState state);
        public delegate void OutputByteRowCallback(in ReadState state, int row, ReadOnlySpan<byte> pixels);
        public delegate void OutputShortRowCallback(in ReadState state, int row, ReadOnlySpan<ushort> pixels);
        public delegate void OutputFloatRowCallback(in ReadState state, int row, ReadOnlySpan<float> pixels);

        public struct ReadState
        {
            public readonly StateReadyCallback StateReadyCallback;
            public readonly OutputByteRowCallback OutputByteRowCallback;
            public readonly OutputShortRowCallback OutputShortRowCallback;
            public readonly OutputFloatRowCallback OutputFloatRowCallback;

            public int Width;
            public int Height;
            public ImageOrientation Orientation;
            public int Depth;
            public int Components;

            public int OutDepth;
            public int OutComponents;

            public ReadState(
                StateReadyCallback onStateReady,
                OutputByteRowCallback onOutputBytes,
                OutputShortRowCallback onOutputShorts,
                OutputFloatRowCallback onOutputFloats)
                : this()
            {
                StateReadyCallback = onStateReady;
                OutputByteRowCallback = onOutputBytes;
                OutputShortRowCallback = onOutputShorts;
                OutputFloatRowCallback = onOutputFloats;
            }

            public readonly void StateReady()
            {
                StateReadyCallback?.Invoke(this);
            }

            public readonly void OutputByteRow(int row, ReadOnlySpan<byte> pixels)
            {
                OutputByteRowCallback?.Invoke(this, row, pixels);
            }

            public readonly void OutputShortRow(int row, ReadOnlySpan<ushort> pixels)
            {
                OutputShortRowCallback?.Invoke(this, row, pixels);
            }

            public readonly void OutputFloatRow(int row, ReadOnlySpan<float> pixels)
            {
                OutputFloatRowCallback?.Invoke(this, row, pixels);
            }
        }
    }
}