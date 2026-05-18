namespace SyncthingPause.Tests;

// Covers P1-A2 from Run 2b: the original `isUnc = path.StartsWith(@"\\")` guard
// in OpenFolder only caught backslash UNC. Forward-slash and mixed-slash UNC
// variants (`//srv/s`, `\/srv\s`, `/\srv\s`) were still returning
// IsPathFullyQualified=true and flowing to Directory.Exists (20-30 s SMB
// timeout) + Process.Start(UseShellExecute=true), reopening the NTLM-hash-leak
// threat model. IsUncPath replaces the prefix check with a character-pair
// predicate at positions [0] and [1] to close all four slash permutations.
[TestClass]
public class UncPathDetectionTests
{
    [DataTestMethod]
    [DataRow(@"\\server\share", DisplayName = "UNC backslash-backslash")]
    [DataRow("//server/share", DisplayName = "UNC forward-forward")]
    [DataRow(@"\/server\share", DisplayName = "UNC back-forward mixed")]
    [DataRow(@"/\server\share", DisplayName = "UNC forward-back mixed")]
    [DataRow(@"\\?\C:\", DisplayName = "UNC DOS-device prefix (must still reject)")]
    public void IsUncPath_RejectsAllSlashPermutations(string path)
    {
        Assert.IsTrue(TrayApplicationContext.IsUncPath(path),
            $"Expected '{path}' to be classified as UNC.");
    }

    [DataTestMethod]
    [DataRow(@"C:\Users\Tester\Syncthing", DisplayName = "Valid local drive-letter path")]
    [DataRow(@"D:\", DisplayName = "Drive root")]
    [DataRow("relative/path", DisplayName = "Relative forward-slash (not UNC)")]
    [DataRow(@"relative\path", DisplayName = "Relative backslash (not UNC)")]
    [DataRow("", DisplayName = "Empty string")]
    [DataRow(@"\", DisplayName = "Single backslash")]
    [DataRow("/", DisplayName = "Single forward slash")]
    public void IsUncPath_AcceptsLocalAndRelativePaths(string path)
    {
        Assert.IsFalse(TrayApplicationContext.IsUncPath(path),
            $"Expected '{path}' to NOT be classified as UNC.");
    }
}
