using System.Runtime.InteropServices;

namespace UsbIntegrityTester.Core.Devices;

/// <summary>Basic host machine info worth showing alongside a USB test — useful context when comparing results across videos or machines.</summary>
public static class SystemInfo
{
    public static string OperatingSystem => $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} (Build {Environment.OSVersion.Version.Build})";

    public static string Architecture => RuntimeInformation.OSArchitecture.ToString();

    public static string MachineName => Environment.MachineName;

    public static bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
