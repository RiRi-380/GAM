namespace GmodAddonManager.Core.Models
{
    public enum WorkshopCollectionLookupStatus
    {
        Found,
        NotFound,
        Unavailable
    }

    public sealed class WorkshopCollectionLookupResult
    {
        private WorkshopCollectionLookupResult(WorkshopCollectionLookupStatus status, WorkshopCollectionInfo? collectionInfo)
        {
            Status = status;
            CollectionInfo = collectionInfo;
        }

        public WorkshopCollectionLookupStatus Status { get; }
        public WorkshopCollectionInfo? CollectionInfo { get; }

        public bool IsFound => Status == WorkshopCollectionLookupStatus.Found && CollectionInfo != null;

        public static WorkshopCollectionLookupResult Found(WorkshopCollectionInfo collectionInfo)
        {
            return new WorkshopCollectionLookupResult(WorkshopCollectionLookupStatus.Found, collectionInfo);
        }

        public static WorkshopCollectionLookupResult NotFound()
        {
            return new WorkshopCollectionLookupResult(WorkshopCollectionLookupStatus.NotFound, null);
        }

        public static WorkshopCollectionLookupResult Unavailable()
        {
            return new WorkshopCollectionLookupResult(WorkshopCollectionLookupStatus.Unavailable, null);
        }
    }
}
