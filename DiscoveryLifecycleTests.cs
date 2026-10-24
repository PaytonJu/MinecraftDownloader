using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

public static class DiscoveryLifecycleTests {
    class FakeProcess:ILauncherProcess {
        public int Id {get;set;}
        public bool HasExited {get;set;}
        public bool ExitNormally,CloseCalled,Killed,Disposed,KillFails;
        public Action OnClose=delegate{};
        public bool CloseMainWindow(){CloseCalled=true;OnClose();if(ExitNormally)HasExited=true;return ExitNormally;}
        public bool WaitForExit(int ms){return HasExited;}
        public void Kill(){Killed=true;if(!KillFails)HasExited=true;}
        public void Dispose(){Disposed=true;}
    }
    static int checks;
    static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
    public static void Run(){
        checks=0;string root=Path.Combine(Path.GetTempPath(),"MinecraftDiscoveryTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            string missing=Path.Combine(root,".minecraft");
            Check(LauncherDiscovery.FindGameFolder(new[]{missing})==null,"missing directory must not produce a fallback path");
            Check(!Directory.Exists(missing),"discovery does not create missing directories");
            Check(LauncherDiscovery.ResolveManualFolder(root)==missing,"first run resolves a manually selected parent to .minecraft");
            Check(!Directory.Exists(missing),"resolving first-run location is read-only");
            Directory.CreateDirectory(missing);Check(LauncherDiscovery.FindGameFolder(new[]{missing})==missing,"existing empty .minecraft is a real directory");
            Directory.Delete(missing);Check(LauncherDiscovery.FindGameFolder(new[]{missing})==null,"deleted directory disappears on rescan");
            string ordinary=Path.Combine(root,"ordinary");Directory.CreateDirectory(ordinary);Check(LauncherDiscovery.FindGameFolder(new[]{ordinary})==null,"arbitrary folder is not auto-detected as Minecraft");
            string fixtureDirectory=Path.Combine(root,"fixture");Directory.CreateDirectory(fixtureDirectory);
            string real=Path.Combine(fixtureDirectory,"IdentityFixture.exe");
            using(var provider=new Microsoft.CSharp.CSharpCodeProvider()){
                var parameters=new System.CodeDom.Compiler.CompilerParameters{GenerateExecutable=true,OutputAssembly=real};
                var result=provider.CompileAssemblyFromSource(parameters,"[assembly:System.Reflection.AssemblyProduct(\"Plain Craft Launcher identity test fixture\")] class Fixture { static void Main() {} }");
                if(result.Errors.HasErrors)throw new Exception("Could not compile metadata-only test fixture");
            }
            string renamed=Path.Combine(root,"随意改名后的启动器.exe");File.Copy(real,renamed);
            Check(LauncherDiscovery.IsPcl(renamed),"renamed fixture detected by embedded identity without a local PCL installation");
            Check(LauncherDiscovery.Candidates(new[]{root}).Single()==renamed,"renamed PCL found without a PCL settings folder");
            Check(!Directory.Exists(Path.Combine(root,"PCL")),"PCL discovery does not pre-create settings directories");
            string firstGame=Path.Combine(root,"first-game",".minecraft"),firstVersion=Path.Combine(firstGame,"versions","test");Directory.CreateDirectory(firstVersion);
            File.WriteAllText(Path.Combine(firstVersion,"test.jar"),"abc",new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(firstVersion,"test.json"),"{\"id\":\"test\",\"downloads\":{\"client\":{\"sha1\":\"a9993e364706816aba3e25717850c26c9cd0d89d\",\"size\":3}}}");
            PclIntegration.Register(firstGame,"test",renamed,false);
            Check(File.Exists(Path.Combine(root,"PCL","Setup.ini")),"first import creates configuration beside the selected renamed EXE");
            Check(File.ReadAllText(Path.Combine(root,"PCL","Setup.ini")).Contains(firstGame),"new PCL configuration points to the selected game root");
            Check(!Directory.Exists(Path.Combine(firstGame,"PCL")),"launcher settings are not accidentally created inside the game root");
            Check(File.Exists(Path.Combine(firstGame,"launcher_profiles.json")),"first import creates required game root metadata");
            File.WriteAllText(Path.Combine(root,"Plain Craft Launcher 2.exe"),"not an executable");
            Check(!LauncherDiscovery.IsPcl(Path.Combine(root,"Plain Craft Launcher 2.exe")),"matching file name alone does not establish PCL identity");
            string duplicate=Path.Combine(root,"another.exe");File.Copy(real,duplicate);
            Check(LauncherDiscovery.Candidates(new[]{root}).Length==2,"ambiguous PCL candidates are exposed instead of arbitrary selection");
            Check(LauncherLifecycle.MatchesPath(renamed,renamed.ToUpperInvariant()),"process matching ignores Windows path case");
            Check(!LauncherLifecycle.MatchesPath(renamed,Path.Combine(root,"other",Path.GetFileName(renamed))),"same exe name in another folder is not targeted");
            var normal=new FakeProcess{Id=1,ExitNormally=true};int closed=LauncherLifecycle.CloseProcesses(new[]{normal},CancellationToken.None,delegate{},400);
            Check(closed==1&&normal.CloseCalled&&!normal.Killed&&normal.Disposed,"normal exit is preferred and never force killed");
            var tray=new FakeProcess{Id=2};LauncherLifecycle.CloseProcesses(new[]{tray},CancellationToken.None,delegate{},400);
            Check(tray.CloseCalled&&tray.Killed&&tray.HasExited&&tray.Disposed,"unresponsive tray process is ended after the grace period");
            var exited=new FakeProcess{Id=3,HasExited=true};closed=LauncherLifecycle.CloseProcesses(new[]{exited},CancellationToken.None,delegate{},0);
            Check(closed==0&&!exited.CloseCalled&&!exited.Killed,"already exited processes are untouched");
            using(var cancel=new CancellationTokenSource()){
                var cancelled=new FakeProcess{Id=4,OnClose=()=>cancel.Cancel()};bool stopped=false;
                try{LauncherLifecycle.CloseProcesses(new[]{cancelled},cancel.Token,delegate{},400);}catch(OperationCanceledException){stopped=true;}
                Check(stopped&&!cancelled.Killed&&cancelled.Disposed,"cancellation stops escalation and releases the process object");
            }
            var denied=new FakeProcess{Id=5,KillFails=true};bool failed=false;
            try{LauncherLifecycle.CloseProcesses(new[]{denied},CancellationToken.None,delegate{},0);}catch(IOException){failed=true;}
            Check(failed&&denied.Disposed,"failed process termination blocks further import");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"discovery-lifecycle-test-result.txt"),"PASS: "+checks+" discovery and lifecycle checks\r\n");
        }finally{Directory.Delete(root,true);}
    }
}
