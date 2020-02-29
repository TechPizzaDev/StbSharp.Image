using System.Runtime.InteropServices;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public static string LastError;

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

        public enum ErrorCode
        {
            Ok,
            
            NotBMP,
            NotGIF,
            NotPNG,

            UnknownHeader,
            BadBMP,
            MonochromeNotSupported,
            RLENotSupported,
            TooLarge,
            OutOfMemory,
            InvalidPalette,
            BadBitsPerPixel,
            BadMasks,
            NoClearCode,
            TooManyCodes,
            IllegalCodeInRaster,
            BadImageDescriptor,
            MissingColorTable,
            UnknownCode,
            BadCodeLengths,
            BadHuffmanCode,
            CantMergeDcAndAc, 
            ExpectedMarker,
            BadDRILength,
            BadDQTType,
            BadDQTTable,
            BadDHTHeader,
            BadCOMLength,
            BadAPPLength,
            UnknownMarker,
            BadSOSComponentCount,
            BadSOSLength,
            BadDCHuffman,
            BadACHuffman,
            BadSOS,
            BadSOFLength,
            Only8Bit,
            ZeroHeight, 
            ZeroWidth,
            BadComponentCount,
            BadH,
            BadV,
            BadTQ,
            NoSOI,
            NoSOF,
            BadDNLLength,
            BadDNLHeight,
            NotEnoughPixels,
            InvalidFilter,
            MultipleIHDR,
            BadIHDRLength, 
            EmptyImage,
            UnsupportedBitDepth,
            BadCtype,
            BadCompressionMethod,
            BadFilterMethod,
            BadInterlaceMethod,
            IHDRNotFirst,
            tRNSAfterIDAT,
            tRNSBeforePLTE,
            BadtRNSLength,
            tRNSWithAlpha,
            NoPLTE,
            OutOfData,
            NoIDAT,
            UnknownChunk,
            BadCompRequest,
            Corrupt,
            WrongChannelCount,
            WrongColorFormat,
            BadCompression,
            BadFormat,
            BadPalette,
            InvalidImageLength, 
            InvalidArguments,
            UnknownImageType
        }
    }
}