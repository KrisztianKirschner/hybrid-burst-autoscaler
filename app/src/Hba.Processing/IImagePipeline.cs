namespace Hba.Processing;

/// <summary>
/// The worker's only way into image processing. An interface so worker tests can swap in a fake,
/// for example a deliberately slow one for the SIGTERM-mid-job test (docs/csharp-workload §14.2).
/// </summary>
public interface IImagePipeline
{
    ProcessedImage Process(ReadOnlySpan<byte> source, string presetName);
}

/// <summary>
/// Encoded output plus what the worker needs to store it, so it never looks up the preset itself.
/// </summary>
public sealed record ProcessedImage(byte[] Data, string Extension, string ContentType);
