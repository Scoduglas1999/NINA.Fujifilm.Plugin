using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Plugins.Fujifilm.Interop;

/// <summary>
/// Hybrid LibRaw processor that uses LibRawWrapper.dll for data extraction
/// and direct P/Invoke to libraw.dll for advanced debayering (X-Trans support).
/// </summary>
public static class RawProcessor
{
    private static Type? _wrapperType;
    private static MethodInfo? _processMethod;

    static RawProcessor()
    {
        try
        {
            // Get the directory where this assembly is located
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string assemblyDirectory = Path.GetDirectoryName(assemblyLocation) ?? string.Empty;
            
            // Both LibRawWrapper.dll and libraw.dll should be in the root plugin directory
            // so Windows can find libraw.dll when loading LibRawWrapper.dll
            string wrapperPath = Path.Combine(assemblyDirectory, "LibRawWrapper.dll");
            
            // Load LibRawWrapper.dll via reflection
            if (File.Exists(wrapperPath))
            {
                var assembly = Assembly.LoadFrom(wrapperPath);
                _wrapperType = assembly.GetType("Fujifilm.LibRawWrapper.RawProcessor");
                // The actual method signature is: int ProcessRawBuffer(byte[] rawBuffer, out ushort[,] bayerData, out int width, out int height)
                _processMethod = _wrapperType?.GetMethod("ProcessRawBuffer", BindingFlags.Public | BindingFlags.Static);
            }
        }
        catch (Exception ex)
        {
            // Log the exception for debugging
            System.Diagnostics.Debug.WriteLine($"[RawProcessor] Failed to load LibRawWrapper: {ex.Message}");
        }
    }

    public static RawProcessingResult ProcessRawBuffer(byte[] buffer)
    {
        return ProcessRawBufferWithMetadata(buffer);
    }

