using System;
using System.Runtime.InteropServices;

namespace NINA.Plugins.Fujifilm.Interop;

/// <summary>
/// P/Invoke declarations for libraw.dll C API
/// </summary>
internal static class LibRawNative
{
    private const string LibRawDll = "libraw.dll";

    #region Lifecycle Functions
    
    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_init(uint flags);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_close(IntPtr data);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr libraw_version();

    #endregion

    #region Data Loading

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_open_buffer(IntPtr data, IntPtr buffer, UIntPtr size);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_open_file(IntPtr data, [MarshalAs(UnmanagedType.LPStr)] string fileName);

    #endregion

    #region Processing

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_unpack(IntPtr data);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_dcraw_process(IntPtr data);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_dcraw_make_mem_image(IntPtr data, ref int errcode);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_dcraw_clear_mem(IntPtr img);

    #endregion

    #region Error Handling

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_strerror(int errorcode);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libraw_get_decoder_info(IntPtr data, ref LibRaw_Decoder_Info info);

    #endregion

    #region Data Access

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_get_iparams(IntPtr data);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_get_imgother(IntPtr data);

    [DllImport(LibRawDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_get_image_params(IntPtr data);

    #endregion

    #region Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_ImageSizes
    {
        public ushort raw_height;
        public ushort raw_width;
        public ushort height;
        public ushort width;
        public ushort top_margin;
        public ushort left_margin;
        public ushort iheight;
        public ushort iwidth;
        public uint raw_pitch;
        public double pixel_aspect;
        public int flip;
        
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] mask;
        
        public ushort raw_inset_crop_top;
        public ushort raw_inset_crop_left;
        public ushort raw_inset_crop_bottom;
        public ushort raw_inset_crop_right;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_ColorData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x10000)]
        public ushort[] curve;

        public uint cblack_0;
        public uint cblack_1;
        public uint cblack_2;
        public uint cblack_3;
        public uint cblack_4;
        public uint cblack_5;
        public uint cblack_6;
        public uint black;
        public uint data_maximum;
        public uint maximum;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public int[] linear_max;

