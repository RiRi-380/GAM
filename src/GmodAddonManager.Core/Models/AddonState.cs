namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// アドオンの状態を表す列挙型
    /// </summary>
    public enum AddonState
    {
        /// <summary>
        /// 有効: サブスクライブに入っていて、他のアセットで有効なら有効（全部有効）
        /// </summary>
        Enabled = 0,
        
        /// <summary>
        /// 無効: サブスクライブに入っているなら有効、入っていないなら無効。このアセットのみでも無効
        /// </summary>
        Disabled = 1,
        
        /// <summary>
        /// 除外: サブスクライブに入っていて有効になっていても無効になる（全部無効）
        /// </summary>
        Excluded = 2
    }
}