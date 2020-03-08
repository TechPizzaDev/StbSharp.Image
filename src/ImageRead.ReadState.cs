using System;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public delegate void StateReadyCallback(in ReadState state);
        public delegate void OutputBytePixelsCallback(in ReadState state, int row, ReadOnlySpan<byte> pixels);
        public delegate void OutputFloatPixelsCallback(in ReadState state, int row, ReadOnlySpan<float> pixels);

        public struct ReadState
        {
            public readonly StateReadyCallback StateReadyCallback;
            public readonly OutputBytePixelsCallback OutputBytesCallback;
            public readonly OutputFloatPixelsCallback OutputFloatsCallback;

            public int Width;
            public int Height;
            public ImageOrientation Orientation;
            public int Depth;
            public int Components;

            public ReadState(
                StateReadyCallback onStateReady = null,
                OutputBytePixelsCallback onOutputBytes = null,
                OutputFloatPixelsCallback onOutputFloats = null) 
                : this()
            {
                StateReadyCallback = onStateReady;
                OutputBytesCallback = onOutputBytes;
                OutputFloatsCallback = onOutputFloats;
            }

            public readonly void StateReady()
            {
                StateReadyCallback?.Invoke(this);
            }

            public readonly void OutputBytes(int row, ReadOnlySpan<byte> pixels)
            {
                OutputBytesCallback?.Invoke(this, row, pixels);
            }

            public readonly void OutputFloats(int row, ReadOnlySpan<float> pixels)
            {
                OutputFloatsCallback?.Invoke(this, row, pixels);
            }
        }
    }
}