namespace Hba.Processing;

public sealed record ImagePresetDefinition(
    string Name,
    int MaxEdge,
    string OutputExtension,
    string ContentType,
    int Quality);
