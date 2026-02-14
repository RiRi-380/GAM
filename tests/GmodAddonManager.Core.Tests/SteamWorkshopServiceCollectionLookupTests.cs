using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SteamWorkshopServiceCollectionLookupTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public async Task GetCollectionDetailsWithStatusAsync_EmptyId_ReturnsNotFound(string collectionId)
    {
        var service = new SteamWorkshopService();

        var result = await service.GetCollectionDetailsWithStatusAsync(collectionId).ConfigureAwait(true);

        Assert.Equal(WorkshopCollectionLookupStatus.NotFound, result.Status);
        Assert.Null(result.CollectionInfo);
        Assert.False(result.IsFound);
    }

    [Fact]
    public void WorkshopCollectionLookupResult_FactoryMethods_ReturnExpectedStatus()
    {
        var info = new WorkshopCollectionInfo { Id = "12345" };

        var found = WorkshopCollectionLookupResult.Found(info);
        var notFound = WorkshopCollectionLookupResult.NotFound();
        var unavailable = WorkshopCollectionLookupResult.Unavailable();

        Assert.Equal(WorkshopCollectionLookupStatus.Found, found.Status);
        Assert.Same(info, found.CollectionInfo);
        Assert.True(found.IsFound);

        Assert.Equal(WorkshopCollectionLookupStatus.NotFound, notFound.Status);
        Assert.Null(notFound.CollectionInfo);
        Assert.False(notFound.IsFound);

        Assert.Equal(WorkshopCollectionLookupStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.CollectionInfo);
        Assert.False(unavailable.IsFound);
    }
}
