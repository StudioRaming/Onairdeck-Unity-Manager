using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OnAirDeck.UnityManager.EditModeTests
{
    /// <summary>
    /// U2 in a real Editor: Unity's own Console hook, the unobserved-task hook and the card list. Each test dismisses the card it
    /// made, so the window that a card opens on the next delayCall finds nothing to show.
    /// </summary>
    public class ManagerErrorWatchTests
    {
        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            ManagerErrorWatch.Drain();
            ClearCards();
        }

        [TearDown]
        public void TearDown()
        {
            ManagerErrorWatch.Drain();
            ClearCards();
            LogAssert.ignoreFailingMessages = false;
        }

        [Test]
        public void AnExceptionOfTheManagerInTheConsole_OpensTheManagerCard_Redacted()
        {
            var marker = Guid.NewGuid().ToString("N").Substring(0, 8);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Debug.LogException(Catch(() => ThrowsInTheManager.Now("console " + marker + " at " + home + "\\cache.json")));
            ManagerErrorWatch.Drain();

            var card = InstallErrorReporter.Current;
            Assert.IsNotNull(card, "no card");
            Assert.IsTrue(ManagerErrorLogic.IsManagerReport(card));
            Assert.AreEqual(ManagerErrorLogic.ExceptionCode, card.ErrorCode);
            var shot = card.TakeSnapshot(FeedbackClient.MaxConsoleLogBytes, FeedbackClient.MaxMessage, FeedbackClient.LocalContext());
            StringAssert.Contains(marker, shot.Excerpt);
            StringAssert.Contains("<path>/cache.json", shot.Excerpt);
            StringAssert.DoesNotContain(Environment.UserName, shot.Excerpt);
        }

        [Test]
        public void TheSameExceptionOnEveryRepaint_IsOneEntry()
        {
            var e = Catch(() => ThrowsInTheManager.Now("repaint " + Guid.NewGuid().ToString("N")));
            for (var i = 0; i < 30; i++) Debug.LogException(e);
            ManagerErrorWatch.Drain();
            Assert.IsNotNull(InstallErrorReporter.Current);
            Assert.AreEqual(1, InstallErrorReporter.Current.Count);
        }

        [Test]
        public void AnExceptionOfAProduct_DoesNotOpenTheManagerCard()
        {
            Debug.LogException(Catch(() => SomeSeller.Product.ProductCode.Throw("product " + Guid.NewGuid().ToString("N"))));
            ManagerErrorWatch.Drain();
            Assert.IsNull(InstallErrorReporter.Current);
        }

        [Test]
        public void ACaughtNetworkFailure_StaysAMessage_ACaughtBugOpensTheCard()
        {
            Assert.IsNull(ManagerErrorWatch.OfferCaught("Loading the product list", new Exception("Loading failed: Cannot resolve destination host")));
            Assert.IsNull(ManagerErrorWatch.OfferCaught("Loading the product list", new ApiException("Loading failed (503).", 503, "", "")));
            Assert.IsNull(InstallErrorReporter.Current);

            var report = ManagerErrorWatch.OfferCaught("Loading the product list", Catch(() => ThrowsInTheManager.Now("caught " + Guid.NewGuid().ToString("N"))));
            Assert.IsNotNull(report);
            Assert.IsNotNull(InstallErrorReporter.Current);
            StringAssert.StartsWith("Loading the product list: NullReferenceException", InstallErrorReporter.Current.Items[0].Text);
            // The button next to the red message reopens the same entry, not a second one.
            Assert.AreEqual(report.Items[0].Signature, InstallErrorReporter.Current.Items[0].Signature);
        }

        [Test]
        public void AFaultedTaskNobodyAwaited_OpensTheCard()
        {
            var marker = "unobserved " + Guid.NewGuid().ToString("N");
            StartAndForget(marker);
            for (var i = 0; i < 5 && InstallErrorReporter.Current == null; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                ManagerErrorWatch.Drain();
            }
            Assert.IsNotNull(InstallErrorReporter.Current, "UnobservedTaskException did not reach the card");
            StringAssert.Contains(marker, InstallErrorReporter.Current.Items[0].Text);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartAndForget(string marker)
        {
            var task = Task.Run(() => ThrowsInTheManager.Now(marker));
            // Wait without Wait(): a Wait that throws, or reading task.Exception, would observe the failure.
            while (!task.IsCompleted) System.Threading.Thread.Sleep(1);
        }

        private static void ClearCards()
        {
            for (var i = 0; i < 20 && InstallErrorReporter.Current != null; i++) InstallErrorReporter.Dismiss(InstallErrorReporter.Current);
        }

        private static Exception Catch(Action action)
        {
            try { action(); }
            catch (Exception e) { return e; }
            throw new Exception("Nothing was thrown");
        }
    }

    internal static class ThrowsInTheManager
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Now(string message) { throw new NullReferenceException(message); }
    }
}

namespace SomeSeller.Product
{
    internal static class ProductCode
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw(string message) { throw new InvalidOperationException(message); }
    }
}
