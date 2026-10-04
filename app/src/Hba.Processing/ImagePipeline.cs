using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hba.Processing;

public sealed class ImagePipeline
{
    private readonly Configuration _configuration;

    public ImagePipeline()
    {
        _configuration = Configuration.Default.Clone();
        _configuration.MaxDegreeOfParallelism = 1;
    }

    public byte[] Process(ReadOnlySpan<byte> source, string presetName)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Image data cannot be empty.", nameof(source));
        }

        var preset = ImagePresets.Get(presetName);
        using var image = Image.Load<Rgb24>(
            new DecoderOptions { Configuration = _configuration },
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
        return output.ToArray();
    }
}

// <summary>
// ImagePipeline that takes image bytes and a preset name, decodes to Rgb24, resizes, and returns the encoded image bytes. It limits ImageSharp processing to one thread and disposes its image and streams after processing.
// </summary>

