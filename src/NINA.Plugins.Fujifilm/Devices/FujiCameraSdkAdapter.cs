using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;
using NINA.Plugins.Fujifilm.Imaging;
using NINA.Plugins.Fujifilm.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
﻿using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Devices;

#nullable enable
internal sealed class FujiCameraSdkAdapter : IGenericCameraSDK, IDisposable
{
    private readonly FujiCamera _camera;
    private readonly FujifilmCameraDescriptor _descriptor;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly ILibRawAdapter _libRawAdapter;
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly CameraImageBuilder _imageBuilder;
    private readonly IDisposable _cameraLifetime;
    private readonly IDisposable _libRawLifetime;

    private readonly object _sync = new();

    private CameraConfig? _config;
    private int[] _isoValues = Array.Empty<int>();
    private int _currentIso;
    private bool _connected;

    private Task<RawCaptureResult>? _captureTask;
    private CancellationTokenSource? _captureCts;
    private double _lastExposureSeconds;
    private bool _imageReady;
    private FujiCameraExposureState _cameraState = FujiCameraExposureState.Idle;

    private int _roiX;
    private int _roiY;
    private int _roiWidth;
    private int _roiHeight;
    private int _roiBin = 1;
    private bool _disposed;
    private DateTime _lastExposureStartUtc = DateTime.MinValue;
    private FujiCameraCapabilities _capabilities = FujiCameraCapabilities.Empty;
    private FujiImagePackage? _lastImagePackage;

    private readonly IProfileService _profileService;

    public FujiCameraSdkAdapter(
        FujiCamera camera,
        FujifilmCameraDescriptor descriptor,
        IFujifilmDiagnosticsService diagnostics,
        ILibRawAdapter libRawAdapter,
        IFujiSettingsProvider settingsProvider,
        IProfileService profileService,
        IDisposable cameraLifetime,
        IDisposable libRawLifetime)
    {
        _camera = camera;
        _descriptor = descriptor;
        _diagnostics = diagnostics;
        _libRawAdapter = libRawAdapter;
        _settingsProvider = settingsProvider;
        _profileService = profileService;
        _imageBuilder = new CameraImageBuilder(settingsProvider, diagnostics, profileService);
        _cameraLifetime = cameraLifetime;
        _libRawLifetime = libRawLifetime;
    }

    public bool Connected => _connected;

