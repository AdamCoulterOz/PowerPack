namespace PowerPack.Models;

public static class PackageQuality
{
    public const string Local = "local";
    public const string Prerelease = "prerelease";
    public const string Release = "release";

    public static string Parse(string? rawValue)
    {
        var normalized = rawValue?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Local => Local,
            Prerelease => Prerelease,
            Release => Release,
            _ => throw new PowerPackValidationException(
                "Package quality must be one of: local, prerelease, release."
            ),
        };
    }
}
