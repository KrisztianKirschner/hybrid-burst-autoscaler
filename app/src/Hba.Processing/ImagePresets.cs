namespace Hba.Processing;

public static class ImagePresets
{
    private static readonly IReadOnlyDictionary<string, ImagePresetDefinition> Definitions =
        new Dictionary<string, ImagePresetDefinition>(StringComparer.Ordinal)
        {
            ["small"] = new("small", 640, "jpg", "image/jpeg", 80),
            ["medium"] = new("medium", 2048, "webp", "image/webp", 80),
            ["large"] = new("large", 3840, "webp", "image/webp", 90)
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

// <summary>
// small resizes to a 640-pixel maximum edge and outputs JPEG; medium uses 2048 pixels and WebP; large uses 3840 pixels, sharpens, and outputs WebP
// </summary>
