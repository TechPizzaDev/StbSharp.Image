// Generated by Sichem at 1/6/2018 7:16:35 PM


namespace StbSharp
{
    public static unsafe partial class ImageRead
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
            return (int)
                ((AreValidMul2Sizes((int)a, (int)b) != 0) &&
                 (AreValidAddSizes((int)(a * b), (int)add) != 0)
                    ? 1
                    : 0);
        }

        public static int AreValidMad3Sizes(int a, int b, int c, int add)
        {
            return
                (int)
                ((AreValidMul2Sizes((int)a, (int)b) != 0) &&
                  (AreValidMul2Sizes((int)(a * b), (int)c) != 0) &&
                 (AreValidAddSizes((int)(a * b * c), (int)add) != 0)
                    ? 1
                    : 0);
        }

        public static void* MAllocMad2(int a, int b, int add)
        {
            if (AreValidMad2Sizes((int)a, (int)b, (int)add) == 0)
                return null;

            int length = a * b + add;
            return CRuntime.MAlloc(length);
        }

        public static void* MAllocMad3(int a, int b, int c, int add)
        {
            if (AreValidMad3Sizes((int)a, (int)b, (int)c, (int)add) == 0)
                return null;

            int length = a * b * c + add;
            return CRuntime.MAlloc(length);
        }

        public static IMemoryHolder Convert16To8(IMemoryHolder orig, int w, int h, int components)
        {
            int img_len = (int)(w * h * components);
            if (orig.Length != img_len * 2)
            {
                Error("invalid image length");
                return null;
            }

            byte* reduced = (byte*)CRuntime.MAlloc(img_len);
            if (reduced == null)
            {
                Error("outofmem");
                return null;
            }

            using (orig)
            {
                var origPtr = (ushort*)orig.Pointer;
                for (int i = 0; i < img_len; ++i)
                    reduced[i] = (byte)((origPtr[i] >> 8) & 0xFF);
            }
            return new HGlobalMemoryResult(reduced, img_len);
        }

        public static IMemoryHolder Convert8To16(IMemoryHolder orig, int w, int h, int components)
        {
            int img_len = (int)(w * h * components);
            if (orig.Length != img_len)
            {
                Error("invalid image length");
                return null;
            }

            int enlarged_len = img_len * 2;
            ushort* enlarged = (ushort*)CRuntime.MAlloc(enlarged_len);
            if (enlarged == null)
            {
                Error("outofmem");
                return null;
            }

            using (orig)
            {
                var origPtr = (byte*)orig.Pointer;
                for (int i = 0; i < img_len; ++i)
                    enlarged[i] = (ushort)((origPtr[i] << 8) + origPtr[i]);

                return new HGlobalMemoryResult(enlarged, enlarged_len);
            }
        }

        public static void VerticalFlip(byte* data, int w, int h, int comp, int depth)
        {
            int stride = w * comp * depth / 8;
            byte* rowBuffer = stackalloc byte[2048];
            for (int row = 0; row < (h >> 1); row++)
            {
                byte* row1 = data + row * stride;
                byte* row2 = data + (h - row - 1) * stride;

                int bytes_left = stride;
                while (bytes_left != 0)
                {
                    int bytes_copy = bytes_left < 2048 ? bytes_left : 2048;
                    CRuntime.MemCopy(rowBuffer, row1, bytes_copy);
                    CRuntime.MemCopy(row1, row2, bytes_copy);
                    CRuntime.MemCopy(row2, rowBuffer, bytes_copy);
                    row1 += bytes_copy;
                    row2 += bytes_copy;
                    bytes_left -= bytes_copy;
                }
            }
        }

        public static void VerticalFlip(IMemoryHolder data, int w, int h, int comp, int depth)
        {
            VerticalFlip((byte*)data.Pointer, w, h, comp, depth);
        }

        public static IMemoryHolder LoadAndPostprocess(
            ReadContext s, int? requestedComponents, int? requestedDepth, out ReadState ri)
        {
            ri = new ReadState(requestedComponents, requestedDepth);

            var result = LoadMain(s, ref ri);
            if (result == null)
                return null;

            result = ConvertFormat(result, ref ri);

            if (s.vertically_flip_on_load)
                VerticalFlip(result, ri.Width, ri.Height, ri.OutComponents, ri.OutDepth);

            return result;
        }

        public static byte ComputeY8(int r, int g, int b)
        {
            return (byte)(((r * 77) + (g * 150) + (29 * b)) >> 8);
        }

        public static IMemoryHolder ConvertFormat8(
            IMemoryHolder data, int img_n, int req_comp, int width, int height)
        {
            if (req_comp == img_n)
                return data;

            int goodLength = req_comp * width * height;
            byte* good = (byte*)MAllocMad3((int)req_comp, (int)width, (int)height, 0);
            if (good == null)
            {
                data.Dispose();
                Error("outofmem");
                return null;
            }

            using (data)
            {
                int i;
                var dataPtr = (byte*)data.Pointer;

                for (int j = 0; j < ((int)height); ++j)
                {
                    byte* src = dataPtr + j * width * img_n;
                    byte* dest = good + j * width * req_comp;

                    switch (img_n * 8 + req_comp)
                    {
                        case 1 * 8 + 2:
                            for (i = (int)(width - 1); i >= 0; --i, src += 1, dest += 2)
                            {
                                dest[0] = (byte)src[0];
                                dest[1] = 255;
                            }
                            break;

                        case 1 * 8 + 3:
                            for (i = (int)(width - 1); i >= 0; --i, src += 1, dest += 3)
                                dest[0] = (byte)(dest[1] = (byte)(dest[2] = (byte)src[0]));
                            break;

                        case 1 * 8 + 4:
                            for (i = (int)(width - 1); i >= 0; --i, src += 1, dest += 4)
                            {
                                dest[0] = (byte)(dest[1] = (byte)(dest[2] = (byte)src[0]));
                                dest[3] = 255;
                            }
                            break;

                        case 2 * 8 + 1:
                            for (i = (int)(width - 1); i >= 0; --i, src += 2, dest += 1)
                                dest[0] = (byte)src[0];
                            break;

                        case 2 * 8 + 3:
                            for (i = (int)(width - 1); i >= 0; --i, src += 2, dest += 3)
                                dest[0] = (byte)(dest[1] = (byte)(dest[2] = (byte)src[0]));
                            break;

                        case 2 * 8 + 4:
                            for (i = (int)(width - 1); i >= 0; --i, src += 2, dest += 4)
                            {
                                dest[0] = (byte)(dest[1] = (byte)(dest[2] = (byte)src[0]));
                                dest[3] = (byte)src[1];
                            }
                            break;

                        case 3 * 8 + 4:
                            for (i = (int)(width - 1); i >= 0; --i, src += 3, dest += 4)
                            {
                                dest[0] = (byte)src[0];
                                dest[1] = (byte)src[1];
                                dest[2] = (byte)src[2];
                                dest[3] = 255;
                            }
                            break;

                        case 3 * 8 + 1:
                            for (i = (int)(width - 1); i >= 0; --i, src += 3, dest += 1)
                                dest[0] = (byte)ComputeY8((int)src[0], (int)src[1], (int)src[2]);
                            break;

                        case 3 * 8 + 2:
                            for (i = (int)(width - 1); i >= 0; --i, src += 3, dest += 2)
                            {
                                dest[0] = (byte)ComputeY8((int)src[0], (int)src[1], (int)src[2]);
                                dest[1] = 255;
                            }
                            break;

                        case 4 * 8 + 1:
                            for (i = (int)(width - 1); i >= 0; --i, src += 4, dest += 1)
                                dest[0] = (byte)ComputeY8((int)src[0], (int)src[1], (int)src[2]);
                            break;

                        case 4 * 8 + 2:
                            for (i = (int)(width - 1); i >= 0; --i, src += 4, dest += 2)
                                dest[0] = (byte)ComputeY8((int)src[0], (int)src[1], (int)src[2]);
                            dest[1] = (byte)src[3];
                            break;

                        case 4 * 8 + 3:
                            for (i = (int)(width - 1); i >= 0; --i, src += 4, dest += 3)
                                dest[0] = (byte)src[0];
                            dest[1] = (byte)src[1];
                            dest[2] = (byte)src[2];
                            break;

                        default:
                            Error("0");
                            return null;
                    }
                }
                return new HGlobalMemoryResult(good, goodLength);
            }
        }

        public static ushort ComputeY16(int r, int g, int b)
        {
            return (ushort)(((r * 77) + (g * 150) + (29 * b)) >> 8);
        }

        public static IMemoryHolder ConvertFormat16(
            IMemoryHolder data, int img_n, int req_comp, int width, int height)
        {
            if (req_comp == img_n)
                return data;

            int goodLength = req_comp * width * height * 2;
            ushort* good = (ushort*)CRuntime.MAlloc(goodLength);
            if (good == null)
            {
                data.Dispose();
                Error("outofmem");
                return null;
            }

            using (data)
            {
                int i;
                var dataPtr = (ushort*)data.Pointer;

                for (int j = 0; j < ((int)height); ++j)
                {
                    ushort* src = dataPtr + j * width * img_n;
                    ushort* dest = good + j * width * req_comp;

                    switch (img_n * 8 + req_comp)
                    {
                        case 1 * 8 + 2:
                            for (i = (int)(width - 1); i >= 0; --i, src += 1, dest += 2)
                            {
                                dest[0] = (ushort)src[0];
                                dest[1] = (ushort)0xffff;
                            }
                            break;

                        case 1 * 8 + 3:
                            for (i = (int)(width - 1); i >= 0; --i, src += 1, dest += 3)
                                dest[0] = (ushort)(dest[1] = (ushort)(dest[2] = (ushort)src[0]));
                            break;

                        case 1 * 8 + 4:
                            for (i = (int)(width - 1); i >= 0; --i, src += 1, dest += 4)
                            {
                                dest[0] = (ushort)(dest[1] = (ushort)(dest[2] = (ushort)src[0]));
                                dest[3] = (ushort)0xffff;
                            }
                            break;

                        case 2 * 8 + 1:
                            for (i = (int)(width - 1); i >= 0; --i, src += 2, dest += 1)
                                dest[0] = (ushort)src[0];
                            break;

                        case 2 * 8 + 3:
                            for (i = (int)(width - 1); i >= 0; --i, src += 2, dest += 3)
                                dest[0] = (ushort)(dest[1] = (ushort)(dest[2] = (ushort)src[0]));
                            break;

                        case 2 * 8 + 4:
                            for (i = (int)(width - 1); i >= 0; --i, src += 2, dest += 4)
                            {
                                dest[0] = (ushort)(dest[1] = (ushort)(dest[2] = (ushort)src[0]));
                                dest[3] = (ushort)src[1];
                            }
                            break;

                        case 3 * 8 + 4:
                            for (i = (int)(width - 1); i >= 0; --i, src += 3, dest += 4)
                            {
                                dest[0] = (ushort)src[0];
                                dest[1] = (ushort)src[1];
                                dest[2] = (ushort)src[2];
                                dest[3] = (ushort)0xffff;
                            }
                            break;

                        case 3 * 8 + 1:
                            for (i = (int)(width - 1); i >= 0; --i, src += 3, dest += 1)
                                dest[0] = (ushort)ComputeY16((int)src[0], (int)src[1], (int)src[2]);
                            break;

                        case 3 * 8 + 2:
                            for (i = (int)(width - 1); i >= 0; --i, src += 3, dest += 2)
                                dest[0] = (ushort)ComputeY16((int)src[0], (int)src[1], (int)src[2]);
                            dest[1] = (ushort)0xffff;
                            break;

                        case 4 * 8 + 1:
                            for (i = (int)(width - 1); i >= 0; --i, src += 4, dest += 1)
                                dest[0] = (ushort)ComputeY16((int)src[0], (int)src[1], (int)src[2]);
                            break;

                        case 4 * 8 + 2:
                            for (i = (int)(width - 1); i >= 0; --i, src += 4, dest += 2)
                            {
                                dest[0] = (ushort)ComputeY16((int)src[0], (int)src[1], (int)src[2]);
                                dest[1] = (ushort)src[3];
                            }
                            break;

                        case 4 * 8 + 3:
                            for (i = (int)(width - 1); i >= 0; --i, src += 4, dest += 3)
                            {
                                dest[0] = (ushort)src[0];
                                dest[1] = (ushort)src[1];
                                dest[2] = (ushort)src[2];
                            }
                            break;

                        default:
                            Error("0");
                            return null;
                    }
                }
                return new HGlobalMemoryResult(good, goodLength);
            }
        }

        public static IMemoryHolder ConvertFormat(IMemoryHolder data, ref ReadState ri)
        {
            int requestedDepth = ri.RequestedDepth.GetValueOrDefault();
            if (ri.RequestedDepth.HasValue && ri.OutDepth != requestedDepth)
            {
                if (requestedDepth == 8)
                    data = Convert16To8(data, ri.Width, ri.Height, ri.OutComponents);
                else if (requestedDepth == 16)
                    data = Convert8To16(data, ri.Width, ri.Height, ri.OutComponents);

                ri.OutDepth = requestedDepth;
            }

            int requestedComponents = ri.RequestedComponents.GetValueOrDefault();
            if (ri.RequestedComponents.HasValue && ri.OutComponents != requestedComponents)
            {
                if (ri.OutDepth == 8)
                    data = ConvertFormat8(data, ri.OutComponents, requestedComponents, ri.Width, ri.Height);
                else
                    data = ConvertFormat16(data, ri.OutComponents, requestedComponents, ri.Width, ri.Height);

                ri.OutComponents = requestedComponents;
            }

            return data;
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

            Error("unknown image type");
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

            Error("unknown image type");
            return false;
        }

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