using System.IO.Compression;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeDriverPackagePolicyTests
{
    private static readonly string[] PackageFiles =
    [
        "vbMmeCable64_win10.inf", "pin_in.ico", "pin_out.ico", "readme.txt",
        "vbaudio_cable_2003.cat", "vbaudio_cable_2003.sys", "vbaudio_cable_vista.cat", "vbaudio_cable_vista.sys",
        "vbaudio_cable_win7.cat", "vbaudio_cable_win7.sys", "vbaudio_cable_xp.cat", "vbaudio_cable_xp.sys",
        "vbaudio_cable64_2003.cat", "vbaudio_cable64_2003.sys", "vbaudio_cable64_vista.cat", "vbaudio_cable64_vista.sys",
        "vbaudio_cable64_win7.cat", "vbaudio_cable64_win7.sys", "vbaudio_cable64_win10.cat", "vbaudio_cable64_win10.sys",
        "vbaudio_cable64arm_win10.sys", "VBCABLE_ControlPanel.exe", "VBCABLE_Setup.exe", "VBCABLE_Setup_x64.exe",
        "vbMmeCable_2003.inf", "vbMmeCable_vista.inf", "vbMmeCable_win7.inf", "vbMmeCable_xp.inf",
        "vbMmeCable64_2003.inf", "vbMmeCable64_vista.inf", "vbMmeCable64_win7.inf"
    ];

    [Fact]
    public void AnUnpinnedArchiveCannotSupplyAnInstaller()
    {
        using var bytes = new MemoryStream([0x50, 0x4b, 0x03, 0x04]);
        Assert.Throws<InvalidDataException>(() => NativeDriverPackagePolicy.VerifyArchive(bytes));
    }

    [Fact]
    public void TheCompleteOriginalFlatFileSetIsAccepted()
    {
        using var bytes = Archive(PackageFiles);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        NativeDriverPackagePolicy.ValidateEntries(archive);
    }

    [Theory]
    [InlineData("../VBCABLE_Setup_x64.exe")]
    [InlineData("..\\VBCABLE_Setup_x64.exe")]
    [InlineData("/VBCABLE_Setup_x64.exe")]
    [InlineData("C:\\VBCABLE_Setup_x64.exe")]
    [InlineData("\\\\server\\VBCABLE_Setup_x64.exe")]
    [InlineData("sub/VBCABLE_Setup_x64.exe")]
    [InlineData("sub\\VBCABLE_Setup_x64.exe")]
    [InlineData("VBCABLE_Setup_x64.exe:payload")]
    [InlineData("VBCABLE_Setup_x64.exe.")]
    [InlineData("VBCABLE_Setup_x64.exe ")]
    [InlineData("another-installer.exe")]
    [InlineData("another-payload.dll")]
    public void UnsafeOrUnexpectedFilesAreRejectedBeforeExtraction(string name)
    {
        using var bytes = Archive(PackageFiles.Append(name));
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NativeDriverPackagePolicy.ValidateEntries(archive));
    }

    [Fact]
    public void MissingOriginalNoticesRejectThePackage()
    {
        using var bytes = Archive(PackageFiles.Where(name => name != "readme.txt"));
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NativeDriverPackagePolicy.ValidateEntries(archive));
    }

    [Theory]
    [InlineData("VBCABLE_Setup_x64.exe")]
    [InlineData("vbcable_setup_X64.EXE")]
    public void DuplicateWindowsNamesCannotReplaceTheTrustedInstaller(string duplicate)
    {
        using var bytes = Archive(PackageFiles.Append(duplicate));
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NativeDriverPackagePolicy.ValidateEntries(archive));
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))]
    [InlineData(0x400)]
    [InlineData(0x10)]
    public void ArchiveLinksOrDirectoriesCannotReachExtraction(int attributes)
    {
        using var bytes = Archive(PackageFiles, attributes);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NativeDriverPackagePolicy.ValidateEntries(archive));
    }

    [Fact]
    public void ExitZeroStillRequiresVerifiedDriverEnumeration()
    {
        Assert.Equal(NativeDriverInstallOutcome.VerificationRequired, NativeDriverPackagePolicy.InstallerExited(0));
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public void RebootExitCodesCannotContinueSetupWithoutARestart(int exitCode)
    {
        Assert.Equal(NativeDriverInstallOutcome.RestartRequired, NativeDriverPackagePolicy.InstallerExited(exitCode));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(-1)]
    public void OtherInstallerExitsRemainFailures(int exitCode)
    {
        Assert.Throws<IOException>(() => NativeDriverPackagePolicy.InstallerExited(exitCode));
    }

    [Fact]
    public void VerifiedFilesCannotBeRewrittenOrDeletedUntilTheInstallerFinishes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = ExtractedFixture();
        try
        {
            var locks = NativeDriverPackagePolicy.LockAndVerifyFiles(directory, Manifest());
            try
            {
                var installer = Path.Combine(directory, "VBCABLE_Setup_x64.exe");
                Assert.Throws<IOException>(() => File.WriteAllText(installer, "xyz"));
                Assert.Throws<IOException>(() => File.Delete(installer));
            }
            finally { foreach (var file in locks) file.Dispose(); }
            File.WriteAllText(Path.Combine(directory, "VBCABLE_Setup_x64.exe"), "xyz");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ADirectoryHandleKeepsTheValidatedInstallerPathFromBeingRenamed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = ExtractedFixture();
        try
        {
            using var handle = NativeDriverPackagePolicy.LockDirectory(directory);
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (Directory.Exists(directory + "-moved")) Directory.Delete(directory + "-moved", true);
        }
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("directory")]
    public void ChangedExtractedFilesCannotBeLaunchedAndFailureReleasesLocks(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = ExtractedFixture();
        var installer = Path.Combine(directory, "VBCABLE_Setup_x64.exe");
        try
        {
            if (change == "tamper") File.WriteAllText(installer, "xyz");
            if (change == "missing") File.Delete(installer);
            if (change == "extra") File.WriteAllText(Path.Combine(directory, "injected.dll"), "xyz");
            if (change == "directory") { File.Delete(installer); Directory.CreateDirectory(installer); }
            var error = Record.Exception(() =>
            {
                if (OperatingSystem.IsWindows()) NativeDriverPackagePolicy.LockAndVerifyFiles(directory, Manifest());
            });
            Assert.True(error is IOException or InvalidDataException, error?.ToString());
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "xyz");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AManifestCannotSupplyAnOutsideExecutablePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = ExtractedFixture();
        try
        {
            var files = Manifest().Select(file => file.Name == "VBCABLE_Setup_x64.exe"
                ? file with { Name = "../VBCABLE_Setup_x64.exe" } : file).ToArray();
            Assert.Throws<InvalidDataException>(() =>
            {
                if (OperatingSystem.IsWindows()) NativeDriverPackagePolicy.LockAndVerifyFiles(directory, files);
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    private static NativeDriverPackageFile[] Manifest() => PackageFiles.Select(name => new NativeDriverPackageFile(
        name, 3, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")).ToArray();

    private static string ExtractedFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "airflash-driver-policy-tests", Guid.NewGuid().ToString("N"));
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Driver test paths must not traverse a link.");
        Directory.CreateDirectory(directory);
        foreach (var name in PackageFiles) File.WriteAllBytes(Path.Combine(directory, name), [97, 98, 99]);
        return directory;
    }

    private static MemoryStream Archive(IEnumerable<string> names, int? attributes = null)
    {
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in names)
            {
                var entry = archive.CreateEntry(name);
                if (attributes is { } value && name == "VBCABLE_Setup_x64.exe") entry.ExternalAttributes = value;
            }
        bytes.Position = 0;
        return bytes;
    }
}
