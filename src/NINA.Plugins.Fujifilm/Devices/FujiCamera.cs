using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Configuration;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Plugins.Fujifilm.Interop.Native;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

[Export(typeof(FujiCamera))]
[PartCreationPolicy(CreationPolicy.NonShared)]
public sealed class FujiCamera : IAsyncDisposable
{
    private readonly IFujifilmInterop _interop;
    private readonly ICameraModelCatalog _catalog;
    private readonly IFujiSettingsProvider _settingsProvider;
    private readonly IFujifilmDiagnosticsService _diagnostics;

    private FujifilmCameraSession? _session;
    private CameraConfig? _config;
    private IReadOnlyList<int> _supportedSensitivities = Array.Empty<int>();
    private IReadOnlyDictionary<int, double> _shutterCodeToDuration = new Dictionary<int, double>();
    private IReadOnlyList<int> _supportedShutterCodes = Array.Empty<int>(); // Store originally queried codes for validation
    private bool _bulbCapable;
    private const double DefaultMinExposureSeconds = 0.001;
    private int _bufferShootCapacity;
    private int _bufferTotalCapacity;
    private int _lastModeCode;
    private int _lastAEModeCode;
    private int _lastDynamicRangeCode;
    private int _lastApiErrorCode;
    private int _lastSdkErrorCode;
    private FujiCameraMetadata _metadata = FujiCameraMetadata.Empty;

    public bool SupportsBulb => _bulbCapable;

    public FujiCameraCapabilities GetCapabilitiesSnapshot()
    {
        var isoValues = GetAvailableIsoValues();
        var sensorWidth = _config?.CameraXSize ?? 0;
        var sensorHeight = _config?.CameraYSize ?? 0;
        _diagnostics.RecordEvent("Camera", $"GetCapabilitiesSnapshot: Config={(_config != null ? "Present" : "Null")} Width={sensorWidth} Height={sensorHeight}");
        var minExposure = GetMinExposureSecondsInternal();
        var maxExposure = GetMaxExposureSecondsInternal();
        var defaultIso = SelectClosestIsoInternal(_config?.DefaultMinSensitivity ?? (isoValues.Length > 0 ? isoValues[0] : 200));

        return new FujiCameraCapabilities(
            Array.AsReadOnly(isoValues),
            defaultIso,
            minExposure,
            maxExposure,
            _bulbCapable,
            sensorWidth,
            sensorHeight,
            _bufferShootCapacity,
            _bufferTotalCapacity,
            _lastModeCode,
            _lastAEModeCode,
            _lastDynamicRangeCode,
            _lastApiErrorCode,
            _lastSdkErrorCode,
            _metadata,
            maxExposure,
            _bulbCapable ? 3600.0 : maxExposure);
    }

    public int[] GetAvailableIsoValues()
    {
        if (_supportedSensitivities.Count > 0)
        {
            return _supportedSensitivities.ToArray();
        }

        return BuildFallbackIsoArray();
    }

    public int SelectClosestIso(int iso)
    {
        return SelectClosestIsoInternal(iso);
    }

    public double GetMinExposureSeconds()
    {
        return GetMinExposureSecondsInternal();
    }

    public double GetMaxExposureSeconds()
    {
        return GetMaxExposureSecondsInternal();
    }

    public (int shoot, int total) GetBufferCapacity()
    {
        return (_bufferShootCapacity, _bufferTotalCapacity);
    }

    private int SelectClosestIsoInternal(int iso)
    {
        var isoValues = GetAvailableIsoValues();
        if (isoValues.Length == 0)
        {
            return iso;
        }

        var closest = isoValues[0];
        var delta = Math.Abs(iso - closest);
        foreach (var candidate in isoValues)
        {
            var currentDelta = Math.Abs(iso - candidate);
            if (currentDelta < delta)
            {
                closest = candidate;
                delta = currentDelta;
            }
        }

        return closest;
    }

    private double GetMinExposureSecondsInternal()
    {
        var timed = _shutterCodeToDuration
            .Where(pair => pair.Key != FujifilmSdkWrapper.XSDK_SHUTTER_BULB && pair.Value > 0)
            .Select(pair => pair.Value)
            .ToList();

        if (timed.Count > 0)
        {
            return timed.Min();
        }

        return _config?.DefaultMinExposure ?? DefaultMinExposureSeconds;
    }

    private double GetMaxExposureSecondsInternal()
    {
        var timed = _shutterCodeToDuration
            .Where(pair => pair.Key != FujifilmSdkWrapper.XSDK_SHUTTER_BULB && pair.Value > 0)
            .Select(pair => pair.Value)
            .ToList();

        var timedMax = timed.Count > 0 ? timed.Max() : (_config?.DefaultMaxExposure ?? 600.0);
        if (_bulbCapable)
        {
            var bulbDefault = _config?.DefaultMaxExposure ?? 3600.0;
            var bulbConfigured = _shutterCodeToDuration.TryGetValue(FujifilmSdkWrapper.XSDK_SHUTTER_BULB, out var bulbValue)
                ? bulbValue
                : bulbDefault;
            return Math.Max(timedMax, bulbConfigured);
        }

        return timedMax;
    }

    private int[] BuildFallbackIsoArray()
    {
        if (_config == null)
        {
            _diagnostics.RecordEvent("Camera", "BuildFallbackIsoArray: No config available, returning empty array.");
            return Array.Empty<int>();
        }

        var minIso = _config.DefaultMinSensitivity > 0 ? _config.DefaultMinSensitivity : 160;
        var maxIso = _config.DefaultMaxSensitivity > 0 ? _config.DefaultMaxSensitivity : 12800;
        
        if (minIso >= maxIso)
        {
            _diagnostics.RecordEvent("Camera", $"BuildFallbackIsoArray: Returning single value [{minIso}]");
            return new[] { minIso };
        }

        // Generate common ISO values between min and max
        var commonIsoValues = new[] { 100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800 };
        var isoList = new System.Collections.Generic.List<int>();
        
        foreach (var iso in commonIsoValues)
        {
            if (iso >= minIso && iso <= maxIso)
            {
                isoList.Add(iso);
            }
        }
        
        // Ensure min and max are included
        if (!isoList.Contains(minIso)) isoList.Insert(0, minIso);
        if (!isoList.Contains(maxIso)) isoList.Add(maxIso);
        
        _diagnostics.RecordEvent("Camera", $"BuildFallbackIsoArray: Returning {isoList.Count} values from {isoList[0]} to {isoList[isoList.Count - 1]}");
        return isoList.ToArray();
    }

    [ImportingConstructor]
    public FujiCamera(
        IFujifilmInterop interop,
        ICameraModelCatalog catalog,
        IFujiSettingsProvider settingsProvider,
        IFujifilmDiagnosticsService diagnostics)
    {
        _interop = interop;
        _catalog = catalog;
        _settingsProvider = settingsProvider;
        _diagnostics = diagnostics;
    }

    public bool IsConnected => _session != null && _session.Handle != IntPtr.Zero;
    public CameraConfig? Configuration => _config;
    public IReadOnlyList<int> SupportedIsoValues => _supportedSensitivities;
    public IReadOnlyDictionary<int, double> ShutterCodeToDuration => _shutterCodeToDuration;

