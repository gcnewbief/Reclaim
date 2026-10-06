// FlaUI smoke test for the Reclaim WPF app. Must run ELEVATED so it can
// attach to the elevated Reclaim process (UIA refuses lower->higher).
// Drives: launch app -> wait for drive list -> pick target -> SCAN -> monitor.
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

string exe = args.Length > 0 ? args[0]
    : @"C:\Users\Gcnewbief\Devin working\Reclaim\publish\Reclaim.exe";
int targetDisk = args.Length > 1 ? int.Parse(args[1]) : 1;
int scanSeconds = args.Length > 2 ? int.Parse(args[2]) : 30;

Console.WriteLine($"launching {exe}");
var app = FlaUI.Core.Application.Launch(exe);
using var uia = new UIA3Automation();
var win = app.GetMainWindow(uia, TimeSpan.FromSeconds(20));
Console.WriteLine($"window: {win.Title}");

var cf = uia.ConditionFactory;
ComboBox driveList = win.FindFirstDescendant(cf.ByAutomationId("DriveList")).AsComboBox();
var statusLbl = win.FindFirstDescendant(cf.ByAutomationId("StatusLbl"));
var logBox = win.FindFirstDescendant(cf.ByAutomationId("LogBox"));
var scanBtn = win.FindFirstDescendant(cf.ByAutomationId("ScanBtn")).AsButton();

// wait for drives to enumerate
for (int i = 0; i < 30 && (driveList.Items == null || driveList.Items.Length == 0); i++)
    Thread.Sleep(500);
Thread.Sleep(1000);
var items = driveList.Items;
Console.WriteLine($"drives: {items.Length}");
foreach (var it in items) Console.WriteLine($"  item: {it.Text}");

if (items.Length == 0) { Console.WriteLine("FAIL: no drives enumerated"); return 1; }
var target = items.FirstOrDefault(x => x.Text.Contains($"Disk {targetDisk}"));
if (target == null) { Console.WriteLine($"FAIL: Disk {targetDisk} not in list"); return 1; }
target.Select();
Console.WriteLine($"selected: {target.Text}");

// screenshot the drive ComboBox so we can verify readability
try
{
    var shot = driveList.Capture();
    var shotPath = @"C:\Users\Gcnewbief\Devin working\Reclaim\uitest_combo.png";
    shot.Save(shotPath);
    Console.WriteLine($"combo screenshot: {shotPath}");
    Thread.Sleep(4000);   // let the drive-health SMART read settle so the light shows
    var shot2 = win.Capture();
    shot2.Save(@"C:\Users\Gcnewbief\Devin working\Reclaim\uitest_window.png");
    Console.WriteLine("window screenshot saved");
}
catch (Exception ex) { Console.WriteLine($"screenshot failed: {ex.Message}"); }

string Log() => logBox.Patterns.Value.PatternOrDefault?.Value ?? "(no log)";
string Status() => statusLbl.Patterns.Value.PatternOrDefault?.Value
                   ?? statusLbl.Name ?? "?";

Console.WriteLine($"scanBtn: enabled={scanBtn.IsEnabled} invokeable={scanBtn.Patterns.Invoke.IsSupported}");
scanBtn.Patterns.Invoke.Pattern.Invoke();
Thread.Sleep(1500);
Console.WriteLine($"after invoke: enabled={scanBtn.IsEnabled}");
if (scanBtn.IsEnabled)   // Invoke didn't take - try a real mouse click
{
    var r = scanBtn.BoundingRectangle;
    var p = new System.Drawing.Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
    Mouse.MoveTo(p);
    Thread.Sleep(200);
    Mouse.Click(p);
    Console.WriteLine($"mouse-clicked at {p.X},{p.Y}");
    Thread.Sleep(1500);
    Console.WriteLine($"after mouse click: enabled={scanBtn.IsEnabled}");
}
Console.WriteLine($"scan clicked at {DateTime.Now:HH:mm:ss}");
var t0 = DateTime.Now;
while (DateTime.Now - t0 < TimeSpan.FromSeconds(scanSeconds))
{
    Thread.Sleep(2000);
    var st = Status();
    Console.WriteLine($"  status: {st}");
    if (st.Contains("Scan complete") || st.Contains("failed") || st.Contains("Cancelled"))
        break;
}
Console.WriteLine("--- log tail ---");
var log = Log();
foreach (var l in log.Split('\n').TakeLast(15)) Console.WriteLine($"  {l}");

// exercise the row checkboxes - toggle the first two rows
try
{
    var grid = win.FindFirstDescendant(cf.ByAutomationId("Grid"));
    var boxes = grid?.FindAllDescendants(cf.ByControlType(ControlType.CheckBox))
        .Take(2).ToArray() ?? [];
    Console.WriteLine($"checkboxes found: {grid?.FindAllDescendants(cf.ByControlType(ControlType.CheckBox)).Length}");
    foreach (var b in boxes) { b.AsCheckBox().Toggle(); }
    Thread.Sleep(800);
    var recover = win.FindFirstDescendant(cf.ByAutomationId("RecoverBtn"));
    Console.WriteLine($"after checking {boxes.Length}: recover enabled={recover?.IsEnabled}");
    win.Capture().Save(@"C:\Users\Gcnewbief\Devin working\Reclaim\uitest_checked.png");
    Console.WriteLine("checked screenshot saved");
}
catch (Exception ex) { Console.WriteLine($"checkbox exercise failed: {ex.Message}"); }

// SMART button exercise
var smartBtn = win.FindFirstDescendant(cf.ByAutomationId("SmartBtn"));
if (smartBtn != null)
{
    Console.WriteLine($"clicking SMART… (enabled={smartBtn.IsEnabled})");
    if (!smartBtn.IsEnabled) { Console.WriteLine("SMART btn disabled - scan still running"); }
    else smartBtn.AsButton().Invoke();
    Thread.Sleep(8000);
    Console.WriteLine("log after SMART click:");
    foreach (var l in Log().Split('\n').TakeLast(4)) Console.WriteLine($"    {l}");
    // report window is owned/non-modal; verify via the app's own log line
    var after = Log();
    Console.WriteLine(after.Contains("report window") ? "SMART PASS: report window shown"
                                                    : "SMART FAIL: no report window");
}

// check for modal error dialogs
var modals = win.ModalWindows;
foreach (var m in modals) Console.WriteLine($"  MODAL: {m.Title} / {m.Name}");

app.Kill();
Console.WriteLine("DONE");
return 0;
