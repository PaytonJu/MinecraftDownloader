using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;

public interface ILauncherProcess:IDisposable {
    int Id {get;}
    bool HasExited {get;}
    bool CloseMainWindow();
    bool WaitForExit(int milliseconds);
    void Kill();
}
public static class LauncherLifecycle {
    class SelectedProcess:ILauncherProcess {
        readonly Process process;
        public SelectedProcess(Process value){process=value;}
        public int Id {get{return process.Id;}}
        public bool HasExited {get{return process.HasExited;}}
        public bool CloseMainWindow(){return process.CloseMainWindow();}
        public bool WaitForExit(int ms){return process.WaitForExit(ms);}
        public void Kill(){process.Kill();}
        public void Dispose(){process.Dispose();}
    }
    public static ILauncherProcess[] Selected(string launcher){
        string exact=Path.GetFullPath(launcher);var result=new List<ILauncherProcess>();int ownId;using(var own=Process.GetCurrentProcess())ownId=own.Id;
        foreach(var process in Process.GetProcesses()){
            bool matched=false;
            try{if(process.Id!=ownId&&!process.HasExited&&MatchesPath(process.MainModule.FileName,exact)){result.Add(new SelectedProcess(process));matched=true;}}
            catch(System.ComponentModel.Win32Exception){if(process.ProcessName.Equals(Path.GetFileNameWithoutExtension(exact),StringComparison.OrdinalIgnoreCase)){foreach(var saved in result)saved.Dispose();throw new IOException("无法检查所选 PCL 进程，请先手动关闭它，再继续导入。");}}
            catch(InvalidOperationException){}
            finally{if(!matched)process.Dispose();}
        }
        return result.ToArray();
    }
    public static bool MatchesPath(string actual,string selected){return String.Equals(Path.GetFullPath(actual),Path.GetFullPath(selected),StringComparison.OrdinalIgnoreCase);}
    public static bool IsRunning(string launcher){
        if(String.IsNullOrWhiteSpace(launcher))return false;
        var selected=Selected(launcher);try{return selected.Length>0;}finally{foreach(var process in selected)process.Dispose();}
    }
    public static int CloseProcesses(IEnumerable<ILauncherProcess> selected,CancellationToken token,Action<string> log,int graceMilliseconds){
        var processes=selected.ToArray();int count=0;
        try{
            foreach(var p in processes){token.ThrowIfCancellationRequested();if(!p.HasExited){log("请求所选 PCL 正常退出（进程 "+p.Id+"）");p.CloseMainWindow();count++;}}
            foreach(var p in processes){
                int elapsed=0;while(!p.HasExited&&elapsed<graceMilliseconds){token.ThrowIfCancellationRequested();int interval=Math.Min(200,graceMilliseconds-elapsed);if(p.WaitForExit(interval))break;elapsed+=interval;}
                token.ThrowIfCancellationRequested();
                if(!p.HasExited){log("所选 PCL 未在等待时间内退出，正在结束该启动器进程 "+p.Id);p.Kill();if(!p.WaitForExit(5000))throw new IOException("PCL 进程未退出，停止导入。");}
            }
            return count;
        }finally{foreach(var p in processes)p.Dispose();}
    }
    public static int Close(string launcher,CancellationToken token,Action<string> log){
        if(!LauncherDiscovery.IsPcl(launcher))throw new IOException("所选程序不是可识别的 PCL2，停止处理进程。");
        return CloseProcesses(Selected(launcher),token,log,8000);
    }
    public static void Open(string launcher){
        if(!LauncherDiscovery.IsPcl(launcher))throw new IOException("找不到先前选择的 PCL EXE，请重新选择。");
        if(IsRunning(launcher))return;
        Process.Start(new ProcessStartInfo(launcher){WorkingDirectory=Path.GetDirectoryName(launcher),UseShellExecute=true,WindowStyle=ProcessWindowStyle.Normal});
    }
}
