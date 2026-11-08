using Jellyfin.Plugin.Kubarr.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Kubarr.Tests;

public class ServerSetupTests
{
    [Theory]
    [InlineData("intel", HardwareAccelerationType.qsv)]
    [InlineData("amd", HardwareAccelerationType.vaapi)]
    [InlineData("nvidia", HardwareAccelerationType.nvenc)]
    [InlineData("", HardwareAccelerationType.none)]
    [InlineData("matrox", HardwareAccelerationType.none)]
    public void MapsGpuVendors(string vendor, HardwareAccelerationType expected) =>
        Assert.Equal(expected, ServerSetup.AccelerationFor(vendor));

    [Fact]
    public void CreatesOnlyMissingNeverDeletedLibrariesWithExistingFolders()
    {
        var wanted = new[]
        {
            new LibrarySpec { Name = "Movies", Type = "movies", Paths = { "/media/movies" } },
            new LibrarySpec { Name = "Shows", Type = "tvshows", Paths = { "/media/shows" } },
            new LibrarySpec { Name = "Music", Type = "music", Paths = { "/media/music" } },
            new LibrarySpec { Name = "Books", Type = "books", Paths = { "/media/books" } },
        };
        var result = ServerSetup.LibrariesToCreate(
            wanted,
            existingNames: new[] { "movies" },
            createdBefore: new[] { "Shows" },
            directoryExists: p => p != "/media/books");
        Assert.Equal(new[] { "Music" }, result.Select(l => l.Name));
    }

    [Fact]
    public void ParsesCollectionTypes()
    {
        Assert.Equal(CollectionTypeOptions.tvshows, ServerSetup.CollectionType("tvshows"));
        Assert.Null(ServerSetup.CollectionType("nonsense"));
    }
}
