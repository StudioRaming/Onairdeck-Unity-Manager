using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public static class OnAirDeckPackageBootstrap
{
    private static AddRequest _request;
    private static double _deadline;

    // Launch the Editor with -executeMethod OnAirDeckPackageBootstrap.Install, without -quit.
    public static void Install()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "-onairdeckPackage")
            {
                _request = Client.Add("file:" + args[i + 1].Replace('\\', '/'));
                _deadline = EditorApplication.timeSinceStartup + 180;
                EditorApplication.update += Poll;
                return;
            }
        Debug.LogError("Missing -onairdeckPackage <local package directory>.");
        EditorApplication.Exit(1);
    }

    private static void Poll()
    {
        if (!_request.IsCompleted)
        {
            if (EditorApplication.timeSinceStartup > _deadline) EditorApplication.Exit(2);
            return;
        }
        EditorApplication.update -= Poll;
        if (_request.Status == StatusCode.Success)
        {
            Debug.Log("Installed " + _request.Result.name + "@" + _request.Result.version);
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError(_request.Error.message);
            EditorApplication.Exit(1);
        }
    }
}
