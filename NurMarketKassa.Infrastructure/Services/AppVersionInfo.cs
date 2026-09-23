using System.Reflection;

namespace NurMarketKassa.Services;

public static class AppVersionInfo
{
    public static string CurrentVersionLabel
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var i = info.IndexOf('+', StringComparison.Ordinal);
                return i > 0 ? info[..i].Trim() : info.Trim();
            }

            var v = asm.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
