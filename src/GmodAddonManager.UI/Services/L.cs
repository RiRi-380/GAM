using System;

namespace GmodAddonManager.UI.Services
{
    /// <summary>
    /// ローカライゼーションのヘルパークラス
    /// ViewModelやコードビハインドで使用
    /// </summary>
    public static class L
    {
        static L()
        {
#if DEBUG
            try
            {
                System.IO.File.AppendAllText("app_startup.log", $"L static constructor called at: {System.DateTime.Now}\n");
            }
            catch 
            {
                // Ignore debug logging errors - non-critical
            }
#endif
        }
        
        /// <summary>
        /// ローカライズされた文字列を取得
        /// </summary>
        public static string Get(string key)
        {
            try
            {
                return LocalizationManager.Instance.GetString(key);
            }
            catch (Exception ex)
            {
#if DEBUG
                System.IO.File.AppendAllText("app_startup.log", $"L.Get error for key {key}: {ex.Message} at: {System.DateTime.Now}\n");
#endif
                throw;
            }
        }
        
        /// <summary>
        /// フォーマット付きローカライズ文字列を取得
        /// </summary>
        public static string Format(string key, params object[] args)
        {
            var format = LocalizationManager.Instance.GetString(key);
            return string.Format(format, args);
        }
    }
}