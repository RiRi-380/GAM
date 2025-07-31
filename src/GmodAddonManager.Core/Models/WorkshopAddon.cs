using System;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class WorkshopAddon
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [JsonProperty("thumbnailUrl")]
        public string ThumbnailUrl { get; set; }

        [JsonProperty("author")]
        public string Author { get; set; }

        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonProperty("folderPath")]
        public string FolderPath { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("tags")]
        public string[] Tags { get; set; }

        [JsonProperty("isGmaFile")]
        public bool IsGmaFile { get; set; }

        [JsonProperty("needsTitleUpdate")]
        public bool NeedsTitleUpdate { get; set; }
        
        [JsonProperty("isFavorite")]
        public bool IsFavorite { get; set; }

        public WorkshopAddon()
        {
            Id = string.Empty;
            Title = string.Empty;
            Size = 0;
            LastUpdated = DateTime.UtcNow;
            ThumbnailUrl = string.Empty;
            Author = string.Empty;
            IsEnabled = true;
            FolderPath = string.Empty;
            Description = string.Empty;
            Type = string.Empty;
            Tags = Array.Empty<string>();
            IsGmaFile = false;
            NeedsTitleUpdate = false;
            IsFavorite = false;
        }

        public WorkshopAddon(string id, string folderPath) : this()
        {
            Id = id;
            FolderPath = folderPath;
        }

        public string GetFormattedSize()
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = Size;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}