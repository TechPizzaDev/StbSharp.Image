using System.IO;
using System.IO.Compression;

namespace StbSharp.ImageRead
{
    public static class ZlibHelper
    {
        /// <summary>
        /// Delegate for a wrapping a <see cref="Stream"/> in a zlib deflate (RFC 1951) decompressor.
        /// </summary>
        public delegate Stream DeflateDecompressorDelegate(Stream input);

        /// <summary>
        /// Custom zlib deflate (RFC 1951) decompressor that replaces the default.
        /// </summary>
        public static DeflateDecompressorDelegate? CustomDeflateDecompressor { get; set; }

        /// <summary>
        /// Decompresses data using a <see cref="DeflateStream"/>.
        /// <para>Can be replaced by assigning <see cref="CustomDeflateDecompressor"/>.</para>
        /// </summary>
        public static Stream CreateDecompressor(Stream input)
        {
            if (CustomDeflateDecompressor != null)
                return CustomDeflateDecompressor.Invoke(input);

            return new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
        }
    }
}
