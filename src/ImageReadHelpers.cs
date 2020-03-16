using System;
using System.Runtime.InteropServices;
using static StbSharp.ImageRead;

namespace StbSharp
{
    public static unsafe class ImageReadHelpers
    {
        public static int AreValidAddSizes(int a, int b)
        {
            if (b < 0)
                return 0;
            return (a <= 2147483647 - b) ? 1 : 0;
        }

        public static int AreValidMul2Sizes(int a, int b)
        {
            if ((a < 0) || (b < 0))
                return 0;
            if (b == 0)
                return 1;
            return (a <= 2147483647 / b) ? 1 : 0;
        }

        public static int AreValidMad2Sizes(int a, int b, int add)
        {
            return
                (AreValidMul2Sizes(a, b) != 0) &&
                 (AreValidAddSizes(a * b, add) != 0)
                    ? 1
                    : 0;
        }

        public static int AreValidMad3Sizes(int a, int b, int c, int add)
        {
            return

                (AreValidMul2Sizes(a, b) != 0) &&
                  (AreValidMul2Sizes(a * b, c) != 0) &&
                 (AreValidAddSizes(a * b * c, add) != 0)
                    ? 1
                    : 0;
        }

        public static void* MAllocMad2(int a, int b, int add)
        {
            if (AreValidMad2Sizes(a, b, add) == 0)
                return null;

            int length = a * b + add;
            return CRuntime.MAlloc(length);
        }

        public static void* MAllocMad3(int a, int b, int c, int add)
        {
            if (AreValidMad3Sizes(a, b, c, add) == 0)
                return null;

            int length = a * b * c + add;
            return CRuntime.MAlloc(length);
        }

