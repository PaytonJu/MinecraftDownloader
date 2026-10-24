using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Script.Serialization;
using Microsoft.Win32;

public static class PclIntegration {
    public static string Normalize(string path) {
        if(String.IsNullOrWhiteSpace(path))throw new IOException("请选择 Minecraft 游戏根目录");
        path=Path.GetFullPath(path.Trim());
        var dir=new DirectoryInfo(path);
        for(var p=dir;p!=null;p=p.Parent) {
            if(p.Name.Equals("versions",StringComparison.OrdinalIgnoreCase)||p.Name.Equals(".download-staging",StringComparison.OrdinalIgnoreCase)) {
                if(p.Parent==null)throw new IOException("无法确定游戏根目录");return p.Parent.FullName;
            }
            if(p.Name.Equals(".minecraft",StringComparison.OrdinalIgnoreCase))return p.FullName;
        }
        if(Directory.Exists(Path.Combine(path,".minecraft")))return Path.Combine(path,".minecraft");
        if(Directory.Exists(path)&&Directory.GetFiles(path,"*.json").Any(f=>File.Exists(Path.ChangeExtension(f,".jar"))&&!Path.GetFileName(f).Equals("launcher_profiles.json",StringComparison.OrdinalIgnoreCase)))
            throw new IOException("这里是单个版本文件夹，请选择上级 .minecraft 游戏根目录");
        if(dir.Parent==null)throw new IOException("请选择 .minecraft 文件夹，不要选择磁盘根目录");return path;
    }
    public static string FindLauncher(string root) {
        return LauncherDiscovery.Find(root);
    }
    public static bool Running(string launcher) {
        return LauncherLifecycle.IsRunning(launcher);
    }
    public static void WriteIni(string file,Dictionary<string,string> values,string backupDir) {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var lines=File.Exists(file)?File.ReadAllLines(file,Encoding.UTF8).ToList():new List<string>();
        if(File.Exists(file)&&backupDir!=null){Directory.CreateDirectory(backupDir);File.Copy(file,Path.Combine(backupDir,Guid.NewGuid().ToString("N")+"-"+Path.GetFileName(file)),false);}
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var output=new List<string>();
        foreach(string line in lines){int colon=line.IndexOf(':');string key=colon<0?"":line.Substring(0,colon).Trim();
            string replacement;if(values.TryGetValue(key,out replacement)){if(seen.Add(key))output.Add(key+":"+replacement);}else output.Add(line);}
        foreach(var pair in values)if(seen.Add(pair.Key))output.Add(pair.Key+":"+pair.Value);
        string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllLines(temp,output,new UTF8Encoding(false));
        Publish(temp,file);
    }
    public static void Publish(string temp,string file){
        if(!File.Exists(file)){File.Move(temp,file);return;}
        try{File.Replace(temp,file,null);return;}catch(UnauthorizedAccessException){}catch(PlatformNotSupportedException){}
        string previous=file+"."+Guid.NewGuid().ToString("N")+".previous";
        File.Move(file,previous);
        try{File.Move(temp,file);}catch{File.Move(previous,file);throw;}
        File.Delete(previous);
    }
    public static string MergeFolders(string existing,string root) {
        string normalized=Path.GetFullPath(root).TrimEnd('\\')+"\\";
        string staging=normalized+".download-staging\\";
        var result=new List<string>();bool found=false;
        foreach(string record in (existing??"").Split('|')){
            int separator=record.IndexOf('>');if(separator<0){if(record!="")result.Add(record);continue;}
            string path=record.Substring(separator+1).Replace('/','\\').TrimEnd('\\')+"\\";
            if(path.StartsWith(staging,StringComparison.OrdinalIgnoreCase))continue;
            if(path.Equals(normalized,StringComparison.OrdinalIgnoreCase))found=true;
            result.Add(record);
        }
        if(!found)result.Add("Minecraft>"+normalized);return String.Join("|",result);
    }
    public static void ValidateVersion(string root,string id) {
        root=Normalize(root);string version=Engine.Safe(root,"versions/"+id);
        if(!File.Exists(Engine.Safe(version,id+".json"))||!File.Exists(Engine.Safe(version,id+".jar")))throw new IOException("版本尚未安装完成，不能导入临时下载文件夹");
        var meta=Engine.Parse(File.ReadAllText(Engine.Safe(version,id+".json")));
        if(Engine.Str(meta,"id")!=id||!Engine.Valid(Engine.Safe(version,id+".jar"),Engine.Str(Engine.Get(Engine.Get(meta,"downloads"),"client"),"sha1"),Engine.Size(Engine.Get(Engine.Get(meta,"downloads"),"client"))))throw new IOException("游戏本体校验失败，请先修复下载");
    }
    public static string Register(string root,string id,string launcher,bool registry) {
        root=Normalize(root);ValidateVersion(root,id);
        if(launcher==null||!LauncherDiscovery.IsPcl(launcher))throw new IOException("未找到可确认身份的 PCL2 程序，请手动选择 PCL 的 EXE。");
        if(registry&&Running(launcher))throw new IOException("PCL2 正在运行。请关闭 PCL2，再点击“导入 / 修复 PCL”，避免旧缓存覆盖设置。");
        string backup=Engine.Safe(root,".download-helper-backups/"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
        string setup=Engine.Safe(Path.GetDirectoryName(launcher),"PCL/Setup.ini");
        if(!File.Exists(Engine.Safe(root,"launcher_profiles.json")))File.WriteAllText(Engine.Safe(root,"launcher_profiles.json"),"{\"profiles\":{}}",new UTF8Encoding(false));
        if(registry){using(var key=Registry.CurrentUser.CreateSubKey(@"Software\PCL")){
            string old=Convert.ToString(key.GetValue("LaunchFolders",""));Directory.CreateDirectory(backup);File.WriteAllText(Path.Combine(backup,"LaunchFolders.json"),new JavaScriptSerializer().Serialize(new Dictionary<string,string>{{"LaunchFolders",old}}),new UTF8Encoding(false));
            key.SetValue("LaunchFolders",MergeFolders(old,root),RegistryValueKind.String);
        }}
        WriteIni(Engine.Safe(root,"PCL.ini"),new Dictionary<string,string>{{"InstanceCache",""},{"Version",id}},backup);
        WriteIni(setup,new Dictionary<string,string>{{"LaunchFolderSelect",root.TrimEnd('\\')+"\\"}},backup);
        return backup;
    }
}
