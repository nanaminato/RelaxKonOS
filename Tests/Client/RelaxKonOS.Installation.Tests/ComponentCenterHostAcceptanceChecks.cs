internal static class ComponentCenterHostAcceptanceChecks
{
    public static void Run()
    {
        var valid = new[] { "--component-center-host", "https://test.invalid/", "https://test.invalid/", "acceptance-user",
            "sing-box-acceptance", "older", "newer", Path.GetFullPath("artifacts/component-center-host") };
        var options = ComponentCenterHostAcceptance.Parse(valid);
        Check(options.Server.Scheme == "https" && options.Older != options.Newer, "explicit HTTPS test target and distinct release identities are required");
        foreach (var changed in new[] {
            (1, "http://test.invalid/"), (2, "https://another.invalid/"), (1, "https://user:secret@test.invalid/"),
            (1, "https://test.invalid/?token=x"), (1, "https://test.invalid/#fragment"), (1, "https://test.invalid/other/"),
            (4, "source/../other"), (4, ""), (5, "newer") })
        {
            var candidate = valid.ToArray(); candidate[changed.Item1] = changed.Item2;
            if (changed.Item1 == 1) candidate[2] = candidate[1];
            try { ComponentCenterHostAcceptance.Parse(candidate); throw new Exception("Unsafe acceptance arguments admitted"); }
            catch (ArgumentException) { Check(true, "unsafe target, confirmation, source or lifecycle selection is rejected before network access"); }
        }
        try { ComponentCenterHostAcceptance.Parse(valid[..^1]); throw new Exception("Incomplete invocation admitted"); }
        catch (ArgumentException) { Check(true, "incomplete invocation cannot log in or create a host operation"); }
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS: Host acceptance gate " + label); }
}
