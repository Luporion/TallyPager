using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TallyPager;

internal static class LocalSecret
{
    public const string Prefix = "dpapi:";

    [SupportedOSPlatform("windows")]
    public static string Seal(string value) => Prefix + Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    [SupportedOSPlatform("windows")]
    public static string Open(string value) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), null, DataProtectionScope.CurrentUser));
}
