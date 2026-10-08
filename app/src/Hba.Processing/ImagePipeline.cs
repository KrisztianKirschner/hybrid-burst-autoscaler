using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hba.Processing;

/// <summary>
/// Takes image bytes and a preset name, decodes to Rgb24, resizes, sharpens if the preset says so,
/// and returns the encoded image bytes. It limits ImageSharp processing to one thread and disposes
/// its image and streams after processing.
/// </summary>
public sealed class ImagePipeline : IImagePipeline
{
    private readonly Configuration _configuration;

    public ImagePipeline()
    {
        _configuration = Configuration.Default.Clone();
        _configuration.MaxDegreeOfParallelism = 1;
    }

    // One replica = one vCPU; exposed so a test guards it
    internal int MaxDegreeOfParallelism => _configuration.MaxDegreeOfParallelism;

    public ProcessedImage Process(ReadOnlySpan<byte> source, string presetName)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Image data cannot be empty.", nameof(source));
        }

        var preset = ImagePresets.Get(presetName);

        // SkipMetadata: EXIF (incl. GPS), ICC and thumbnails would otherwise be copied into every output.
        // TargetSize is left unset on purpose: decoder-side downscaling would make every job cheaper
        // and invalidate the calibration (§10.2). Every job does the full decode.
        using var image = Image.Load<Rgb24>(
            new DecoderOptions { Configuration = _configuration, SkipMetadata = true },
            source);

        image.Mutate(context =>
        {
            context.Resize(new ResizeOptions
            {
                Size = new Size(preset.MaxEdge, preset.MaxEdge),
                Mode = ResizeMode.Max,
                Sampler = preset.Resampler
            });

            if (preset.SharpenSigma is { } sigma)
            {
                context.GaussianSharpen(sigma);
            }
        });

        using var output = new MemoryStream();
        image.Save(output, preset.Encoder);
        return new ProcessedImage(output.ToArray(), preset.OutputExtension, preset.ContentType);
    }
}
