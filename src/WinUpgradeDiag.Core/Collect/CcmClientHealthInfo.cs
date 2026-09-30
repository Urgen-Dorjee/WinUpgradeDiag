using System;
using System.Collections.Generic;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// The state of the ConfigMgr client itself.
    /// <para>
    /// Collected because a broken client is a different failure from a broken upgrade, and the
    /// symptoms overlap enough to waste a day. "Software Center shows Installing forever" can mean
    /// an orphaned task sequence, or it can mean the client cannot reach its management point; the
    /// Configuration Manager control panel applet refusing to open means the client is not running
    /// at all. Before this, a technician chased those by hand — service state, WMI namespaces,
    /// registry, four separate logs — one PowerShell command at a time.
    /// </para>
    /// </summary>
    public sealed class CcmClientHealthInfo
    {
        /// <summary>Whether C:\Windows\CCM exists at all, which is the crudest "is it installed".</summary>
        public bool ClientFolderExists { get; set; }

        public string ClientVersion { get; set; }

        /// <summary>SMS Agent Host. Running, Stopped, or absent entirely.</summary>
        public string CcmExecState { get; set; }
        public string CcmExecStartMode { get; set; }
        public bool CcmExecInstalled { get; set; }

        /// <summary>
        /// Whether root\ccm answers a query. The decisive check: the applet, Software Center and
        /// ccmsetup itself all go through this provider, so when it fails to load they all fail
        /// together and the cause is invisible from any one of them.
        /// </summary>
        public bool CcmNamespaceResponds { get; set; }

        /// <summary>The error root\ccm returned, verbatim, when it did not answer.</summary>
        public string CcmNamespaceError { get; set; }

        /// <summary>HRESULT from the namespace probe, e.g. 0x80041013 (provider load failure).</summary>
        public string CcmNamespaceHResult { get; set; }

        /// <summary>Assigned site code. Empty on a client that installed but never registered.</summary>
        public string SiteCode { get; set; }

        /// <summary>Management point the client is actually using.</summary>
        public string ManagementPoint { get; set; }

        /// <summary>
        /// Whether the client can find a management point without being told one. False here is
        /// why a client installed with only /mp: never registers: that switch sets the download
        /// source, not the assignment, so lookup has to come from AD or DNS.
        /// </summary>
        public bool? LookupMpAvailable { get; set; }

        /// <summary>WMI repository consistency, from the same check winmgmt /verifyrepository makes.</summary>
        public bool? WmiRepositoryConsistent { get; set; }

        /// <summary>Namespaces a healthy client owns, and whether each is present.</summary>
        public IDictionary<string, bool> Namespaces { get; set; } = new Dictionary<string, bool>();

        public IList<string> Errors { get; } = new List<string>();

        /// <summary>True when the client is installed but cannot serve a query — the broken middle.</summary>
        public bool InstalledButUnresponsive =>
            ClientFolderExists && !CcmNamespaceResponds;
    }
}
