using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NINA.Plugins.Fujifilm.Configuration.Loading;

[Export(typeof(ICameraModelCatalog))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class CameraModelCatalog : ICameraModelCatalog
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private IReadOnlyList<CameraConfig> _configs = Array.Empty<CameraConfig>();
    private Dictionary<string, CameraConfig> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public CameraModelCatalog()
    {
        try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog Constructor called\n"); } catch {}
        Reload();
    }

    public CameraConfig? TryGetByProductName(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        lock (_sync)
        {
            _lookup.TryGetValue(productName.Trim(), out var config);
            if (config != null)
            {
                return config;
            }

            var sanitized = SanitizeModelName(productName);
            _lookup.TryGetValue(sanitized, out config);
            return config;
        }
    }

    public IReadOnlyList<CameraConfig> GetAll()
    {
        lock (_sync)
        {
            return _configs;
        }
    }

    public void Reload()
    {
        lock (_sync)
        {
            var directory = ResolveConfigDirectory();
            try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Looking for configs in '{directory}'\n"); } catch {}
            if (!Directory.Exists(directory))
            {
                try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Directory DOES NOT EXIST\n"); } catch {}
                _configs = Array.Empty<CameraConfig>();
                _lookup = new Dictionary<string, CameraConfig>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Found {files.Length} JSON files\n"); } catch {}
            var configs = new List<CameraConfig>(files.Length);
            var lookup = new Dictionary<string, CameraConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var config = JsonSerializer.Deserialize<CameraConfig>(stream, _serializerOptions);
                    if (config == null || string.IsNullOrWhiteSpace(config.ModelName))
                    {
                        continue;
                    }

                    configs.Add(config);
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        config.ModelName,
                        SanitizeModelName(config.ModelName)
                    };

                    foreach (var key in keys)
                    {
                        lookup[key] = config;
                        // try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Mapped key '{key}' to {config.ModelName}\n"); } catch {}
                    }
                    try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Loaded config for {config.ModelName}\n"); } catch {}
                }
                catch (Exception ex)
                {
                    // Ignore invalid individual config file for now.
                    try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Failed to load {file}: {ex.Message}\n"); } catch {}
                }
            }

            _configs = configs;
            _lookup = lookup;
            try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] CameraModelCatalog.Reload: Total loaded: {configs.Count} configs. Keys: {string.Join(", ", _lookup.Keys)}\n"); } catch {}
        }
    }

    private static string ResolveConfigDirectory()
    {
        // AppContext.BaseDirectory returns NINA's exe directory, not the plugin directory.
        // We need to use the plugin assembly's location instead.
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        try { System.IO.File.AppendAllText(@"c:\Users\scdou\Documents\NINA.Fujifilm.Plugin\debug_log.txt", $"[{DateTime.Now}] ResolveConfigDirectory: Assembly Location: '{assemblyLocation}'\n"); } catch {}
        var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDir, "Configuration", "Assets", "CameraConfigs");
    }

    private static string SanitizeModelName(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }
}
