using System;
using OnAirDeck.UnityManager;

// Device-limit client checks that need no Unity: the real DeviceIdentityCore and ApiErrorParser sources are compiled in.
// The 409/426 bodies follow the contract in Ops work/mgr-device-ui-20261027/contract.md.
internal static class DeviceChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("device hash matches a known SHA-256 vector", HashVector);
        check("device hash is empty without a usable hardware id", HashUnusable);
        check("device name: truncated to 64 before escaping, then escaped", NameTruncation);
        check("device name: surrogate pair is never split", NameSurrogates);
        check("device platform mapping", PlatformMapping);
        check("409 device_limit parses licenses and devices", Parse409);
        check("409 device_limit tolerates missing arrays and fields", Parse409Sparse);
        check("426 maps to ManagerUpdateRequiredException", Parse426);
        check("other failures keep status and code, never put the body in Message", ParseOther);
        check("non-JSON, empty and transport failures", ParseNonJson);
        check("error code reader", ReadCode);
    }

    private static void HashVector()
    {
        // python: hashlib.sha256(b"onairdeck-device-v1|abc123").hexdigest()
        Equal("63b34489a5d1fccc3313591f00d996d75cf72fd791a406efb1115cb315f450f9", DeviceIdentityCore.Hash("abc123"));
        Equal("42c3ea34377613f8928f8a236cdbd95507fb52a5d6a7f540f1c92cc1f4d4d60e", DeviceIdentityCore.Hash("기기-ID"));
        Equal(64, DeviceIdentityCore.Hash("x").Length);
    }

    private static void HashUnusable()
    {
        Equal("", DeviceIdentityCore.Hash(null));
        Equal("", DeviceIdentityCore.Hash(""));
        Equal("", DeviceIdentityCore.Hash("n/a"));
    }

    private static void NameTruncation()
    {
        var long70 = new string('a', 70);
        Equal(new string('a', 64), DeviceIdentityCore.Name(long70));
        // Cut first: 64 spaces escape to 192 characters, not 64.
        Equal(192, DeviceIdentityCore.Name(new string(' ', 70)).Length);
        Equal("My%20PC%20%231", DeviceIdentityCore.Name("My PC #1"));
        Equal("%ED%99%8D%EA%B8%B8%EB%8F%99", DeviceIdentityCore.Name("홍길동"));
        Equal("", DeviceIdentityCore.Name(null));
    }

    private static void NameSurrogates()
    {
        // 63 letters, then an emoji (2 chars): the cut at 64 would split it, so it is dropped whole.
        var name = new string('a', 63) + "\U0001F600" + "tail";
        Equal(new string('a', 63), DeviceIdentityCore.Name(name));
        // An emoji that fits is kept.
        Equal(new string('a', 62) + "%F0%9F%98%80", DeviceIdentityCore.Name(new string('a', 62) + "\U0001F600" + "tail"));
        // A lone surrogate in the source must not throw.
        Equal("ab", DeviceIdentityCore.Name("a\uD83Db"));
    }

    private static void PlatformMapping()
    {
        Equal("Windows", DeviceIdentityCore.Platform("Windows"));
        Equal("macOS", DeviceIdentityCore.Platform("MacOSX"));
        Equal("Linux", DeviceIdentityCore.Platform("Linux"));
        Equal("", DeviceIdentityCore.Platform("Other"));
        Equal("", DeviceIdentityCore.Platform(null));
    }

    private const string Body409 =
        "{\"error\":\"device_limit\",\"message\":\"This license is already used on the maximum number of devices.\","
        + "\"licenses\":[{\"license_id\":\"L1\",\"name\":\"MingToon\",\"max_devices\":3,\"used\":3}],"
        + "\"devices\":[{\"device_id\":\"D1\",\"name\":\"Desk\",\"platform\":\"Windows\",\"first_seen_at\":\"2026-10-01T00:00:00Z\","
        + "\"last_seen_at\":\"2026-10-20T00:00:00Z\",\"this_device\":false,\"licenses\":[{\"license_id\":\"L1\",\"name\":\"MingToon\"}]},"
        + "{\"device_id\":\"D2\",\"name\":\"Laptop\",\"platform\":\"macOS\",\"first_seen_at\":\"\",\"last_seen_at\":\"\",\"this_device\":true,\"licenses\":[]}]}";

    private static void Parse409()
    {
        var e = ApiErrorParser.ToException(409, Body409, "Preparing the download", null) as DeviceLimitException;
        Require(e != null);
        Equal(409L, e.Status);
        Equal("device_limit", e.Code);
        Equal("This license is already used on the maximum number of devices.", e.Message);
        Equal(1, e.Licenses.Length);
        Equal("L1", e.Licenses[0].license_id);
        Equal(3, e.Licenses[0].max_devices);
        Equal(3, e.Licenses[0].used);
        Equal(2, e.Devices.Length);
        Equal("Desk", e.Devices[0].name);
        Equal(false, e.Devices[0].this_device);
        Equal("MingToon", e.Devices[0].licenses[0].name);
        Equal(true, e.Devices[1].this_device);
        Equal(0, e.Devices[1].licenses.Length);
        Equal(Body409, e.ResponseBody);
    }

    private static void Parse409Sparse()
    {
        var e = ApiErrorParser.ToException(409, "{\"error\":\"device_limit\"}", "x", null) as DeviceLimitException;
        Require(e != null);
        Equal(0, e.Licenses.Length);
        Equal(0, e.Devices.Length);
        Require(e.Message.Length > 0);
        e = ApiErrorParser.ToException(409, "{\"error\":\"device_limit\",\"licenses\":\"oops\",\"devices\":[{\"name\":\"A\"},5,null]}", "x", null) as DeviceLimitException;
        Require(e != null);
        Equal(0, e.Licenses.Length);
        Equal(1, e.Devices.Length);
        Equal("A", e.Devices[0].name);
        Equal("", e.Devices[0].device_id);
        Equal(0, e.Devices[0].licenses.Length);
        // 409 with another code is not a device limit.
        var other = ApiErrorParser.ToException(409, "{\"error\":\"conflict\"}", "x", null);
        Require(!(other is DeviceLimitException));
        Equal("conflict", other.Code);
    }

    private static void Parse426()
    {
        var e = ApiErrorParser.ToException(426, "{\"error\":\"manager_update_required\",\"message\":\"Update the Atelier Manager to download this item.\"}", "x", null);
        Require(e is ManagerUpdateRequiredException);
        Equal(426L, e.Status);
        Equal("Update the Atelier Manager to download this item.", e.Message);
        // Status alone is enough, even for an unreadable body.
        Require(ApiErrorParser.ToException(426, "<html>", "x", null) is ManagerUpdateRequiredException);
    }

    private static void ParseOther()
    {
        var body = "{\"error\":\"not_found\",\"detail\":{\"secret\":1}}";
        var e = ApiErrorParser.ToException(404, body, "Loading purchases", null);
        Require(e.GetType() == typeof(ApiException));
        Equal(404L, e.Status);
        Equal("not_found", e.Code);
        Equal(body, e.ResponseBody);
        Equal("Loading purchases failed: HTTP 404", e.Message);
        Require(!e.Message.Contains("{"));
        var withMessage = ApiErrorParser.ToException(400, "{\"error\":\"bad\",\"message\":\"Missing item.\"}", "Preparing the download", null);
        Equal("Preparing the download failed: Missing item.", withMessage.Message);
        Equal(new string('m', 200), ApiErrorParser.ToException(500, "{\"message\":\"" + new string('m', 500) + "\"}", "x", null).Message.Substring("x failed: ".Length));
    }

    private static void ParseNonJson()
    {
        var html = ApiErrorParser.ToException(502, "<html><body>Bad gateway</body></html>", "Loading purchases", null);
        Equal("Loading purchases failed: HTTP 502", html.Message);
        Equal("", html.Code);
        Equal("Loading purchases failed: HTTP 500", ApiErrorParser.ToException(500, "", "Loading purchases", null).Message);
        Equal("Loading purchases failed: HTTP 500", ApiErrorParser.ToException(500, null, "Loading purchases", null).Message);
        Equal("Loading purchases failed: Cannot connect to destination host", ApiErrorParser.ToException(0, null, "Loading purchases", "Cannot connect to destination host").Message);
        Equal("Loading purchases failed: no answer from the server", ApiErrorParser.ToException(0, null, "Loading purchases", null).Message);
        // A JSON array or scalar is not an error object.
        Equal("Loading purchases failed: HTTP 500", ApiErrorParser.ToException(500, "[1,2]", "Loading purchases", null).Message);
    }

    private static void ReadCode()
    {
        Equal("rate_limited", ApiErrorParser.ReadErrorCode("{\"error\":\"rate_limited\"}"));
        Equal(null, ApiErrorParser.ReadErrorCode("not json"));
        Equal(null, ApiErrorParser.ReadErrorCode(""));
        Equal(null, ApiErrorParser.ReadErrorCode("{\"other\":1}"));
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
