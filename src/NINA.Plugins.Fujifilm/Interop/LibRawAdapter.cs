using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.Fujifilm.Interop;

public interface ILibRawAdapter
{
    Task<LibRawResult> ProcessRawAsync(byte[] buffer, CancellationToken cancellationToken);
}

[Export(typeof(ILibRawAdapter))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class LibRawAdapter : ILibRawAdapter
{
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly IProfileService _profileService;
    private readonly ICameraModelCatalog _catalog;

    [ImportingConstructor]
    public LibRawAdapter(
        IFujifilmDiagnosticsService diagnostics,
        IProfileService profileService,
        ICameraModelCatalog catalog)
    {
        try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] LibRawAdapter Constructor called\n"); } catch {}
        _diagnostics = diagnostics;
        _profileService = profileService;
        _catalog = catalog;
    }

    public async Task<LibRawResult> ProcessRawAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (buffer == null || buffer.Length == 0)
        {
            _diagnostics.RecordEvent("LibRaw", "Empty buffer provided; skipping processing.");
            return LibRawResult.FromFailure(LibRawProcessingStatus.InvalidBuffer, null);
        }

        return await Task.Run(() =>
        {
            RawProcessingResult processed;
            
            try
            {
                processed = RawProcessor.ProcessRawBufferWithMetadata(buffer);
            }
            catch (Exception ex)
            {
                _diagnostics.RecordEvent("LibRaw", $"RawProcessor threw exception: {ex.GetType().Name} - {ex.Message}");
                _diagnostics.RecordEvent("LibRaw", $"Exception stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _diagnostics.RecordEvent("LibRaw", $"Inner exception: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                }
                var rafPath = TryPersistRaf(buffer);
                return LibRawResult.FromFailure(LibRawProcessingStatus.UnhandledException, rafPath);
            }

            if (!processed.Success)
            {
                var rafPath = processed.RafSidecarPath ?? TryPersistRaf(buffer);
                processed.RafSidecarPath = rafPath;
            }

            _diagnostics.RecordEvent("LibRaw", $"LibRaw status={processed.Status} size={processed.Width}x{processed.Height} pattern={processed.ColorFilterPattern} ({processed.PatternWidth}x{processed.PatternHeight}) sidecar={(processed.RafSidecarPath ?? "none")}");
            
            if (!string.IsNullOrEmpty(processed.ErrorMessage))
            {
                _diagnostics.RecordEvent("LibRaw", $"Error details: {processed.ErrorMessage}");
            }
            
            // Diagnostic logging for black image investigation
            if (processed.BayerData != null && processed.BayerData.Length > 0)
            {
                var samplePixels = string.Join(" ", processed.BayerData.Take(20));
                _diagnostics.RecordEvent("LibRaw", $"Bayer data sample (first 20 pixels): {samplePixels}");
                
                // Check for all zeros
                bool allZeros = true;
                for (int i = 0; i < Math.Min(processed.BayerData.Length, 1000); i++)
                {
                    if (processed.BayerData[i] != 0)
                    {
                        allZeros = false;
                        break;
                    }
                }
                if (allZeros) _diagnostics.RecordEvent("LibRaw", "WARNING: First 1000 pixels are all zeros!");
            }
            else
            {
                _diagnostics.RecordEvent("LibRaw", "WARNING: BayerData is null or empty!");
            }

            return MapToLibRawResult(processed);
        }, cancellationToken).ConfigureAwait(false);
    }

    private string? TryPersistRaf(byte[] buffer)
    {
        try
        {
            // Use NINA's configured image directory
            var imageDirectory = _profileService.ActiveProfile.ImageFileSettings.FilePath;
            Directory.CreateDirectory(imageDirectory);
            
            // Generate filename with timestamp (basic pattern - no exposure/ISO info available here)
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"Fuji_{timestamp}_recovery.raf";
            
            var path = Path.Combine(imageDirectory, fileName);
            File.WriteAllBytes(path, buffer);
            
            _diagnostics.RecordEvent("LibRaw", $"Saved RAF recovery file to: {path}");
            return path;
        }
        catch (Exception ex)
        {
            _diagnostics.RecordEvent("LibRaw", $"Failed to save RAF recovery file: {ex.Message}");
            return null;
        }
    }

    private LibRawResult MapToLibRawResult(RawProcessingResult processed)
{
    // Crop optical black padding if present
    // Use the active area detected by LibRaw
    var (croppedData, croppedWidth, croppedHeight) = CropBayerPadding(
        processed.BayerData,
        processed.Width,
        processed.Height,
        processed.ActiveLeft,
        processed.ActiveTop,
        processed.ActiveWidth,
        processed.ActiveHeight);
    
    return new LibRawResult(
        BayerData: croppedData,
        Width: croppedWidth,
        Height: croppedHeight,
        ColorFilterPattern: processed.ColorFilterPattern,
        PatternWidth: processed.PatternWidth,
        PatternHeight: processed.PatternHeight,
        BlackLevel: processed.BlackLevel,
        WhiteLevel: processed.WhiteLevel,
        Status: processed.Status,
        RafSidecarPath: processed.RafSidecarPath,
        DebayeredRgb: processed.DebayeredRgb
    );
}

