using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace StbSharp
{
    public static unsafe partial class ImageRead
    {
        public static unsafe class Zlib
        {
            /// <summary>
            /// Delegate for a zlib deflate (RFC 1951) decompression implementation.
            /// </summary>
            public delegate IMemoryResult ZlibDeflateDecompressDelegate(
                ReadOnlySpan<byte> data, int expectedSize, bool parseHeader);

            /// <summary>
            /// Custom zlib deflate (RFC 1951) decompression implementation 
            /// that replaces the default <see cref="DeflateDecompress"/>.
            /// </summary>
            public static ZlibDeflateDecompressDelegate CustomDeflateDecompress;

            /// <summary>
            /// Decompresses data using a <see cref="DeflateStream"/>,
            /// optionally skipping the zlib (RFC 1951) header.
            /// <para>Can be replaced by assigning <see cref="CustomDeflateDecompress"/>.</para>
            /// </summary>
            public static IMemoryResult DeflateDecompress(
                ReadOnlySpan<byte> compressed, int uncompressedSize, bool skipHeader)
            {
                int srcOffset = skipHeader ? 2 : 0;
                var resultPtr = (byte*)CRuntime.MAlloc(uncompressedSize);
                int resultLength;
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(compressed))
                {
                    using (var src = new UnmanagedMemoryStream(dataPtr + srcOffset, compressed.Length - srcOffset))
                    using (var dst = new UnmanagedMemoryStream(resultPtr, 0, uncompressedSize, FileAccess.Write))
                    {
                        using (var ds = new DeflateStream(src, CompressionMode.Decompress, false))
                            ds.CopyTo(dst, Math.Min(uncompressedSize, 1024 * 80));

                        resultLength = (int)dst.Length;
                    }
                }
                return new HGlobalMemoryResult(resultPtr, resultLength);
            }
        }
    }
}