    public void Connect()
    {
        if (_connected)
        {
            return;
        }

        _camera.ConnectAsync(_descriptor, CancellationToken.None).GetAwaiter().GetResult();
        _config = _camera.Configuration;
        _capabilities = _camera.GetCapabilitiesSnapshot();
        _cameraState = FujiCameraExposureState.Idle;
        _imageReady = false;
        _lastImagePackage = null;
        _isoValues = _capabilities.IsoValues.Count > 0 ? CopyIsoValues(_capabilities.IsoValues) : Array.Empty<int>();
        _currentIso = _camera.SelectClosestIso(_capabilities.DefaultIso > 0 ? _capabilities.DefaultIso : (_isoValues.Length > 0 ? _isoValues[0] : 200));
        _roiX = 0;
        _roiY = 0;
        _roiWidth = _capabilities.SensorWidth > 0 ? _capabilities.SensorWidth : (_config?.CameraXSize ?? 0);
        _roiHeight = _capabilities.SensorHeight > 0 ? _capabilities.SensorHeight : (_config?.CameraYSize ?? 0);
        _roiBin = 1;
        _connected = true;
        _diagnostics.RecordEvent("Adapter", $"Connected to {_descriptor.DisplayName}");
        _diagnostics.RecordEvent("Adapter", $"Available ISO values: [{string.Join(", ", _isoValues)}]");
        _diagnostics.RecordEvent("Adapter", $"Initial ISO set to: {_currentIso}");
        _diagnostics.RecordEvent("Adapter", $"Buffer capacity: {_capabilities.BufferShootCapacity}/{_capabilities.BufferTotalCapacity}");
        _diagnostics.RecordEvent("Adapter", $"State Mode={_capabilities.ModeCode}, AE={_capabilities.AEModeCode}, DR={_capabilities.DynamicRangeCode}, LastError={_capabilities.LastSdkErrorCode} (API {_capabilities.LastApiErrorCode})");
    }

    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        CancelCapture();
        _camera.DisconnectAsync().GetAwaiter().GetResult();
        _connected = false;
        _cameraState = FujiCameraExposureState.Idle;
        _imageReady = false;
        _lastImagePackage = null;
        _diagnostics.RecordEvent("Adapter", $"Disconnected {_descriptor.DisplayName}");
    }

    /// <summary>
    /// Gets supported binning modes.
    /// Note: Fujifilm cameras do not support binning via the SDK.
    /// Only 1x1 (no binning) is available.
    /// </summary>
    public int[] GetBinningInfo()
    {
        // Binning is not supported by Fujifilm cameras
        return new[] { 1 };
    }

    public (int, int) GetDimensions()
    {
        var width = _roiWidth > 0 ? _roiWidth : (_config?.CameraXSize ?? 0);
        var height = _roiHeight > 0 ? _roiHeight : (_config?.CameraYSize ?? 0);
        return (width, height);
    }

    public int GetGain() => _currentIso;

    public int GetMaxGain() => (_isoValues.Length > 0) ? _isoValues[_isoValues.Length - 1] : 0;

    public int GetMaxOffset() => 0;

    public int GetMaxUSBLimit() => 0;

    public int GetMinGain() => (_isoValues.Length > 0) ? _isoValues[0] : 0;

    public int GetMinOffset() => 0;

    public int GetMinUSBLimit() => 0;

    public int GetOffset() => 0;

    public double GetPixelSize() => _config?.PixelSizeX ?? double.NaN;

    public int GetUSBLimit() => 0;

    public SensorType GetSensorInfo()
    {
        // NINA requires a specific Bayer pattern (RGGB, BGGR, etc.) to enable debayering.
        // Returning SensorType.Color causes "Unsupported CFA/Bayer pattern" error because 
        // NINA expects debayered data but we are returning RAW data (unless X-Trans preview).
        //
        // For GFX cameras (Bayer), RGGB is the standard.
        // For X-Trans, NINA doesn't have an XTrans enum, so we use RGGB as a placeholder
        // and rely on the BAYERPAT metadata ("XTRANS") to inform advanced processors.
        //
        // If we are in X-Trans mode and successfully debayered for preview, we might want Color,
        // but GetSensorInfo is called before exposure. So RGGB is the safest default for RAW capture.
        return SensorType.RGGB;
    }

    public bool SetGain(int value)
    {
        var previousIso = _currentIso;
        _currentIso = _camera.SelectClosestIso(value);
        _diagnostics.RecordEvent("Adapter", $"SetGain called: requested={value}, selected={_currentIso} (was {previousIso})");
        return true;
    }

    public bool SetOffset(int value) => false;

    public bool SetUSBLimit(int value) => false;

    public double GetMaxExposureTime() => _capabilities.MaxExposureSeconds > 0 ? _capabilities.MaxExposureSeconds : (_config?.DefaultMaxExposure ?? 600.0);

    public double GetMinExposureTime() => _capabilities.MinExposureSeconds > 0 ? _capabilities.MinExposureSeconds : (_config?.DefaultMinExposure ?? 0.001);

    public void StartExposure(double exposureTime, int width, int height)
    {
        EnsureConnected();

        lock (_sync)
        {
            if (_captureTask != null && !_captureTask.IsCompleted)
            {
                throw new InvalidOperationException("Exposure already in progress.");
            }

            _captureCts = new CancellationTokenSource();
            _lastExposureSeconds = exposureTime;
            _cameraState = FujiCameraExposureState.Exposing;
            _imageReady = false;
            _lastImagePackage = null;

            _captureTask = Task.Run(() =>
                _camera.CaptureRawAsync(exposureTime, _currentIso, _captureCts.Token),
                CancellationToken.None);
            _captureTask.ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    _cameraState = FujiCameraExposureState.Error;
                    _imageReady = false;
                    return;
                }

                if (task.IsFaulted)
                {
                    _cameraState = FujiCameraExposureState.Error;
                    _imageReady = false;
                    _diagnostics.RecordEvent("Adapter", $"Exposure task faulted: {task.Exception?.GetBaseException().Message}");
                    return;
                }

                _imageReady = true;
                _cameraState = FujiCameraExposureState.Ready;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _lastExposureStartUtc = DateTime.UtcNow;
    }

    public void StopExposure()
    {
        CancelCapture();
    }

    /// <summary>
    /// Sets the Region of Interest (ROI) for image capture.
    /// Note: Fujifilm cameras do not support arbitrary ROI or binning via the SDK.
    /// Only full-frame capture is supported. This method validates that the requested
    /// ROI matches the full sensor dimensions.
    /// </summary>
    public bool SetROI(int startX, int startY, int width, int height, int binning)
    {
        var fullWidth = _capabilities.SensorWidth > 0 ? _capabilities.SensorWidth : (_config?.CameraXSize ?? width);
        var fullHeight = _capabilities.SensorHeight > 0 ? _capabilities.SensorHeight : (_config?.CameraYSize ?? height);
        
        // Fujifilm cameras don't support arbitrary ROI or binning - only full frame
        if (startX != 0 || startY != 0 || width != fullWidth || height != fullHeight || binning != 1)
        {
            _diagnostics.RecordEvent("Adapter", $"ROI/Binning not supported by Fujifilm SDK. Requested: ({startX},{startY}) {width}x{height} bin{binning}, Required: (0,0) {fullWidth}x{fullHeight} bin1");
            return false;
        }

        // Store the full-frame ROI for GetROI() compatibility
        _roiX = startX;
        _roiY = startY;
        _roiWidth = width;
        _roiHeight = height;
        _roiBin = 1; // Binning is not supported
        return true;
    }

    public int GetBitDepth() => 16;

    public (int, int, int, int, int) GetROI() => (_roiX, _roiY, _roiWidth, _roiHeight, _roiBin);

    public bool HasTemperatureReadout() => false;

    public bool HasTemperatureControl() => false;

    public bool SetCooler(bool onOff) => false;

    public bool GetCoolerOnOff() => false;

    public bool SetTargetTemperature(double temperature) => false;

    public double GetTargetTemperature() => double.NaN;

    public double GetTemperature() => double.NaN;

    public double GetCoolerPower() => double.NaN;

    public FujiImagePackage? LastImagePackage => _lastImagePackage;

    public FujiCameraExposureState GetCameraState() => _cameraState;

    public double GetExposureProgress()
    {
        if (_cameraState == FujiCameraExposureState.Exposing && _lastExposureSeconds > 0)
        {
            var elapsed = (DateTime.UtcNow - _lastExposureStartUtc).TotalSeconds;
            return Math.Clamp((elapsed / _lastExposureSeconds) * 100.0, 0.0, 100.0);
        }

        return _cameraState switch
        {
            FujiCameraExposureState.Downloading => 90.0,
            FujiCameraExposureState.Ready => 100.0,
            _ => 0.0
        };
    }

    public async Task<ushort[]> GetExposure(double exposureTime, int width, int height, CancellationToken ct)
    {
        Task<RawCaptureResult>? captureTask;
        lock (_sync)
        {
            captureTask = _captureTask;
        }

        if (captureTask == null)
        {
            throw new InvalidOperationException("No exposure has been started.");
        }

        _cameraState = FujiCameraExposureState.Downloading;

        RawCaptureResult raw;
        try
        {
            raw = await captureTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _captureTask = null;
                _captureCts?.Dispose();
                _captureCts = null;
            }
        }

        _diagnostics.RecordEvent("Adapter", $"Raw buffer captured: {raw.RawBuffer.Length} bytes");
        var libRaw = await _libRawAdapter.ProcessRawAsync(raw.RawBuffer, ct).ConfigureAwait(false);
        _diagnostics.RecordEvent("Adapter", $"LibRaw processing: Success={libRaw.Success}, Width={libRaw.Width}, Height={libRaw.Height}, BayerDataLen={libRaw.BayerData.Length}");
        var package = _imageBuilder.Build(raw, libRaw, _capabilities, _config);
        _diagnostics.RecordEvent("Adapter", $"Image package: Width={package.Width}, Height={package.Height}, PixelsLen={package.Pixels.Length}");
        
        // Diagnostic logging for black image investigation
        if (package.Pixels.Length > 0)
        {
            ushort min = ushort.MaxValue;
            ushort max = ushort.MinValue;
            long sum = 0;
            
            // Sample every 100th pixel to avoid performance hit
            for (int i = 0; i < package.Pixels.Length; i += 100)
            {
                var val = package.Pixels[i];
                if (val < min) min = val;
                if (val > max) max = val;
                sum += val;
            }
            
            double avg = (double)sum / (package.Pixels.Length / 100 + 1);
            _diagnostics.RecordEvent("Adapter", $"Image stats (sampled): Min={min}, Max={max}, Avg={avg:F2}");
        }

        _roiWidth = package.Width;
        _roiHeight = package.Height;
        _lastImagePackage = package;
        _imageReady = false;
        _cameraState = FujiCameraExposureState.Idle;
        
        // For X-Trans cameras: We use LibRaw to debayer non-destructively (raw data is preserved).
        // The debayered RGB data is used for NINA's live preview to show color images.
        // 
        // Since NINA's GetExposure() expects single-channel data (width*height), we have options:
        // 1. Return debayered RGB converted to luminance (grayscale preview, shows image content)
        // 2. Return raw bayer data (NINA can't debayer X-Trans, so this shows incorrectly)
        // 3. Try returning RGB data in a format NINA might understand (experimental)
        //
        // The metadata with BAYERPAT=XTRANS is already set in the image package,
        // which will be written to FITS/XISF files for stacking software.
        // The raw bayer data in package.Pixels is always preserved for proper stacking.
        var debayeredRgb = package.GetDebayeredRgb();
        if (debayeredRgb != null && package.ColorFilterPattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.RecordEvent("Adapter", $"X-Trans detected with debayered RGB available from LibRaw (non-destructive).");
            
            // Try returning RGB data for color preview
            // Note: NINA's GetExposure() signature expects width*height pixels.
            // Returning interleaved RGB (width*height*3) causes the image to appear zoomed in and corrupted
            // because NINA interprets it as a single channel image.
            //
            // Return Synthetic RGGB Bayer data for color preview.
            // NINA expects a single-channel Bayer image (Width * Height).
            // We take our high-quality debayered RGB data and "re-mosaic" it into an RGGB pattern.
            // NINA will then debayer this synthetic image, producing a color preview.
            // This preserves the correct image dimensions and provides color.
            try
            {
                var syntheticBayer = ConvertRgbToSyntheticBayer(debayeredRgb, package.Width, package.Height);
                _diagnostics.RecordEvent("Adapter", $"Returning Synthetic RGGB data for X-Trans preview ({syntheticBayer.Length} pixels)");
                return syntheticBayer;
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("Adapter", $"Synthetic Bayer conversion failed, falling back to raw: {ex.Message}");
                return package.Pixels;
            }
        }
        
        // For Bayer cameras or when debayered RGB is not available, return raw bayer data
        // NINA will debayer this using the BAYERPAT metadata we provide
        return package.Pixels;
    }
    
    /// <summary>
    /// Converts RGB data to a synthetic RGGB Bayer pattern.
    /// This allows NINA to display a color preview for X-Trans cameras by treating the data as standard Bayer.
    /// </summary>
    private ushort[] ConvertRgbToSyntheticBayer(ushort[] rgbData, int width, int height)
    {
        var bayer = new ushort[width * height];
        
        // Calculate the source width from the RGB data length
        // The RGB data might be wider than the target width if we applied a safety crop
        // rgbData.Length = sourceWidth * height * 3
        int sourceWidth = rgbData.Length / (height * 3);
        
        // Parallel loop for performance
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                // Target index (packed)
                int index = y * width + x;
                
                // Source index (using source stride)
                int rgbIndex = (y * sourceWidth + x) * 3;
                
                // RGGB Pattern:
                // R G
                // G B
                
                bool isEvenRow = (y % 2 == 0);
                bool isEvenCol = (x % 2 == 0);
                
                if (isEvenRow)
                {
                    if (isEvenCol)
                    {
                        // Red
                        bayer[index] = rgbData[rgbIndex];
                    }
                    else
                    {
                        // Green (on Red row)
                        bayer[index] = rgbData[rgbIndex + 1];
                    }
                }
                else
                {
                    if (isEvenCol)
                    {
                        // Green (on Blue row)
                        bayer[index] = rgbData[rgbIndex + 1];
                    }
                    else
                    {
                        // Blue
                        bayer[index] = rgbData[rgbIndex + 2];
                    }
                }
            }
        });
        
        return bayer;
    }

    /// <summary>
    /// Converts RGB data (RGBRGB... format) to luminance for NINA preview display.
    /// This is the fallback method when RGB preview doesn't work.
    /// Since NINA's GetExposure() expects single-channel data (width*height), we convert
    /// RGB to grayscale luminance for preview. The raw bayer data is still available in
    /// the image package for proper stacking.
    /// 
    /// This debayering is non-destructive - it's only for live preview.
    /// </summary>
    private ushort[] ConvertRgbToLuminance(ushort[] rgbData, int width, int height)
    {
        // Convert RGB to luminance (Y = 0.299*R + 0.587*G + 0.114*B)
        // This provides a grayscale preview that shows the image content
        // The raw bayer data is preserved in the image package for stacking software
        var luminance = new ushort[width * height];
        for (int i = 0; i < width * height; i++)
        {
            var r = rgbData[i * 3];
            var g = rgbData[i * 3 + 1];
            var b = rgbData[i * 3 + 2];
            
            // ITU-R BT.601 luminance formula
            var y = (ushort)(0.299 * r + 0.587 * g + 0.114 * b);
            luminance[i] = y;
        }
        
        _diagnostics.RecordEvent("Adapter", $"Converted RGB to luminance for X-Trans preview ({luminance.Length} pixels)");
        return luminance;
    }

    public bool IsExposureReady()
    {
        lock (_sync)
        {
            return _imageReady;
        }
    }

    public bool HasDewHeater() => false;

    public bool SetDewHeater(int power) => false;

    public bool IsDewHeaterOn() => false;

    /// <summary>
    /// Starts live view video capture.
    /// Note: The Fujifilm SDK supports live view via model-dependent APIs (section 4.2.16),
    /// but this feature is not yet implemented. Live view requires:
    /// - Checking API availability (StartLiveView, StopLiveView, GetLiveViewStatus)
    /// - Model-specific API codes and constants
    /// - Continuous frame grabbing and JPEG decoding
    /// </summary>
    public void StartVideoCapture(double exposureTime, int width, int height)
    {
        _diagnostics.RecordEvent("Adapter", "Live view requested but not yet implemented. SDK supports it via model-dependent APIs.");
        throw new NotSupportedException("Live view is not yet implemented for Fujifilm cameras. The SDK supports it, but implementation is pending.");
    }

    /// <summary>
    /// Stops live view video capture.
    /// Note: See StartVideoCapture() for implementation status.
    /// </summary>
    public void StopVideoCapture()
    {
        throw new NotSupportedException("Live view is not yet implemented for Fujifilm cameras.");
    }

    /// <summary>
    /// Gets a live view frame.
    /// Note: See StartVideoCapture() for implementation status.
    /// </summary>
    public Task<ushort[]> GetVideoCapture(double exposureTime, int width, int height, CancellationToken ct)
    {
        throw new NotSupportedException("Live view is not yet implemented for Fujifilm cameras.");
    }

    public List<string> GetReadoutModes() => new() { "Default" };

    public int GetReadoutMode() => 0;

    public void SetReadoutMode(int modeIndex)
    {
        if (modeIndex != 0)
        {
            _diagnostics.RecordEvent("Adapter", "Readout mode selection is not supported; defaulting to mode 0.");
        }
    }

    public bool HasAdjustableFan() => false;

    public bool SetFanPercentage(int fanPercentage) => false;

    public int GetFanPercentage() => 0;

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }
    }

    private void CancelCapture()
    {
        lock (_sync)
        {
            if (_captureTask == null)
            {
                return;
            }

            try
            {
                _camera.StopExposureAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("Adapter", $"StopExposure failed during cancel: {ex.Message}");
            }

            _captureCts?.Cancel();
            _captureTask = null;
            _captureCts?.Dispose();
            _captureCts = null;
            _cameraState = FujiCameraExposureState.Idle;
            _imageReady = false;
            _lastImagePackage = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_connected)
        {
            Disconnect();
        }

        _cameraLifetime.Dispose();
        _libRawLifetime.Dispose();
    }

    private static int[] CopyIsoValues(IReadOnlyList<int> isoValues)
    {
        var result = new int[isoValues.Count];
        for (var i = 0; i < isoValues.Count; i++)
        {
            result[i] = isoValues[i];
        }

        return result;
    }
}

internal enum FujiCameraExposureState
{
    Idle,
    Exposing,
    Downloading,
    Ready,
    Error
}