/// <summary>
/// Crops optical black padding from Bayer data using explicit margins.
/// </summary>
private (ushort[] Data, int Width, int Height) CropBayerPadding(
    ushort[] bayerData,
    int libRawWidth,
    int libRawHeight,
    int activeLeft,
    int activeTop,
    int activeWidth,
    int activeHeight)
{
    // If no valid active area info, return original
    if (activeWidth <= 0 || activeHeight <= 0 || (activeWidth == libRawWidth && activeHeight == libRawHeight))
    {
        _diagnostics.RecordEvent("LibRaw", $"No cropping needed: LibRaw={libRawWidth}x{libRawHeight}");
        return (bayerData, libRawWidth, libRawHeight);
    }
    
    _diagnostics.RecordEvent("LibRaw", 
        $"Cropping Bayer data: {libRawWidth}x{libRawHeight} → {activeWidth}x{activeHeight}, " +
        $"margins: left={activeLeft}, top={activeTop}");
    
    // Crop row by row
    var croppedData = new ushort[activeWidth * activeHeight];
    for (int y = 0; y < activeHeight; y++)
    {
        int srcRow = activeTop + y;
        int srcOffset = srcRow * libRawWidth + activeLeft;
        int destOffset = y * activeWidth;
        
        // Boundary check
        if (srcRow >= libRawHeight) break;
        
        // Copy one row
        int copyLength = Math.Min(activeWidth, libRawWidth - activeLeft);
        if (copyLength > 0)
        {
            Array.Copy(bayerData, srcOffset, croppedData, destOffset, copyLength);
        }
    }
    
    return (croppedData, activeWidth, activeHeight);
}
}

public enum LibRawProcessingStatus
{
    Success = 0,
    InvalidBuffer,
    WrapperUnavailable,
    ProcessingFailed,
    UnhandledException,
    InitializationFailed,
    UnsupportedFormat,
    InsufficientMemory,
    CorruptData,
    IoError,
    LibRawError,
    MetadataExtractionFailed,
    DataExtractionFailed,
    UnknownError
}

public readonly record struct LibRawResult(
    ushort[] BayerData,
    int Width,
    int Height,
    string ColorFilterPattern,
    int PatternWidth,
    int PatternHeight,
    int BlackLevel,
    int WhiteLevel,
    LibRawProcessingStatus Status,
    string? RafSidecarPath,
    ushort[]? DebayeredRgb = null)
{
    public bool Success => Status == LibRawProcessingStatus.Success && BayerData.Length > 0 && Width > 0 && Height > 0;
    
    /// <summary>
    /// Gets debayered RGB data if available from LibRaw (for X-Trans display in NINA).
    /// Returns null if not available or for Bayer sensors.
    /// </summary>
    public ushort[]? GetDebayeredRgb() => DebayeredRgb;

    public static LibRawResult FromFailure(LibRawProcessingStatus status, string? rafPath)
        => new(Array.Empty<ushort>(), 0, 0, string.Empty, 0, 0, 0, 0, status, rafPath, null);
}
