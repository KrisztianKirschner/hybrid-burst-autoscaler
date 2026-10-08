using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace Hba.Processing;

/// <summary>
/// Everything one preset does: resize the long edge to <see cref="MaxEdge"/> with
/// <see cref="Resampler"/>, sharpen if <see cref="SharpenSigma"/> is set, then encode with
/// <see cref="Encoder"/>. The pipeline reads all of it from here, so calibrating a preset
/// changes one entry in <see cref="ImagePresets"/> and nothing else.
/// </summary>
/// <param name="SharpenSigma">Gaussian sharpen strength, or <c>null</c> for no sharpening.</param>
/// <param name="OutputExtension">File extension for the output object; must match <paramref name="Encoder"/>.</param>
/// <param name="ContentType">MIME type for the output object; must match <paramref name="Encoder"/>.</param>
public sealed record ImagePresetDefinition(
    string Name,
    int MaxEdge,
    IResampler Resampler,
    float? SharpenSigma,
    IImageEncoder Encoder,
    string OutputExtension,
    string ContentType);
