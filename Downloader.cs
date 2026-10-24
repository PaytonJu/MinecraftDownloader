using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class Engine {
    public const string Mirror = "https://bmclapi2.bangbang93.com";
    public static Dictionary<string,object> Obj(object x) { return x as Dictionary<string,object> ?? new Dictionary<string,object>(); }
    public static object Get(object x, string k) { object v; return Obj(x).TryGetValue(k,out v)?v:null; }
    public static string Str(object x, string k) { return Convert.ToString(Get(x,k)); }
    public static object[] Arr(object x) { return x as object[] ?? new object[0]; }
    public static object Parse(string s) { return new JavaScriptSerializer { MaxJsonLength=int.MaxValue, RecursionLimit=200 }.DeserializeObject(s); }
    public Action<string> Log = delegate {};
    public readonly TransferProgress Transfer = new TransferProgress();
    public CancellationToken Token;
    public int Source;
    public bool Proxy;
    int mirrorFailures;long preferOfficialUntil;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> labels=new System.Collections.Concurrent.ConcurrentDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    public static string Safe(string root,string relative) {
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":")) throw new IOException("非法相对路径");
        string basePath=Path.GetFullPath(root).TrimEnd('\\')+"\\";
        string full=Path.GetFullPath(Path.Combine(basePath,relative.Replace('/', '\\')));
        if (!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)) throw new IOException("路径越界");
        string parent=full;
        while (parent.Length>=basePath.TrimEnd('\\').Length) {
            if ((Directory.Exists(parent)||File.Exists(parent)) && (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0) throw new IOException("目标含目录链接，请选择普通文件夹");
            parent=Path.GetDirectoryName(parent); if(parent==null) break;
        }
        return full;
    }
    public static string Mirrored(string url) {
        Uri u=new Uri(url); string host=u.Host.ToLowerInvariant();
        if(host=="resources.download.minecraft.net") return Mirror+"/assets"+u.AbsolutePath;
        if(host=="libraries.minecraft.net") return Mirror+"/maven"+u.AbsolutePath;
        if(new[]{"piston-meta.mojang.com","piston-data.mojang.com","launchermeta.mojang.com","launcher.mojang.com"}.Contains(host)) return Mirror+u.AbsolutePath+u.Query;
        return url;
    }
    public IEnumerable<string> Sources(string url) {
        if(new Uri(url).Scheme!="https") throw new IOException("仅允许 HTTPS 下载");
        string mirror=Mirrored(url);
        bool officialFirst=Source==0&&Interlocked.Read(ref preferOfficialUntil)>DateTime.UtcNow.Ticks;
        return (Source==2?new[]{url}:Source==1?new[]{mirror}:officialFirst?new[]{url,mirror}:new[]{mirror,url}).Distinct();
    }
    IEnumerable<string> Attempts(string url){var sources=Sources(url).ToArray();return sources.Concat(sources);}
    void SourceFailed(string src){
        if(Source==0&&new Uri(src).Host==new Uri(Mirror).Host&&Interlocked.Increment(ref mirrorFailures)>=3){
            long until=DateTime.UtcNow.AddMinutes(3).Ticks;if(Interlocked.Exchange(ref preferOfficialUntil,until)<DateTime.UtcNow.Ticks)Log("国内镜像连续响应失败，接下来优先官方源；3 分钟后重新尝试国内优先。");
        }
    }
    HttpWebRequest Request(string url) {
        var r=(HttpWebRequest)WebRequest.Create(url); r.UserAgent="MinecraftFolderDownloader/2.0";
        r.Timeout=12000; r.ReadWriteTimeout=15000; r.Proxy=Proxy?WebRequest.GetSystemWebProxy():null;
        r.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate; return r;
    }
    public byte[] Bytes(string url) {
        Exception last=null;
        foreach(string src in Attempts(url)) {
            Token.ThrowIfCancellationRequested();
            Transfer.Begin(url,"元数据 · "+new Uri(src).Host);
            try { var r=Request(src); using(Token.Register(r.Abort)) using(var response=r.GetResponse()) using(var stream=response.GetResponseStream()) using(var ms=new MemoryStream()) {
                var buffer=new byte[65536]; int n; while((n=stream.Read(buffer,0,buffer.Length))>0) { Token.ThrowIfCancellationRequested(); ms.Write(buffer,0,n);Transfer.Receive(url,n); if(ms.Length>64*1024*1024) throw new IOException("元数据超过大小上限"); } return ms.ToArray();
            }} catch(Exception ex) {Token.ThrowIfCancellationRequested();last=ex;SourceFailed(src);Log("元数据请求失败，准备重试或切换来源："+new Uri(src).Host+"；"+ex.Message);}
            finally{Transfer.End(url);}
        }
        throw new IOException("获取失败："+url+"；"+last.Message,last);
    }
    public object Json(string url) { return Parse(Encoding.UTF8.GetString(Bytes(url))); }
    public static string Hash(string path) {using(var sha=SHA1.Create()) using(var f=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
    public static bool Valid(string path,string sha,long size) { return File.Exists(path) && (size<0||new FileInfo(path).Length==size) && (!String.IsNullOrEmpty(sha)&&Hash(path).Equals(sha,StringComparison.OrdinalIgnoreCase)); }
    public virtual void Download(string url,string path,string sha,long size) {
        Token.ThrowIfCancellationRequested(); if(Valid(path,sha,size)) return;
        if(String.IsNullOrEmpty(sha)) throw new IOException("下载元数据缺少 SHA-1："+url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); Exception last=null;
        int attempt=0;
        foreach(var src in Attempts(url)) {
            attempt++;
            Token.ThrowIfCancellationRequested(); string part=path+"."+Guid.NewGuid().ToString("N")+".part";
            string display; if(!labels.TryGetValue(path,out display))display=Path.GetFileName(path);
            Transfer.Begin(path,display+" · "+new Uri(src).Host+"（尝试 "+attempt+"）");
            try {
                var r=Request(src); using(Token.Register(r.Abort)) using(var response=r.GetResponse()) using(var input=response.GetResponseStream()) using(var output=File.Create(part)) {
                    byte[] buffer=new byte[131072];int n;while((n=input.Read(buffer,0,buffer.Length))>0) {Token.ThrowIfCancellationRequested();output.Write(buffer,0,n);Transfer.Receive(path,n);if(size>=0&&output.Length>size) throw new IOException("文件大小异常");}
                }
                if(!Valid(part,sha,size)) throw new IOException("文件 SHA-1 或大小校验失败");
                Token.ThrowIfCancellationRequested();
                PclIntegration.Publish(part,path);return;
            } catch(Exception ex) {Token.ThrowIfCancellationRequested();last=ex;SourceFailed(src);Log("重试 / 切换来源："+Path.GetFileName(path)+"，"+new Uri(src).Host+"；"+ex.Message);}
            finally {Transfer.End(path);if(File.Exists(part)) File.Delete(part);}
        }
        throw new IOException("下载失败："+url+"；"+last.Message,last);
    }
    public static long Size(object o) {var n=Get(o,"size");return n==null?-1:Convert.ToInt64(n);}
    void Stage(string name){Transfer.Stage(name);Log(name);}
    long ProbeSize(string url){
        foreach(string src in Sources(url))try{Token.ThrowIfCancellationRequested();var request=Request(src);request.Method="HEAD";using(Token.Register(request.Abort))using(var response=request.GetResponse())if(response.ContentLength>=0)return response.ContentLength;}catch{Token.ThrowIfCancellationRequested();}
        throw new IOException("无法获取文件大小，尚未开始主体下载："+url);
    }
    public static bool Allowed(object lib) {
        var rules=Get(lib,"rules"); if(rules==null) return true; bool allowed=false;
        foreach(var rule in Arr(rules)) {
            bool matches=true; var os=Get(rule,"os");
            string name=Str(os,"name"),arch=Str(os,"arch"),version=Str(os,"version");
            if(name!=""&&name!="windows") matches=false;
            if(arch!=""&&arch!="x86_64"&&arch!="amd64") matches=false;
            if(version!=""&&!System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(),version)) matches=false;
            foreach(var feature in Obj(Get(rule,"features"))) if(Convert.ToBoolean(feature.Value)) matches=false;
            if(matches) allowed=Str(rule,"action")=="allow";
        } return allowed;
    }
    class Job {public string Url,Path,Sha,Name; public long Size;}
    void Add(List<Job> jobs,object artifact,string baseDir) {
        if(artifact==null) return;
        jobs.Add(new Job{Url=Str(artifact,"url"),Path=Safe(baseDir,Str(artifact,"path")),Sha=Str(artifact,"sha1"),Size=Size(artifact),Name=Path.GetFileName(Str(artifact,"path"))});
    }
    public void Install(object entry,string root,bool isolation) {
        root=PclIntegration.Normalize(root);
        string id=Str(entry,"id"); string versionDir=Safe(root,"versions/"+id);
        if(Directory.Exists(versionDir)) throw new IOException("该版本目录已经存在；为保护现有存档与模组，请在 PCL 中检查该版本。工具不会覆盖现有版本。");
        Directory.CreateDirectory(root);
        string stage=Safe(root,".download-staging/"+id); Directory.CreateDirectory(stage);
        Stage("1/5 获取版本清单："+id);
        string jsonPath=Safe(stage,id+".json"); Download(Str(entry,"url"),jsonPath,Str(entry,"sha1"),-1);
        var meta=Parse(File.ReadAllText(jsonPath));
        if(Str(meta,"id")!=id) throw new IOException("版本 ID 不一致");
        var client=Get(Get(meta,"downloads"),"client");
        if(client==null||Get(meta,"inheritsFrom")!=null) throw new IOException("此版本不是受支持的独立原版客户端");
        var jobs=new List<Job>();
        jobs.Add(new Job{Url=Str(client,"url"),Path=Safe(stage,id+".jar"),Sha=Str(client,"sha1"),Size=Size(client),Name="游戏本体 "+id+".jar"});
        foreach(var lib in Arr(Get(meta,"libraries"))) {
            if(!Allowed(lib)) continue;
            var downloads=Get(lib,"downloads"); Add(jobs,Get(downloads,"artifact"),Safe(root,"libraries"));
            string native=Str(Get(lib,"natives"),"windows").Replace("${arch}","64");
            if(native!="") Add(jobs,Get(Get(downloads,"classifiers"),native),Safe(root,"libraries"));
            if(downloads==null) throw new IOException("该旧版本依赖格式暂不支持，安装未提交");
        }
        var logging=Get(Get(Get(meta,"logging"),"client"),"file");
        if(logging!=null) jobs.Add(new Job{Url=Str(logging,"url"),Path=Safe(root,"assets/log_configs/"+Str(logging,"id")),Sha=Str(logging,"sha1"),Size=Size(logging)});
        var index=Get(meta,"assetIndex"); object assets=null;
        long prepared=new FileInfo(jsonPath).Length;
        if(index!=null) {
            Stage("1/5 获取资源索引，计算下载总量");
            string ip=Safe(root,"assets/indexes/"+Str(index,"id")+".json");
            Download(Str(index,"url"),ip,Str(index,"sha1"),Size(index)); assets=Parse(File.ReadAllText(ip));
            prepared+=new FileInfo(ip).Length;
            foreach(var item in Obj(Get(assets,"objects"))) {
                string hash=Str(item.Value,"hash"); if(!System.Text.RegularExpressions.Regex.IsMatch(hash,"^[0-9a-f]{40}$")) throw new IOException("资源哈希格式异常");
                string rel=hash.Substring(0,2)+"/"+hash;
                jobs.Add(new Job{Url="https://resources.download.minecraft.net/"+rel,Path=Safe(root,"assets/objects/"+rel),Sha=hash,Size=Size(item.Value),Name="资源 "+item.Key});
            }
        }
        jobs=jobs.GroupBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).Select(x=>x.First()).ToList();
        foreach(var job in jobs){if(job.Size<0)job.Size=ProbeSize(job.Url);labels[job.Path]=job.Name??Path.GetFileName(job.Path);}
        Transfer.Plan(jobs.Sum(x=>x.Size)+prepared,jobs.Count,prepared);
        Stage("2/5 核对本地缓存与完整文件，计算剩余下载量");
        var pending=new List<Job>();
        foreach(var job in jobs){Token.ThrowIfCancellationRequested();if(Valid(job.Path,job.Sha,job.Size))Transfer.Cache(job.Size);else{pending.Add(job);Transfer.Need(job.Size);}}
        var summary=Transfer.Snapshot();Log("资源总量："+TransferProgress.FormatBytes(summary.Total)+"；本地复用："+TransferProgress.FormatBytes(summary.Reused)+"；本次需下载："+TransferProgress.FormatBytes(summary.DownloadTotal)+"，"+pending.Count+" 个文件");
        Stage("3/5 下载并校验游戏本体、依赖与资源");
        Parallel.ForEach(pending,new ParallelOptions{MaxDegreeOfParallelism=8,CancellationToken=Token},job=> {
            Download(job.Url,job.Path,job.Sha,job.Size);Transfer.Complete(job.Size);
        });
        Stage("4/5 整理资源与版本隔离目录");
        if(assets!=null&&(Convert.ToBoolean(Get(assets,"virtual")??false)||Convert.ToBoolean(Get(assets,"map_to_resources")??false))) {
            Log("整理旧版资源目录");
            foreach(var item in Obj(Get(assets,"objects"))) {
                Token.ThrowIfCancellationRequested(); string h=Str(item.Value,"hash"); string source=Safe(root,"assets/objects/"+h.Substring(0,2)+"/"+h);
                var targets=new List<string>();
                if(Convert.ToBoolean(Get(assets,"virtual")??false)) targets.Add(Safe(root,"assets/virtual/"+Str(index,"id")+"/"+item.Key));
                if(Convert.ToBoolean(Get(assets,"map_to_resources")??false)) targets.Add(Safe(isolation?stage:root,"resources/"+item.Key));
                foreach(string target in targets) { Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(source,target,true); }
            }
        }
        Directory.CreateDirectory(Safe(stage,"PCL"));
        PclIntegration.WriteIni(Safe(stage,"PCL/Setup.ini"),new Dictionary<string,string>{{"VersionArgumentIndie",isolation?"1":"2"},{"VersionArgumentIndieV2",isolation?"True":"False"}},null);
        if(isolation){Directory.CreateDirectory(Safe(stage,"saves"));Directory.CreateDirectory(Safe(stage,"mods"));}
        Stage("5/5 发布已校验版本到 versions 目录");
        Token.ThrowIfCancellationRequested();Directory.CreateDirectory(Safe(root,"versions"));Directory.Move(stage,versionDir);
        string java=Str(Get(meta,"javaVersion"),"majorVersion");
        Log("安装完成："+versionDir+"。所需 Java："+(java==""?"8（旧版）":java)+"，请由 PCL 选择或安装。重新打开 PCL 查看版本。");
        Transfer.Stage("完成");
    }
}

