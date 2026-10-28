using System;

namespace OnAirDeck.License
{
    /// <summary>Why a license check passed or failed. Branch on this, not on Message.</summary>
    public enum LicenseStatus
    {
        Valid = 0,
        /// <summary>No Atelier Manager session marker: nobody is signed in on this computer.</summary>
        NotSignedIn,
        /// <summary>No onairdeck.license for this product under Assets/StudioRaming_Onairdeck/.</summary>
        LicenseMissing,
        /// <summary>The file is not "&lt;payload&gt;.&lt;signature&gt;", or the payload is not the expected JSON.</summary>
        LicenseMalformed,
        /// <summary>The signature does not match OnAirDeck's public key (edited or forged file).</summary>
        InvalidSignature,
        /// <summary>Not issued by OnAirDeck for Unity Manager, or an unknown payload version.</summary>
        WrongIssuer,
        /// <summary>The license belongs to another product (or no product id was given to the check).</summary>
        WrongProduct,
        /// <summary>The license was issued to a different OnAirDeck account than the one signed in.</summary>
        UserMismatch,
        /// <summary>Issued in the future, beyond the allowed clock skew.</summary>
        NotYetValid,
        /// <summary>exp has passed. The Manager re-issues licenses for installed products when its window opens.</summary>
        Expired
    }

    /// <summary>Outcome of OnAirDeckLicense.Check / LicenseVerifier.Verify.</summary>
    public sealed class LicenseCheckResult
    {
        public LicenseStatus Status { get; internal set; }
        public bool IsValid { get { return Status == LicenseStatus.Valid; } }

        /// <summary>Short English explanation, suitable for a HelpBox or dialog.</summary>
        public string Message { get; internal set; }

        /// <summary>Absolute path of the license file that was checked, or null when none was read.</summary>
        public string LicensePath { get; internal set; }

        // Payload fields, filled once the signature has been verified (null / 0 otherwise).
        public string UserId { get; internal set; }
        public string ProductId { get; internal set; }
        public string ProductName { get; internal set; }
        public long IssuedAtUnix { get; internal set; }
        public long ExpiresAtUnix { get; internal set; }

        public DateTimeOffset? ExpiresAt
        {
            get { return ExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix) : (DateTimeOffset?)null; }
        }

        internal static LicenseCheckResult Fail(LicenseStatus status, string message)
        {
            return new LicenseCheckResult { Status = status, Message = message };
        }

        internal LicenseCheckResult With(LicenseStatus status, string message)
        {
            Status = status;
            Message = message;
            return this;
        }

        public override string ToString()
        {
            return Status + ": " + Message;
        }
    }
}
