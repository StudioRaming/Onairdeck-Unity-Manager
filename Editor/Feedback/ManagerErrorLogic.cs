using System;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Pure rules of the Manager's own error report: which exception is the Manager's bug, and the card it makes. No UnityEngine
    /// here, so the rules are checked headless; <see cref="ManagerErrorWatch"/> feeds them from the Console, unobserved tasks and
    /// the catch blocks of the window.
    /// </summary>
    internal static class ManagerErrorLogic
    {
        /// <summary>The Manager's package id, the product id its own cards are keyed on.</summary>
        public const string ManagerId = "com.studioraming.manager";
        public const string ManagerName = "Atelier Manager";
        /// <summary>An unexpected exception of the Manager's own code (FeedbackRequest.errorCode).</summary>
        public const string ExceptionCode = "manager.exception";
        /// <summary>The namespace every Manager type lives in; the runtime license check (OnAirDeck.License) is the products' code.</summary>
        public const string ManagerNamespace = "OnAirDeck.UnityManager.";
        public const int MaxStackLines = 20;

        /// <summary>True for a card of the Manager itself (its title does not say "installed").</summary>
        public static bool IsManagerReport(InstallErrorReport report)
        {
            return report != null && report.ProductId == ManagerId;
        }

        /// <summary>
        /// True when the first frame of <paramref name="stack"/> that is not the engine's or the runtime's belongs to the Manager:
        /// the exception was thrown by the Manager's code (or by Unity or .NET called from it). A product's code that comes first
        /// keeps the error the product's (<see cref="InstallErrorWatch"/> reports that one).
        /// </summary>
        public static bool IsManagerStack(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return false;
            // FirstProductFrame skips the first line (the message); a stack alone has none.
            var frame = InstallErrorLogic.FirstProductFrame("-\n" + stack);
            return frame.StartsWith(ManagerNamespace, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when <paramref name="e"/> is a bug and not a state the window already explains: a cancel, a timeout, an expired
        /// sign-in, a server or network failure (the API layer throws plain <see cref="Exception"/> or <see cref="ApiException"/>
        /// for those, with a message for the person) and the device-limit and update-required refusals are not bugs. Everything else
        /// (NullReferenceException, IOException, a JSON or argument error, a Manager install refusal) is.
        /// </summary>
        public static bool IsUnexpected(Exception e)
        {
            e = Unwrap(e);
            if (e == null) return false;
            if (e.GetType() == typeof(Exception)) return false;
            if (e is ApiException || e is SessionExpiredException) return false;
            if (e is OperationCanceledException || e is TimeoutException) return false;
            // Unity's way to leave OnGUI early; never an error.
            if (e.GetType().Name == "ExitGUIException") return false;
            return true;
        }

        /// <summary>The one inner exception of an AggregateException (a task's failure), else <paramref name="e"/>.</summary>
        public static Exception Unwrap(Exception e)
        {
            var aggregate = e as AggregateException;
            while (aggregate != null && aggregate.InnerExceptions.Count == 1)
            {
                e = aggregate.InnerExceptions[0];
                aggregate = e as AggregateException;
            }
            return e;
        }

        /// <summary>True when <paramref name="e"/> (or the one exception inside it) was thrown by the Manager's code.</summary>
        public static bool ThrownByManager(Exception e)
        {
            e = Unwrap(e);
            if (e == null) return false;
            for (var inner = e; inner != null; inner = inner.InnerException)
                if (IsManagerStack(inner.StackTrace)) return true;
            return false;
        }

        /// <summary>"step: Type: message" and the first stack lines, the text of one card entry (redacted later, before showing).</summary>
        public static string Describe(string step, Exception e)
        {
            e = Unwrap(e);
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(step)) sb.Append(step).Append(": ");
            sb.Append(e.GetType().Name).Append(": ").Append((e.Message ?? "").Trim());
            var stack = FirstLines(e.ToString(), MaxStackLines + 1);
            // e.ToString() starts with "Type: message"; keep only the frames after it.
            var newline = stack.IndexOf('\n');
            if (newline > 0) sb.Append('\n').Append(stack.Substring(newline + 1).TrimEnd());
            return sb.ToString();
        }

        /// <summary>The text of a Console exception: "Exception: condition" and its stack lines.</summary>
        public static string DescribeLog(string condition, string stack)
        {
            var text = "Exception: " + (condition ?? "").TrimEnd();
            stack = FirstLines(stack ?? "", MaxStackLines);
            return stack.Length > 0 ? text + "\n" + stack : text;
        }

        /// <summary>A card of the Manager itself with one entry; null for an empty text.</summary>
        public static InstallErrorReport NewReport(string version, string reason, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var report = new InstallErrorReport
            {
                Product = ManagerName,
                ProductId = ManagerId,
                Version = version ?? "",
                BaseCode = ExceptionCode,
                Reason = reason ?? "",
            };
            report.AddOther(text);
            return report;
        }

        /// <summary>The first <paramref name="maxLines"/> lines of <paramref name="text"/>.</summary>
        public static string FirstLines(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var index = -1;
            for (var line = 0; line < maxLines; line++)
            {
                index = text.IndexOf('\n', index + 1);
                if (index < 0) return text.TrimEnd();
            }
            return text.Substring(0, index).TrimEnd();
        }
    }
}