    private async Task ExecuteWithRetryAsync(Func<int> sdkCall, string operationName, CancellationToken cancellationToken = default)
    {
        int retryCount = 0;
        const int maxRetries = 5;
        const int delayMs = 500;

        if (_session == null)
        {
            throw new InvalidOperationException("Camera session is not initialized.");
        }

        while (true)
        {
            int result = sdkCall();
            if (result == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"{operationName} succeeded.");
                return;
            }

            var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
            if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_BUSY && retryCount < maxRetries)
            {
                retryCount++;
                _diagnostics.RecordEvent("Camera", $"{operationName} failed with BUSY. Retrying ({retryCount}/{maxRetries}) in {delayMs}ms...");
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new FujifilmSdkException(operationName, result, error.ApiCode, error.ErrorCode);
            }
        }
    }

    public async Task ConnectAsync(FujifilmCameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        await _interop.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (IsConnected)
        {
            _diagnostics.RecordEvent("Camera", "Camera already connected. Disconnecting before reconnecting.");
            await DisconnectAsync().ConfigureAwait(false);
        }

        _session = await _interop.OpenCameraAsync(descriptor.DeviceId, cancellationToken).ConfigureAwait(false);
        _diagnostics.RecordEvent("Camera", $"Opened handle {_session.Handle} for {descriptor.DeviceId}");

        // Give the camera a moment to settle after opening connection
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        // Give the camera a moment to settle after opening connection
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteWithRetryAsync(() => 
                FujifilmSdkWrapper.XSDK_SetPriorityMode(_session.Handle, FujifilmSdkWrapper.XSDK_PRIORITY_PC), 
                nameof(FujifilmSdkWrapper.XSDK_SetPriorityMode), 
                cancellationToken).ConfigureAwait(false);
            _diagnostics.RecordEvent("Camera", "Set Priority Mode to PC (matching ASCOM driver behavior).");
        }
        catch (Exception ex)
        {
             _diagnostics.RecordEvent("Camera", $"Failed to set Priority Mode: {ex.Message}");
             // Proceeding, as sometimes it might already be set or non-fatal
        }

        _config = ResolveConfiguration(descriptor.DisplayName);
        if (_config == null)
        {
            _diagnostics.RecordEvent("Camera", $"No configuration found for camera '{descriptor.DisplayName}'. Using defaults.");
        }

        if (_config != null)
        {
            await ApplyConfigurationAsync(_config, cancellationToken).ConfigureAwait(false);
        }

        // Set Dynamic Range to 100 before querying capabilities (required by SDK)
        // CapSensitivity requires DR to be set first, and supported ISO values depend on DR
        // This matches the ASCOM driver's behavior
        _diagnostics.RecordEvent("Camera", "Setting Dynamic Range to 100 before querying capabilities...");
        try
        {
            // Use numeric value 100 (XSDK_DRANGE_100 = 0x0064 = 100)
            var setDrResult = FujifilmSdkWrapper.XSDK_SetDynamicRange(_session.Handle, 100);
            if (setDrResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", "Dynamic Range set to 100 successfully.");
                // Small delay after setting DR to allow camera to process (matching ASCOM driver)
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                _diagnostics.RecordEvent("Camera", $"Warning: Failed to set Dynamic Range to 100 (result={setDrResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X}). Capability queries may fail.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Warning: Exception setting Dynamic Range: {ex.Message}. Proceeding with capability queries.");
        }

        // Cache capabilities (ISO, shutter speeds) - must be done after DR is set
        CacheCapabilities();
        RefreshBufferCapacity();
        RefreshOperatingState();
        
        // Check and disable Long Exposure Noise Reduction (LENR) if enabled
        // Do this AFTER capabilities are cached, when camera is fully initialized
        // LENR causes the camera to take dark frames after exposures, which delays image availability
        DisableLongExposureNoiseReduction();

        _diagnostics.RecordEvent("Camera", $"Fujifilm camera {descriptor.DisplayName} connected. ISO count={_supportedSensitivities.Count}, shutter codes={_shutterCodeToDuration.Count}");
    }

    private async Task ApplyConfigurationAsync(CameraConfig config, CancellationToken cancellationToken)
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera session not available.");
        }

        _diagnostics.RecordEvent("Camera", $"ApplyConfiguration called for {config.ModelName}");

        // Check if camera is in Manual mode (mode dial must be set to M physically)
        try
        {
            var modeResult = FujifilmSdkWrapper.XSDK_GetMode(_session.Handle, out var currentMode);
            if (modeResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"Current camera mode: {currentMode} (0x{currentMode:X})");
                // Note: Mode codes are model-specific. For GFX100S, Manual is 1 (not 0x1101)
                // The physical mode dial must be set to M for full manual control
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Failed to get current mode: {ex.Message}");
        }
        
        // NOTE: We do not set AE Mode programmatically here.
        // The ASCOM driver does not do this, and it seems to rely on the user's physical camera settings.
        // Explicitly setting AE Mode caused COMBINATION errors in previous attempts.
        _diagnostics.RecordEvent("Camera", "Skipping programmatic AE mode setting (relying on physical camera state).");

        _diagnostics.RecordEvent("Camera", "Setting Media Record Mode to OFF (0) to prevent SD card conflicts...");
        try
        {
            await ExecuteWithRetryAsync(() => 
                FujifilmSdkWrapper.XSDK_SetMediaRecord(_session.Handle, FujifilmSdkWrapper.XSDK_MEDIAREC_OFF),
                nameof(FujifilmSdkWrapper.XSDK_SetMediaRecord),
                cancellationToken).ConfigureAwait(false);
            _diagnostics.RecordEvent("Camera", "Media Record Mode set to OFF.");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Failed to set Media Record Mode: {ex.Message}. Exposure might fail if SD card is full or slow.");
        }
    }

    private void CacheCapabilities()
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera session not available.");
        }

        _supportedSensitivities = QuerySensitivityValues();
        var shutterCodes = QueryShutterCodes();
        _supportedShutterCodes = shutterCodes; // Store for validation
        _shutterCodeToDuration = BuildShutterSpeedDictionary(shutterCodes);
    }

    private void RefreshBufferCapacity()
    {
        if (_session == null)
        {
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
            return;
        }

        var result = FujifilmSdkWrapper.XSDK_GetBufferCapacity(_session.Handle, out var shootFrames, out var totalFrames);
        if (result != FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _diagnostics.RecordEvent("Camera", $"XSDK_GetBufferCapacity failed with code {result}");
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
            return;
        }

        _bufferShootCapacity = shootFrames;
        _bufferTotalCapacity = totalFrames;
    }

    private void RefreshOperatingState()
    {
        if (_session == null)
        {
            _lastModeCode = 0;
            _lastAEModeCode = 0;
            _lastDynamicRangeCode = 0;
            _lastApiErrorCode = 0;
            _lastSdkErrorCode = 0;
            return;
        }

        if (FujifilmSdkWrapper.XSDK_GetMode(_session.Handle, out var mode) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastModeCode = mode;
        }

        if (FujifilmSdkWrapper.XSDK_GetAEMode(_session.Handle, out var aeMode) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastAEModeCode = aeMode;
        }

        if (FujifilmSdkWrapper.XSDK_GetDynamicRange(_session.Handle, out var dRange) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastDynamicRangeCode = dRange;
        }

        if (FujifilmSdkWrapper.XSDK_GetErrorNumber(_session.Handle, out var apiCode, out var errCode) == FujifilmSdkWrapper.XSDK_COMPLETE)
        {
            _lastApiErrorCode = apiCode;
            _lastSdkErrorCode = errCode;
        }
    }

    private IReadOnlyList<int> QuerySensitivityValues()
    {
        if (_session == null)
        {
            return Array.Empty<int>();
        }

        // Use DR=100 explicitly (should have been set during connection, but use explicit value for safety)
        // CapSensitivity requires DR to be set, and we set it to 100 during ConnectAsync
        int drToQuery = 100; // XSDK_DRANGE_100
        
        int count = 0;
        try
        {
            // Step 1: Get count
            var countResult = FujifilmSdkWrapper.XSDK_CapSensitivity(_session.Handle, ref drToQuery, ref count, IntPtr.Zero);
            
            if (countResult != FujifilmSdkWrapper.XSDK_COMPLETE || count <= 0)
            {
                _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: CapSensitivity query failed or returned 0 values (Result={countResult}, Count={count}). Using fallback ISO values.");
                return BuildFallbackIsoArray();
            }

            // Step 2: Get data
            var bufferSize = count * sizeof(int);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var dataResult = FujifilmSdkWrapper.XSDK_CapSensitivity(_session.Handle, ref drToQuery, ref count, buffer);
                
                if (dataResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Failed to get sensitivity data (Result={dataResult}). Using fallback ISO values.");
                    return BuildFallbackIsoArray();
                }

                var sensitivities = new List<int>(count);
                for (int i = 0; i < count; i++)
                {
                    var val = Marshal.ReadInt32(buffer, i * sizeof(int));
                    sensitivities.Add(val);
                }
                
                _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Successfully queried {sensitivities.Count} ISO values from camera.");
                return sensitivities;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"QuerySensitivityValues: Exception during ISO query: {ex.Message}. Using fallback ISO values.");
            return BuildFallbackIsoArray();
        }
    }

    private IReadOnlyList<int> QueryShutterCodes()
    {
        if (_session == null)
        {
            return Array.Empty<int>();
        }

        int count = 0;
        int bulbCapable;
        // Step 1: Get count
        var countResult = FujifilmSdkWrapper.XSDK_CapShutterSpeed(_session.Handle, ref count, IntPtr.Zero, out bulbCapable);
        FujifilmSdkWrapper.CheckResult(_session.Handle, countResult, nameof(FujifilmSdkWrapper.XSDK_CapShutterSpeed));
        bool sdkBulbCapable = bulbCapable != 0; // Store SDK result temporarily

        // Apply fallback logic: if SDK says no but config says yes, use config (same as ASCOM driver)
        if (!sdkBulbCapable && _config?.DefaultBulbCapable == true)
        {
            _diagnostics.RecordEvent("Camera", $"WARNING: SDK reported Bulb NOT capable, but config default is TRUE. Using config default.");
            _bulbCapable = true; // Override with config default
        }
        else
        {
            _bulbCapable = sdkBulbCapable; // Use SDK value
        }
        _diagnostics.RecordEvent("Camera", $"Bulb capability: SDK={sdkBulbCapable}, Config={_config?.DefaultBulbCapable}, Final={_bulbCapable}");

        if (count == 0)
        {
            // If SDK query fails or returns 0 codes, fall back to config default for bulb capability
            if (_config?.DefaultBulbCapable == true)
            {
                _bulbCapable = true;
                _diagnostics.RecordEvent("Camera", "SDK returned 0 shutter codes, using config DefaultBulbCapable=true");
            }
            return Array.Empty<int>();
        }

        // Step 2: Get data
        var bufferSize = count * sizeof(int);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // Note: bulbCapable is an output, but for the second call we just pass a dummy variable or the same one.
            var dataResult = FujifilmSdkWrapper.XSDK_CapShutterSpeed(_session.Handle, ref count, buffer, out bulbCapable);
            FujifilmSdkWrapper.CheckResult(_session.Handle, dataResult, nameof(FujifilmSdkWrapper.XSDK_CapShutterSpeed));

            var shutterCodes = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                var val = Marshal.ReadInt32(buffer, i * sizeof(int));
                shutterCodes.Add(val);
            }
            return shutterCodes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IReadOnlyDictionary<int, double> BuildShutterSpeedDictionary(IReadOnlyList<int> shutterCodes)
    {
        var map = new Dictionary<int, double>();
        
        // First, populate the universal hardcoded map from SDK PDF (same as ASCOM driver)
        // This map is consistent across all Fujifilm cameras and is based on SDK documentation
        PopulateUniversalShutterSpeedMap(map);
        
        // Then, add entries from config map if available (config takes precedence for any overrides)
        // Note: Config maps are typically minimal and may only have a few entries
        if (_config?.ShutterSpeedMap != null && _config.ShutterSpeedMap.Count > 0)
        {
            foreach (var mapping in _config.ShutterSpeedMap)
            {
                map[mapping.SdkCode] = mapping.Duration;
            }
        }

        // Finally, ensure all queried codes have mappings (should already be in universal map)
        // Only calculate as fallback for codes not in universal or config maps
        foreach (var code in shutterCodes)
        {
            if (code <= 0)
            {
                continue;
            }

            // Only calculate if not already in map (from universal or config)
            if (!map.ContainsKey(code))
            {
                // Fallback: calculate as 1.0/code for codes not in any map
                // This should rarely happen if universal map is complete
                map[code] = 1.0 / code;
            }
        }

        if (!map.ContainsKey(FujifilmSdkWrapper.XSDK_SHUTTER_BULB))
        {
            map[FujifilmSdkWrapper.XSDK_SHUTTER_BULB] = _config?.DefaultMaxExposure ?? 3600.0;
        }

        return map;
    }

    private static void PopulateUniversalShutterSpeedMap(Dictionary<int, double> map)
    {
        // Universal shutter speed mappings based on SDK PDF pp. 91-95
        // These mappings are consistent across all Fujifilm cameras
        // Same as ASCOM driver's PopulateShutterSpeedMaps()
        map[5] = 1.0 / 180000.0; map[6] = 1.0 / 160000.0; map[7] = 1.0 / 128000.0;
        map[9] = 1.0 / 102400.0; map[12] = 1.0 / 80000.0; map[15] = 1.0 / 64000.0;
        map[19] = 1.0 / 51200.0; map[24] = 1.0 / 40000.0; map[30] = 1.0 / 32000.0;
        map[38] = 1.0 / 25600.0; map[43] = 1.0 / 24000.0; map[48] = 1.0 / 20000.0;
        map[61] = 1.0 / 16000.0; map[76] = 1.0 / 12800.0; map[86] = 1.0 / 12000.0;
        map[96] = 1.0 / 10000.0; map[122] = 1.0 / 8000.0; map[153] = 1.0 / 6400.0;
        map[172] = 1.0 / 6000.0; map[193] = 1.0 / 5000.0; map[244] = 1.0 / 4000.0;
        map[307] = 1.0 / 3200.0; map[345] = 1.0 / 3000.0; map[387] = 1.0 / 2500.0;
        map[488] = 1.0 / 2000.0; map[615] = 1.0 / 1600.0; map[690] = 1.0 / 1500.0;
        map[775] = 1.0 / 1250.0; map[976] = 1.0 / 1000.0; map[1230] = 1.0 / 800.0;
        map[1381] = 1.0 / 750.0; map[1550] = 1.0 / 640.0; map[1953] = 1.0 / 500.0;
        map[2460] = 1.0 / 400.0; map[2762] = 1.0 / 350.0; map[3100] = 1.0 / 320.0;
        map[3906] = 1.0 / 250.0; map[4921] = 1.0 / 200.0; map[5524] = 1.0 / 180.0;
        map[6200] = 1.0 / 160.0; map[7812] = 1.0 / 125.0; map[9843] = 1.0 / 100.0;
        map[11048] = 1.0 / 90.0; map[12401] = 1.0 / 80.0; map[15625] = 1.0 / 60.0;
        map[19686] = 1.0 / 50.0; map[22097] = 1.0 / 45.0; map[24803] = 1.0 / 40.0;
        map[31250] = 1.0 / 30.0; map[39372] = 1.0 / 25.0; map[49606] = 1.0 / 20.0;
        map[62500] = 1.0 / 15.0; map[78745] = 1.0 / 13.0; map[99212] = 1.0 / 10.0;
        map[125000] = 1.0 / 8.0; map[157490] = 1.0 / 6.0; map[198425] = 1.0 / 5.0;
        map[250000] = 1.0 / 4.0; map[314980] = 1.0 / 3.0; map[396850] = 1.0 / 2.5;
        map[500000] = 1.0 / 2.0; map[629960] = 1.0 / 1.6; map[707106] = 1.0 / 1.5;
        map[793700] = 1.0 / 1.3; map[1000000] = 1.0; map[1259921] = 1.3;
        map[1414213] = 1.5; map[1587401] = 1.6; map[2000000] = 2.0;
        map[2519842] = 2.5; map[3174802] = 3.0; map[4000000] = 4.0;
        map[5039684] = 5.0; map[6349604] = 6.0; map[8000000] = 8.0;
        map[10079368] = 10.0; map[12699208] = 13.0; map[16000000] = 15.0;
        map[20158736] = 20.0; map[25398416] = 25.0; map[32000000] = 30.0;
        map[64000000] = 60.0;
    }

    public async Task<RawCaptureResult> CaptureRawAsync(double exposureSeconds, int iso, CancellationToken cancellationToken)
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Camera is not connected.");
        }

        if (exposureSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds), "Exposure must be positive.");
        }

        if (_supportedSensitivities.Count > 0 && !_supportedSensitivities.Contains(iso))
        {
            _diagnostics.RecordEvent("Camera", $"Requested ISO {iso} not in supported list; using closest.");
            iso = _supportedSensitivities.OrderBy(value => Math.Abs(value - iso)).First();
        }

        var shutterCode = ResolveShutterCode(exposureSeconds);
        if (shutterCode == FujifilmSdkWrapper.XSDK_SHUTTER_BULB && !_bulbCapable)
        {
            throw new InvalidOperationException("Camera does not report bulb capability; exposure exceeds timed range.");
        }

        _diagnostics.RecordEvent("Camera", $"Starting exposure. Duration={exposureSeconds}s ISO={iso} ShutterCode={shutterCode}");

        // Set Sensitivity (ISO)
        _diagnostics.RecordEvent("Camera", $"Setting ISO to {iso}...");
        var setIsoResult = FujifilmSdkWrapper.XSDK_SetSensitivity(_session.Handle, iso);
        FujifilmSdkWrapper.CheckResult(_session.Handle, setIsoResult, nameof(FujifilmSdkWrapper.XSDK_SetSensitivity));
        _diagnostics.RecordEvent("Camera", $"ISO set successfully to {iso}");

        // Add delay to allow camera to process ISO change and update internal state
        // This is critical as shutter speed support may vary with ISO/Dynamic Range combination
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        // Re-query shutter codes after ISO change to ensure we have valid codes for current state
        var currentShutterCodes = QueryShutterCodes();
        if (currentShutterCodes.Count > 0)
        {
            _diagnostics.RecordEvent("Camera", $"Re-queried shutter codes after ISO change: {currentShutterCodes.Count} codes available");
            _supportedShutterCodes = currentShutterCodes;
            // Rebuild duration map with current codes, but preserve config mappings
            // This ensures config entries (like code 1 = 1.0s) are available even if not in queried codes
            var newDurationMap = new Dictionary<int, double>(BuildShutterSpeedDictionary(currentShutterCodes));
            if (newDurationMap.Count > 0)
            {
                // Merge with existing map to preserve config entries that might not be in newly queried codes
                // This is important because config has correct mappings (e.g., code 1 = 1.0s)
                foreach (var existingEntry in _shutterCodeToDuration)
                {
                    // Preserve config entries (from original map) even if not in newly queried codes
                    // This allows us to find alternatives using config mappings
                    if (!newDurationMap.ContainsKey(existingEntry.Key))
                    {
                        // Only preserve if it's likely a config entry (has a reasonable duration mapping)
                        // Config entries typically have durations that don't match simple 1.0/code calculation
                        var calculatedDuration = existingEntry.Key > 0 ? 1.0 / existingEntry.Key : 0;
                        var diff = Math.Abs(existingEntry.Value - calculatedDuration);
                        // If the duration differs significantly from calculated, it's likely a config entry
                        if (diff > 0.001 || existingEntry.Key <= 0)
                        {
                            newDurationMap[existingEntry.Key] = existingEntry.Value;
                        }
                    }
                }
                _shutterCodeToDuration = newDurationMap;
                _diagnostics.RecordEvent("Camera", $"Rebuilt duration map with {newDurationMap.Count} entries (preserved config mappings)");
            }
        }

        // Validate shutter code against currently supported codes
        if (shutterCode != FujifilmSdkWrapper.XSDK_SHUTTER_BULB && _supportedShutterCodes.Count > 0)
        {
            if (!_supportedShutterCodes.Contains(shutterCode))
            {
                _diagnostics.RecordEvent("Camera", $"WARNING: Shutter code {shutterCode} not in supported list. Attempting to find closest valid code...");
                _diagnostics.RecordEvent("Camera", $"Requested duration: {exposureSeconds}s, Available codes in duration map: {_shutterCodeToDuration.Count}, Supported codes: {_supportedShutterCodes.Count}");
                
                // Log some sample codes and durations for debugging
                var sampleCodes = _shutterCodeToDuration
                    .Where(pair => _supportedShutterCodes.Contains(pair.Key) && pair.Key > 0)
                    .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                    .Take(5)
                    .ToList();
                if (sampleCodes.Count > 0)
                {
                    var sampleStr = string.Join(", ", sampleCodes.Select(c => $"code {c.Key}={c.Value:F6}s"));
                    _diagnostics.RecordEvent("Camera", $"Sample supported codes near {exposureSeconds}s: {sampleStr}");
                }
                
                // First, try to find the closest code to the requested duration (prefer codes close to requested)
                // Use a tolerance of 20% to prefer reasonably close matches
                var tolerance = exposureSeconds * 0.2;
                var closeCodes = _shutterCodeToDuration
                    .Where(pair => _supportedShutterCodes.Contains(pair.Key) && pair.Key > 0 && Math.Abs(pair.Value - exposureSeconds) <= tolerance)
                    .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                    .FirstOrDefault();
                
                if (closeCodes.Key != 0)
                {
                    _diagnostics.RecordEvent("Camera", $"Using close match shutter code {closeCodes.Key} (duration={closeCodes.Value}s, diff={Math.Abs(closeCodes.Value - exposureSeconds):F4}s) instead of {shutterCode}");
                    shutterCode = closeCodes.Key;
                }
                else
                {
                    // If no close match, try to find codes <= requested (prefer not to over-expose)
                    var validCodes = _shutterCodeToDuration
                        .Where(pair => _supportedShutterCodes.Contains(pair.Key) && pair.Value <= exposureSeconds + 1e-6 && pair.Key > 0)
                        .OrderByDescending(pair => pair.Value)
                        .FirstOrDefault();
                    
                    if (validCodes.Key != 0)
                    {
                        _diagnostics.RecordEvent("Camera", $"Using alternative shutter code {validCodes.Key} (duration={validCodes.Value}s) <= requested {exposureSeconds}s instead of {shutterCode}");
                        shutterCode = validCodes.Key;
                    }
                    else
                    {
                        // Last resort: find closest code overall (may over-expose)
                        _diagnostics.RecordEvent("Camera", $"No code found <= {exposureSeconds}s. Searching for closest code overall...");
                        var closestCode = _shutterCodeToDuration
                            .Where(pair => _supportedShutterCodes.Contains(pair.Key) && pair.Key > 0)
                            .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                            .FirstOrDefault();
                        
                        if (closestCode.Key != 0)
                        {
                            _diagnostics.RecordEvent("Camera", $"Using closest available code {closestCode.Key} (duration={closestCode.Value}s, diff={Math.Abs(closestCode.Value - exposureSeconds):F4}s) instead of {shutterCode}");
                            shutterCode = closestCode.Key;
                        }
                        else
                        {
                            _diagnostics.RecordEvent("Camera", $"No valid alternative found. Proceeding with code {shutterCode} (may fail if invalid for current state)");
                        }
                    }
                }
            }
        }

        // Set Shutter Speed
        var bulbVal = (shutterCode == FujifilmSdkWrapper.XSDK_SHUTTER_BULB) ? 1 : 0;
        _diagnostics.RecordEvent("Camera", $"Setting Shutter Speed to {shutterCode} (Bulb={bulbVal})...");
        
        int retryCount = 0;
        const int maxRetries = 3;
        bool shutterSet = false;

        while (!shutterSet && retryCount <= maxRetries)
        {
            var setSpeedResult = FujifilmSdkWrapper.XSDK_SetShutterSpeed(_session.Handle, shutterCode, bulbVal);
            
            if (setSpeedResult == FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                shutterSet = true;
                _diagnostics.RecordEvent("Camera", $"Shutter Speed set successfully to {shutterCode}");
            }
            else
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                
                // Handle BUSY state
                if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_BUSY && retryCount < maxRetries)
                {
                    retryCount++;
                    _diagnostics.RecordEvent("Camera", $"SetShutterSpeed failed with BUSY. Retrying ({retryCount}/{maxRetries}) in 250ms...");
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                // Handle COMBINATION error (function call combination error - invalid code for current state)
                else if (error.ErrorCode == FujifilmSdkWrapper.XSDK_ERRCODE_COMBINATION)
                {
                    // Log current camera state for diagnostics
                    RefreshOperatingState();
                    var currentMode = _lastModeCode;
                    var currentAEMode = _lastAEModeCode;
                    var currentDR = _lastDynamicRangeCode;
                    var currentIso = -1;
                    if (FujifilmSdkWrapper.XSDK_GetSensitivity(_session.Handle, out var currentIsoValue) == FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        currentIso = currentIsoValue;
                    }
                    
                    _diagnostics.RecordEvent("Camera", "ERROR: Failed to set shutter speed due to COMBINATION error (0x2003).");
                    _diagnostics.RecordEvent("Camera", $"Current camera state: Mode={currentMode}, AE={currentAEMode}, DR={currentDR}, ISO={currentIso}");
                    _diagnostics.RecordEvent("Camera", $"Attempted shutter code: {shutterCode} (Bulb={bulbVal})");
                    
                    // Re-query supported shutter codes to find a valid alternative
                    _diagnostics.RecordEvent("Camera", "Re-querying supported shutter codes to find valid alternative...");
                    var validCodes = QueryShutterCodes();
                    if (validCodes.Count > 0)
                    {
                        _supportedShutterCodes = validCodes;
                        _diagnostics.RecordEvent("Camera", $"Found {validCodes.Count} supported shutter codes for current state");
                        
                        // Rebuild duration map with newly queried codes
                        var newDurationMap = BuildShutterSpeedDictionary(validCodes);
                        _shutterCodeToDuration = newDurationMap;
                        _diagnostics.RecordEvent("Camera", $"Rebuilt duration map with {newDurationMap.Count} entries");
                        
                        // First, try to find the closest code to the requested duration (prefer codes close to requested)
                        // Use a tolerance of 20% to prefer reasonably close matches
                        var tolerance = exposureSeconds * 0.2;
                        var alternativeCode = newDurationMap
                            .Where(pair => pair.Key > 0 && Math.Abs(pair.Value - exposureSeconds) <= tolerance)
                            .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                            .FirstOrDefault();
                        
                        // If no close match, try codes <= requested (prefer not to over-expose)
                        if (alternativeCode.Key == 0)
                        {
                            alternativeCode = newDurationMap
                                .Where(pair => pair.Value <= exposureSeconds + 1e-6 && pair.Key > 0)
                                .OrderByDescending(pair => pair.Value)
                                .FirstOrDefault();
                        }
                        
                        // If still no match, find closest overall (may over-expose)
                        if (alternativeCode.Key == 0)
                        {
                            _diagnostics.RecordEvent("Camera", $"No code found <= {exposureSeconds}s. Searching for closest code overall...");
                            alternativeCode = newDurationMap
                                .Where(pair => pair.Key > 0)
                                .OrderBy(pair => Math.Abs(pair.Value - exposureSeconds))
                                .FirstOrDefault();
                        }
                        
                        if (alternativeCode.Key != 0 && alternativeCode.Key != shutterCode)
                        {
                            _diagnostics.RecordEvent("Camera", $"Attempting alternative shutter code {alternativeCode.Key} (duration={alternativeCode.Value}s)");
                            shutterCode = alternativeCode.Key;
                            bulbVal = (shutterCode == FujifilmSdkWrapper.XSDK_SHUTTER_BULB) ? 1 : 0;
                            
                            // Retry with alternative code
                            var retryResult = FujifilmSdkWrapper.XSDK_SetShutterSpeed(_session.Handle, shutterCode, bulbVal);
                            if (retryResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                            {
                                _diagnostics.RecordEvent("Camera", $"Successfully set alternative shutter code {shutterCode}");
                                shutterSet = true;
                                break;
                            }
                            else
                            {
                                var retryError = FujifilmSdkWrapper.GetLastError(_session.Handle);
                                _diagnostics.RecordEvent("Camera", $"Alternative code {shutterCode} also failed (result={retryResult}, errCode=0x{retryError.ErrorCode:X})");
                            }
                        }
                        else if (alternativeCode.Key == 0)
                        {
                            _diagnostics.RecordEvent("Camera", "No alternative code could be found in the queried codes");
                        }
                    }
                    
                    _diagnostics.RecordEvent("Camera", "CRITICAL TIP: Ensure the physical Shutter Speed dial is set to 'T' (Time) or 'A' (Auto) to allow software control.");
                    _diagnostics.RecordEvent("Camera", "Also ensure camera is in Manual (M) mode and that the requested shutter speed is valid for current ISO/Dynamic Range combination.");
                    _diagnostics.RecordEvent("Camera", "Falling back to current physical dial setting...");
                    
                    // We can't set it, so we break and hope the user has set it manually or will see the error
                    // We don't throw here to allow the exposure to proceed with whatever is on the dial, 
                    // but we logged the critical warning.
                    break; 
                }
                else
                {
                    // Throw for other errors
                    throw new FujifilmSdkException(nameof(FujifilmSdkWrapper.XSDK_SetShutterSpeed), setSpeedResult, error.ApiCode, error.ErrorCode);
                }
            }
        }


        if (shutterCode != FujifilmSdkWrapper.XSDK_SHUTTER_BULB)
        {
            await ExecuteTimedExposureAsync(exposureSeconds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExecuteBulbExposureAsync(exposureSeconds, cancellationToken).ConfigureAwait(false);
        }

        var raw = await DownloadImageAsync(cancellationToken).ConfigureAwait(false);
        var finalized = raw with { ExposureSeconds = exposureSeconds, Iso = iso, ShutterCode = shutterCode, TimestampTicks = DateTime.UtcNow.Ticks };
        RefreshBufferCapacity();
        RefreshOperatingState();
        return finalized;
    }

    private int ResolveShutterCode(double exposureSeconds)
    {
        _diagnostics.RecordEvent("Camera", $"ResolveShutterCode: Requested duration={exposureSeconds}s, ShutterCodeToDuration has {_shutterCodeToDuration.Count} entries");
        
        // Maximum programmable exposure is 60 seconds (code 64000000)
        // Exposures longer than 60s require BULB mode
        const double maxProgrammableExposure = 60.0;
        
        if (exposureSeconds > maxProgrammableExposure)
        {
            if (!_bulbCapable)
            {
                _diagnostics.RecordEvent("Camera", $"ResolveShutterCode: Requested {exposureSeconds}s > {maxProgrammableExposure}s, but camera does not support BULB");
                throw new InvalidOperationException($"Exposure duration {exposureSeconds}s exceeds maximum programmable exposure ({maxProgrammableExposure}s) and camera does not support BULB mode.");
            }
            _diagnostics.RecordEvent("Camera", $"ResolveShutterCode: Requested {exposureSeconds}s > {maxProgrammableExposure}s, using BULB mode");
            return FujifilmSdkWrapper.XSDK_SHUTTER_BULB;
        }
        
        if (_shutterCodeToDuration.Count == 0)
        {
            _diagnostics.RecordEvent("Camera", "ResolveShutterCode: No shutter speed map available, defaulting to BULB");
            return FujifilmSdkWrapper.XSDK_SHUTTER_BULB;
        }

        var closest = _shutterCodeToDuration
            .Where(pair => pair.Key != FujifilmSdkWrapper.XSDK_SHUTTER_BULB && pair.Value <= exposureSeconds + 1e-6)
            .OrderByDescending(pair => pair.Value)
            .FirstOrDefault();

        if (closest.Key == 0)
        {
            _diagnostics.RecordEvent("Camera", "ResolveShutterCode: No suitable code found, using BULB");
            return FujifilmSdkWrapper.XSDK_SHUTTER_BULB;
        }

        _diagnostics.RecordEvent("Camera", $"ResolveShutterCode: Selected code={closest.Key} for duration={closest.Value}s (requested {exposureSeconds}s)");
        return closest.Key;
    }

    public async Task StopExposureAsync()
    {
        if (_session == null)
        {
            return;
        }

        // Only send stop commands if we're actually in an exposure
        // Note: For bulb exposures, the stop command is already sent by ExecuteBulbExposureAsync
        // For timed exposures, we can't actually stop them mid-exposure (camera limitation)
        // So we only send the bulb stop command as a safety measure
        await Task.Run(() =>
        {
            // Only send bulb stop command - sending SHOOT_S1OFF would trigger a new exposure!
            // The ASCOM driver doesn't support StopExposure for this reason
            IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_N_BULBS1OFF, "Stop exposure (bulb safety)");
            // DO NOT send XSDK_RELEASE_SHOOT_S1OFF here - it would trigger a new timed exposure!
        }).ConfigureAwait(false);

        RefreshBufferCapacity();
        RefreshOperatingState();
    }

    private async Task ExecuteTimedExposureAsync(double exposureSeconds, CancellationToken cancellationToken)
    {
        if (_session == null)
        {
            return;
        }

        IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_SHOOT_S1OFF, "Timed exposure trigger");
        var extra = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(5.0, exposureSeconds * 0.2)));
        await Task.Delay(TimeSpan.FromSeconds(exposureSeconds) + extra, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteBulbExposureAsync(double exposureSeconds, CancellationToken cancellationToken)
    {
        if (_session == null)
        {
            return;
        }

        // Bulb sequence: S1ON -> Delay -> BULBS2_ON (same as ASCOM driver)
        // 1. Press Halfway (S1ON)
        _diagnostics.RecordEvent("Camera", "Starting bulb exposure sequence: S1ON");
        IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_S1ON, "Bulb S1ON");
        
        // Delay between S1ON and BULBS2_ON (500ms as per ASCOM driver)
        // User setting delay is additional if specified
        const int baseDelayMs = 500;
        var userDelay = Math.Max(0, _settingsProvider.Settings.BulbReleaseDelayMs);
        var totalDelay = baseDelayMs + userDelay;
        _diagnostics.RecordEvent("Camera", $"Delay between S1ON and BULBS2_ON: {totalDelay}ms (base={baseDelayMs}ms, user={userDelay}ms)");
        await Task.Delay(TimeSpan.FromMilliseconds(totalDelay), cancellationToken).ConfigureAwait(false);
        
        // 2. Start Bulb (BULBS2_ON)
        _diagnostics.RecordEvent("Camera", "Starting bulb exposure: BULBS2_ON");
        IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_BULBS2_ON, "Bulb start");

        // Wait for the requested exposure duration
        _diagnostics.RecordEvent("Camera", $"Waiting for bulb exposure duration: {exposureSeconds}s");
        await Task.Delay(TimeSpan.FromSeconds(exposureSeconds), cancellationToken).ConfigureAwait(false);
        
        // Add delay before sending stop command (same as ASCOM driver)
        // This gives the camera time to process the exposure before stopping
        _diagnostics.RecordEvent("Camera", "Adding delay before sending bulb stop command...");
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        
        // Stop bulb exposure
        _diagnostics.RecordEvent("Camera", "Stopping bulb exposure: BULBS1OFF");
        IssueReleaseCommand(FujifilmSdkWrapper.XSDK_RELEASE_N_BULBS1OFF, "Bulb stop");
        
        // Add delay after stop command to allow camera to process
        // The camera needs time to finalize the exposure and prepare image data
        _diagnostics.RecordEvent("Camera", "Adding delay after bulb stop to allow camera processing...");
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RawCaptureResult> DownloadImageAsync(CancellationToken cancellationToken)
    {
        if (_session == null)
        {
            throw new InvalidOperationException("Camera session not available.");
        }

        // For bulb exposures, the camera needs more time to process after stopping
        // Increase timeout and polling interval for bulb exposures
        const int maxAttempts = 30; // Increased from 10 to allow more time for bulb processing
        const int pollIntervalMs = 500; // Increased from 200ms to match ASCOM driver polling
        
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var infoResult = FujifilmSdkWrapper.XSDK_ReadImageInfo(_session.Handle, out var info);
            
            // Don't throw on error, just log and continue polling
            if (infoResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"ReadImageInfo failed (attempt {attempt + 1}): result={infoResult}. Continuing to poll...");
                await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (info.lDataSize > 0)
            {
                var buffer = new byte[info.lDataSize];
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var readResult = FujifilmSdkWrapper.XSDK_ReadImage(_session.Handle, handle.AddrOfPinnedObject(), (uint)buffer.Length);
                    FujifilmSdkWrapper.CheckResult(_session.Handle, readResult, nameof(FujifilmSdkWrapper.XSDK_ReadImage));

                    _diagnostics.RecordEvent("Camera", $"Downloaded RAW frame {info.lImagePixWidth}x{info.lImagePixHeight} bytes={buffer.Length}");
                    return new RawCaptureResult(buffer, info.lImagePixWidth, info.lImagePixHeight, info.lFormat, info.lImageBitDepth, 0, 0, 0.0, 0);
                }
                finally
                {
                    handle.Free();
                    var deleteResult = FujifilmSdkWrapper.XSDK_DeleteImage(_session.Handle);
                    if (deleteResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        _diagnostics.RecordEvent("Camera", $"XSDK_DeleteImage returned {deleteResult}");
                    }
                }
            }

            _diagnostics.RecordEvent("Camera", $"Image not ready yet (attempt {attempt + 1}/{maxAttempts}). Waiting {pollIntervalMs}ms...");
            await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), cancellationToken).ConfigureAwait(false);
            RefreshBufferCapacity();
            RefreshOperatingState();
        }

        throw new TimeoutException($"Timed out waiting for Fujifilm image data after exposure. Polled for {maxAttempts * pollIntervalMs / 1000.0}s.");
    }

    private void IssueReleaseCommand(int releaseMode, string context)
    {
        if (_session == null)
        {
            return;
        }

        IntPtr shotOptPtr = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(shotOptPtr, 0L);
            var releaseResult = FujifilmSdkWrapper.XSDK_Release(_session.Handle, releaseMode, shotOptPtr, out var status);
            if (releaseResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                _diagnostics.RecordEvent("Camera", $"{context} failed (result={releaseResult}, status={status})");
            }
            else
            {
                _diagnostics.RecordEvent("Camera", $"{context} succeeded (status={status})");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"{context} exception: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(shotOptPtr);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_session != null && _session.Handle != IntPtr.Zero)
        {
            _diagnostics.RecordEvent("Camera", $"Closing camera session {_session.Handle}");
            await _interop.CloseCameraAsync(_session).ConfigureAwait(false);
            _session = null;
            _config = null;
            _supportedSensitivities = Array.Empty<int>();
            _shutterCodeToDuration = new Dictionary<int, double>();
            _supportedShutterCodes = Array.Empty<int>();
            _bufferShootCapacity = 0;
            _bufferTotalCapacity = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private void DisableLongExposureNoiseReduction()
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // Get API codes from device info or use lookup
            // The API codes are model-specific: <model>_API_CODE_GetLongExposureNR, etc.
            // For now, we'll try to get them from GetDeviceInfoEx or use a lookup approach
            
            // Try to get current LENR setting
            // Note: API codes need to be obtained from XSDK_GetDeviceInfoEx or from model-specific headers
            // For GFX100S and other models, we need the actual API codes
            
            // Since we don't have direct access to the header files, we'll need to:
            // 1. Get API codes from XSDK_GetDeviceInfoEx (which lists all supported APIs)
            // 2. Or use a lookup table based on model name
            // 3. Or add them to config JSON files
            
            // For now, let's try a lookup approach for known models
            var modelName = _config?.ModelName ?? string.Empty;
            if (string.IsNullOrEmpty(modelName))
            {
                _diagnostics.RecordEvent("Camera", "Cannot check LENR: Model name not available");
                return;
            }

            // Try dynamic approach first: get API codes from GetDeviceInfoEx
            var apiCodes = GetLongExposureNrApiCodesFromDeviceInfo(modelName);
            
            // Fallback to lookup table if dynamic approach failed
            if (apiCodes == null)
            {
                _diagnostics.RecordEvent("Camera", $"Dynamic API code lookup failed for '{modelName}'. Trying lookup table...");
                apiCodes = GetLongExposureNrApiCodesFromLookup(modelName);
            }

            if (apiCodes == null)
            {
                _diagnostics.RecordEvent("Camera", $"LENR API codes not available for model '{modelName}'. Skipping LENR check. User should manually disable LENR in camera settings.");
                return;
            }

            // Check current LENR setting
            var getResult = FujifilmSdkWrapper.XSDK_GetProp(_session.Handle, apiCodes.Value.GetApiCode, apiCodes.Value.GetApiParam, out long currentValue);
            if (getResult != FujifilmSdkWrapper.XSDK_COMPLETE)
            {
                var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                _diagnostics.RecordEvent("Camera", $"Could not get LENR setting (result={getResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X}). LENR may not be supported or API codes incorrect.");
                return;
            }

            // Check if LENR is ON (typically 1) or OFF (typically 0)
            // According to SDK: <model>_ON = ON, <model>_OFF = OFF
            // We need to determine which value means ON/OFF - typically 1 = ON, 0 = OFF
            const long LENR_OFF = 0; // OFF value
            const long LENR_ON = 1;   // ON value

            if (currentValue == LENR_ON)
            {
                _diagnostics.RecordEvent("Camera", $"Long Exposure Noise Reduction is ON. Disabling it...");
                var setResult = FujifilmSdkWrapper.XSDK_SetProp(_session.Handle, apiCodes.Value.SetApiCode, apiCodes.Value.SetApiParam, LENR_OFF);
                if (setResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    _diagnostics.RecordEvent("Camera", "Long Exposure Noise Reduction disabled successfully.");
                }
                else
                {
                    var error = FujifilmSdkWrapper.GetLastError(_session.Handle);
                    _diagnostics.RecordEvent("Camera", $"Failed to disable LENR (result={setResult}, ApiCode=0x{error.ApiCode:X}, ErrCode=0x{error.ErrorCode:X})");
                }
            }
            else
            {
                _diagnostics.RecordEvent("Camera", $"Long Exposure Noise Reduction is already OFF (value={currentValue}).");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error checking/disabling LENR: {ex.Message}");
            // Don't throw - LENR control is optional
        }
    }

    private (int GetApiCode, int GetApiParam, int SetApiCode, int SetApiParam)? GetLongExposureNrApiCodesFromDeviceInfo(string modelName)
    {
        if (_session == null || _session.Handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // Step 1: Get count of API codes
            var countResult = FujifilmSdkWrapper.XSDK_GetDeviceInfoEx(_session.Handle, out var deviceInfo, out int apiCount, IntPtr.Zero);
            if (countResult != FujifilmSdkWrapper.XSDK_COMPLETE || apiCount <= 0)
            {
                _diagnostics.RecordEvent("Camera", $"GetDeviceInfoEx returned no API codes (result={countResult}, count={apiCount})");
                return null;
            }

            // Step 2: Allocate buffer and get API codes
            var bufferSize = apiCount * sizeof(int);
            var apiCodeBuffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var dataResult = FujifilmSdkWrapper.XSDK_GetDeviceInfoEx(_session.Handle, out deviceInfo, out apiCount, apiCodeBuffer);
                if (dataResult != FujifilmSdkWrapper.XSDK_COMPLETE)
                {
                    _diagnostics.RecordEvent("Camera", $"GetDeviceInfoEx (get data) failed (result={dataResult})");
                    return null;
                }

                // Read API codes from buffer
                var apiCodes = new int[apiCount];
                Marshal.Copy(apiCodeBuffer, apiCodes, 0, apiCount);

                _diagnostics.RecordEvent("Camera", $"Retrieved {apiCount} API codes from GetDeviceInfoEx");

                // Try to identify LENR API codes using heuristic approach
                // LENR Get/Set functions should return/set values of 0 (OFF) or 1 (ON)
                // We'll try GetProp on codes to find ones that return 0 or 1
                // Note: We test with API_PARAM = 0 first (most common), but some functions use param = 1
                
                const int maxCodesToTest = 100; // Limit to avoid being too slow
                var candidateCodes = new List<(int apiCode, int apiParam, long value)>();
                
                // First pass: Try with API_PARAM = 0
                for (int i = 0; i < Math.Min(apiCodes.Length, maxCodesToTest); i++)
                {
                    var testApiCode = apiCodes[i];
                    
                    // Try GetProp with API_PARAM = 0
                    var getResult = FujifilmSdkWrapper.XSDK_GetProp(_session.Handle, testApiCode, 0, out long testValue);
                    
                    if (getResult == FujifilmSdkWrapper.XSDK_COMPLETE)
                    {
                        // Check if value is 0 or 1 (typical ON/OFF values for LENR)
                        if (testValue == 0 || testValue == 1)
                        {
                            candidateCodes.Add((testApiCode, 0, testValue));
                            _diagnostics.RecordEvent("Camera", $"Found candidate LENR code: 0x{testApiCode:X} (param=0) returned value {testValue}");
                        }
                    }
                }
                
                // If no candidates found with param=0, try param=1 for a few codes
                if (candidateCodes.Count == 0 && apiCodes.Length > 0)
                {
                    _diagnostics.RecordEvent("Camera", "No candidates found with param=0, trying param=1 for first 20 codes...");
                    for (int i = 0; i < Math.Min(apiCodes.Length, 20); i++)
                    {
                        var testApiCode = apiCodes[i];
                        var getResult = FujifilmSdkWrapper.XSDK_GetProp(_session.Handle, testApiCode, 1, out long testValue);
                        
                        if (getResult == FujifilmSdkWrapper.XSDK_COMPLETE && (testValue == 0 || testValue == 1))
                        {
                            candidateCodes.Add((testApiCode, 1, testValue));
                            _diagnostics.RecordEvent("Camera", $"Found candidate LENR code: 0x{testApiCode:X} (param=1) returned value {testValue}");
                        }
                    }
                }
                
                // If we found candidates, use the first one
                // Note: We can't definitively verify it's LENR without changing settings,
                // but if it returns 0/1 and SetProp works, it's likely an ON/OFF setting
                if (candidateCodes.Count > 0)
                {
                    var candidate = candidateCodes[0];
                    _diagnostics.RecordEvent("Camera", $"Using candidate LENR API code: 0x{candidate.apiCode:X} (param={candidate.apiParam}, current value={candidate.value})");
                    _diagnostics.RecordEvent("Camera", $"Note: This is a heuristic match. If LENR control doesn't work, codes may need to be added to lookup table.");
                    
                    // Return codes (assuming Get and Set use same code and param)
                    return (candidate.apiCode, candidate.apiParam, candidate.apiCode, candidate.apiParam);
                }
                
                _diagnostics.RecordEvent("Camera", $"Tested {Math.Min(apiCodes.Length, maxCodesToTest)} API codes but found no candidates returning 0/1 values. Using lookup table fallback.");
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(apiCodeBuffer);
            }
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("Camera", $"Error getting API codes from GetDeviceInfoEx: {ex.Message}");
            return null;
        }
    }

    private (int GetApiCode, int GetApiParam, int SetApiCode, int SetApiParam)? GetLongExposureNrApiCodesFromLookup(string modelName)
    {
        // Lookup table for known API codes
        // These would typically come from model-specific header files (e.g., GFX100S.h)
        // Format: (GetApiCode, GetApiParam, SetApiCode, SetApiParam)
        // API_PARAM values are typically 0 or 1 depending on the function
        
        // Note: These codes need to be discovered from SDK header files or through testing
        // This is a placeholder structure that can be populated as codes are discovered
        // 
        // To find these codes:
        // 1. Check SDK header files (e.g., GFX100S.h) for constants like:
        //    - GFX100S_API_CODE_GetLongExposureNR
        //    - GFX100S_API_PARAM_GetLongExposureNR
        //    - GFX100S_API_CODE_SetLongExposureNR
        //    - GFX100S_API_PARAM_SetLongExposureNR
        // 2. Or test by calling GetProp/SetProp with suspected codes
        
        var lookup = new Dictionary<string, (int GetApiCode, int GetApiParam, int SetApiCode, int SetApiParam)>(StringComparer.OrdinalIgnoreCase)
        {
            // Example format (these are placeholder values - actual codes need to be determined):
            // { "GFX100S", (0x1234, 0, 0x1235, 0) },
            // { "GFX100", (0x1236, 0, 0x1237, 0) },
            // Add more models as API codes are discovered from SDK headers or testing
        };

        if (lookup.TryGetValue(modelName, out var codes))
        {
            _diagnostics.RecordEvent("Camera", $"Found LENR API codes in lookup table for '{modelName}': Get=0x{codes.GetApiCode:X}, Set=0x{codes.SetApiCode:X}");
            return codes;
        }

        _diagnostics.RecordEvent("Camera", $"No LENR API codes found in lookup table for model '{modelName}'");
        return null;
    }

    private CameraConfig? ResolveConfiguration(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            _diagnostics.RecordEvent("Camera", "ResolveConfiguration: DisplayName is null or empty");
            return null;
        }

        var config = _catalog.TryGetByProductName(displayName);
        if (config != null)
        {
            _diagnostics.RecordEvent("Camera", $"ResolveConfiguration: Found config for '{displayName}'");
        }
        else
        {
            _diagnostics.RecordEvent("Camera", $"ResolveConfiguration: No config found for '{displayName}'");
        }

        return config;
    }
}

public sealed record RawCaptureResult(
    byte[] RawBuffer,
    int Width,
    int Height,
    int Format,
    int BitDepth,
    int Iso,
    int ShutterCode,
    double ExposureSeconds,
    long TimestampTicks);

public sealed record FujiCameraCapabilities(
    IReadOnlyList<int> IsoValues,
    int DefaultIso,
    double MinExposureSeconds,
    double MaxExposureSeconds,
    bool SupportsBulb,
    int SensorWidth,
    int SensorHeight,
    int BufferShootCapacity,
    int BufferTotalCapacity,
    int ModeCode,
    int AEModeCode,
    int DynamicRangeCode,
    int LastApiErrorCode,
    int LastSdkErrorCode,
    FujiCameraMetadata Metadata,
    double TimedExposureMaxSeconds,
    double BulbExposureMaxSeconds)
{
    public static FujiCameraCapabilities Empty { get; } = new(
        Array.Empty<int>(),
        0,
        0,
        0,
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        FujiCameraMetadata.Empty,
        0,
        0);
}
