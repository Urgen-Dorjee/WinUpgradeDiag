using System;
using System.IO;

namespace WinUpgradeDiag.Tests.Support
{
    /// <summary>A throwaway directory under the system temp folder, deleted on dispose.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinUpgradeDiagTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string relativePath, string content, DateTime? lastWriteUtc = null)
        {
            var full = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            System.IO.File.WriteAllText(full, content);
            if (lastWriteUtc.HasValue)
            {
                System.IO.File.SetLastWriteTimeUtc(full, lastWriteUtc.Value);
            }
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception)
            {
                // Best effort; a leftover temp folder is not a test failure.
            }
        }
    }
}
