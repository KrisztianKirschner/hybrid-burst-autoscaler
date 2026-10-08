using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hba.Processing.Tests;

public sealed class ImagePipelineTests
{
    private const int SourceWidth = 4000;
    private const int SourceHeight = 3000;

    [Theory]
    [InlineData("small", 640, 480, "Jpeg")]
    [InlineData("medium", 2048, 1536, "Webp")]
    [InlineData("large", 3840, 2880, "Webp")]
    public void Process_ResizesAndEncodesUsingPreset(
        string preset,
        int expectedWidth,
        int expectedHeight,
        string expectedFormat)
    {
        var pipeline = new ImagePipeline();
        var input = CreateJpeg(SourceWidth, SourceHeight);

        var result = pipeline.Process(input, preset);

        var format = Image.DetectFormat(result.Data);
        Assert.NotNull(format);
        Assert.Equal(expectedFormat, format.Name, ignoreCase: true);

        // Extension and content type are stored next to the encoder, so check they agree with it
        Assert.Contains(result.Extension, format.FileExtensions);
        Assert.Equal(result.ContentType, format.DefaultMimeType);

        using var output = Image.Load<Rgb24>(result.Data);
        Assert.Equal(expectedWidth, output.Width);
        Assert.Equal(expectedHeight, output.Height);
    }

    [Theory]
    [InlineData("small")]
    [InlineData("medium")]
    [InlineData("large")]
    public void Process_ProducesIdenticalOutputForRepeatedIdenticalInput(string preset)
    {
        var pipeline = new ImagePipeline();
        var input = CreateJpeg(SourceWidth, SourceHeight);

        var first = pipeline.Process(input, preset);
        var second = pipeline.Process(input, preset);

        Assert.Equal(first.Data, second.Data);
    }

    [Fact]
    public void Process_RejectsNonImageInputWithAPermanentFailureType()
    {
        // The worker treats exactly these types as "bad input, don't retry" (JobLoop).
        // If an ImageSharp update changes what it throws, this test fails instead of jobs retrying.
        var pipeline = new ImagePipeline();
        var garbage = new byte[4096];
        new Random(42).NextBytes(garbage);

        var exception = Assert.ThrowsAny<Exception>(() => pipeline.Process(garbage, "small"));

        Assert.True(
            exception is UnknownImageFormatException or InvalidImageContentException,
            $"Unexpected exception type {exception.GetType().Name}.");
    }

    [Theory]
    [InlineData("tiff")]
    [InlineData("webp")]
    [InlineData("gif")]
    public void Process_RejectsFormatsOutsideTheSourceSet(string format)
    {
        // Only JPEG and PNG are decoded; other formats must not reach their decoders (TIFF has open advisories).
        var pipeline = new ImagePipeline();
        using var image = new Image<Rgb24>(64, 48, new Rgb24(80, 140, 200));
        using var stream = new MemoryStream();
        switch (format)
        {
            case "tiff": image.SaveAsTiff(stream); break;
            case "webp": image.SaveAsWebp(stream); break;
            case "gif": image.SaveAsGif(stream); break;
        }

        Assert.Throws<UnknownImageFormatException>(() => pipeline.Process(stream.ToArray(), "small"));
    }

    [Fact]
    public void Process_AcceptsPng()
    {
        var pipeline = new ImagePipeline();
        using var image = new Image<Rgb24>(800, 600, new Rgb24(80, 140, 200));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var result = pipeline.Process(stream.ToArray(), "small");

        Assert.Equal("jpg", result.Extension);
    }

    [Fact]
    public void Process_DropsSourceMetadata()
    {
        var pipeline = new ImagePipeline();
        var input = CreateJpegWithExif(640, 480);

        var result = pipeline.Process(input, "small");

        using var output = Image.Load<Rgb24>(result.Data);
        Assert.Null(output.Metadata.ExifProfile);
    }

    [Fact]
    public void Pipeline_IsSingleThreaded()
    {
        // One replica = one vCPU (docs/csharp-workload §10.1). If this fails, every scaling result is wrong.
        Assert.Equal(1, new ImagePipeline().MaxDegreeOfParallelism);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Medium")]
    public void Process_RejectsUnknownPreset(string preset)
    {
        var pipeline = new ImagePipeline();
        var input = CreateJpeg(64, 48);

        Assert.Throws<ArgumentException>(() => pipeline.Process(input, preset));
    }

    [Fact]
    public void Process_RejectsEmptyInput()
    {
        var pipeline = new ImagePipeline();

        Assert.Throws<ArgumentException>(() => pipeline.Process([], "small"));
    }

    [Fact]
    public void Presets_ExposeStableNamesAndOutputFormats()
    {
        Assert.Collection(
            ImagePresets.All.OrderBy(preset => preset.Name, StringComparer.Ordinal),
            preset =>
            {
                Assert.Equal("large", preset.Name);
                Assert.Equal(3840, preset.MaxEdge);
                Assert.Equal(KnownResamplers.Lanczos3, preset.Resampler);
                Assert.Equal(3f, preset.SharpenSigma);
                Assert.Equal(90, Assert.IsType<WebpEncoder>(preset.Encoder).Quality);
                Assert.Equal("webp", preset.OutputExtension);
            },
            preset =>
            {
                Assert.Equal("medium", preset.Name);
                Assert.Equal(2048, preset.MaxEdge);
                Assert.Equal(KnownResamplers.Lanczos3, preset.Resampler);
                Assert.Null(preset.SharpenSigma);
                Assert.Equal(80, Assert.IsType<WebpEncoder>(preset.Encoder).Quality);
                Assert.Equal("webp", preset.OutputExtension);
            },
            preset =>
            {
                Assert.Equal("small", preset.Name);
                Assert.Equal(640, preset.MaxEdge);
                Assert.Equal(KnownResamplers.Bicubic, preset.Resampler);
                Assert.Null(preset.SharpenSigma);
                Assert.Equal(80, Assert.IsType<JpegEncoder>(preset.Encoder).Quality);
                Assert.Equal("jpg", preset.OutputExtension);
            });
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(80, 140, 200));
        using var output = new MemoryStream();
        image.SaveAsJpeg(output);
        return output.ToArray();
    }

    private static byte[] CreateJpegWithExif(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(80, 140, 200));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "Test Camera");
        using var output = new MemoryStream();
        image.SaveAsJpeg(output);
        return output.ToArray();
    }
}
