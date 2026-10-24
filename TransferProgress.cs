using System;
using System.Collections.Generic;
using System.Linq;

public class TransferSnapshot {
    public bool Planned;
    public long Total, Verified, Reused, DownloadTotal, Network, InFlight;
    public int Files, Completed;
    public string Stage, Current;
    public double Percent { get { return Total<=0?0:Math.Min(Stage=="完成"?100:99.9,100.0*(Verified+InFlight)/Total); } }
}

public class TransferProgress {
    readonly object gate=new object();
    class Active { public long Bytes; public string Display; }
    readonly Dictionary<string,Active> active=new Dictionary<string,Active>(StringComparer.OrdinalIgnoreCase);
    long total,verified,reused,network,downloadTotal;
    int files,completed;
    bool planned;
    string stage="准备下载";
    public void Stage(string value) { lock(gate) stage=value; }
    public void Plan(long bytes,int count,long prepared) {lock(gate){planned=true;total=bytes;files=count;verified=prepared;completed=0;reused=0;downloadTotal=0;}}
    public void Cache(long bytes) {lock(gate){verified+=bytes;reused+=bytes;completed++;}}
    public void Need(long bytes) {lock(gate)downloadTotal+=bytes;}
    public void Complete(long bytes) {lock(gate){verified+=bytes;completed++;}}
    public void Begin(string key,string display) {lock(gate)active[key]=new Active{Display=display};}
    public void Receive(string key,int count) {lock(gate){network+=count;Active a;if(active.TryGetValue(key,out a))a.Bytes+=count;}}
    public void End(string key) {lock(gate)active.Remove(key);}
    public TransferSnapshot Snapshot() {lock(gate)return new TransferSnapshot{Planned=planned,Total=total,Verified=verified,Reused=reused,DownloadTotal=downloadTotal,Network=network,InFlight=active.Values.Sum(x=>x.Bytes),Files=files,Completed=completed,Stage=stage,Current=String.Join("  |  ",active.Values.Take(3).Select(x=>x.Display))};}
    public static string FormatBytes(double value) {
        if(value>=1024*1024*1024)return (value/(1024*1024*1024)).ToString("0.00")+" GB";
        if(value>=1024*1024)return (value/(1024*1024)).ToString("0.00")+" MB";
        return (Math.Max(0,value)/1024).ToString("0.0")+" KB";
    }
}
