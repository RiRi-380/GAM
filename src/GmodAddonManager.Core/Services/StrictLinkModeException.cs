using System;

namespace GmodAddonManager.Core.Services
{
    public class StrictLinkModeException : InvalidOperationException
    {
        public StrictLinkModeException(string message) : base(message)
        {
        }
    }
}
