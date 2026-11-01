using Android.App;
using Android.OS;
using Android.Widget;
using XboxIsoLibrary.Analysis;
using XboxIsoLibrary.IO;
using XboxIsoLibrary.Xdvdfs;

namespace AndroidSmoke;

/// <summary>
/// Calls into the library on Android without needing a disc image: output naming, the FATX split
/// size, the built-in shrink rules (parsed from JSON at run time) and glob matching.
/// </summary>
[Activity(Label = "Xbox ISO library smoke test", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var rules = ShrinkRuleSet.BuiltIn;
        SetContentView(new TextView(this)
        {
            Text = $"CCI extension: {ImageWriter.ExtensionFor(OutputFormat.Cci)}\n"
                   + $"FATX split size: {XdvdfsConstants.FatxSplitSize:N0} bytes\n"
                   + $"Built-in rules: {rules.Global.Count} global\n"
                   + $"Glob '/$SystemUpdate/**' matches update file: "
                   + $"{new GlobMatcher(["/$SystemUpdate/**"]).IsMatch("/$SystemUpdate/xboxupd.xbe")}",
        });
    }
}