public class MainForm:Form {
    TextBox folder=new TextBox(),log=new TextBox(),launcher=new TextBox(); ComboBox versions=new ComboBox(),source=new ComboBox();
    CheckBox snapshots=new CheckBox(),isolation=new CheckBox(),proxy=new CheckBox(),autoImport=new CheckBox(),reopen=new CheckBox();
    Button refresh=new Button(),install=new Button(),cancel=new Button(),browse=new Button(),scan=new Button(),import=new Button(),chooseLauncher=new Button();
    ProgressBar progress=new ProgressBar(); Label status=new Label(),stats=new Label(),current=new Label(),target=new Label(); CancellationTokenSource cts; bool busy;
    Label folderHint=new Label();string approvedNewRoot;DateTime nextDirectoryCheck=DateTime.MinValue;
    Engine engine;System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly System.Collections.Concurrent.ConcurrentQueue<string> messages=new System.Collections.Concurrent.ConcurrentQueue<string>();
    readonly Queue<Tuple<DateTime,long>> samples=new Queue<Tuple<DateTime,long>>();
    string lastId,sessionLog;
    List<object> entries=new List<object>();
    public MainForm() {
        Text="Minecraft 版本下载助手 2.1";Size=new Size(940,915);MinimumSize=new Size(940,915);Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;
        var title=new Label{Text="Minecraft 版本下载助手",Font=new Font(Font.FontFamily,18,FontStyle.Bold),AutoSize=true,Location=new Point(24,20)};Controls.Add(title);
        Controls.Add(new Label{Text="选择版本 → 汇总大小 → 实时下载与校验 → 自动导入 PCL2",AutoSize=true,Location=new Point(26,62)});
        folder.SetBounds(26,105,648,30);browse.Text="浏览";browse.SetBounds(685,103,80,32);scan.Text="自动查找";scan.SetBounds(775,103,100,32);
        folderHint.SetBounds(26,138,849,26);folderHint.AutoEllipsis=true;
        launcher.SetBounds(26,175,648,30);launcher.ReadOnly=true;chooseLauncher.Text="选择 PCL EXE";chooseLauncher.SetBounds(685,173,190,32);
        source.SetBounds(26,224,400,30);source.DropDownStyle=ComboBoxStyle.DropDownList;source.Items.AddRange(new object[]{"国内优先：BMCLAPI → 官方源","仅 BMCLAPI 国内镜像","仅 Mojang 官方源"});source.SelectedIndex=0;
        proxy.Text="使用系统代理";proxy.SetBounds(458,224,200,30);
        versions.SetBounds(26,275,400,30);versions.DropDownStyle=ComboBoxStyle.DropDownList;
        refresh.Text="获取版本列表";refresh.SetBounds(440,273,140,34);snapshots.Text="显示快照";snapshots.SetBounds(600,275,155,30);
        isolation.Text="适配 PCL 系统版本隔离（存档 / 模组 / 设置独立放入 versions\\版本号）";isolation.Checked=true;isolation.SetBounds(26,322,849,30);
        autoImport.Text="自动导入 PCL2（导入前正常退出 PCL；8 秒未退出则结束所选启动器进程）";autoImport.Checked=true;autoImport.SetBounds(26,355,870,30);
        reopen.Text="导入完成后打开所选 PCL2";reopen.Checked=true;reopen.SetBounds(26,388,849,30);
        install.Text="下载 / 继续所选版本";install.SetBounds(26,432,210,42);cancel.Text="取消";cancel.SetBounds(250,432,95,42);cancel.Enabled=false;
        import.Text="导入 / 修复 PCL";import.SetBounds(360,432,180,42);
        progress.SetBounds(26,492,849,22);status.Text="先获取版本列表。支持原版；Java 请在 PCL 中配置。";status.SetBounds(26,526,849,26);
        stats.Text="总量：待获取清单    网速：0.0 KB/s";stats.SetBounds(26,556,849,50);
        current.SetBounds(26,609,849,40);current.AutoEllipsis=true;
        target.SetBounds(26,651,849,26);target.AutoEllipsis=true;
        log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.SetBounds(26,684,849,137);log.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;
        var credit=new LinkLabel{Text="国内镜像由 BMCLAPI / bangbang93 提供",AutoSize=true,Location=new Point(26,836),Anchor=AnchorStyles.Bottom|AnchorStyles.Left};
        credit.LinkClicked+=delegate{System.Diagnostics.Process.Start("https://bmclapidoc.bangbang93.com/");};
        Controls.AddRange(new Control[]{folder,browse,scan,folderHint,launcher,chooseLauncher,source,proxy,versions,refresh,snapshots,isolation,autoImport,reopen,install,cancel,import,progress,status,stats,current,target,log,credit});
        folder.Text=FindFolder()??"";launcher.Text=PclIntegration.FindLauncher(folder.Text)??"";install.Enabled=false;
        browse.Click+=delegate{using(var dialog=new FolderBrowserDialog{Description="选择已有 .minecraft，或选择首次安装位置（将在其中创建 .minecraft）"})if(dialog.ShowDialog()==DialogResult.OK)try{SelectRoot(LauncherDiscovery.ResolveManualFolder(dialog.SelectedPath));}catch(Exception ex){MessageBox.Show(this,ex.Message,"选择目录");}};
        scan.Click+=delegate{approvedNewRoot=null;folder.Text=FindFolder()??"";launcher.Text=PclIntegration.FindLauncher(folder.Text)??"";CheckDirectory();if(folder.Text=="")status.Text="找不到 Minecraft 文件夹，请手动选择。";};
        chooseLauncher.Click+=delegate{ChooseLauncher();};
        snapshots.CheckedChanged+=delegate{ShowVersions();};
        folder.TextChanged+=delegate{approvedNewRoot=null;UpdateTarget();CheckDirectory();};versions.SelectedIndexChanged+=delegate{UpdateTarget();};UpdateTarget();CheckDirectory();if(!RootReady())status.Text="找不到 Minecraft 文件夹，请手动选择。";
        refresh.Click+=async delegate {await Run(false);}; install.Click+=async delegate{await Run(true);};cancel.Click+=delegate{if(cts!=null) cts.Cancel();};
        import.Click+=async delegate{await Import();};
        timer.Interval=250;timer.Tick+=delegate{Tick();};timer.Start();
        FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){if(cts!=null)cts.Cancel();e.Cancel=true;Append("正在取消，请等待当前操作停止后关闭窗口。");}};
        FormClosed+=delegate{timer.Dispose();};
    }
    public static string Normalize(string path) {return PclIntegration.Normalize(path);}
    public static string FindFolder() {
        string here=AppDomain.CurrentDomain.BaseDirectory;
        var candidates=new List<string>{Path.Combine(here,".minecraft"),here,@"D:\Minecraft\.minecraft",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),".minecraft")};
        string pcl=LauncherDiscovery.Find(null);if(pcl!=null)candidates.Insert(0,Path.Combine(Path.GetDirectoryName(pcl),".minecraft"));
        foreach(var drive in DriveInfo.GetDrives()){try{if(!drive.IsReady||drive.DriveType!=DriveType.Fixed)continue;foreach(string name in new[]{"Minecraft","Games\\Minecraft","MC"})candidates.Add(Path.Combine(drive.RootDirectory.FullName,name,".minecraft"));}catch{}}
        return LauncherDiscovery.FindGameFolder(candidates);
    }
    void SelectRoot(string root){
        bool exists=Directory.Exists(root);
        if(!exists&&MessageBox.Show(this,"这里还没有 Minecraft 资料目录。\n首次下载时将在下列位置创建：\n"+root+"\n\n是否使用这个位置？","首次安装位置",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        folder.Text=root;approvedNewRoot=exists?null:root;string found=PclIntegration.FindLauncher(root);if(found!=null)launcher.Text=found;UpdateTarget();CheckDirectory();
    }
    bool RootReady(){try{string path=Normalize(folder.Text);return Directory.Exists(path)||String.Equals(path,approvedNewRoot,StringComparison.OrdinalIgnoreCase);}catch{return false;}}
    void CheckDirectory(){
        bool exists=false;try{exists=Directory.Exists(Normalize(folder.Text));}catch{}
        if(exists)approvedNewRoot=null;
        bool planned=!exists&&RootReady();folderHint.ForeColor=exists?Color.DarkGreen:planned?Color.DarkOrange:Color.Firebrick;
        folderHint.Text=exists?"目录存在（游戏文件会在下载 / 导入时校验）":planned?"首次安装：该目录尚不存在，已确认在开始下载时创建。":"找不到 Minecraft 文件夹，请点击“浏览”手动选择。";
        if(!busy){install.Enabled=versions.Items.Count>0&&RootReady();import.Enabled=exists;}
        if(!exists&&!planned&&!busy)target.Text="尚未确定安装位置，请手动选择文件夹。";else UpdateTarget();
    }
    bool ChooseLauncher(){
        using(var dialog=new OpenFileDialog{Filter="PCL 启动器程序 (*.exe)|*.exe",Title="选择 PCL2 的 EXE（改过名字也可以）"}){
            if(dialog.ShowDialog(this)!=DialogResult.OK)return false;
            try{LauncherDiscovery.Remember(dialog.FileName);launcher.Text=Path.GetFullPath(dialog.FileName);
                if(!RootReady())SelectRoot(Path.Combine(Path.GetDirectoryName(launcher.Text),".minecraft"));return true;
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"选择 PCL");return false;}
        }
    }
    string EnsureLauncher(){if(LauncherDiscovery.IsPcl(launcher.Text))return launcher.Text;string detected=PclIntegration.FindLauncher(RootReady()?Normalize(folder.Text):null);if(detected!=null){launcher.Text=detected;return detected;}MessageBox.Show(this,"找不到唯一可识别的 PCL 程序，请手动选择对应的 EXE。","选择 PCL");return ChooseLauncher()?launcher.Text:null;}
    bool ConfirmLifecycle(string exe){return !LauncherLifecycle.IsRunning(exe)||MessageBox.Show(this,"检测到所选 PCL 正在运行。\n导入前将先请求它正常退出；8 秒仍未退出则结束该启动器进程。\n"+exe+"\n\n游戏进程不会被结束，完成后按勾选项重新打开 PCL。\n是否继续？","PCL 导入准备",MessageBoxButtons.OKCancel)==DialogResult.OK;}
    void UpdateTarget(){try{string id=versions.SelectedItem is Item?Engine.Str(((Item)versions.SelectedItem).Entry,"id"):"版本号";target.Text="最终安装位置："+Path.Combine(Normalize(folder.Text),"versions",id);}catch{target.Text="请选择有效的游戏根目录";}}
    void Append(string text){string line=DateTime.Now.ToString("HH:mm:ss")+" "+text+Environment.NewLine;log.AppendText(line);try{if(sessionLog!=null)File.AppendAllText(sessionLog,line,Encoding.UTF8);}catch{}}
    void NewLog(){string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MinecraftFolderDownloader","logs");Directory.CreateDirectory(dir);sessionLog=Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".log");}
    void Tick(){string message;while(messages.TryDequeue(out message))Append(message);if(DateTime.UtcNow>=nextDirectoryCheck){nextDirectoryCheck=DateTime.UtcNow.AddSeconds(1);CheckDirectory();}if(!busy||engine==null)return;
        var s=engine.Transfer.Snapshot();var now=DateTime.UtcNow;samples.Enqueue(Tuple.Create(now,s.Network));while(samples.Count>1&&(now-samples.Peek().Item1).TotalSeconds>2)samples.Dequeue();
        var first=samples.Peek();double speed=(s.Network-first.Item2)/Math.Max(0.001,(now-first.Item1).TotalSeconds);
        progress.Style=s.Planned?ProgressBarStyle.Continuous:ProgressBarStyle.Marquee;
        progress.Value=(int)Math.Min(100,Math.Max(0,s.Percent));status.Text=s.Stage;
        if(s.Planned){double remaining=Math.Max(0,s.Total-s.Verified-s.InFlight);string eta=speed>1024?TimeSpan.FromSeconds(Math.Min(remaining/speed,359999)).ToString(@"hh\:mm\:ss"):"计算中";
            stats.Text="总进度 "+s.Percent.ToString("0.0")+"%   "+TransferProgress.FormatBytes(Math.Min(s.Total,s.Verified+s.InFlight))+" / "+TransferProgress.FormatBytes(s.Total)+"   网速 "+TransferProgress.FormatBytes(speed)+"/s   剩余约 "+eta+"\r\n已校验 "+s.Completed+" / "+s.Files+" 个文件   本地复用 "+TransferProgress.FormatBytes(s.Reused)+"   本次计划下载 "+TransferProgress.FormatBytes(s.DownloadTotal);
        }else stats.Text="正在获取版本与资源清单，随后汇总准确大小…   网速 "+TransferProgress.FormatBytes(speed)+"/s";
        current.Text=s.Current==""?"当前：检查本地文件 / 等待下一项任务":"当前："+s.Current;
    }
    void ShowVersions(){versions.Items.Clear();foreach(var e in entries)if(StrType(e)=="release"||snapshots.Checked&&StrType(e)=="snapshot") versions.Items.Add(new Item(e));if(versions.Items.Count>0)versions.SelectedIndex=0; install.Enabled=!busy&&versions.Items.Count>0&&RootReady();}
    static string StrType(object e){return Engine.Str(e,"type");}
    class Item{public object Entry;public Item(object e){Entry=e;}public override string ToString(){return Engine.Str(Entry,"id")+(StrType(Entry)=="snapshot"?"  [快照]":"  [正式版]");}}
    async Task Run(bool installing) {
        if(busy)return;var selected=versions.SelectedItem as Item; if(installing&&selected==null)return;
        string root;try{if(!RootReady())throw new IOException("找不到 Minecraft 文件夹，请点击“浏览”手动选择；首次安装也需先确认创建位置。");root=Normalize(folder.Text.Trim());if(new DirectoryInfo(root).Parent==null)throw new IOException("请选择 .minecraft 文件夹，不要选择磁盘根目录");}catch(Exception ex){MessageBox.Show(this,ex.Message,"选择目录");CheckDirectory();return;}
        string exe=null;bool register=autoImport.Checked,openAfter=reopen.Checked;
        if(installing&&register){try{exe=EnsureLauncher();if(exe==null||!RootReady()||!ConfirmLifecycle(exe))return;root=Normalize(folder.Text);}catch(Exception ex){MessageBox.Show(this,ex.Message,"PCL 检查");return;}}
        if(installing&&MessageBox.Show("版本："+Engine.Str(selected.Entry,"id")+"\n目录："+root+"\n\n将下载游戏本体、依赖和资源；完成后用 PCL 启动。", "开始下载",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        string previouslyApproved=approvedNewRoot;folder.Text=root;if(String.Equals(root,previouslyApproved,StringComparison.OrdinalIgnoreCase))approvedNewRoot=root;NewLog();busy=true;cts=new CancellationTokenSource();SetBusy();progress.Value=0;samples.Clear();
        engine=new Engine{Source=source.SelectedIndex,Proxy=proxy.Checked,Token=cts.Token,Log=s=>messages.Enqueue(s)};
        bool indie=isolation.Checked;
        try {
            status.Text=installing?"正在下载…":"正在获取版本列表…";
            if(installing) {await Task.Run(()=>engine.Install(selected.Entry,root,indie));lastId=Engine.Str(selected.Entry,"id");Tick();engine=null;
                if(register){try{bool opened=await ImportSelected(root,lastId,exe,openAfter);status.Text=opened?"完成：已导入并打开 PCL2，版本 "+lastId:"完成：已导入 PCL2，版本 "+lastId;}catch(Exception ex){Append("游戏已下载；PCL 导入待完成："+ex.Message);status.Text="下载完成；PCL 导入未完成，可点击“导入 / 修复 PCL”重试。";}}
                else status.Text="下载完成："+lastId+"。可点击“导入 / 修复 PCL”。";
                progress.Style=ProgressBarStyle.Continuous;progress.Value=100;current.Text="已保存到："+Path.Combine(root,"versions",lastId);
            }
            else {var manifest=await Task.Run(()=>engine.Json("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));entries=Engine.Arr(Engine.Get(manifest,"versions")).ToList();ShowVersions();Append("已获取 "+entries.Count+" 个版本。来源策略："+source.Text);status.Text="请选择要下载的版本。";}
        }catch(Exception ex){var agg=ex as AggregateException;if(agg!=null)ex=agg.Flatten().InnerExceptions[0];status.Text=cts.IsCancellationRequested?"已取消；再次下载会校验并复用已完成文件。":"操作失败，请查看下方日志。";Append(cts.IsCancellationRequested?"下载已取消。":ex.Message);}
        finally {busy=false;progress.Style=ProgressBarStyle.Continuous;string message;while(messages.TryDequeue(out message))Append(message);Append("本次日志："+sessionLog);cts.Dispose();cts=null;SetBusy();}
    }
    async Task<bool> ImportSelected(string root,string id,string exe,bool openAfter){
        status.Text="正在退出所选 PCL 并导入版本…";bool success=false;bool wasRunning=LauncherLifecycle.IsRunning(exe);Exception failure=null;
        try{
            await Task.Run(()=>PclIntegration.ValidateVersion(root,id));
            await Task.Run(()=>LauncherLifecycle.Close(exe,cts.Token,s=>messages.Enqueue(s)));
            cts.Token.ThrowIfCancellationRequested();
            string backup=await Task.Run(()=>PclIntegration.Register(root,id,exe,true));success=true;
            Append("已导入 PCL2，原设置备份："+backup);
        }catch(Exception ex){failure=ex;}
        if(success&&openAfter||!success&&wasRunning){try{await Task.Run(()=>LauncherLifecycle.Open(exe));Append("已重新打开所选 PCL："+exe);}catch(Exception ex){Append("PCL 需要手动打开："+ex.Message);}}
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return success&&openAfter&&LauncherLifecycle.IsRunning(exe);
    }
    async Task Import(){if(busy)return;bool started=false;try{if(!RootReady())throw new IOException("找不到 Minecraft 文件夹，请先手动选择。");string root=Normalize(folder.Text);string id=lastId;var selected=versions.SelectedItem as Item;if(selected!=null&&Directory.Exists(Engine.Safe(root,"versions/"+Engine.Str(selected.Entry,"id"))))id=Engine.Str(selected.Entry,"id");
        if(id==null){string versionsRoot=Engine.Safe(root,"versions");var available=Directory.Exists(versionsRoot)?Directory.GetDirectories(versionsRoot).Where(p=>File.Exists(Path.Combine(p,Path.GetFileName(p)+".json"))&&File.Exists(Path.Combine(p,Path.GetFileName(p)+".jar"))).OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray():new string[0];if(available.Length==0)throw new IOException("尚无完整版本。请先下载 / 继续下载，不能导入 .download-staging 临时目录。");id=Path.GetFileName(available[0]);}
        string exe=EnsureLauncher();if(exe==null||!ConfirmLifecycle(exe))return;NewLog();busy=true;started=true;engine=null;cts=new CancellationTokenSource();SetBusy();bool opened=await ImportSelected(root,id,exe,reopen.Checked);status.Text=opened?"已导入 "+id+" 并打开 PCL2。":"已导入 "+id+"，现在可以打开 PCL2。";
        }catch(Exception ex){Append(ex.Message);status.Text="导入未完成，请查看日志。";}finally{if(started){busy=false;cts.Dispose();cts=null;}SetBusy();}}
    void SetBusy(){folder.Enabled=browse.Enabled=scan.Enabled=source.Enabled=proxy.Enabled=versions.Enabled=refresh.Enabled=snapshots.Enabled=isolation.Enabled=autoImport.Enabled=reopen.Enabled=chooseLauncher.Enabled=!busy;install.Enabled=!busy&&versions.Items.Count>0&&RootReady();import.Enabled=!busy&&RootReady();cancel.Enabled=busy;CheckDirectory();}
    [STAThread] public static void Main(string[] args){
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;ServicePointManager.DefaultConnectionLimit=16;
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Contains("--self-test")){try{SelfTest.Run();RegressionTests.Run();DiscoveryLifecycleTests.Run();}catch(Exception ex){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test-error.txt"),ex.ToString());Environment.Exit(1);}return;}
        if(args.Contains("--network-test")){SelfTest.Network();return;}
        if(args.Contains("--install")){Headless.Run(args);return;}
        if(args.Contains("--preview")||args.Contains("--preview-progress")||args.Contains("--preview-missing")||args.Contains("--preview-selected")){using(var f=new MainForm()){f.Opacity=0;f.ShowInTaskbar=false;f.Show();Application.DoEvents();if(args.Contains("--preview-selected")||args.Contains("--preview-progress")){f.entries.Add(Engine.Parse("{\"id\":\"1.20.1\",\"type\":\"release\"}"));f.ShowVersions();}if(args.Contains("--preview-progress"))f.PreviewProgress();if(args.Contains("--preview-missing")){f.folder.Text="";f.approvedNewRoot=null;f.CheckDirectory();f.status.Text="找不到 Minecraft 文件夹，请手动选择。";}using(var bitmap=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(bitmap,new Rectangle(0,0,f.Width,f.Height));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,args.Contains("--preview-progress")?"preview-progress.png":args.Contains("--preview-missing")?"preview-missing.png":args.Contains("--preview-selected")?"preview-selected.png":"preview.png"));}}return;}
        Application.Run(new MainForm());
    }
    public void PreviewProgress(){engine=new Engine();engine.Transfer.Plan(735854596,3641,413067);engine.Transfer.Cache(120000000);engine.Transfer.Need(615441529);engine.Transfer.Begin("preview","示例 · 游戏本体 1.20.1.jar");engine.Transfer.Receive("preview",15728640);engine.Transfer.Stage("3/5 下载并校验游戏本体、依赖与资源（示例进度）");samples.Enqueue(Tuple.Create(DateTime.UtcNow.AddSeconds(-1),0L));busy=true;Tick();busy=false;}
}

public static class SelfTest {
    public static void Network(){
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"network-test");Directory.CreateDirectory(root);
        try{
            var engine=new Engine{Source=0};var manifest=engine.Json("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            object entry=Engine.Arr(Engine.Get(manifest,"versions")).First(x=>Engine.Str(x,"id")=="1.20.1");
            string metadata=Path.Combine(root,"1.20.1.json");engine.Download(Engine.Str(entry,"url"),metadata,Engine.Str(entry,"sha1"),-1);
            var parsed=Engine.Parse(File.ReadAllText(metadata));
            var artifact=Engine.Get(Engine.Get(Engine.Arr(Engine.Get(parsed,"libraries")).First(x=>Engine.Allowed(x)&&Engine.Get(Engine.Get(x,"downloads"),"artifact")!=null),"downloads"),"artifact");
            engine.Download(Engine.Str(artifact,"url"),Path.Combine(root,"library.jar"),Engine.Str(artifact,"sha1"),Engine.Size(artifact));
            var index=Engine.Get(parsed,"assetIndex");string ip=Path.Combine(root,"assets.json");engine.Download(Engine.Str(index,"url"),ip,Engine.Str(index,"sha1"),Engine.Size(index));
            var asset=Engine.Obj(Engine.Get(Engine.Parse(File.ReadAllText(ip)),"objects")).First();string h=Engine.Str(asset.Value,"hash");
            engine.Download("https://resources.download.minecraft.net/"+h.Substring(0,2)+"/"+h,Path.Combine(root,"asset.bin"),h,Engine.Size(asset.Value));
            File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: live manifest, 1.20.1 metadata, library, asset index and asset; SHA-1 verified.\r\n");
        }catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.txt"),"FAIL: "+ex.ToString());}
    }
    class FixtureEngine:Engine {
        public Dictionary<string,byte[]> Files=new Dictionary<string,byte[]>();
        public override void Download(string url,string path,string sha,long size){Token.ThrowIfCancellationRequested();Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,Files[url]);if(!Valid(path,sha,size))throw new IOException("fixture hash mismatch");}
        public object FileEntry(string url,string path,string text){byte[] bytes=Encoding.UTF8.GetBytes(text);Files[url]=bytes;string sha;using(var hash=SHA1.Create())sha=BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();return new Dictionary<string,object>{{"url",url},{"path",path},{"sha1",sha},{"size",bytes.Length}};}
    }
    public static void Run(){
        int checks=0; Action<bool> check=b=>{if(!b)throw new Exception("测试失败："+checks);checks++;};
        string root=Path.Combine(Path.GetTempPath(),"MinecraftDownloader-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            check(Engine.Mirrored("https://resources.download.minecraft.net/ab/abcdef")==Engine.Mirror+"/assets/ab/abcdef");
            check(Engine.Mirrored("https://libraries.minecraft.net/a.jar")==Engine.Mirror+"/maven/a.jar");
            foreach(string bad in new[]{"../escape","C:\\escape","a/../../escape","a:b"}) {bool rejected=false;try{Engine.Safe(root,bad);}catch(IOException){rejected=true;}check(rejected);}
            check(Engine.Allowed(Engine.Parse("{}")));
            check(!Engine.Allowed(Engine.Parse("{\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":\"linux\"}}]}")));
            check(Engine.Allowed(Engine.Parse("{\"rules\":[{\"action\":\"allow\"},{\"action\":\"disallow\",\"os\":{\"name\":\"osx\"}}]}")));
            check(!Engine.Allowed(Engine.Parse("{\"rules\":[{\"action\":\"allow\",\"features\":{\"is_demo_user\":true}}]}")));
            string f=Path.Combine(root,"hash");File.WriteAllText(f,"abc",new UTF8Encoding(false));check(Engine.Valid(f,"a9993e364706816aba3e25717850c26c9cd0d89d",3));check(!Engine.Valid(f,"a9993e364706816aba3e25717850c26c9cd0d89d",4));
            var engine=new Engine();engine.Source=0;check(engine.Sources("https://libraries.minecraft.net/a").Count()==2);engine.Source=1;check(engine.Sources("https://libraries.minecraft.net/a").Count()==1);
            string v=Engine.Safe(root,"versions/1.20.1");Directory.CreateDirectory(v);bool protectedExisting=false;try{engine.Install(Engine.Parse("{\"id\":\"1.20.1\"}"),root,true);}catch(IOException){protectedExisting=true;}check(protectedExisting);
            var fixture=new FixtureEngine();var serializer=new JavaScriptSerializer();
            var client=fixture.FileEntry("https://fixture/client","","client fixture");
            var library=fixture.FileEntry("https://fixture/library","org/test/1/test.jar","library fixture");
            var meta=new Dictionary<string,object>();
            meta["id"]="test-version";
            meta["downloads"]=new Dictionary<string,object>{{"client",client}};
            var lib=new Dictionary<string,object>();lib["downloads"]=new Dictionary<string,object>{{"artifact",library}};
            meta["libraries"]=new object[]{lib};
            var entry=Engine.Obj(fixture.FileEntry("https://fixture/meta","",serializer.Serialize(meta)));entry["id"]="test-version";
            fixture.Install(entry,root,true);
            check(File.Exists(Engine.Safe(root,"versions/test-version/test-version.json")));
            check(File.Exists(Engine.Safe(root,"versions/test-version/test-version.jar")));
            check(File.ReadAllText(Engine.Safe(root,"versions/test-version/PCL/Setup.ini")).Contains("VersionArgumentIndieV2:True"));
            check(File.Exists(Engine.Safe(root,"libraries/org/test/1/test.jar")));
            entry["id"]="cancel-test";var cancelled=new CancellationTokenSource();cancelled.Cancel();fixture.Token=cancelled.Token;bool didCancel=false;try{fixture.Install(entry,root,true);}catch(OperationCanceledException){didCancel=true;}check(didCancel&&!Directory.Exists(Engine.Safe(root,"versions/cancel-test")));
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test-result.txt"),"PASS: "+checks+" checks, including fixture install and cancellation\r\n",Encoding.UTF8);
        }finally{Directory.Delete(root,true);}
    }
}