        public float fmaximum;
        public float fnorm;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x2000)]
        public ushort[] white;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3 * 4)]
        public float[] cam_mul;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public float[] pre_mul;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3 * 4)]
        public float[] cmatrix;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3 * 4)]
        public float[] ccm;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3 * 4)]
        public float[] rgb_cam;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4 * 3)]
        public float[] cam_xyz;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public LibRaw_Ph1_QuadrantCorrection[] phase_one_data;

        public float flash_used;
        public float canon_ev;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] model2;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] UniqueCameraModel;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] LocalizedCameraModel;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] ImageUniqueID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] RawDataUniqueID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] OriginalRawFileName;

        public IntPtr profile;
        public uint profile_length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] black_stat;

        public IntPtr dng_color_0;
        public IntPtr dng_color_1;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public LibRaw_DNGLevels[] dng_levels;

        public int WB_Coeffs_0_0, WB_Coeffs_0_1, WB_Coeffs_0_2, WB_Coeffs_0_3;
        public int WB_Coeffs_1_0, WB_Coeffs_1_1, WB_Coeffs_1_2, WB_Coeffs_1_3;
        public int WB_Coeffs_2_0, WB_Coeffs_2_1, WB_Coeffs_2_2, WB_Coeffs_2_3;
        public int WB_Coeffs_3_0, WB_Coeffs_3_1, WB_Coeffs_3_2, WB_Coeffs_3_3;
        public int WB_Coeffs_4_0, WB_Coeffs_4_1, WB_Coeffs_4_2, WB_Coeffs_4_3;
        public int WB_Coeffs_5_0, WB_Coeffs_5_1, WB_Coeffs_5_2, WB_Coeffs_5_3;
        public int WB_Coeffs_6_0, WB_Coeffs_6_1, WB_Coeffs_6_2, WB_Coeffs_6_3;
        public int WB_Coeffs_7_0, WB_Coeffs_7_1, WB_Coeffs_7_2, WB_Coeffs_7_3;
        public int WB_Coeffs_8_0, WB_Coeffs_8_1, WB_Coeffs_8_2, WB_Coeffs_8_3;
        public int WB_Coeffs_9_0, WB_Coeffs_9_1, WB_Coeffs_9_2, WB_Coeffs_9_3;
        public float WBCT_Coeffs_6_0, WBCT_Coeffs_6_1, WBCT_Coeffs_6_2, WBCT_Coeffs_6_3, WBCT_Coeffs_6_4;
        public float WBCT_Coeffs_7_0, WBCT_Coeffs_7_1, WBCT_Coeffs_7_2, WBCT_Coeffs_7_3, WBCT_Coeffs_7_4;
        public float WBCT_Coeffs_8_0, WBCT_Coeffs_8_1, WBCT_Coeffs_8_2, WBCT_Coeffs_8_3, WBCT_Coeffs_8_4;
        public float WBCT_Coeffs_9_0, WBCT_Coeffs_9_1, WBCT_Coeffs_9_2, WBCT_Coeffs_9_3, WBCT_Coeffs_9_4;
        public float WBCT_Coeffs_10_0, WBCT_Coeffs_10_1, WBCT_Coeffs_10_2, WBCT_Coeffs_10_3, WBCT_Coeffs_10_4;
        public float WBCT_Coeffs_11_0, WBCT_Coeffs_11_1, WBCT_Coeffs_11_2, WBCT_Coeffs_11_3, WBCT_Coeffs_11_4;
        public float WBCT_Coeffs_12_0, WBCT_Coeffs_12_1, WBCT_Coeffs_12_2, WBCT_Coeffs_12_3, WBCT_Coeffs_12_4;
        public float WBCT_Coeffs_13_0, WBCT_Coeffs_13_1, WBCT_Coeffs_13_2, WBCT_Coeffs_13_3, WBCT_Coeffs_13_4;
        public float WBCT_Coeffs_14_0, WBCT_Coeffs_14_1, WBCT_Coeffs_14_2, WBCT_Coeffs_14_3, WBCT_Coeffs_14_4;
        public float WBCT_Coeffs_15_0, WBCT_Coeffs_15_1, WBCT_Coeffs_15_2, WBCT_Coeffs_15_3, WBCT_Coeffs_15_4;

        public LibRaw_P1_Color P1_color_0;
        public LibRaw_P1_Color P1_color_1;
        public LibRaw_P1_Color P1_color_2;

        public uint raw_bps;
        public int ExifColorSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_Ph1_QuadrantCorrection
    {
        public uint quadrant;
        public uint split_row;
        public uint split_col;
        public float black_0;
        public float black_1;
        public float black_2;
        public float black_3;
        public float scale_0;
        public float scale_1;
        public float scale_2;
        public float scale_3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_DNGLevels
    {
        public uint parsedfields;
        public uint dng_cblack_0;
        public uint dng_cblack_1;
        public uint dng_cblack_2;
        public uint dng_cblack_3;
        public uint dng_black;
        public float dng_fcblack_0;
        public float dng_fcblack_1;
        public float dng_fcblack_2;
        public float dng_fcblack_3;
        public float dng_fblack;
        public uint dng_whitelevel_0;
        public uint dng_whitelevel_1;
        public uint dng_whitelevel_2;
        public uint dng_whitelevel_3;
        public uint default_crop_origin_0;
        public uint default_crop_origin_1;
        public uint default_crop_size_0;
        public uint default_crop_size_1;
        public uint fuji_width;
        public uint fuji_total_width;
        public uint fuji_total_height;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] preview_colorspace;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public float[] analogbalance;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public uint[] asshotneutral;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2 * 3)]
        public float[] baseline_exposure;

        public LibRaw_LinearityLimit LinearizationTable;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_P1_Color
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public float[] romm_cam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_LinearityLimit
    {
        public int CoeffTablePresent;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public float[] LinearizationCoeffs;
        public int ActiveArea_0;
        public int ActiveArea_1;
        public int ActiveArea_2;
        public int ActiveArea_3;
        public int MaskedArea_count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32 * 4)]
        public int[] MaskedArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_ImageParams
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] guard;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] make;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] model;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] software;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] normalized_make;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] normalized_model;

        public uint maker_index;
        public uint raw_count;
        public uint dng_version;
        public uint is_foveon;
        public int colors;
        public uint filters;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public byte[] xtrans;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public byte[] xtrans_abs;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] cdesc;

        public uint xmplen;
        public IntPtr xmpdata;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_Decoder_Info
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] decoder_name;

        public uint decoder_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_ProcessedImage
    {
        public int type; // LibRaw_image_formats enum (4 bytes)
        public ushort height;
        public ushort width;
        public ushort colors;
        public ushort bits;
        public uint data_size;

        // data follows this header as a flexible array
        // We'll handle this specially in marshaling code
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibRaw_RawData
    {
        public IntPtr raw_alloc;
        public IntPtr raw_image;
        public IntPtr color4_image;
        public IntPtr color3_image;
        public IntPtr ph1_cblack;
        public IntPtr ph1_rblack;
        public IntPtr iparams;
        public LibRaw_ImageSizes sizes;
        public IntPtr ioparams;
        public LibRaw_ColorData color;
    }

    #endregion

    #region Error Codes

    public const int LIBRAW_SUCCESS = 0;
    public const int LIBRAW_UNSPECIFIED_ERROR = -1;
    public const int LIBRAW_FILE_UNSUPPORTED = -2;
    public const int LIBRAW_REQUEST_FOR_NONEXISTENT_IMAGE = -3;
    public const int LIBRAW_OUT_OF_ORDER_CALL = -4;
    public const int LIBRAW_NO_THUMBNAIL = -5;
    public const int LIBRAW_UNSUPPORTED_THUMBNAIL = -6;
    public const int LIBRAW_INPUT_CLOSED = -7;
    public const int LIBRAW_NOT_IMPLEMENTED = -8;
    public const int LIBRAW_UNSUFFICIENT_MEMORY = -100007;
    public const int LIBRAW_DATA_ERROR = -100008;
    public const int LIBRAW_IO_ERROR = -100009;
    public const int LIBRAW_CANCELLED_BY_CALLBACK = -100010;
    public const int LIBRAW_BAD_CROP = -100011;
    public const int LIBRAW_TOO_BIG = -100012;
    public const int LIBRAW_MEMPOOL_OVERFLOW = -100013;

    #endregion
}
