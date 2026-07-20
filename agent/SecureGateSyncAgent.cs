// SecureGateSyncAgent — QR 업로드 서버에서 '내 토큰' 사진을 주기적으로 당겨와
// (feed=true 면) SecureGate 전송 대기 목록에 자동으로 얹는 올인원 컴파일 에이전트.
//
// ※ powershell.exe 는 보안정책에 막히지만, 이 컴파일 exe 는 허용되는 환경을 위해 C#로 작성.
// 빌드: csc /nologo /target:exe /out:SecureGateSyncAgent.exe /r:System.Web.Extensions.dll SecureGateSyncAgent.cs
// 실행: SecureGateSyncAgent.exe [config파일경로]   (기본: exe 옆의 SecureGateSyncAgent.config)
//
// config(key=value) 예:
//   server=https://...onrender.com
//   token=<개인토큰>
//   dest=C:\Users\...\AppData\Local\SecureGateSync\incoming
//   interval=4000
//   feed=true
//   securegate=C:\HANSSAK\SecureGateEX\SecureGate.exe
//   listdir=C:\Users\...\AppData\LocalLow\HANSSAK\RList
//   log=...\agent.log
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class Agent {
    static string server = "", token = "", dest = "", logPath = "";
    static string securegate = "", listdir = "";
    static bool feed = true;
    static int intervalMs = 4000;

    static void Log(string msg) {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + "\r\n", new UTF8Encoding(false)); } catch { }
    }

    static void Main(string[] args) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        string cfg = args.Length > 0 ? args[0]
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SecureGateSyncAgent.config");
        var kv = new Dictionary<string, string>();
        try {
            foreach (var ln in File.ReadAllLines(cfg, Encoding.UTF8)) {
                int i = ln.IndexOf('=');
                if (i > 0) kv[ln.Substring(0, i).Trim()] = ln.Substring(i + 1).Trim();
            }
        } catch (Exception e) { Console.WriteLine("설정 읽기 실패: " + e.Message); return; }

        if (kv.ContainsKey("server")) server = kv["server"].TrimEnd('/');
        if (kv.ContainsKey("token"))  token = kv["token"];
        if (kv.ContainsKey("dest"))   dest = kv["dest"];
        if (kv.ContainsKey("securegate")) securegate = kv["securegate"];
        if (kv.ContainsKey("listdir")) listdir = kv["listdir"];
        if (kv.ContainsKey("feed")) feed = kv["feed"].Trim().ToLower() == "true";
        if (kv.ContainsKey("interval")) int.TryParse(kv["interval"], out intervalMs);
        logPath = kv.ContainsKey("log") ? kv["log"]
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.log");
        if (intervalMs < 1000) intervalMs = 1000;

        try { Directory.CreateDirectory(dest); } catch { }
        Log("SecureGateSyncAgent 시작  server=" + server + "  dest=" + dest + "  feed=" + feed + "  interval=" + intervalMs + "ms");
        if (server == "" || token == "" || dest == "") { Log("설정 부족(server/token/dest) — 종료"); return; }

        while (true) {
            try { SyncOnce(); } catch (Exception e) { Log("루프 오류: " + e.Message); }
            Thread.Sleep(intervalMs);
        }
    }

    static void SyncOnce() {
        string body;
        var req = (HttpWebRequest)WebRequest.Create(server + "/u/" + token + "/list");
        req.Timeout = 30000;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            body = sr.ReadToEnd();

        var js = new JavaScriptSerializer();
        var obj = js.DeserializeObject(body) as Dictionary<string, object>;
        if (obj == null || !obj.ContainsKey("files")) return;
        var files = obj["files"] as object[];
        if (files == null || files.Length == 0) return;
        Log("새 파일 " + files.Length + "개 → 다운로드");

        var got = new List<string>();
        foreach (var fo in files) {
            var d = fo as Dictionary<string, object>;
            if (d == null || !d.ContainsKey("name")) continue;
            string name = Convert.ToString(d["name"]);
            if (string.IsNullOrEmpty(name)) continue;
            string url = server + "/u/" + token + "/file/" + Uri.EscapeDataString(name);
            string finalPath = UniquePath(Path.Combine(dest, name));
            string part = finalPath + ".part";
            try {
                var fr = (HttpWebRequest)WebRequest.Create(url); fr.Timeout = 120000;
                using (var fresp = (HttpWebResponse)fr.GetResponse())
                using (var input = fresp.GetResponseStream())
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
                    input.CopyTo(fs);
                File.Move(part, finalPath);
                got.Add(finalPath);
                Log("저장: " + finalPath);
                try {
                    var dr = (HttpWebRequest)WebRequest.Create(url); dr.Method = "DELETE"; dr.Timeout = 30000;
                    using (var dresp = (HttpWebResponse)dr.GetResponse()) { }
                } catch (Exception de) { Log("서버삭제 실패: " + name + " (" + de.Message + ")"); }
            } catch (Exception e) {
                Log("다운로드 실패: " + name + " (" + e.Message + ")");
                try { if (File.Exists(part)) File.Delete(part); } catch { }
            }
        }

        if (feed && got.Count > 0) FeedSecureGate(got);
    }

    // SecureGate 전송 대기 목록에 얹기: UTF-16LE+BOM 목록 txt 생성 후
    //   SecureGate.exe F <개수> <목록경로>   (목록경로는 따옴표 없이 — SecureGate가 따옴표를 안 벗김)
    static void FeedSecureGate(List<string> paths) {
        if (string.IsNullOrEmpty(securegate) || !File.Exists(securegate)) {
            Log("SecureGate 실행파일 없음(feed 생략): " + securegate); return;
        }
        try {
            if (string.IsNullOrEmpty(listdir)) listdir = Path.Combine(dest, "RList");
            Directory.CreateDirectory(listdir);
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string lp = Path.Combine(listdir, stamp + ".txt");
            int n = 1;
            while (File.Exists(lp)) { lp = Path.Combine(listdir, stamp + "_" + n + ".txt"); n++; }
            File.WriteAllText(lp, string.Join("\r\n", paths.ToArray()) + "\r\n",
                              new UnicodeEncoding(false, true));   // UTF-16LE + BOM
            var psi = new ProcessStartInfo(securegate, "F " + paths.Count + " " + lp);
            psi.UseShellExecute = true;
            Process.Start(psi);
            Log("SecureGate 투입: F " + paths.Count + " " + lp);
        } catch (Exception e) { Log("SecureGate 투입 실패: " + e.Message); }
    }

    static string UniquePath(string p) {
        if (!File.Exists(p)) return p;
        string dir = Path.GetDirectoryName(p), b = Path.GetFileNameWithoutExtension(p), e = Path.GetExtension(p);
        int i = 1; string c;
        do { c = Path.Combine(dir, b + "(" + i + ")" + e); i++; } while (File.Exists(c));
        return c;
    }
}
