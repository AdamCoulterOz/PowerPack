using PowerPack.TestFixtures;

var outputDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "test", "fixtures", "solution-packages"));

var writtenPaths = SolutionPackageFixtureWriter.WriteAll(outputDirectory);
Console.WriteLine($"Wrote {writtenPaths.Count} Power Platform solution package fixtures to {outputDirectory}");
foreach (var path in writtenPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
    Console.WriteLine(path);
