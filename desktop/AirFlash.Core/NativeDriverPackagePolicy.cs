using System.IO.Compression;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
namespace AirFlash.Core;

public enum NativeDriverInstallOutcome { VerificationRequired, RestartRequired }
public sealed record NativeDriverPackageFile(string Name, long Length, string Sha256);

public static class NativeDriverPackagePolicy
{
    public const string ResourceName = "AirFlash.VbCable.zip";
    public const string ArchiveFileName = "VBCABLE_Driver_Pack45.zip";
    public const string ArchiveSha256 = "b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb";
    public const string InstallerFileName = "VBCABLE_Setup_x64.exe";
    private static readonly HashSet<string> PackageFiles = new(StringComparer.Ordinal)
    {
        "vbMmeCable64_win10.inf", "pin_in.ico", "pin_out.ico", "readme.txt",
        "vbaudio_cable_2003.cat", "vbaudio_cable_2003.sys", "vbaudio_cable_vista.cat", "vbaudio_cable_vista.sys",
        "vbaudio_cable_win7.cat", "vbaudio_cable_win7.sys", "vbaudio_cable_xp.cat", "vbaudio_cable_xp.sys",
        "vbaudio_cable64_2003.cat", "vbaudio_cable64_2003.sys", "vbaudio_cable64_vista.cat", "vbaudio_cable64_vista.sys",
        "vbaudio_cable64_win7.cat", "vbaudio_cable64_win7.sys", "vbaudio_cable64_win10.cat", "vbaudio_cable64_win10.sys",
        "vbaudio_cable64arm_win10.sys", "VBCABLE_ControlPanel.exe", "VBCABLE_Setup.exe", "VBCABLE_Setup_x64.exe",
        "vbMmeCable_2003.inf", "vbMmeCable_vista.inf", "vbMmeCable_win7.inf", "vbMmeCable_xp.inf",
        "vbMmeCable64_2003.inf", "vbMmeCable64_vista.inf", "vbMmeCable64_win7.inf"
    };

    public static IReadOnlyList<NativeDriverPackageFile> VerifyArchive(Stream bytes)
    {
        if (!bytes.CanSeek) throw new InvalidDataException("The embedded VB-CABLE archive must be seekable.");
        bytes.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded VB-CABLE archive does not match the verified original package.");
        bytes.Position = 0;
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: true);
        ValidateEntries(archive);
        var files = new List<NativeDriverPackageFile>();
        foreach (var entry in archive.Entries)
        {
            using var file = entry.Open();
            files.Add(new(entry.FullName, entry.Length, Convert.ToHexString(SHA256.HashData(file))));
        }
        return files.AsReadOnly();
    }

    internal static void ValidateEntries(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!PackageFiles.Contains(entry.FullName) || !names.Add(entry.FullName)
                || (entry.ExternalAttributes & ((int)FileAttributes.ReparsePoint | (int)FileAttributes.Directory)) != 0
                || kind is not (0 or 0x8000))
                throw new InvalidDataException("The VB-CABLE archive contains an unexpected path, duplicate, or link.");
        }
        if (names.Count != PackageFiles.Count)
            throw new InvalidDataException("The VB-CABLE archive is incomplete; retain all original files and notices.");
    }

    [SupportedOSPlatform("windows")]
    public static SafeFileHandle LockDirectory(string path) => OpenLockedPath(path, directory: true);

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<FileStream> LockAndVerifyFiles(string directory, IReadOnlyList<NativeDriverPackageFile> files)
    {
        if (files.Count != PackageFiles.Count || !PackageFiles.SetEquals(files.Select(file => file.Name)))
            throw new InvalidDataException("The extracted VB-CABLE file manifest is incomplete or unsafe.");
        var paths = Directory.GetFileSystemEntries(directory);
        if (paths.Length != PackageFiles.Count || !PackageFiles.SetEquals(paths.Select(path => Path.GetFileName(path)!)))
            throw new InvalidDataException("The extracted VB-CABLE directory contains missing or unexpected files.");
        var locks = new List<FileStream>();
        try
        {
            foreach (var file in files)
            {
                var stream = new FileStream(OpenLockedPath(Path.Combine(directory, file.Name), directory: false), FileAccess.Read);
                locks.Add(stream);
                if (stream.Length != file.Length
                    || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The extracted VB-CABLE file {file.Name} does not match the original package.");
            }
            return locks.AsReadOnly();
        }
        catch
        {
            foreach (var file in locks) file.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenLockedPath(string path, bool directory)
    {
        // Attribute-only handles do not block directory replacement; include FILE_LIST_DIRECTORY.
        var handle = CreateFileW(path, directory ? 0x81u : 0x80000000u,
            directory ? FileShare.ReadWrite : FileShare.Read, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new IOException("Could not lock the VB-CABLE package path.", new Win32Exception(Marshal.GetLastWin32Error()));
            var attributes = File.GetAttributes(handle);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != (directory ? FileAttributes.Directory : 0))
                throw new InvalidDataException("VB-CABLE package paths must be ordinary files and directories, without links.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    public static NativeDriverInstallOutcome InstallerExited(int exitCode) => exitCode switch
    {
        0 => NativeDriverInstallOutcome.VerificationRequired,
        3010 or 1641 => NativeDriverInstallOutcome.RestartRequired,
        _ => throw new IOException($"The original VB-CABLE installer exited with code {exitCode}.")
    };
}
