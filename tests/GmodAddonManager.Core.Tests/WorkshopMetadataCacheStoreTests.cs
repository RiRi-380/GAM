using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class WorkshopMetadataCacheStoreTests
{
    [Fact]
    public void CacheStore_RoundTripsMetadataAndNegativeEntries_WithNativeSqliteBundle()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"gam-sqlite-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(testRoot, "workshop.db");

        try
        {
            var store = new WorkshopMetadataCacheStore(dbPath);
            store.UpsertNegative(new[] { "missing" });
            store.UpsertBatch(new[]
            {
                new WorkshopItemDetails
                {
                    Id = "123",
                    Title = "Test addon",
                    Description = new string('x', 350),
                    PreviewUrl = "https://example.invalid/preview.png",
                    TimeCreated = 10,
                    TimeUpdated = 20,
                    Creator = "456",
                    FileSize = 789,
                    Tags = new[] { "fun", "FUN", "tool" }
                }
            });

            var cached = Assert.Single(store.GetCoreBatch(new[] { "123" }));
            Assert.Equal("123", cached.Key);
            Assert.Equal("Test addon", cached.Value.Details.Title);
            Assert.Equal(300, cached.Value.Details.Description?.Length);
            Assert.Equal(new[] { "fun", "tool" }, cached.Value.Details.Tags);
            Assert.True(store.TryGetFullDescription("123", out var description));
            Assert.Equal(350, description?.Length);
            Assert.Contains("missing", store.GetNegativeBatch(new[] { "missing" }));

            store.DeleteNegative(new[] { "missing" });
            Assert.Empty(store.GetNegativeBatch(new[] { "missing" }));
            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
