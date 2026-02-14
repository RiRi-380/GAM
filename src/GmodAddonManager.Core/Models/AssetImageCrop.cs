using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public class AssetImageCrop
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }

        [JsonProperty("width")]
        public double Width { get; set; }

        [JsonProperty("height")]
        public double Height { get; set; }

        public AssetImageCrop()
        {
            X = 0;
            Y = 0;
            Width = 1;
            Height = 1;
        }
    }
}
