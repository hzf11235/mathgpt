using System.Reflection;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using MathGPT.Core;
using MathGPT.Mac;

var project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var output=args.Length>0?Path.GetFullPath(args[0]):Path.Combine(project,"build","verification","mac-headless");Directory.CreateDirectory(output);
var results=new List<object>();
var validator=typeof(MainWindow).Assembly.GetType("MathGPT.Mac.MacPackageSafety")!.GetMethod("Validate",BindingFlags.NonPublic|BindingFlags.Static)!;
void Archive(string name,bool allowed,params (string Path,string Value,bool Link)[] entries)
{
 using var stream=new MemoryStream();
 using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))foreach(var item in entries)
 {
  var entry=zip.CreateEntry(item.Path);entry.ExternalAttributes=(item.Link?0xA1FF:0x81A4)<<16;
  using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));writer.Write(item.Value);
 }
 stream.Position=0;using var archive=new ZipArchive(stream,ZipArchiveMode.Read);
 var accepted=true;try{validator.Invoke(null,[archive]);}catch(TargetInvocationException error)when(error.InnerException is UserException){accepted=false;}
 if(accepted!=allowed)throw new Exception("Archive contract failed: "+name);
 results.Add(new {name,passed=true});
}
Archive("Electron internal relative symlinks",true,("Codex.app/Contents/Resources/file","data",false),("Codex.app/Contents/Helpers/resources","../Resources",true));
Archive("Framework Current symlink",true,("Codex.app/Contents/Frameworks/F.framework/Versions/A/F","binary",false),("Codex.app/Contents/Frameworks/F.framework/Versions/Current","A",true),("Codex.app/Contents/Frameworks/F.framework/F","Versions/Current/F",true));
Archive("External relative symlink",false,("Codex.app/link","../../outside",true));
Archive("Absolute symlink",false,("Codex.app/link","/tmp/outside",true));
Archive("Cyclic link graph",false,("Codex.app/A","B",true),("Codex.app/B","A",true));
Archive("Writing through symlink parent",false,("Codex.app/dir","other",true),("Codex.app/dir/file","data",false));
Archive("Case alias of symlink parent",false,("Codex.app/dir","other",true),("Codex.app/DIR/file","data",false));
Archive("Case collision",false,("Codex.app/A","first",false),("Codex.app/a","second",false));
Archive("Root traversal",false,("../outside","data",false));
var data=Path.Combine(output,"isolated-data");
var store=new StateStore(data);store.State.WorkDirectory=Path.Combine(data,"work");store.State.DownloadDirectory=Path.Combine(data,"downloads");store.Save();
typeof(MainWindow).Assembly.GetType("MathGPT.Mac.Program")!.GetField("Root",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,data);
AppBuilder.Configure<MacApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions {UseHeadlessDrawing=false}).SetupWithoutStarting();
var window=new MainWindow();window.Show();
var fields=BindingFlags.NonPublic|BindingFlags.Instance;
var actualStore=(StateStore)typeof(MainWindow).GetField("store",fields)!.GetValue(window)!;
var one=new ConnectionProfile {Name="one",Model="gpt-6.1-sol"};var two=new ConnectionProfile {Name="two",Model="claude-opus-5-5"};
actualStore.State.Profiles.AddRange([one,two]);
typeof(MainWindow).GetField("profile",fields)!.SetValue(window,one);
typeof(MainWindow).GetField("routes",fields)!.SetValue(window,new List<ModelRoute> {new() {Id=one.Model,Group="default",Available=true,Protocols=[WireProtocol.Responses]}});
typeof(MainWindow).GetMethod("Fill",fields)!.Invoke(window,[true]);
var profiles=(ComboBox)typeof(MainWindow).GetField("profiles",fields)!.GetValue(window)!;profiles.SelectedItem=two;
var models=(ComboBox)typeof(MainWindow).GetField("models",fields)!.GetValue(window)!;
if(models.Items.Count!=0 || ((List<ModelRoute>)typeof(MainWindow).GetField("routes",fields)!.GetValue(window)!).Count!=0)throw new Exception("Old profile model catalog remained selected");
results.Add(new {name="Profile switch clears previous model and effort catalog",passed=true});
foreach(var width in new[]{1040,740})
{
 window.Width=width;window.Height=780;
 var tabs=window.GetVisualDescendants().OfType<TabControl>().Single();
 for(var tab=0;tab<3;tab++)
 {
  tabs.SelectedIndex=tab;Avalonia.Threading.Dispatcher.UIThread.RunJobs();
  using var bitmap=window.CaptureRenderedFrame()??throw new Exception("No rendered frame");
  bitmap.Save(Path.Combine(output,$"mac-{width}-tab-{tab}.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
  if(!window.GetVisualDescendants().OfType<Button>().Any(x=>x.IsVisible&&x.Bounds.Width>0))throw new Exception("No usable actions");
  if(tab==1)
  {
   var buttons=window.GetVisualDescendants().OfType<Button>().Where(x=>x.Content is string).ToArray();
   if(buttons.Count(x=>Equals(x.Content,"安装并接入"))!=3 || !buttons.Any(x=>Equals(x.Content,"运行本机自检")))throw new Exception("Missing one-click install or self-check action");
  }
 }
}
window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=0;
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
MacContinuityAudit.Run(window);
DesktopMirrorUiAudit.Run(window,output);
await MacAccountUiAudit.Run(output);
window.Close();File.WriteAllText(Path.Combine(output,"mac-contracts.json"),System.Text.Json.JsonSerializer.Serialize(new{results,rendered=6,realMacTested=false},JsonData.Options));
Console.WriteLine($"Passed {results.Count} Mac package/profile contracts and rendered 6 UI previews on Windows. No real macOS execution or Keychain validation.");
