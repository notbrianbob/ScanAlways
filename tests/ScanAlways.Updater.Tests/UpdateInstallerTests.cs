using Xunit;

namespace ScanAlways.Updater.Tests;

public class UpdateInstallerTests
{
    private static InstallComponent Hub(TempDirectory temp) =>
        new("Hub", temp.Combine("staging", "Hub"), temp.Combine("install", "Hub"), new[] { "appsettings.json" });

    private static void Stage(TempDirectory temp)
    {
        temp.Write("staging/Hub/ScanAlways.Protection.Service.exe", "hub new");
        temp.Write("staging/Hub/New.dll", "added");
        temp.Write("staging/Hub/appsettings.json", "release defaults");
        temp.Write("staging/Hub/runtimes/win/lib/x.dll", "x new");
    }

    private static void InstallOld(TempDirectory temp)
    {
        temp.Write("install/Hub/ScanAlways.Protection.Service.exe", "hub old");
        temp.Write("install/Hub/appsettings.json", "my settings");
        temp.Write("install/Hub/runtimes/win/lib/x.dll", "x old");
        temp.Write("install/Hub/my-notes.txt", "user file");
    }

    [Fact]
    public void Install_ReplacesPackageFiles_KeepsSettings_AndLeavesOtherFilesAlone()
    {
        using var temp = new TempDirectory();
        Stage(temp);
        InstallOld(temp);

        var result = UpdateInstaller.Install(new[] { Hub(temp) });

        Assert.Equal("hub new", File.ReadAllText(temp.Combine("install", "Hub", "ScanAlways.Protection.Service.exe")));
        Assert.Equal("x new", File.ReadAllText(temp.Combine("install", "Hub", "runtimes", "win", "lib", "x.dll")));
        Assert.Equal("added", File.ReadAllText(temp.Combine("install", "Hub", "New.dll")));
        Assert.Equal("my settings", File.ReadAllText(temp.Combine("install", "Hub", "appsettings.json")));
        Assert.Equal("release defaults", File.ReadAllText(temp.Combine("install", "Hub", "appsettings.json.default")));
        Assert.Equal("user file", File.ReadAllText(temp.Combine("install", "Hub", "my-notes.txt")));
        Assert.Single(result.PreservedFiles);
        Assert.Equal(2, result.FilesReplaced);
        Assert.Equal(2, result.FilesAdded);
        Assert.Empty(Directory.GetDirectories(temp.Combine("install", "Hub"), UpdateInstaller.BackupPrefix + "*"));
    }

    [Fact]
    public void Install_WritesTheReleaseSettings_WhenThereAreNone()
    {
        using var temp = new TempDirectory();
        Stage(temp);
        Directory.CreateDirectory(temp.Combine("install", "Hub"));

        UpdateInstaller.Install(new[] { Hub(temp) });

        Assert.Equal("release defaults", File.ReadAllText(temp.Combine("install", "Hub", "appsettings.json")));
        Assert.False(File.Exists(temp.Combine("install", "Hub", "appsettings.json.default")));
    }

    [Fact]
    public void Install_RestoresEveryOriginalFile_WhenAnyStepFails()
    {
        using var temp = new TempDirectory();
        Stage(temp);
        InstallOld(temp);
        // A directory where the package wants a file makes the copy fail midway,
        // after earlier files were already swapped.
        temp.Write("staging/Hub/zz-blocked.dll", "new");
        Directory.CreateDirectory(temp.Combine("install", "Hub", "zz-blocked.dll", "child"));

        // IOException on Linux, UnauthorizedAccessException on Windows.
        var error = Record.Exception(() => UpdateInstaller.Install(new[] { Hub(temp) }));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());

        Assert.Equal("hub old", File.ReadAllText(temp.Combine("install", "Hub", "ScanAlways.Protection.Service.exe")));
        Assert.Equal("x old", File.ReadAllText(temp.Combine("install", "Hub", "runtimes", "win", "lib", "x.dll")));
        Assert.Equal("my settings", File.ReadAllText(temp.Combine("install", "Hub", "appsettings.json")));
        Assert.False(File.Exists(temp.Combine("install", "Hub", "New.dll")));
        Assert.False(File.Exists(temp.Combine("install", "Hub", "appsettings.json.default")));
        Assert.Empty(Directory.GetDirectories(temp.Combine("install", "Hub"), UpdateInstaller.BackupPrefix + "*"));
    }

    [Fact]
    public void Install_UpdatesSeveralComponents_AndClearsOldBackups()
    {
        using var temp = new TempDirectory();
        Stage(temp);
        InstallOld(temp);
        temp.Write("staging/Tray/ScanAlways.Tray.exe", "tray new");
        temp.Write("install/Tray/ScanAlways.Tray.exe", "tray old");
        temp.Write("install/Tray/" + UpdateInstaller.BackupPrefix + "old/ScanAlways.Tray.exe", "stale backup");

        UpdateInstaller.Install(new[] { Hub(temp), new InstallComponent("Tray", temp.Combine("staging", "Tray"), temp.Combine("install", "Tray")) });

        Assert.Equal("tray new", File.ReadAllText(temp.Combine("install", "Tray", "ScanAlways.Tray.exe")));
        Assert.Empty(Directory.GetDirectories(temp.Combine("install", "Tray"), UpdateInstaller.BackupPrefix + "*"));
    }
}
