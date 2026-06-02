namespace PaperTradingBot.Utilities;

public static class PathHelper
{
    public static string ResolveFile(string relativeOrAbsolutePath)
    {
        if (Path.IsPathRooted(relativeOrAbsolutePath))
        {
            return relativeOrAbsolutePath;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, relativeOrAbsolutePath),
            Path.Combine(Directory.GetCurrentDirectory(), relativeOrAbsolutePath)
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
                return fullPath;
        }

        throw new FileNotFoundException(
            "Could not find file. Checked paths:\n" +
            string.Join(Environment.NewLine, candidates.Select(Path.GetFullPath)));
    }
}
