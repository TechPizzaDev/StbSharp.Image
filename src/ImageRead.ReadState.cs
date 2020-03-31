using System;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public delegate void StateReadyCallback(in ReadState state);
        public delegate void OutputByteDataCallback(in ReadState state, int address, AddressingMajor addressMajor, ReadOnlySpan<byte> pixels);
        public delegate void OutputShortDataCallback(in ReadState state, int address, AddressingMajor addressMajor, ReadOnlySpan<ushort> pixels);
        public delegate void OutputFloatDataCallback(in ReadState state, int address, AddressingMajor addressMajor, ReadOnlySpan<float> pixels);

        public struct ReadState
        {
            public readonly StateReadyCallback StateReadyCallback;
            public readonly OutputByteDataCallback OutputByteRowCallback;
            public readonly OutputShortDataCallback OutputShortRowCallback;
            public readonly OutputFloatDataCallback OutputFloatRowCallback;

            public int Width;
            public int Height;
            public ImageOrientation Orientation;
            public int Depth;
            public int Components;

            public int OutDepth;
            public int OutComponents;

            public ReadState(
                StateReadyCallback onStateReady,
                OutputByteDataCallback onOutputBytes,
                OutputShortDataCallback onOutputShorts,
                OutputFloatDataCallback onOutputFloats)
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

            public readonly void OutputByteRow(int address, AddressingMajor addressMajor, ReadOnlySpan<byte> pixels)
            {
                OutputByteRowCallback?.Invoke(this, address, addressMajor, pixels);
            }

            public readonly void OutputShortRow(int address, AddressingMajor addressMajor, ReadOnlySpan<ushort> pixels)
            {
                OutputShortRowCallback?.Invoke(this, address, addressMajor, pixels);
            }

            public readonly void OutputFloatRow(int address, AddressingMajor addressMajor, ReadOnlySpan<float> pixels)
            {
                OutputFloatRowCallback?.Invoke(this, address, addressMajor, pixels);
            }
        }
    }
}