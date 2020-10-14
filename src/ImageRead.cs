using System;
using System.Runtime.InteropServices;

namespace StbSharp.ImageRead
{
    public readonly struct Palette
    {
        public ReadOnlyMemory<Rgba32> Data { get; }
        public int Components { get; }

        public Palette(ReadOnlyMemory<Rgba32> data, int components)
        {
            Data = data;
            Components = components;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rgb24
    {
        public byte R;
        public byte G;
        public byte B;

        public Rgb24(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rgba32
    {
        public Rgb24 Rgb;
        public byte A;

        public Rgba32(Rgb24 rgb, byte a)
        {
            Rgb = rgb;
            A = a;
        }

        public Rgba32(byte r, byte g, byte b, byte a) : this(new Rgb24(r, g, b), a)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rgb48
    {
        [CLSCompliant(false)]
        public ushort R;

        [CLSCompliant(false)]
        public ushort G;

        [CLSCompliant(false)]
        public ushort B;

        [CLSCompliant(false)]
        public Rgb48(ushort r, ushort g, ushort b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    public enum ScanMode
    {
        Load = 0,
        Header = 1
    }

    public enum AddressingMajor
    {
        Row,
        Column
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
        Undefined,
        Ok,

        UnknownFormat,
        UnknownHeader,
        BadBMP,
        MonochromeNotSupported,
        RLENotSupported,
        TooLarge,
        OutOfMemory,
        InvalidPLTE,
        BadBitsPerPixel,
        BadMasks,
        NoClearCode,
        TooManyCodes,
        IllegalCodeInRaster,
        BadImageDescriptor,
        NoColorTable,
        UnknownCode,
        BadCodeLengths,
        BadHuffmanCode,
        CantMergeDcAndAc,
        ExpectedMarker,
        NoResetMarker,
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
        BadImageType,
        BadCompressionMethod,
        BadFilterMethod,
        BadInterlaceMethod,
        IHDRNotFirst,
        tRNSAfterIDAT,
        tRNSBeforePLTE,
        BadtRNSLength,
        tRNSWithAlpha,
        NoPLTE,
        NoIDAT,
        UnknownChunk,
        Corrupt,
        BadChannelCount,
        BadColorPlane,
        BadCompression,
        BadFormat,
        BadPalette,
        InvalidImageLength,
        InvalidArguments
    }
}