        /*

        public static ErrorCode Convert16To8(ReadOnlySpan<ushort> data, int w, int h, int components, out IMemoryHolder result)
        {
            result = null;

            int img_len = w * h * components;
            if (data.Length != img_len * 2)
                return ErrorCode.InvalidImageLength;

            byte* reduced = (byte*)CRuntime.MAlloc(img_len);
            if (reduced == null)
                return ErrorCode.OutOfMemory;

            for (int i = 0; i < img_len; ++i)
                reduced[i] = (byte)((data[i] >> 8) & 0xFF);

            result = new HGlobalMemoryHolder(reduced, img_len);
            return ErrorCode.Ok;
        }

        public static ErrorCode Convert8To16(ReadOnlySpan<byte> data, int w, int h, int components, out IMemoryHolder result)
        {
            result = null;

            int img_len = w * h * components;
            if (data.Length != img_len)
                return ErrorCode.InvalidImageLength;

            int enlarged_len = img_len * 2;
            ushort* enlarged = (ushort*)CRuntime.MAlloc(enlarged_len);
            if (enlarged == null)
                return ErrorCode.OutOfMemory;

            for (int i = 0; i < img_len; ++i)
                enlarged[i] = (ushort)((data[i] << 8) + data[i]);

            result = new HGlobalMemoryHolder(enlarged, enlarged_len);
            return ErrorCode.Ok;
        }

        public static void VerticalFlip(Span<byte> data, int w, int h, int comp, int depth)
        {
            int stride = (w * comp * depth + 7) / 8;
            Span<byte> rowBuffer = stackalloc byte[2048];

            for (int row = 0; row < (h / 2); row++)
            {
                Span<byte> row1 = data.Slice(row * stride);
                Span<byte> row2 = data.Slice((h - row - 1) * stride);

                int bytes_left = stride;
                while (bytes_left != 0)
                {
                    int count = Math.Min(bytes_left, rowBuffer.Length);

                    row1.Slice(stride - bytes_left, count).CopyTo(rowBuffer);
                    row2.Slice(stride - bytes_left, count).CopyTo(row1);
                    rowBuffer.Slice(0, count).CopyTo(row2);

                    bytes_left -= count;
                }
            }
        }

        public static void VerticalFlip(IMemoryHolder data, int w, int h, int comp, int depth)
        {
            VerticalFlip(data.Span, w, h, comp, depth);
        }

        public static ErrorCode LoadAndPostprocess(
            ReadContext s, int? requestedComponents, int? requestedDepth, out ReadState ri, out IMemoryHolder result)
        {
            ri = new ReadState(requestedComponents, requestedDepth);

            result = LoadMain(s, ref ri);
            if (s.ErrorCode != ErrorCode.Ok)
                return s.ErrorCode;

            if (s.VerticallyFlipOnLoad)
                VerticalFlip(result, ri.Width, ri.Height, ri.OutComponents, ri.OutDepth);

            return ErrorCode.Ok;
        }

        public static byte ComputeY8(int r, int g, int b)
        {
            return (byte)(((r * 77) + (g * 150) + (29 * b)) >> 8);
        }

        public static ErrorCode ConvertFormat8(
            IMemoryHolder data, int img_n, int req_comp, int width, int height, out IMemoryHolder result)
        {
            if (req_comp == img_n)
            {
                result = data;
                return ErrorCode.Ok;
            }

            int goodLength = req_comp * width * height;
            byte* good = (byte*)MAllocMad3(req_comp, width, height, 0);
            if (good == null)
            {
                data.Dispose();
                result = null;
                return ErrorCode.OutOfMemory;
            }

            using (data)
            {
                fixed (byte* data_ptr = data.Span)
                {
                    int i;
                    for (int j = 0; j < height; ++j)
                    {
                        byte* src = data_ptr + (j * width * img_n);
                        byte* dst = good + j * width * req_comp;

                        int srcOffset = 0;
                        switch (img_n * 8 + req_comp)
                        {
                            case 1 * 8 + 2:
                                for (i = width - 1; i >= 0; --i, srcOffset += 1, dst += 2)
                                {
                                    dst[0] = src[srcOffset];
                                    dst[1] = 255;
                                }
                                break;

                            case 1 * 8 + 3:
                                for (i = width - 1; i >= 0; --i, srcOffset += 1, dst += 3)
                                    dst[0] = dst[1] = dst[2] = src[srcOffset];
                                break;

                            case 1 * 8 + 4:
                                for (i = width - 1; i >= 0; --i, srcOffset += 1, dst += 4)
                                {
                                    dst[0] = dst[1] = dst[2] = src[srcOffset];
                                    dst[3] = 255;
                                }
                                break;

                            case 2 * 8 + 1:
                                for (i = width - 1; i >= 0; --i, srcOffset += 2, dst += 1)
                                    dst[0] = src[srcOffset];
                                break;

                            case 2 * 8 + 3:
                                for (i = width - 1; i >= 0; --i, srcOffset += 2, dst += 3)
                                    dst[0] = dst[1] = dst[2] = src[srcOffset];
                                break;

                            case 2 * 8 + 4:
                                for (i = width - 1; i >= 0; --i, srcOffset += 2, dst += 4)
                                {
                                    dst[0] = dst[1] = dst[2] = src[srcOffset];
                                    dst[3] = src[srcOffset + 1];
                                }
                                break;

                            case 3 * 8 + 4:
                                for (i = width - 1; i >= 0; --i, srcOffset += 3, dst += 4)
                                {
                                    dst[0] = src[srcOffset + 0];
                                    dst[1] = src[srcOffset + 1];
                                    dst[2] = src[srcOffset + 2];
                                    dst[3] = 255;
                                }
                                break;

                            case 3 * 8 + 1:
                                for (i = width - 1; i >= 0; --i, srcOffset += 3, dst += 1)
                                    dst[0] = ComputeY8(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                                break;

                            case 3 * 8 + 2:
                                for (i = width - 1; i >= 0; --i, srcOffset += 3, dst += 2)
                                {
                                    dst[0] = ComputeY8(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                                    dst[1] = 255;
                                }
                                break;

                            case 4 * 8 + 1:
                                for (i = width - 1; i >= 0; --i, srcOffset += 4, dst += 1)
                                    dst[0] = ComputeY8(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                                break;

                            case 4 * 8 + 2:
                                for (i = width - 1; i >= 0; --i, srcOffset += 4, dst += 2)
                                {
                                    dst[0] = ComputeY8(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                                    dst[1] = src[srcOffset + 3];
                                }
                                break;

                            case 4 * 8 + 3:
                                for (i = width - 1; i >= 0; --i, srcOffset += 4, dst += 3)
                                {
                                    dst[0] = src[srcOffset];
                                    dst[1] = src[srcOffset + 1];
                                    dst[2] = src[srcOffset + 2];
                                }
                                break;

                            default:
                                result = null;
                                return ErrorCode.InvalidArguments;
                        }
                    }

                    result = new HGlobalMemoryHolder(good, goodLength);
                    return ErrorCode.Ok;
                }
            }
        }

        public static ushort ComputeY16(int r, int g, int b)
        {
            return (ushort)(((r * 77) + (g * 150) + (29 * b)) >> 8);
        }

        public static ErrorCode ConvertFormat16(
            IMemoryHolder data, int img_n, int req_comp, int width, int height, out IMemoryHolder result)
        {
            if (req_comp == img_n)
            {
                result = data;
                return ErrorCode.Ok;
            }

            int goodLength = req_comp * width * height * 2;
            ushort* good = (ushort*)CRuntime.MAlloc(goodLength);
            if (good == null)
            {
                data.Dispose();
                result = null;
                return ErrorCode.OutOfMemory;
            }

            using (data)
            {
                var dataSpan = MemoryMarshal.Cast<byte, ushort>(data.Span);

                int i;
                for (int j = 0; j < height; ++j)
                {
                    Span<ushort> src = dataSpan.Slice(j * width * img_n);
                    ushort* dst = good + j * width * req_comp;

                    int srcOffset = 0;
                    switch (img_n * 8 + req_comp)
                    {
                        case 1 * 8 + 2:
                            for (i = width - 1; i >= 0; --i, srcOffset += 1, dst += 2)
                            {
                                dst[0] = src[srcOffset];
                                dst[1] = 0xffff;
                            }
                            break;

                        case 1 * 8 + 3:
                            for (i = width - 1; i >= 0; --i, srcOffset += 1, dst += 3)
                                dst[0] = dst[1] = dst[2] = src[srcOffset];
                            break;

                        case 1 * 8 + 4:
                            for (i = width - 1; i >= 0; --i, srcOffset += 1, dst += 4)
                            {
                                dst[0] = dst[1] = dst[2] = src[srcOffset];
                                dst[3] = 0xffff;
                            }
                            break;

                        case 2 * 8 + 1:
                            for (i = width - 1; i >= 0; --i, srcOffset += 2, dst += 1)
                                dst[0] = src[srcOffset];
                            break;

                        case 2 * 8 + 3:
                            for (i = width - 1; i >= 0; --i, srcOffset += 2, dst += 3)
                                dst[0] = dst[1] = dst[2] = src[srcOffset];
                            break;

                        case 2 * 8 + 4:
                            for (i = width - 1; i >= 0; --i, srcOffset += 2, dst += 4)
                            {
                                dst[0] = dst[1] = dst[2] = src[srcOffset];
                                dst[3] = src[1];
                            }
                            break;

                        case 3 * 8 + 4:
                            for (i = width - 1; i >= 0; --i, srcOffset += 3, dst += 4)
                            {
                                dst[0] = src[srcOffset + 0];
                                dst[1] = src[srcOffset + 1];
                                dst[2] = src[srcOffset + 2];
                                dst[3] = 0xffff;
                            }
                            break;

                        case 3 * 8 + 1:
                            for (i = width - 1; i >= 0; --i, srcOffset += 3, dst += 1)
                                dst[0] = ComputeY16(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                            break;

                        case 3 * 8 + 2:
                            for (i = width - 1; i >= 0; --i, srcOffset += 3, dst += 2)
                            {
                                dst[0] = ComputeY16(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                                dst[1] = 0xffff;
                            }
                            break;

                        case 4 * 8 + 1:
                            for (i = width - 1; i >= 0; --i, srcOffset += 4, dst += 1)
                                dst[0] = ComputeY16(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                            break;

                        case 4 * 8 + 2:
                            for (i = width - 1; i >= 0; --i, srcOffset += 4, dst += 2)
                            {
                                dst[0] = ComputeY16(src[srcOffset], src[srcOffset + 1], src[srcOffset + 2]);
                                dst[1] = src[srcOffset + 3];
                            }
                            break;

                        case 4 * 8 + 3:
                            for (i = width - 1; i >= 0; --i, srcOffset += 4, dst += 3)
                            {
                                dst[0] = src[srcOffset + 0];
                                dst[1] = src[srcOffset + 1];
                                dst[2] = src[srcOffset + 2];
                            }
                            break;

                        default:
                            result = null;
                            return ErrorCode.InvalidArguments;
                    }
                }

                result = new HGlobalMemoryHolder(good, goodLength);
                return ErrorCode.Ok;
            }
        }

        public static ErrorCode ConvertFormat(IMemoryHolder data, ref ReadState ri, out IMemoryHolder result)
        {
            result = data;
            var errorCode = ErrorCode.Ok;

            int requestedDepth = ri.RequestedDepth.GetValueOrDefault();
            if (ri.RequestedDepth.HasValue && ri.OutDepth != requestedDepth)
            {
                if (requestedDepth == 8)
                    errorCode = Convert16To8(MemoryMarshal.Cast<byte, ushort>(data.Span), ri.Width, ri.Height, ri.OutComponents, out result);
                else if (requestedDepth == 16)
                    errorCode = Convert8To16(data.Span, ri.Width, ri.Height, ri.OutComponents, out result);

                if (errorCode == ErrorCode.Ok)
                    ri.OutDepth = requestedDepth;
            }

            int requestedComponents = ri.RequestedComponents.GetValueOrDefault();
            if (ri.RequestedComponents.HasValue && ri.OutComponents != requestedComponents)
            {
                if (ri.OutDepth == 8)
                    errorCode = ConvertFormat8(data, ri.OutComponents, requestedComponents, ri.Width, ri.Height, out result);
                else
                    errorCode = ConvertFormat16(data, ri.OutComponents, requestedComponents, ri.Width, ri.Height, out result);

                if (errorCode == ErrorCode.Ok)
                    ri.OutComponents = requestedComponents;
            }

            return errorCode;
        }

        public static IMemoryHolder LoadMain(ReadContext s, ref ReadState ri)
        {
            if (Jpeg.Test(s))
                return Jpeg.LoadImage(s, ref ri);
            if (Png.Test(s))
                return Png.Load(s, ref ri);
            if (Bmp.Test(s))
                return Bmp.Load(s, ref ri);
            if (Gif.Test(s))
                return Gif.Load(s, ref ri);
            if (Psd.Test(s))
                return Psd.Load(s, ref ri);
            if (Tga.Test(s))
                return Tga.Load(s, ref ri);

            s.Error(ErrorCode.UnknownImageType);
            return null;
        }

        public static bool InfoMain(ReadContext s, out ReadState ri)
        {
            if (Jpeg.Info(s, out ri))
                return true;
            if (Png.Info(s, out ri))
                return true;
            if (Gif.Info(s, out ri))
                return true;
            if (Bmp.Info(s, out ri))
                return true;
            if (Psd.Info(s, out ri))
                return true;
            if (Tga.Info(s, out ri))
                return true;

            s.Error(ErrorCode.UnknownImageType);
            return false;
        }

        */

        /*
        public static int stbi_info_from_memory(byte* buffer, int len, out ReadState ri)
        {
            ReadContext s = new ReadContext();
            stbi__start_mem(s, buffer, (int)(len));
            return (int)(InfoMain(s, out ri));
        }

        public static int stbi_info_from_callbacks(
            stbi_io_callbacks c, Stream stream, byte[] buffer, out ReadState ri)
        {
            ReadContext s = new ReadContext();
            stbi__start_callbacks(s, c, stream, buffer);
            return (int)(InfoMain(s, out ri));
        }
        */
    }
}