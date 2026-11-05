namespace OnAirDeck.UnityManager
{
    /// <summary>The screen the Manager window draws, picked from its state. Plain C#, so the order is checked outside Unity.</summary>
    internal enum ManagerScreen { Offline, RequiredUpdate, SigningIn, Timeout, SignedOut, DeviceLimit, Signed }

    internal static class ManagerScreenRoute
    {
        /// <summary>
        /// The offline screen goes first: it needs neither a sign-in nor the network, so a forced update or a device limit (both
        /// online states) never hides the local licence-file import (review R34-02).
        /// </summary>
        internal static ManagerScreen Pick(bool offlineView, bool mustUpdate, bool signingIn, bool signedIn, bool timedOut, bool deviceLimit)
        {
            if (offlineView) return ManagerScreen.Offline;
            if (mustUpdate) return ManagerScreen.RequiredUpdate;
            if (signingIn) return ManagerScreen.SigningIn;
            if (!signedIn) return timedOut ? ManagerScreen.Timeout : ManagerScreen.SignedOut;
            if (deviceLimit) return ManagerScreen.DeviceLimit;
            return ManagerScreen.Signed;
        }
    }
}
