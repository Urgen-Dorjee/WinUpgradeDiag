using System;
using System.IO;
using System.Reflection;

namespace WinUpgradeDiag.App
{
    /// <summary>
    /// Loads the application's own dependencies out of the executable, so the tool ships as a
    /// single file.
    /// <para>
    /// A technician copies this onto a machine that is already broken, often over a remote session
    /// or from a USB stick. "Copy these two files and keep them together" is a step that goes
    /// wrong, and a missing side-by-side DLL fails at launch with a message that explains nothing.
    /// It also halves the code-signing and allow-listing work: one file, one hash.
    /// </para>
    /// </summary>
    internal static class EmbeddedAssemblyLoader
    {
        private const string ResourcePrefix = "WinUpgradeDiag.App.Dependencies.";

        /// <summary>
        /// Installs the resolver. Must run before any type from an embedded assembly is used, which
        /// is why it is called from a static constructor rather than from startup code.
        /// </summary>
        internal static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;
                var resource = ResourcePrefix + name + ".dll";

                var self = typeof(EmbeddedAssemblyLoader).Assembly;
                using (var stream = self.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        return null;   // not ours; let the runtime keep looking
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
                // Returning null lets the normal probing path produce its own error, which is more
                // informative than an exception thrown from inside a resolve handler.
                return null;
            }
        }
    }
}
