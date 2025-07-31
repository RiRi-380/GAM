namespace GmodAddonManager.Core.Services
{
    public interface IJunctionService
    {
        void CreateJunction(string junctionPath, string targetPath);
        void RemoveJunction(string junctionPath);
        bool IsJunction(string path);
        string GetJunctionTarget(string junctionPath);
        void ValidateAdminPrivileges();
        
        // ハードリンク関連のメソッド
        void CreateHardLink(string hardLinkPath, string targetPath);
        void RemoveHardLink(string hardLinkPath);
        bool IsHardLink(string filePath, string targetPath);
        
        // Workshop アドオン構造の管理
        void CreateWorkshopAddonStructure(string workshopPath, string addonId, string managedGmaPath);
        void RemoveWorkshopAddonStructure(string workshopPath, string addonId);
    }
}