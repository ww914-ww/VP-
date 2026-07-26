namespace MoveImageForm.Services
{
    /// <summary>
    /// 角色权限判断 — 纯逻辑，不依赖 UI，方便单元测试。
    /// 所有方法都是静态纯函数。后续修改权限规则只需改这个文件。
    /// </summary>
    public static class RoleEnforcer
    {
        public const string RoleUpload = "upload";
        public const string RoleAdmin = "admin";
        public const string RoleReadonly = "readonly";

        /// <summary>是否有上传权限</summary>
        public static bool CanUpload(string role)
        {
            role = (role ?? "").ToLower();
            return role == RoleUpload || role == RoleAdmin;
        }

        /// <summary>是否是管理员</summary>
        public static bool IsAdmin(string role)
        {
            return (role ?? "").ToLower() == RoleAdmin;
        }

        /// <summary>是否只读</summary>
        public static bool IsReadonly(string role)
        {
            return (role ?? "").ToLower() == RoleReadonly;
        }

        /// <summary>获取强制传输模式（upload→Append, readonly→None, admin→自由选择）</summary>
        public static string ForcedTransferMode(string role)
        {
            role = (role ?? "").ToLower();
            switch (role)
            {
                case RoleUpload: return "Append";
                case RoleAdmin: return ""; // 空字符串表示不强制，用户自由选择
                default: return "None";   // 未登录或只读 → 不可传输
            }
        }

        /// <summary>是否允许选择传输模式（只有 admin 可以）</summary>
        public static bool CanChooseTransferMode(string role)
        {
            return IsAdmin(role);
        }

        /// <summary>是否可以访问账号管理</summary>
        public static bool CanManageAccounts(string role)
        {
            return IsAdmin(role);
        }

        /// <summary>是否可以执行版本检查</summary>
        public static bool CanCheckUpdate(bool isLoggedIn)
        {
            return isLoggedIn;
        }
    }
}
