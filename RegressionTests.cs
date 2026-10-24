using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;

public static class RegressionTests {
    static int count;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
    public static void Run(){
        string root=Path.Combine(Path.GetTempPath(),"MinecraftDownloader-regression-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            string game=Path.Combine(root,".minecraft");Directory.CreateDirectory(game);
            string nested=Path.Combine(game,".download-staging","1.20.1");Directory.CreateDirectory(nested);
            Check(PclIntegration.Normalize(nested)==game,"staging must normalize to actual game root");
            Check(PclIntegration.Normalize(Path.Combine(game,"versions","1.20.1"))==game,"version directory must normalize to root");
            Check(PclIntegration.Normalize(Path.Combine(game,"versions"))==game,"versions directory must normalize to root");
            Check(PclIntegration.Normalize(root)==game,"launcher directory must normalize to .minecraft");
            string other=Path.Combine(root,"other");Directory.CreateDirectory(other);File.WriteAllText(Path.Combine(other,"1.json"),"{}");File.WriteAllText(Path.Combine(other,"1.jar"),"data");
            bool blocked=false;try{PclIntegration.Normalize(other);}catch(IOException){blocked=true;}Check(blocked,"unrecognized individual version rejected");
            string old="MineCraft>"+game+"\\|Bad>"+nested+"\\|Other>E:\\Other\\";
            string merged=PclIntegration.MergeFolders(old,game);
            Check(!merged.Contains(".download-staging"),"erroneous staging registration removed");
            Check(merged.Contains("Other>E:\\Other\\"),"other registrations preserved");
            Check(merged.Split('|').Count(x=>x.Contains(game))==1,"root registration deduplicated");
            Check(PclIntegration.MergeFolders("",game).EndsWith(game+"\\"),"new root registration added");
            string ini=Path.Combine(root,"test.ini"),backup=Path.Combine(root,"backup");
            File.WriteAllText(ini,"Keep:value\r\nVersion:old\r\nVersion:duplicate\r\n",new UTF8Encoding(false));
            PclIntegration.WriteIni(ini,new Dictionary<string,string>{{"Version","1.20.1"},{"InstanceCache",""}},backup);
            Check(File.ReadAllText(ini).Contains("Keep:value"),"unrelated PCL settings preserved");
            Check(File.ReadAllLines(ini).Count(x=>x.StartsWith("Version:"))==1,"duplicate settings removed");
            Check(Directory.GetFiles(backup).Length==1,"original settings backed up");
            Check(File.ReadAllText(Directory.GetFiles(backup)[0]).Contains("Version:old"),"backup has original selection");
            var t=new TransferProgress();t.Plan(10000,2,1000);t.Cache(1000);t.Need(8000);
            t.Begin("big","big.jar");t.Receive("big",2000);
            Check(Math.Abs(t.Snapshot().Percent-40)<0.001,"progress updates within a large file");
            Check(t.Snapshot().Completed==1,"large file still unfinished while progress advances");
            t.End("big");Check(t.Snapshot().Verified+t.Snapshot().InFlight==2000,"failed attempt rolled back");
            t.Begin("big","retry");t.Receive("big",8000);t.End("big");t.Complete(8000);
            Check(t.Snapshot().Network==10000,"network counts actual retry bytes");
            Check(t.Snapshot().Verified==10000,"verified bytes do not double count retry");
            Check(t.Snapshot().Percent<100,"download is not marked complete before final commit");t.Stage("完成");Check(t.Snapshot().Percent==100,"published install reports 100 percent");
            var parallel=new TransferProgress();parallel.Plan(100000,100,0);
            Parallel.For(0,100,i=>{string key=i.ToString();parallel.Begin(key,key);parallel.Receive(key,1000);parallel.End(key);parallel.Complete(1000);});
            Check(parallel.Snapshot().Verified==100000&&parallel.Snapshot().Network==100000&&parallel.Snapshot().Completed==100,"parallel byte counters consistent");
            Check(TransferProgress.FormatBytes(2048)=="2.0 KB"&&TransferProgress.FormatBytes(2097152)=="2.00 MB","speed and sizes use KB and MB");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"regression-test-result.txt"),"PASS: "+count+" regression checks\r\n",Encoding.UTF8);
        }finally{Directory.Delete(root,true);}
    }
}
