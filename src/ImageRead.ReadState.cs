using System;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public delegate void StateReadyCallback(in ReadState state);

        public delegate void OutputByteDataCallback(
            in ReadState state, int line, AddressingMajor addressMajor, ReadOnlySpan<byte> pixels);

        public delegate void OutputFloatDataCallback(
            in ReadState state, int line, AddressingMajor addressMajor, ReadOnlySpan<float> pixels);

        public struct ReadState
        {
            public readonly StateReadyCallback StateReadyCallback;
            public readonly OutputByteDataCallback OutputByteRowCallback;
            public readonly OutputFloatDataCallback OutputFloatRowCallback;

            public int Width;
            public int Height;
            public int Depth;
            public int Components;

            public bool Progressive;
            public ImageOrientation Orientation;

            public int OutDepth;
            public int OutComponents;

            public ReadState(
                StateReadyCallback onStateReady,
                OutputByteDataCallback onOutputBytes,
                OutputFloatDataCallback onOutputFloats)
                : this()
            {
                StateReadyCallback = onStateReady;
                OutputByteRowCallback = onOutputBytes;
                OutputFloatRowCallback = onOutputFloats;
            }

            public readonly void StateReady()
            {
                StateReadyCallback?.Invoke(this);
            }

            public readonly void OutputByteRow(
                int line, AddressingMajor addressMajor, ReadOnlySpan<byte> pixels)
            {
                OutputByteRowCallback?.Invoke(this, line, addressMajor, pixels);
            }

            public readonly void OutputFloatRow(
                int line, AddressingMajor addressMajor, ReadOnlySpan<float> pixels)
            {
                OutputFloatRowCallback?.Invoke(this, line, addressMajor, pixels);
            }
        }
    }
}