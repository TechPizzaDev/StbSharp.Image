using System;
using System.IO;

namespace StbSharp.ImageRead
{
    public static class ImageReadHelpers
    {
        public static bool FullRead(Stream stream, Span<byte> destination)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var dst = destination;
            do
            {
                int read = stream.Read(dst);
                if (read == 0)
                    break;

                dst = dst[read..];
            }
            while (!dst.IsEmpty);

            return dst.IsEmpty;
        }
    }
}