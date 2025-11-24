using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.Fujifilm.Interop.Native;

namespace NINA.Plugins.Fujifilm.Interop;

public interface IFujifilmInterop : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task ShutdownAsync();
    Task<IReadOnlyList<FujifilmCameraInfo>> DetectCamerasAsync(CancellationToken cancellationToken);
    Task<FujifilmCameraSession> OpenCameraAsync(string deviceId, CancellationToken cancellationToken);
    Task CloseCameraAsync(FujifilmCameraSession session);
    Task<(int Width, int Height)> GetImageInfoAsync(FujifilmCameraSession session);
    Task<int> GetImageSizeAsync(FujifilmCameraSession session);
    Task<int> GetSensitivityAsync(FujifilmCameraSession session);
}
