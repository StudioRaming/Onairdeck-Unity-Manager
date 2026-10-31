using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>A package could not be validated or installed. The message is written for the person using the window.</summary>
    internal class PackageInstallException : Exception
    {
        public PackageInstallException(string message) : base(message) { }
        public PackageInstallException(string message, Exception inner) : base(message, inner) { }
    }
    /// <summary>A self-update stopped before anything changed because the moment turned unsafe (play mode, a compile, an install); it is tried again later.</summary>
    internal sealed class SelfUpdateDeferredException : PackageInstallException
    {
        public SelfUpdateDeferredException(string reason) : base(reason) { }
    }
}
