using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Hba.Processing;

/// <summary>
/// The fixed, named presets (docs/csharp-workload §10.2): small resizes to a 640-pixel maximum
/// edge and outputs JPEG; medium uses 2048 pixels and WebP; large uses 3840 pixels, sharpens,
/// and outputs WebP. Frozen once calibrated.
/// </summary>
public static class ImagePresets
{
    private static readonly IReadOnlyDictionary<string, ImagePresetDefinition> Definitions =
        new Dictionary<string, ImagePresetDefinition>(StringComparer.Ordinal)
        {
            ["small"] = new("small", 640, KnownResamplers.Bicubic, SharpenSigma: null,
                new JpegEncoder { Quality = 80 }, "jpg", "image/jpeg"),
            ["medium"] = new("medium", 2048, KnownResamplers.Lanczos3, SharpenSigma: null,
                new WebpEncoder { Quality = 80 }, "webp", "image/webp"),
            ["large"] = new("large", 3840, KnownResamplers.Lanczos3, SharpenSigma: 3f,
                new WebpEncoder { Quality = 90 }, "webp", "image/webp")
        };

    public static IReadOnlyCollection<ImagePresetDefinition> All => Definitions.Values.ToArray();

    public static ImagePresetDefinition Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Definitions.TryGetValue(name, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown image preset '{name}'.", nameof(name));
    }
}
