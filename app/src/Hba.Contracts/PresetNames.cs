namespace Hba.Contracts;

/// <summary>
/// The preset names a job may request. Only the names live here: the API validates against them,
/// and the worker checks them before using one as a metric label. The pipeline settings for each
/// name live in Hba.Processing.
/// </summary>
public static class PresetNames
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";

    public static IReadOnlyList<string> All { get; } = [Small, Medium, Large];

    public static bool IsKnown(string name) => All.Contains(name);
}
