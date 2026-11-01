using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>A package could not be validated or installed. The message is written for the person using the window.</summary>
    internal class PackageInstallException : Exception
    {
        public PackageInstallException(string message) : base(message) { }
        public PackageInstallException(string message, Exception inner) : base(message, inner) { }
    }
    /// <summary>A download (UnityWebRequest) failed. ResponseCode 0 means no answer: the connection, not the server, failed.</summary>
    internal sealed class DownloadFailedException : Exception
    {
        public long ResponseCode { get; private set; }
        public DownloadFailedException(string message, long responseCode) : base(message) { ResponseCode = responseCode; }
    }
    /// <summary>
    /// An add-on of MingToon Core (OnAirDeckConfig.CoreAddons) was asked for while the core is neither installed nor part of the run, and this
    /// account has no core to install first. Nothing was changed.
    /// </summary>
    internal sealed class AddonNeedsCoreException : PackageInstallException
    {
        public string Core { get; private set; }
        public AddonNeedsCoreException(string core, string message) : base(message) { Core = core ?? ""; }
    }
    /// <summary>A self-update stopped before anything changed because the moment turned unsafe (play mode, a compile, an install); it is tried again later.</summary>
    internal sealed class SelfUpdateDeferredException : PackageInstallException
    {
        public SelfUpdateDeferredException(string reason) : base(reason) { }
    }
}
