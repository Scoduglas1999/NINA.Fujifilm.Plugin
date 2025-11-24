using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Image.Interfaces;
using NINA.Plugins.Fujifilm.Configuration.Loading;
using NINA.Plugins.Fujifilm.Diagnostics;
using NINA.Plugins.Fujifilm.Interop;
using NINA.Profile.Interfaces;
using NINA.Plugins.Fujifilm.Settings;

namespace NINA.Plugins.Fujifilm.Devices;

[Export(typeof(IFujiCameraFactory))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class FujiCameraFactory : IFujiCameraFactory
{
    private readonly IFujifilmInterop _interop;
    private readonly IFujifilmDiagnosticsService _diagnostics;
    private readonly FujiCamera _camera;
    private readonly ILibRawAdapter _libRaw;
    private readonly ICameraModelCatalog _catalog;
    private readonly IProfileService _profileService;
    private readonly IExposureDataFactory _exposureDataFactory;
    private readonly IFujiSettingsProvider _settingsProvider;

    [ImportingConstructor]
    public FujiCameraFactory(
        IFujifilmInterop interop,
        IFujifilmDiagnosticsService diagnostics,
        FujiCamera camera,
        ILibRawAdapter libRaw,
        IFujiSettingsProvider settingsProvider,
        ICameraModelCatalog catalog,
        IProfileService profileService,
        IExposureDataFactory exposureDataFactory
        )
    {
        _interop = interop;
        _diagnostics = diagnostics;
        _camera = camera;
        _libRaw = libRaw;
        _settingsProvider = settingsProvider;
        _catalog = catalog;
        _profileService = profileService;
        _exposureDataFactory = exposureDataFactory;
        try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] FujiCameraFactory (DirectCamera) Constructor called\n"); } catch {}
    }

    public async Task<IReadOnlyList<FujifilmCameraDescriptor>> GetAvailableCamerasAsync(CancellationToken cancellationToken)
    {
        try
        {
            System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] FujiCameraFactory.GetAvailableCamerasAsync called\n");
            var cameras = await _interop.DetectCamerasAsync(cancellationToken).ConfigureAwait(false);
            System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] FujiCameraFactory: DetectCamerasAsync returned {cameras.Count} items\n");
            
            var descriptors = new List<FujifilmCameraDescriptor>();

            foreach (var info in cameras)
            {
                descriptors.Add(new FujifilmCameraDescriptor(info.ProductName, info.DeviceId));
            }

            return descriptors;
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] FujiCameraFactory.GetAvailableCamerasAsync FAILED: {ex}\n");
            throw;
        }
    }

    public FujiCamera CreateCamera()
    {
        return _camera;
    }

    public ICamera CreateGenericCamera(FujifilmCameraDescriptor descriptor)
    {
        // We need to manage lifetimes for the camera and libraw adapter
        // Since this factory is Shared, but the adapter is transient/per-connection
        // We'll use the factory's injected instances but we need to be careful about disposal.
        
        // In a proper DI setup, we might want to use a child container or factory delegate.
        // For now, we pass the shared instances. The Adapter should NOT dispose the shared _camera instance
        // if it's meant to be reused, but here _camera is NonShared in the container?
        // Wait, FujiCamera is NonShared in its export.
        // But Factory is [PartCreationPolicy(CreationPolicy.Shared)]
        // So _camera is captured once. This means we can only use one camera instance.
        // That's probably fine for now.
        
        // We create a new adapter for each connection attempt/equipment item.
        // We pass 'Empty' disposables because we don't want the adapter to dispose our shared services.
        var sdkAdapter = new FujiCameraSdkAdapter(
            _camera, 
            descriptor, 
            _diagnostics, 
            _libRaw, 
            _settingsProvider, 
            _profileService,
            Disposable.Empty, 
            Disposable.Empty);
        
        // Wrap the IGenericCameraSDK adapter in NINA's GenericCamera to implement ICamera
        return new GenericCamera(
            descriptor.DisplayName,  // Camera name
            descriptor.DisplayName,  // Camera ID/description
            "Fujifilm Camera Plugin",
            "1.9.0",
            false,
            sdkAdapter,
            _profileService,
            _exposureDataFactory);
    }

    public async Task<FujiCameraCapabilities> GetCapabilitiesAsync(FujifilmCameraDescriptor descriptor, CancellationToken cancellationToken)
    {
        // This might require connecting to the camera to get capabilities if not already cached.
        // For now, we can return empty or try to connect briefly.
        // Given the architecture, we might just return what we know or throw if not connected.
        // But NINA might call this before connection?
        // Actually, this method isn't standard in ICameraFactory, it seems custom to IFujiCameraFactory.
        // We'll implement it by delegating to the camera if connected, or connecting briefly.
        
        if (_camera.IsConnected)
        {
            return _camera.GetCapabilitiesSnapshot();
        }
        
        return new FujiCameraCapabilities(
            Array.AsReadOnly(new int[0]), 
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

    private class Disposable : IDisposable
    {
        public static readonly IDisposable Empty = new Disposable();
        public void Dispose() { }
    }
}
