using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

public static class Headless {
    static readonly object gate=new object();
    public static void Run(string[] args){
        string report=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"repair-progress.log");
        Action<string> log=s=>{lock(gate)File.AppendAllText(report,DateTime.Now.ToString("HH:mm:ss")+" "+s+Environment.NewLine,Encoding.UTF8);};
        try{
            int index=Array.IndexOf(args,"--install");string root=PclIntegration.Normalize(args[index+1]),id=args[index+2];
            var cancellation=new CancellationTokenSource();
            var engine=new Engine{Log=log,Token=cancellation.Token,Source=args.Contains("--official")?2:0};
            using(var timer=new System.Threading.Timer(delegate{
                if(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cancel-repair.signal")))cancellation.Cancel();
                var s=engine.Transfer.Snapshot();log(s.Stage+" | "+s.Percent.ToString("0.0")+"% | "+TransferProgress.FormatBytes(s.Verified+s.InFlight)+"/"+TransferProgress.FormatBytes(s.Total)+" | "+s.Completed+"/"+s.Files+" | 收到 "+TransferProgress.FormatBytes(s.Network)+" | "+s.Current);
            },null,0,2000)){
                engine.Transfer.Stage("获取版本列表");
                var manifest=engine.Json("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
                var entry=Engine.Arr(Engine.Get(manifest,"versions")).First(x=>Engine.Str(x,"id")==id);
                engine.Install(entry,root,true);
            }
            if(!args.Contains("--skip-register"))log("PCL 导入完成，备份："+PclIntegration.Register(root,id,PclIntegration.FindLauncher(root),true));
            log("SUCCESS: "+root+"\\versions\\"+id);
        }catch(Exception ex){log("FAILED: "+ex.ToString());Environment.Exit(1);}
    }
}
