namespace PowerPack.Models;

public readonly record struct SolutionVersion(int Major, int Minor, int Patch, int Revision) : IComparable<SolutionVersion>
{
    public static SolutionVersion Parse(string rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            throw new PowerPackValidationException("Version must be a non-empty string.");

        var segments = rawVersion.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > 4)
            throw new PowerPackValidationException(
                $"Version '{rawVersion}' must contain between one and four numeric segments."
            );

        var numericSegments = new int[4];
        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index], out var numericSegment) || numericSegment < 0)
                throw new PowerPackValidationException(
                    $"Version '{rawVersion}' contains a non-numeric segment '{segments[index]}'."
                );

            numericSegments[index] = numericSegment;
        }

        return new SolutionVersion(
            numericSegments[0],
            numericSegments[1],
            numericSegments[2],
            numericSegments[3]
        );
    }

    public int CompareTo(SolutionVersion other)
    {
        var current = (Major, Minor, Patch, Revision);
        var comparison = current.Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = current.Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = current.Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        return current.Revision.CompareTo(other.Revision);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Revision}";
}

public sealed class PowerPackValidationException(string message) : Exception(message);
