using System;
using System.Reflection;

namespace WinUpgradeDiag.Cli
{
    /// <summary>
    /// Loads the Core assembly out of this executable, so the console head is also a single file.
    /// <para>
    /// This head is the one that gets run remotely, from a share, or through a ConfigMgr Run
    /// Script. Every one of those paths copies a file somewhere and executes it; a second file
    /// that has to travel with it is a step that gets missed, and the failure is a
    /// <c>FileNotFoundException</c> that explains nothing to the technician reading it.
    /// </para>
    /// </summary>
    internal static class EmbeddedAssemblyLoader
    {
        private const string ResourcePrefix = "WinUpgradeDiag.Cli.Dependencies.";

        internal static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;
                var self = typeof(EmbeddedAssemblyLoader).Assembly;

                using (var stream = self.GetManifestResourceStream(ResourcePrefix + name + ".dll"))
                {
                    if (stream == null)
                    {
                        return null;   // not ours; let the runtime keep probing
                    }

                    var bytes = new byte[stream.Length];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var chunk = stream.Read(bytes, read, bytes.Length - read);
                        if (chunk <= 0)
                        {
                            break;
                        }
                        read += chunk;
                    }

                    return Assembly.Load(bytes);
                }
            }
            catch (Exception)
            {
                // Returning null lets normal probing produce its own error, which is more
                // informative than an exception thrown from inside a resolve handler.
                return null;
            }
        }
    }
}
