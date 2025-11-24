using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NINA.Plugins.Fujifilm.Diagnostics;

namespace NINA.Plugins.Fujifilm.Imaging;

/// <summary>
/// Encodes metadata as XISF properties for proper X-Trans support.
/// XISF uses strongly-typed properties (scalar, vector, matrix) rather than FITS keywords.
/// NINA handles XISF file writing, but we need to ensure metadata is XISF-compatible.
/// </summary>
internal static class XisfPropertyEncoder
{
    /// <summary>
    /// Converts FITS-style metadata dictionary to XISF-compatible format.
    /// XISF properties are strongly-typed, so we ensure proper type encoding.
    /// </summary>
    public static IReadOnlyDictionary<string, object> EncodeProperties(
        IReadOnlyDictionary<string, string> fitsKeywords,
        string bayerPattern,
        int patternWidth,
        int patternHeight,
        IFujifilmDiagnosticsService? diagnostics = null)
    {
        var properties = new Dictionary<string, object>();

        // Critical X-Trans properties (XISF format)
        if (!string.IsNullOrEmpty(bayerPattern))
        {
            // Standardize pattern name
            var standardizedPattern = bayerPattern.ToUpperInvariant();
            if (standardizedPattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ||
                standardizedPattern.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            {
                standardizedPattern = "XTRANS";
            }

            // BAYERPAT as String property
            properties["BAYERPAT"] = standardizedPattern;

            // CFAAXIS as String property (format: "6x6" or "2x2")
            properties["CFAAXIS"] = $"{patternWidth}x{patternHeight}";

            // CFA as Boolean property (indicates color filter array present)
            properties["CFA"] = true;
        }

        // Convert FITS keywords to XISF properties with proper types
        foreach (var kvp in fitsKeywords)
        {
            var key = kvp.Key;
            var value = kvp.Value;

            // Skip if already added as strongly-typed property
            if (properties.ContainsKey(key))
            {
                continue;
            }

            // Convert to appropriate XISF property type
            object? typedValue = ConvertToXisfType(key, value);
            if (typedValue != null)
            {
                properties[key] = typedValue;
            }
            else
            {
                // Default to string if type conversion fails
                properties[key] = value;
            }
        }

        diagnostics?.RecordEvent("XisfPropertyEncoder", $"Encoded {properties.Count} XISF properties including BAYERPAT={properties.GetValueOrDefault("BAYERPAT", "none")}");

        return properties;
    }

    private static object? ConvertToXisfType(string key, string value)
    {
        // XISF strongly-typed properties
        // Try to infer type from key name and value content

        // Numeric properties
        if (key == "EXPTIME" || key == "XPIXSZ" || key == "YPIXSZ" || key == "EGAIN")
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
            {
                return dbl;
            }
        }

        if (key == "ISO" || key == "XBINNING" || key == "YBINNING" || 
            key == "XBAYROFF" || key == "YBAYROFF" || key == "BLACKLVL" || 
            key == "WHITELVL" || key == "FUJIISO" || key == "FUJISHUT" || 
            key == "FUJIDR")
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            {
                return intVal;
            }
        }

        // Boolean properties
        if (key == "CFA")
        {
            if (bool.TryParse(value, out var boolVal))
            {
                return boolVal;
            }
            if (value == "1" || value == "true" || value == "True")
            {
                return true;
            }
            if (value == "0" || value == "false" || value == "False")
            {
                return false;
            }
        }

        // String properties (default)
        return value;
    }

    /// <summary>
    /// Ensures X-Trans metadata is properly encoded for XISF format.
    /// This is critical since NINA doesn't recognize X-Trans natively.
    /// </summary>
    public static void EnsureXTransProperties(
        Dictionary<string, object> properties,
        string bayerPattern,
        int patternWidth,
        int patternHeight)
    {
        if (string.IsNullOrEmpty(bayerPattern))
        {
            return;
        }

        var standardizedPattern = bayerPattern.ToUpperInvariant();
        if (standardizedPattern.StartsWith("XTRANS", StringComparison.OrdinalIgnoreCase) ||
            standardizedPattern.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
        {
            standardizedPattern = "XTRANS";
        }

        // Ensure X-Trans properties are present and correct
        properties["BAYERPAT"] = standardizedPattern;
        properties["CFAAXIS"] = $"{patternWidth}x{patternHeight}";
        properties["CFA"] = true;

        // X-Trans specific indicator
        properties["XTNSPAT"] = standardizedPattern;
    }
}









