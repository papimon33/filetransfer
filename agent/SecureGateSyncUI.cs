// SecureGateSyncUI — 사번 발급/QR 확인 + 백그라운드 동기화(pull + SecureGate 투입) GUI 앱.
// 빌드: csc /nologo /target:winexe /out:SecureGateSyncUI.exe ^
//        /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll ^
//        SecureGateSyncUI.cs
// 설정: %LOCALAPPDATA%\SecureGateSync\ui.config  (key=value: server, token, sabeon, dest, securegate, listdir)
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class SyncUI : Form {
    string cfgPath, logPath;
    string server = "https://qr-upload-server.onrender.com";
    volatile string token = "";
    string sabeon = "", dest = "", securegate = "", listdir = "";
    int intervalMs = 4000;

    TextBox txtSabeon, txtLog;
    Button btnEnroll;
    Label lblStatus, lblUrl;
    PictureBox picQr;
    CheckBox chkAuto;
    NotifyIcon tray;
    Thread syncThread;
    volatile bool running = true;

    [STAThread]
    static void Main(string[] args) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool startTray = false;
        foreach (var a in args) if (a == "/tray") startTray = true;
        Application.Run(new SyncUI(startTray));
    }

    public SyncUI(bool startTray) {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureGateSync");
        Directory.CreateDirectory(dir);
        cfgPath = Path.Combine(dir, "ui.config");
        logPath = Path.Combine(dir, "ui.log");
        LoadConfig();
        BuildUi();
        if (!string.IsNullOrEmpty(token)) {
            txtSabeon.Text = sabeon;
            LoadQr();
            StartSync();
            SetStatus("동기화 중 — 폰 업로드를 기다립니다.");
            if (startTray) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; BeginInvoke((Action)(() => Hide())); }
        } else {
            SetStatus("사번을 입력하고 [발급/등록]을 누르세요.");
        }
    }

    // ── 설정 ──
    void LoadConfig() {
        try {
            if (File.Exists(cfgPath))
                foreach (var ln in File.ReadAllLines(cfgPath, Encoding.UTF8)) {
                    int i = ln.IndexOf('=');
                    if (i <= 0) continue;
                    string k = ln.Substring(0, i).Trim(), v = ln.Substring(i + 1).Trim();
                    if (k == "server" && v != "") server = v.TrimEnd('/');
                    else if (k == "token") token = v;
                    else if (k == "sabeon") sabeon = v;
                    else if (k == "dest") dest = v;
                    else if (k == "securegate") securegate = v;
                    else if (k == "listdir") listdir = v;
                }
        } catch { }
        if (dest == "") dest = "C:\\SecureGateWatch";
        if (securegate == "") securegate = "C:\\HANSSAK\\SecureGateEX\\SecureGate.exe";
        if (listdir == "") listdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData\\LocalLow\\HANSSAK\\RList");
    }
    void SaveConfig() {
        var sb = new StringBuilder();
        sb.Append("server=").Append(server).Append("\r\n");
        sb.Append("token=").Append(token).Append("\r\n");
        sb.Append("sabeon=").Append(sabeon).Append("\r\n");
        sb.Append("dest=").Append(dest).Append("\r\n");
        sb.Append("securegate=").Append(securegate).Append("\r\n");
        sb.Append("listdir=").Append(listdir).Append("\r\n");
        try { File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    // ── UI ──
    void BuildUi() {
        Text = "SecureGate 사진 자동전송";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(420, 470);
        Font = new Font("Malgun Gothic", 9F);

        var l1 = new Label { Text = "사번(5글자):", Location = new Point(14, 18), AutoSize = true };
        txtSabeon = new TextBox { Location = new Point(100, 15), Size = new Size(120, 24), MaxLength = 5, CharacterCasing = CharacterCasing.Upper };
        btnEnroll = new Button { Text = "발급 / 등록", Location = new Point(232, 14), Size = new Size(160, 26) };
        btnEnroll.Click += (s, e) => DoEnroll();
        txtSabeon.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { DoEnroll(); e.SuppressKeyPress = true; } };

        lblStatus = new Label { Location = new Point(14, 48), Size = new Size(392, 22), ForeColor = Color.DimGray };

        picQr = new PictureBox { Location = new Point(110, 76), Size = new Size(200, 200), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
        var lblQrHint = new Label { Text = "↑ 폰 카메라로 이 QR을 스캔해 사진 업로드", Location = new Point(14, 280), Size = new Size(392, 20), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray };
        lblUrl = new Label { Location = new Point(14, 300), Size = new Size(392, 20), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.SteelBlue, AutoEllipsis = true };

        chkAuto = new CheckBox { Text = "로그인 시 자동 시작", Location = new Point(14, 326), AutoSize = true };
        chkAuto.Checked = File.Exists(StartupLnk());
        chkAuto.CheckedChanged += (s, e) => SetAutostart(chkAuto.Checked);

        txtLog = new TextBox { Location = new Point(14, 352), Size = new Size(392, 104), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

        Controls.AddRange(new Control[] { l1, txtSabeon, btnEnroll, lblStatus, picQr, lblQrHint, lblUrl, chkAuto, txtLog });

        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SecureGate 자동전송", Visible = true };
        var menu = new ContextMenu();
        menu.MenuItems.Add("열기", (s, e) => ShowWindow());
        menu.MenuItems.Add("종료", (s, e) => { running = false; tray.Visible = false; Application.Exit(); });
        tray.ContextMenu = menu;
        tray.DoubleClick += (s, e) => ShowWindow();
        Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) { Hide(); ShowInTaskbar = false; } };
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); ShowInTaskbar = false; tray.ShowBalloonTip(1500, "SecureGate 자동전송", "트레이에서 계속 실행됩니다. 종료하려면 트레이 아이콘 우클릭 → 종료.", ToolTipIcon.Info); } };
    }
    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Activate(); }
    string StartupLnk() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SecureGateSync.lnk"); }

    void SetAutostart(bool on) {
        string lnk = StartupLnk();
        try {
            if (!on) { if (File.Exists(lnk)) File.Delete(lnk); Log("자동시작 해제"); return; }
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic sc = sh.CreateShortcut(lnk);
            sc.TargetPath = Application.ExecutablePath;
            sc.Arguments = "/tray";
            sc.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            sc.WindowStyle = 7;
            sc.Save();
            Log("자동시작 등록");
        } catch (Exception e) { Log("자동시작 설정 실패: " + e.Message); }
    }

    // ── 발급 ──
    void DoEnroll() {
        string sb = (txtSabeon.Text ?? "").Trim();
        if (sb.Length != 5) { MessageBox.Show("사번은 5글자입니다.", "안내"); return; }
        btnEnroll.Enabled = false; SetStatus("발급 요청 중...");
        ThreadPool.QueueUserWorkItem(_ => {
            try {
                var data = new NameValueCollection(); data["sabeon"] = sb;
                byte[] resp;
                using (var wc = new WebClient()) resp = wc.UploadValues(server + "/api/enroll", data);
                var js = new JavaScriptSerializer();
                var o = (Dictionary<string, object>)js.DeserializeObject(Encoding.UTF8.GetString(resp));
                if (o != null && Convert.ToBoolean(o["ok"])) {
                    token = Convert.ToString(o["token"]);
                    sabeon = sb;
                    bool existed = o.ContainsKey("existed") && Convert.ToBoolean(o["existed"]);
                    SaveConfig();
                    Log((existed ? "기존 토큰 등록" : "새 토큰 발급") + " (사번 " + sb + ")");
                    BeginInvoke((Action)(() => { LoadQr(); }));
                    SetStatus("동기화 중 — 폰 업로드를 기다립니다.");
                    StartSync();
                } else {
                    SetStatus("발급 실패");
                }
            } catch (WebException we) {
                string msg = we.Message;
                try { if (we.Response != null) using (var sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8)) msg = sr.ReadToEnd(); } catch { }
                SetStatus("발급 실패: " + msg);
                Log("발급 실패: " + msg);
            } catch (Exception e) { SetStatus("발급 실패: " + e.Message); Log("발급 실패: " + e.Message); }
            finally { BeginInvoke((Action)(() => btnEnroll.Enabled = true)); }
        });
    }

    void LoadQr() {
        if (string.IsNullOrEmpty(token)) return;
        try {
            byte[] b; using (var wc = new WebClient()) b = wc.DownloadData(server + "/u/" + token + "/qr.png");
            picQr.Image = Image.FromStream(new MemoryStream(b));
            lblUrl.Text = server + "/u/" + token;
        } catch (Exception e) { Log("QR 로드 실패: " + e.Message); }
    }

    // ── 동기화 루프 ──
    void StartSync() {
        if (syncThread != null && syncThread.IsAlive) return;
        syncThread = new Thread(SyncLoop); syncThread.IsBackground = true; syncThread.Start();
    }
    void SyncLoop() {
        while (running) {
            try { if (!string.IsNullOrEmpty(token)) SyncOnce(); } catch (Exception e) { Log("동기화 오류: " + e.Message); }
            Thread.Sleep(intervalMs);
        }
    }
    void SyncOnce() {
        string body;
        var req = (HttpWebRequest)WebRequest.Create(server + "/u/" + token + "/list"); req.Timeout = 30000;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
        var js = new JavaScriptSerializer();
        var o = js.DeserializeObject(body) as Dictionary<string, object>;
        if (o == null || !o.ContainsKey("files")) return;
        var files = o["files"] as object[];
        if (files == null || files.Length == 0) return;
        Log(files.Length + "장 수신 → 다운로드");
        Directory.CreateDirectory(dest);
        var got = new List<string>();
        foreach (var fo in files) {
            var d = fo as Dictionary<string, object>; if (d == null || !d.ContainsKey("name")) continue;
            string name = Convert.ToString(d["name"]); if (string.IsNullOrEmpty(name)) continue;
            string url = server + "/u/" + token + "/file/" + Uri.EscapeDataString(name);
            string final = Unique(Path.Combine(dest, name)); string part = final + ".part";
            try {
                var fr = (HttpWebRequest)WebRequest.Create(url); fr.Timeout = 120000;
                using (var fresp = (HttpWebResponse)fr.GetResponse())
                using (var ins = fresp.GetResponseStream())
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write)) ins.CopyTo(fs);
                File.Move(part, final); got.Add(final); Log("저장: " + Path.GetFileName(final));
                try { var dr = (HttpWebRequest)WebRequest.Create(url); dr.Method = "DELETE"; dr.Timeout = 30000; using (var x = (HttpWebResponse)dr.GetResponse()) { } } catch { }
            } catch (Exception e) { Log("다운로드 실패: " + name + " (" + e.Message + ")"); try { if (File.Exists(part)) File.Delete(part); } catch { } }
        }
        if (got.Count > 0) Feed(got);
    }
    void Feed(List<string> paths) {
        if (string.IsNullOrEmpty(securegate) || !File.Exists(securegate)) { Log("SecureGate 없음(투입 생략): " + securegate); return; }
        try {
            Directory.CreateDirectory(listdir);
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string lp = Path.Combine(listdir, stamp + ".txt"); int n = 1;
            while (File.Exists(lp)) { lp = Path.Combine(listdir, stamp + "_" + n + ".txt"); n++; }
            File.WriteAllText(lp, string.Join("\r\n", paths.ToArray()) + "\r\n", new UnicodeEncoding(false, true));
            var psi = new ProcessStartInfo(securegate, "F " + paths.Count + " " + lp); psi.UseShellExecute = true;
            Process.Start(psi);
            Log("SecureGate 투입: " + paths.Count + "장");
        } catch (Exception e) { Log("SecureGate 투입 실패: " + e.Message); }
    }
    static string Unique(string p) {
        if (!File.Exists(p)) return p;
        string dir = Path.GetDirectoryName(p), b = Path.GetFileNameWithoutExtension(p), e = Path.GetExtension(p); int i = 1; string c;
        do { c = Path.Combine(dir, b + "(" + i + ")" + e); i++; } while (File.Exists(c)); return c;
    }

    // ── 로그/상태 ──
    void Log(string msg) {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
        try { File.AppendAllText(logPath, line + "\r\n", new UTF8Encoding(false)); } catch { }
        try { if (txtLog != null && txtLog.IsHandleCreated) txtLog.BeginInvoke((Action)(() => { txtLog.AppendText(line + "\r\n"); })); } catch { }
    }
    void SetStatus(string s) {
        try { if (lblStatus != null && lblStatus.IsHandleCreated) lblStatus.BeginInvoke((Action)(() => lblStatus.Text = s)); else if (lblStatus != null) lblStatus.Text = s; } catch { }
    }
}
