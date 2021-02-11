using System.IO;
using System.IO.Compression;

namespace StbSharp.ImageRead
{
    public static class ZlibHelper
    {
        /// <summary>
        /// Delegate for a wrapping a <see cref="Stream"/> in a zlib deflate (RFC 1951) decompressor.
        /// </summary>
        public delegate Stream DeflateDecompressorFactory(Stream input, bool leaveOpen);

        /// <summary>
        /// Decompresses data using a <see cref="DeflateStream"/>.
        /// </summary>
        /// <param name="input">The stream to read compressed data from.</param>
        /// <param name="deflateDecompressorFactory">
        /// Custom zlib deflate (RFC 1951) decompressor factory that replaces the default.
        /// </param>
        public static Stream CreateDecompressor(
            Stream input, bool leaveOpen, DeflateDecompressorFactory? deflateDecompressorFactory)
        {
            if (deflateDecompressorFactory != null)
                return deflateDecompressorFactory.Invoke(input, leaveOpen);

            return new DeflateStream(input, CompressionMode.Decompress, leaveOpen);
        }
    }
}
