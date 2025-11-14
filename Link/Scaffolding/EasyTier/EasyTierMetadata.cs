using PCL.Core.IO;
using System.IO;
using System.Runtime.InteropServices;

namespace PCL.Core.Link.Scaffolding.EasyTier;

public static class EasyTierMetadata
{
    /// <summary>
    /// Current EasyTier version used by PCL Core.
    /// </summary>
    public const string CurrentEasyTierVersion = "2.4.5";

    /// <summary>
    /// The path to the EasyTier executable file.
    /// </summary>
    public static string EasyTierFilePath => Path.Combine(FileService.LocalDataPath, "EasyTier",
        CurrentEasyTierVersion,
        $"easytier-windows-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64")}");
}