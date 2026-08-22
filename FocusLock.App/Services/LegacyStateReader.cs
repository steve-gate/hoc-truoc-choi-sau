using System.Text.Json;
using FocusLock.Shared.Models;

namespace FocusLock.App.Services;

public static class LegacyStateReader
{
    public static AppState? TryRead()
    {
        foreach (var file in CandidateFiles())
        {
            try
            {
                if (!File.Exists(file)) continue;
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(file));
                if (state is not null) return state;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> CandidateFiles()
    {
        // Portable/code-folder legacy state (if a user manually places an old state.json in Data).
        var appDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (appDir.Name.Equals("App", StringComparison.OrdinalIgnoreCase) && appDir.Parent is not null)
            yield return Path.Combine(appDir.Parent.FullName, "Data", "state.json");

        // Original MVP location.
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusLock", "state.json");
    }
}
