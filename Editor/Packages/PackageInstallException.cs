using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>A package could not be validated or installed. The message is written for the person using the window.</summary>
    internal sealed class PackageInstallException : Exception
    {
        public PackageInstallException(string message) : base(message) { }
        public PackageInstallException(string message, Exception inner) : base(message, inner) { }
    }
}
