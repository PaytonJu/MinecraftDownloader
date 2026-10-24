using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;

public static class LauncherDiscovery {
    public static bool IsPcl(string path){
        try{
            if(!File.Exists(path)||!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))return false;
            var info=FileVersionInfo.GetVersionInfo(path);
            if((info.ProductName??"").IndexOf("Plain Craft Launcher",StringComparison.OrdinalIgnoreCase)>=0)return true;
            string name=AssemblyName.GetAssemblyName(path).Name;
            return name.IndexOf("Plain Craft Launcher",StringComparison.OrdinalIgnoreCase)>=0;
        }catch{return false;}
    }
    public static string[] Candidates(IEnumerable<string> directories){
        var result=new List<string>();
        foreach(string directory in directories.Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)){
            try{if(Directory.Exists(directory))foreach(string file in Directory.GetFiles(directory,"*.exe"))if(IsPcl(file))result.Add(Path.GetFullPath(file));}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string ConfigFile {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MinecraftFolderDownloader","launcher-path.txt");}}
    public static void Remember(string path){
        if(!IsPcl(path))throw new IOException("该 EXE 未识别为 PCL2，请选择真正的 PCL 启动器程序（可以改名）。");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile));File.WriteAllText(ConfigFile,Path.GetFullPath(path));
    }
    public static string Find(string root){
        try{string saved=File.Exists(ConfigFile)?File.ReadAllText(ConfigFile).Trim():null;if(IsPcl(saved))return saved;}catch{}
        var nearby=new List<string>();
        if(!String.IsNullOrWhiteSpace(root)){try{nearby.Add(root);var parent=Directory.GetParent(root);if(parent!=null)nearby.Add(parent.FullName);}catch{}}
        var local=Candidates(nearby);if(local.Length==1)return local[0];if(local.Length>1)return null;
        var others=Candidates(new[]{AppDomain.CurrentDomain.BaseDirectory,@"D:\Minecraft"});return others.Length==1?others[0]:null;
    }
    public static string FindGameFolder(IEnumerable<string> candidates){
        foreach(string candidate in candidates.Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)){
            try{if(Directory.Exists(candidate)&&(Directory.Exists(Path.Combine(candidate,"versions"))||File.Exists(Path.Combine(candidate,"launcher_profiles.json"))||new DirectoryInfo(candidate).Name.Equals(".minecraft",StringComparison.OrdinalIgnoreCase)))return Path.GetFullPath(candidate);}catch{}
        }
        return null;
    }
    public static string ResolveManualFolder(string selected){
        if(!Directory.Exists(selected))throw new IOException("找不到所选文件夹，请重新选择。");
        string path=PclIntegration.Normalize(selected);
        if(new DirectoryInfo(path).Name.Equals(".minecraft",StringComparison.OrdinalIgnoreCase)||Directory.Exists(Path.Combine(path,"versions"))||File.Exists(Path.Combine(path,"launcher_profiles.json")))return path;
        return Path.Combine(path,".minecraft");
    }
}
