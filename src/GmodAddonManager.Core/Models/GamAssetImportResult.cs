using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    public sealed class GamAssetImportResult
    {
        public GamAssetImportResult(
            IEnumerable<Asset> assets,
            IEnumerable<AssetGroup> groups,
            bool isBundle)
        {
            Assets = Copy(assets, nameof(assets));
            Groups = Copy(groups, nameof(groups));
            IsBundle = isBundle;
        }

        public IReadOnlyList<Asset> Assets { get; }

        public IReadOnlyList<AssetGroup> Groups { get; }

        public bool IsBundle { get; }

        private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            return new ReadOnlyCollection<T>(values.ToList());
        }
    }
}
