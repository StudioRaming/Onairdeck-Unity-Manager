// This adapter supplies only the project path and warning sink for filesystem checks.
// Installer and PathSafety are the actual package sources, not copies or mocks.
// These checks do not exercise UnityWebRequest, imports, or script reloads.
namespace UnityEngine
{
    internal static class Application
    {
        public static string dataPath;
    }

    internal static class Debug
    {
        public static void LogWarning(object message) { }
    }
}