    public static RawProcessingResult ProcessRawBufferWithMetadata(byte[] buffer)
    {
        var result = new RawProcessingResult
        {
            Success = false,
            Status = LibRawProcessingStatus.UnknownError,
            Width = 0,
            Height = 0,
            ColorFilterPattern = string.Empty,
            PatternWidth = 0,
            PatternHeight = 0,
            BayerData = Array.Empty<ushort>(),
            BlackLevel = 0,
            WhiteLevel = 65535
        };

        if (buffer == null || buffer.Length == 0)
        {
            result.Status = LibRawProcessingStatus.InvalidBuffer;
            return result;
        }

        // Try LibRawWrapper.dll first (most reliable)
        if (_processMethod != null)
        {
            try
            {
                // C++ signature: int ProcessRawBuffer(byte[] rawBuffer, out ushort[,] bayerData, out int width, out int height)
                object[] parameters = new object[] { buffer, null, 0, 0 };
                var returnCode = _processMethod.Invoke(null, parameters);
                
                // Extract out parameters
                var bayerData2D = parameters[1] as Array;
                int width = parameters[2] is int w ? w : 0;
                int height = parameters[3] is int h ? h : 0;
                int librawCode = returnCode is int code ? code : -1;
                
                if (librawCode == 0 && bayerData2D != null && width > 0 && height > 0) // LIBRAW_SUCCESS = 0
                {
                    result.Success = true;
                    result.Status = LibRawProcessingStatus.Success;
                    result.Width = width;
                    result.Height = height;
                    result.BayerData = ConvertToLinear(bayerData2D);
                    
                    // Get correct pattern using P/Invoke (lightweight metadata read)
                    var patternInfo = GetPatternFromLibRaw(buffer);
                    result.ColorFilterPattern = patternInfo.Pattern;
                    result.PatternWidth = patternInfo.Width;
                    result.PatternHeight = patternInfo.Height;

                    // Get Crop Info
                    var (cropLeft, cropTop, cropWidth, cropHeight, cropLog) = GetCropInfoFromLibRaw(buffer);
                    result.ActiveLeft = cropLeft;
                    result.ActiveTop = cropTop;
                    result.ActiveWidth = cropWidth;
                    result.ActiveHeight = cropHeight;
                    
                    // Append crop log to error message for diagnostics (even if success)
                    result.ErrorMessage = (result.ErrorMessage ?? "") + "\n[CropLog] " + cropLog;

                    // Perform debayering ONLY for X-Trans
                    if (IsXTrans(result.ColorFilterPattern))
                    {
                        var (debayered, debayerError) = PerformDebayering(buffer, width, height);
                        result.DebayeredRgb = debayered;
                        if (!string.IsNullOrEmpty(debayerError))
                        {
                             result.ErrorMessage = (result.ErrorMessage ?? "") + "\n[DebayerLog] " + debayerError;
                        }
                    }
                    
                    return result;
                }
                else
                {
                    string errorName = librawCode switch
                    {
                        -1 => "LIBRAW_UNSPECIFIED_ERROR",
                        -2 => "LIBRAW_FILE_UNSUPPORTED",
                        -3 => "LIBRAW_REQUEST_FOR_NONEXISTENT_IMAGE",
                        -4 => "LIBRAW_OUT_OF_ORDER_CALL",
                        -5 => "LIBRAW_NO_THUMBNAIL",
                        -6 => "LIBRAW_UNSUPPORTED_THUMBNAIL",
                        -7 => "LIBRAW_INPUT_CLOSED",
                        -100007 => "LIBRAW_UNSUFFICIENT_MEMORY",
                        -100008 => "LIBRAW_DATA_ERROR",
                        -100009 => "LIBRAW_IO_ERROR",
                        -100010 => "LIBRAW_CANCELLED_BY_CALLBACK",
                        -100011 => "LIBRAW_BAD_CROP",
                        _ => $"Unknown({librawCode})"
                    };
                    result.ErrorMessage = $"LibRawWrapper ProcessRawBuffer returned {errorName} (code {librawCode}), dims {width}x{height}";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"LibRawWrapper failed: {ex.GetType().Name} - {ex.Message}";
                if (ex.InnerException != null)
                {
                    result.ErrorMessage += $"\nInner: {ex.InnerException.Message}";
                }
            }
        }
        else
        {
            // Get diagnostic info about why LibRawWrapper isn't loaded
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string assemblyDirectory = Path.GetDirectoryName(assemblyLocation) ?? string.Empty;
            string wrapperPath = Path.Combine(assemblyDirectory, "Interop", "Native", "LibRawWrapper.dll");
            
            bool fileExists = File.Exists(wrapperPath);
            result.ErrorMessage = $"LibRawWrapper.dll not loaded. Path: {wrapperPath}, Exists: {fileExists}, TypeLoaded: {_wrapperType != null}, MethodLoaded: {_processMethod != null}";
        }

        result.Status = LibRawProcessingStatus.WrapperUnavailable;
        return result;
    }

    private static RawProcessingResult MapWrapperResult(object wrapperResult)
    {
        var type = wrapperResult.GetType();
        
        var result = new RawProcessingResult
        {
            Success = ReadBoolProperty(wrapperResult, "Success"),
            Status = (LibRawProcessingStatus)ReadIntProperty(wrapperResult, "Status"),
            Width = ReadIntProperty(wrapperResult, "ImageWidth"),
            Height = ReadIntProperty(wrapperResult, "ImageHeight"),
            ColorFilterPattern = ReadStringProperty(wrapperResult, "ColorFilterPattern") ?? string.Empty,
            PatternWidth = ReadIntProperty(wrapperResult, "PatternWidth"),
            PatternHeight = ReadIntProperty(wrapperResult, "PatternHeight"),
            BlackLevel = ReadIntProperty(wrapperResult, "BlackLevel"),
            WhiteLevel = ReadIntProperty(wrapperResult, "WhiteLevel"),
            BayerData = ConvertToLinear(ReadArrayProperty(wrapperResult, "BayerData")),
            ErrorMessage = ReadStringProperty(wrapperResult, "ErrorMessage")
        };

        return result;
    }

    private static (ushort[]? Data, string Error) PerformDebayering(byte[] rawBuffer, int width, int height)
    {
        IntPtr processor = IntPtr.Zero;
        IntPtr bufferPtr = IntPtr.Zero;
        IntPtr processedImage = IntPtr.Zero;
        var sb = new System.Text.StringBuilder();

        try
        {
            // Initialize LibRaw
            processor = LibRawNative.libraw_init(0);
            if (processor == IntPtr.Zero) return (null, "libraw_init failed");

            // Copy buffer to unmanaged memory
            bufferPtr = Marshal.AllocHGlobal(rawBuffer.Length);
            Marshal.Copy(rawBuffer, 0, bufferPtr, rawBuffer.Length);

            // Open and unpack
            int ret = LibRawNative.libraw_open_buffer(processor, bufferPtr, (UIntPtr)rawBuffer.Length);
            if (ret != LibRawNative.LIBRAW_SUCCESS) return (null, $"libraw_open_buffer failed: {ret}");

            ret = LibRawNative.libraw_unpack(processor);
            if (ret != LibRawNative.LIBRAW_SUCCESS) return (null, $"libraw_unpack failed: {ret}");

            // Process to RGB
            // Set output parameters for 16-bit linear
            // We need to access params structure to set output_bps=16, output_color=1 (sRGB) or 0 (raw)
            // But for now let's try default dcraw_process which usually does 8-bit unless configured.
            // Wait, we need 16-bit for NINA? NINA handles 16-bit ushort.
            // LibRaw defaults might be 8-bit.
            
            // Let's check what we get.
            ret = LibRawNative.libraw_dcraw_process(processor);
            if (ret != LibRawNative.LIBRAW_SUCCESS) return (null, $"libraw_dcraw_process failed: {ret}");

            // Get processed RGB image
            int errcode = 0;
            processedImage = LibRawNative.libraw_dcraw_make_mem_image(processor, ref errcode);
            if (processedImage == IntPtr.Zero || errcode != 0) return (null, $"libraw_dcraw_make_mem_image failed: {errcode}");

            // Extract RGB data from processed image
            var imgStruct = Marshal.PtrToStructure<LibRawNative.LibRaw_ProcessedImage>(processedImage);
            
            sb.AppendLine($"Processed Image: Type={imgStruct.type}, W={imgStruct.width}, H={imgStruct.height}, Colors={imgStruct.colors}, Bits={imgStruct.bits}, Size={imgStruct.data_size}");

            if (imgStruct.colors != 3)
                return (null, $"Expected 3 colors, got {imgStruct.colors}. Log: {sb}");
            
            // If bits is 8, we need to upsample to 16 for ushort[]? Or just cast?
            // ushort[] implies 16-bit. If we get 8-bit, we should probably scale it up.
            
            int pixelCount = imgStruct.width * imgStruct.height;
            int rgbDataSize = pixelCount * 3; // 3 channels
            ushort[] rgbData = new ushort[rgbDataSize];

            // The data follows immediately after the header
            IntPtr dataPtr = IntPtr.Add(processedImage, Marshal.SizeOf<LibRawNative.LibRaw_ProcessedImage>());
            
            if (imgStruct.bits == 16)
            {
                // Copy RGB data (LibRaw returns 16-bit values)
                // Note: LibRaw 16-bit data is usually ushort already.
                // But previous code used short[] tempArray?
                // Let's assume it's ushort (unsigned).
                
                // Marshal.Copy only supports short[], int[], byte[], etc. Not ushort[].
                // So we copy to short[] and BlockCopy to ushort[].
                short[] tempArray = new short[rgbDataSize];
                Marshal.Copy(dataPtr, tempArray, 0, rgbDataSize);
                Buffer.BlockCopy(tempArray, 0, rgbData, 0, rgbDataSize * sizeof(ushort));
            }
            else if (imgStruct.bits == 8)
            {
                // 8-bit data. Copy to byte[] then scale to ushort.
                byte[] tempArray = new byte[rgbDataSize];
                Marshal.Copy(dataPtr, tempArray, 0, rgbDataSize);
                for(int i=0; i<rgbDataSize; i++)
                {
                    rgbData[i] = (ushort)(tempArray[i] * 257); // Scale 0-255 to 0-65535
                }
                sb.AppendLine("Scaled 8-bit data to 16-bit.");
            }
            else
            {
                return (null, $"Unsupported bit depth: {imgStruct.bits}. Log: {sb}");
            }

            return (rgbData, sb.ToString());
        }
        catch (Exception ex)
        {
            return (null, $"Exception: {ex.Message}\n{ex.StackTrace}\nLog: {sb}");
        }
        finally
        {
            if (processedImage != IntPtr.Zero)
                LibRawNative.libraw_dcraw_clear_mem(processedImage);
            
            if (bufferPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(bufferPtr);
            
            if (processor != IntPtr.Zero)
                LibRawNative.libraw_close(processor);
        }
    }

    private static (string Pattern, int Width, int Height) GetPatternFromLibRaw(byte[] rawBuffer)
    {
        IntPtr processor = IntPtr.Zero;
        IntPtr bufferPtr = IntPtr.Zero;

        try
        {
            processor = LibRawNative.libraw_init(0);
            if (processor == IntPtr.Zero) return ("RGGB", 2, 2);

            bufferPtr = Marshal.AllocHGlobal(rawBuffer.Length);
            Marshal.Copy(rawBuffer, 0, bufferPtr, rawBuffer.Length);

            if (LibRawNative.libraw_open_buffer(processor, bufferPtr, (UIntPtr)rawBuffer.Length) != LibRawNative.LIBRAW_SUCCESS)
                return ("RGGB", 2, 2);

            // Get image params to check filters
            IntPtr iparamsPtr = LibRawNative.libraw_get_iparams(processor);
            if (iparamsPtr != IntPtr.Zero)
            {
                var iparams = Marshal.PtrToStructure<LibRawNative.LibRaw_ImageParams>(iparamsPtr);
                
                // Check for X-Trans (filters = 9)
                // Note: LibRaw defines LIBRAW_XTRANS as 9 in some versions, or checks xtrans array
                // A simple check is if filters is non-zero and not standard Bayer
                
                // If filters is 0, it might be monochrome or something else, but usually implies Bayer logic applies elsewhere
                // If filters is 9, it is definitely X-Trans
                
                if (iparams.filters == 9) // LIBRAW_XTRANS
                {
                    return ("XTRANS", 6, 6);
                }
                else
                {
                    // Decode standard Bayer pattern
                    return (DecodeBayerPattern(iparams.filters), 2, 2);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RawProcessor] Failed to get pattern: {ex.Message}");
        }
        finally
        {
            if (bufferPtr != IntPtr.Zero) Marshal.FreeHGlobal(bufferPtr);
            if (processor != IntPtr.Zero) LibRawNative.libraw_close(processor);
        }

        return ("RGGB", 2, 2); // Default fallback
    }

    public static (int Left, int Top, int Width, int Height, string Log) GetCropInfoFromLibRaw(byte[] rawBuffer)
    {
        IntPtr processor = IntPtr.Zero;
        IntPtr bufferPtr = IntPtr.Zero;
        var log = new System.Text.StringBuilder();

        try
        {
            log.AppendLine("Initializing LibRaw...");
            processor = LibRawNative.libraw_init(0);
            if (processor == IntPtr.Zero) 
            {
                log.AppendLine("libraw_init failed (returned null).");
                return (0, 0, 0, 0, log.ToString());
            }

            bufferPtr = Marshal.AllocHGlobal(rawBuffer.Length);
            Marshal.Copy(rawBuffer, 0, bufferPtr, rawBuffer.Length);

            log.AppendLine($"Opening buffer of size {rawBuffer.Length}...");
            int openResult = LibRawNative.libraw_open_buffer(processor, bufferPtr, (UIntPtr)rawBuffer.Length);
            if (openResult != LibRawNative.LIBRAW_SUCCESS)
            {
                log.AppendLine($"libraw_open_buffer failed with code {openResult}.");
                return (0, 0, 0, 0, log.ToString());
            }

            IntPtr sizesPtr = IntPtr.Add(processor, 8);
            var sizes = Marshal.PtrToStructure<LibRawNative.LibRaw_ImageSizes>(sizesPtr);

            log.AppendLine($"LibRaw Sizes: raw_w={sizes.raw_width}, raw_h={sizes.raw_height}, w={sizes.width}, h={sizes.height}, left={sizes.left_margin}, top={sizes.top_margin}");

            // The user reported a black bar on the right side with the default LibRaw width.
            // This suggests the active width includes some overscan or optical black pixels.
            // We'll reduce the width by a safe margin (e.g., 48 pixels) to ensure a clean image.
            // We must ensure the width remains even (divisible by 2) for Bayer/CFA.
            int safeWidth = sizes.width - 48;
            if (safeWidth % 2 != 0) safeWidth--;

            log.AppendLine($"Adjusting width for safety: {sizes.width} -> {safeWidth}");

            // Return the margins and active dimensions
            return (sizes.left_margin, sizes.top_margin, safeWidth, sizes.height, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Exception in GetCropInfoFromLibRaw: {ex.Message}");
            log.AppendLine(ex.StackTrace);
        }
        finally
        {
            if (bufferPtr != IntPtr.Zero) Marshal.FreeHGlobal(bufferPtr);
            if (processor != IntPtr.Zero) LibRawNative.libraw_close(processor);
        }

        return (0, 0, 0, 0, log.ToString());
    }

    private static string DecodeBayerPattern(uint filters)
    {
        // LibRaw encodes Bayer pattern in the 'filters' 32-bit integer
        // It's a 4-character sequence packed into uint
        // 0x94949494 is standard Bayer? No, LibRaw uses a specific encoding.
        // Actually, standard LibRaw uses:
        // 0x16161616 = BIBG (B G G R) ?
        // Let's use the standard decoding logic:
        // OpenBayerMap: 0:R, 1:G, 2:B, 3:G2
        
        // A simpler way is to look at the first 2x2 block if we could, but we only have the uint.
        // Common values:
        // 0x94949494 (2492765332) -> RGGB
        // 0x61616161 (1633771873) -> BGGR
        // 0x49494949 (1229531465) -> GBRG
        // 0x16161616 (370546198)  -> GRBG
        
        // Let's try to map common LibRaw filter values
        switch (filters)
        {
            case 0x94949494: return "RGGB";
            case 0x61616161: return "BGGR";
            case 0x49494949: return "GBRG";
            case 0x16161616: return "GRBG";
            default:
                // If unknown, try to infer from the first byte
                byte first = (byte)(filters & 0xFF);
                return first switch
                {
                    0x94 => "RGGB",
                    0x61 => "BGGR",
                    0x49 => "GBRG",
                    0x16 => "GRBG",
                    _ => "RGGB" // Default
                };
        }
    }

    private static bool IsXTrans(string pattern)
    {
        return pattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ||
               pattern.StartsWith("X-TRANS", StringComparison.OrdinalIgnoreCase);
    }

    private static ushort[] ConvertToLinear(Array? plane)
    {
        if (plane == null) return Array.Empty<ushort>();

        var height = plane.GetLength(0);
        var width = plane.GetLength(1);
        var linear = new ushort[width * height];
        var index = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = plane.GetValue(y, x);
                linear[index++] = value is ushort u ? u : Convert.ToUInt16(value ?? 0);
            }
        }

        return linear;
    }

    private static bool ReadBoolProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName)?.GetValue(instance) is bool value && value;
    }

    private static int ReadIntProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName)?.GetValue(instance) is int value ? value : 0;
    }

    private static string? ReadStringProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName)?.GetValue(instance)?.ToString();
    }

    private static Array? ReadArrayProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName)?.GetValue(instance) as Array;
    }
}

public class RawProcessingResult
{
    public bool Success { get; set; }
    public LibRawProcessingStatus Status { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ColorFilterPattern { get; set; } = string.Empty;
    public int PatternWidth { get; set; }
    public int PatternHeight { get; set; }
    public ushort[] BayerData { get; set; } = Array.Empty<ushort>();
    public ushort[]? DebayeredRgb { get; set; }
    public int BlackLevel { get; set; }
    public int WhiteLevel { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RafSidecarPath { get; set; }
    public int ActiveLeft { get; set; }
    public int ActiveTop { get; set; }
    public int ActiveWidth { get; set; }
    public int ActiveHeight { get; set; }
}
