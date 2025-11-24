namespace NINA.Plugins.Fujifilm.Devices;

public sealed record FujiCameraMetadata(
    string ProductName,
    string FirmwareVersion,
    string LensProductName,
    string LensSerialNumber,
    int DynamicRangeCode)
{
    public static FujiCameraMetadata Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0);
}


