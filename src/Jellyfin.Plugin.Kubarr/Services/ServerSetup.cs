using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Kubarr.Services;

/// <summary>Pure decisions behind server setup, kept apart for tests.</summary>
public static class ServerSetup
{
    /// <summary>The render node Kubarr's GPU assignment exposes in the pod.</summary>
    public const string RenderNode = "/dev/dri/renderD128";

    /// <summary>Jellyfin's acceleration for a Kubarr GPU vendor; null means "turn it off".</summary>
    public static HardwareAccelerationType AccelerationFor(string vendor) => vendor switch
    {
        "intel" => HardwareAccelerationType.qsv,
        "amd" => HardwareAccelerationType.vaapi,
        "nvidia" => HardwareAccelerationType.nvenc,
        _ => HardwareAccelerationType.none,
    };

    /// <summary>
    /// Libraries to create: wanted, not present under that name, not already
    /// created once by Kubarr (a person who deleted one meant it), and with at
    /// least one folder that exists.
    /// </summary>
    public static List<LibrarySpec> LibrariesToCreate(
        IEnumerable<LibrarySpec> wanted,
        IEnumerable<string> existingNames,
        IEnumerable<string> createdBefore,
        Func<string, bool> directoryExists)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var before = new HashSet<string>(createdBefore, StringComparer.OrdinalIgnoreCase);
        return wanted
            .Where(l => !string.IsNullOrWhiteSpace(l.Name) && !string.IsNullOrWhiteSpace(l.Type))
            .Where(l => !existing.Contains(l.Name) && !before.Contains(l.Name))
            .Select(l => new LibrarySpec { Name = l.Name, Type = l.Type, Paths = l.Paths.Where(directoryExists).ToList() })
            .Where(l => l.Paths.Count > 0)
            .ToList();
    }

    public static CollectionTypeOptions? CollectionType(string type) =>
        Enum.TryParse<CollectionTypeOptions>(type, ignoreCase: true, out var parsed) ? parsed : null;
}
