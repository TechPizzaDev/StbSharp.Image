using System;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
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
        
        public enum ScanMode
        {
            Load = 0,
            Type = 1,
            Header = 2
        }

        [Flags]
        public enum ImageOrientation
        {
            TopToBottom = 1 << 0,
            BottomToTop = 1 << 1,
            LeftToRight = 1 << 2,
            RightToLeft = 1 << 3,

            TopLeftOrigin = LeftToRight | TopToBottom,
            BottomLeftOrigin = LeftToRight | BottomToTop,
            TopRightOrigin = RightToLeft | TopToBottom,
            BottomRightOrigin = RightToLeft | BottomToTop
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
            BadColorType,
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