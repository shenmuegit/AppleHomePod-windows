using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using AirFlash.Core;
using Microsoft.Win32.SafeHandles;
namespace AirFlash.App.Services;

internal static class NativeDriverInstaller
{
    internal static bool IsEmbedded => typeof(NativeDriverInstaller).Assembly
        .GetManifestResourceInfo(NativeDriverPackagePolicy.ResourceName) is not null;

    internal static Task<NativeDriverInstallOutcome> InstallAsync(CancellationToken cancellation) =>
        Task.Run(() => Install(cancellation), cancellation);

    private static NativeDriverInstallOutcome Install(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var original = typeof(NativeDriverInstaller).Assembly.GetManifestResourceStream(NativeDriverPackagePolicy.ResourceName)
            ?? throw new FileNotFoundException("The integrated original VB-CABLE package is missing. Rebuild the complete AppleHome setup package.");
        NativeDriverPackagePolicy.VerifyArchive(original);
        using var installMutex = new Mutex(false, @"Local\AppleHome-VBCABLE-Install");
        try
        {
            if (!installMutex.WaitOne(0)) throw new IOException("Another AppleHome driver installation is still running.");
        }
        catch (AbandonedMutexException) { }
        var directories = new List<SafeFileHandle>();
        IReadOnlyList<FileStream> files = [];
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData)) throw new IOException("The Windows application cache directory is unavailable.");
            var cache = Path.GetFullPath(localData);
            var parents = new Stack<string>();
            for (var parent = new DirectoryInfo(cache); parent is not null; parent = parent.Parent) parents.Push(parent.FullName);
            while (parents.TryPop(out var parent)) directories.Add(NativeDriverPackagePolicy.LockDirectory(parent));
            foreach (var name in new[] { AppPaths.Name, "drivers", NativeDriverPackagePolicy.ArchiveSha256, Guid.NewGuid().ToString("N") })
            {
                cancellation.ThrowIfCancellationRequested();
                cache = Path.Combine(cache, name);
                Directory.CreateDirectory(cache);
                directories.Add(NativeDriverPackagePolicy.LockDirectory(cache));
            }
            using var archiveFile = new FileStream(Path.Combine(cache, NativeDriverPackagePolicy.ArchiveFileName),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            original.Position = 0;
            original.CopyTo(archiveFile);
            archiveFile.Flush(flushToDisk: true);
            var manifest = NativeDriverPackagePolicy.VerifyArchive(archiveFile);
            var extracted = Path.Combine(cache, "files");
            Directory.CreateDirectory(extracted);
            directories.Add(NativeDriverPackagePolicy.LockDirectory(extracted));
            archiveFile.Position = 0;
            using (var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: true))
                foreach (var entry in archive.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var source = entry.Open();
                    using var target = new FileStream(Path.Combine(extracted, entry.FullName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    source.CopyTo(target);
                    target.Flush(flushToDisk: true);
                }
            files = NativeDriverPackagePolicy.LockAndVerifyFiles(extracted, manifest);
            cancellation.ThrowIfCancellationRequested();
            using var identity = WindowsIdentity.GetCurrent();
            var start = new ProcessStartInfo(Path.Combine(extracted, NativeDriverPackagePolicy.InstallerFileName))
            {
                Arguments = "-i -h", WorkingDirectory = extracted, UseShellExecute = true,
                Verb = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process process;
            cancellation.ThrowIfCancellationRequested();
            try { process = Process.Start(start) ?? throw new IOException("Could not start the original VB-CABLE installer."); }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            { throw new OperationCanceledException("Windows administrator approval for the VB-CABLE installation was cancelled.", error, cancellation); }
            using (process)
            {
                // The original kernel installer must finish; cancellation never terminates it.
                process.WaitForExit();
                return NativeDriverPackagePolicy.InstallerExited(process.ExitCode);
            }
        }
        finally
        {
            foreach (var file in files) file.Dispose();
            for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            installMutex.ReleaseMutex();
        }
    }
}
