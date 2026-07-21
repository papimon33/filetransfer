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
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class SyncUI : Form {
    string cfgPath, logPath;
    string server = "https://qr-upload-server.onrender.com";
    volatile string token = "";
    string sabeon = "", dest = "", securegate = "", listdir = "", srcSha = "";
    int intervalMs = 4000;
    // 받는 폴더에 직접 넣은 파일도 자동 투입(기본 ON)
    bool watchFolder = true;
    readonly HashSet<string> fedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // [파일보내기] 자동 클릭 (기본 OFF — 켠 사람만 사용)
    bool autoSend = false;
    int autoSendStableSec = 5;      // 목록 건수가 이 시간만큼 변화 없어야 클릭(대용량 등록 대기)
    int autoSendTimeoutSec = 900;   // 대용량 감안한 최대 대기

    TextBox txtSabeon, txtPin, txtLog;
    Button btnEnroll;
    Label lblStatus, lblUrl, lblUpdate;
    Button btnUpdate;
    PictureBox picQr;
    CheckBox chkAuto, chkSend, chkWatch;
    NotifyIcon tray;
    Thread syncThread;
    volatile bool running = true;

    const string MUTEX_NAME = "SecureGateSyncUI_SingleInstance";
    const string EVENT_NAME = "SecureGateSyncUI_ShowWindow";
    static Mutex _mutex;
    static EventWaitHandle _showEvent;

    [STAThread]
    static void Main(string[] args) {
        bool startTray = false, afterUpdate = false;
        foreach (var a in args) {
            if (a == "/tray") startTray = true;
            if (a == "/updated") afterUpdate = true;
        }
        bool createdNew;
        _mutex = new Mutex(true, MUTEX_NAME, out createdNew);
        if (!createdNew && afterUpdate) {
            // 업데이트 직후 재시작 — 이전 프로세스가 완전히 끝날 때까지 최대 10초 대기
            for (int i = 0; i < 40 && !createdNew; i++) {
                Thread.Sleep(250);
                try { _mutex.Close(); } catch { }
                _mutex = new Mutex(true, MUTEX_NAME, out createdNew);
            }
        }
        if (!createdNew) {
            // 이미 실행 중 — 기존 인스턴스에게 "창 보이기" 신호만 보내고 종료
            try { EventWaitHandle.OpenExisting(EVENT_NAME).Set(); } catch { }
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EVENT_NAME);
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SyncUI(startTray));
        GC.KeepAlive(_mutex);
    }

    public SyncUI(bool startTray) {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureGateSync");
        Directory.CreateDirectory(dir);
        cfgPath = Path.Combine(dir, "ui.config");
        logPath = Path.Combine(dir, "ui.log");
        LoadConfig();
        BuildUi();
        StartShowListener();
        StartUpdateChecker();
        StartFolderWatch();
        if (!string.IsNullOrEmpty(token)) {
            txtSabeon.Text = sabeon;
            LoadQr();
            StartSync();
            SetStatus("동기화 중 — 폰 업로드를 기다립니다.");
            if (startTray) startHidden = true;   // 실제 숨김은 SetVisibleCore 에서(핸들 생성 후)
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
                    else if (k == "autosend") autoSend = (v == "1" || v.ToLower() == "true");
                    else if (k == "autosend_stable") int.TryParse(v, out autoSendStableSec);
                    else if (k == "autosend_timeout") int.TryParse(v, out autoSendTimeoutSec);
                    else if (k == "srcsha") srcSha = v;
                    else if (k == "watchfolder") watchFolder = (v == "1" || v.ToLower() == "true");
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
        sb.Append("autosend=").Append(autoSend ? "true" : "false").Append("\r\n");
        sb.Append("autosend_stable=").Append(autoSendStableSec).Append("\r\n");
        sb.Append("autosend_timeout=").Append(autoSendTimeoutSec).Append("\r\n");
        sb.Append("srcsha=").Append(srcSha).Append("\r\n");
        sb.Append("watchfolder=").Append(watchFolder ? "true" : "false").Append("\r\n");
        try { File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    // ── UI ──
    Icon appIcon;
    void BuildUi() {
        Text = "SecureGate 사진 자동전송";
        try { appIcon = MakeAppIcon(); Icon = appIcon; } catch { }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(420, 560);
        Font = new Font("Malgun Gothic", 9F);

        var l1 = new Label { Text = "사번", Location = new Point(14, 18), AutoSize = true };
        txtSabeon = new TextBox { Location = new Point(48, 15), Size = new Size(90, 24), MaxLength = 5, CharacterCasing = CharacterCasing.Upper };
        var l2 = new Label { Text = "PIN", Location = new Point(148, 18), AutoSize = true };
        txtPin = new TextBox { Location = new Point(182, 15), Size = new Size(80, 24), MaxLength = 6,
                               UseSystemPasswordChar = true };
        btnEnroll = new Button { Text = "발급 / 등록", Location = new Point(272, 14), Size = new Size(120, 26) };
        btnEnroll.Click += (s, e) => DoEnroll();
        txtSabeon.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { txtPin.Focus(); e.SuppressKeyPress = true; } };
        txtPin.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { DoEnroll(); e.SuppressKeyPress = true; } };
        var lblPinHint = new Label { Text = "PIN 숫자 4~6자리 — 최초 등록 시 정한 PIN이어야 내 사번을 쓸 수 있습니다.",
                                     Location = new Point(14, 44), Size = new Size(392, 18),
                                     ForeColor = Color.DimGray, Font = new Font("Malgun Gothic", 8F) };

        lblStatus = new Label { Location = new Point(14, 64), Size = new Size(392, 22), ForeColor = Color.DimGray };

        picQr = new PictureBox { Location = new Point(110, 90), Size = new Size(200, 200), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
        var lblQrHint = new Label { Text = "↑ 폰 카메라로 이 QR을 스캔해 사진 업로드", Location = new Point(14, 294), Size = new Size(392, 20), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray };
        lblUrl = new Label { Location = new Point(14, 314), Size = new Size(392, 20), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.SteelBlue, AutoEllipsis = true };

        chkAuto = new CheckBox { Text = "로그인 시 자동 시작", Location = new Point(14, 340), AutoSize = true };
        chkAuto.Checked = File.Exists(StartupLnk());
        chkAuto.CheckedChanged += (s, e) => SetAutostart(chkAuto.Checked);

        chkSend = new CheckBox { Text = "전송목록 등록이 끝나면 [파일보내기] 자동 클릭", Location = new Point(14, 362), AutoSize = true };
        chkSend.Checked = autoSend;
        chkSend.CheckedChanged += (s, e) => { autoSend = chkSend.Checked; SaveConfig();
            Log(autoSend ? "자동보내기 켬" : "자동보내기 끔"); };

        chkWatch = new CheckBox { Text = "받는 폴더에 직접 넣은 파일도 자동 투입", Location = new Point(14, 384), AutoSize = true };
        chkWatch.Checked = watchFolder;
        chkWatch.CheckedChanged += (s, e) => { watchFolder = chkWatch.Checked; SaveConfig();
            Log(watchFolder ? "폴더 감시 켬: " + dest : "폴더 감시 끔"); };

        lblUpdate = new Label { Location = new Point(14, 412), Size = new Size(250, 22), ForeColor = Color.OrangeRed,
                                TextAlign = ContentAlignment.MiddleLeft, Visible = false };
        btnUpdate = new Button { Text = "지금 업데이트", Location = new Point(270, 408), Size = new Size(136, 26), Visible = false };
        btnUpdate.Click += (s, e) => ApplyUpdate();

        txtLog = new TextBox { Location = new Point(14, 440), Size = new Size(392, 104), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

        Controls.AddRange(new Control[] { l1, txtSabeon, l2, txtPin, btnEnroll, lblPinHint, lblStatus,
                                          picQr, lblQrHint, lblUrl, chkAuto, chkSend, chkWatch,
                                          lblUpdate, btnUpdate, txtLog });

        tray = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, Text = "SecureGate 자동전송", Visible = true };
        var menu = new ContextMenu();
        menu.MenuItems.Add("열기", (s, e) => ShowWindow());
        menu.MenuItems.Add("종료", (s, e) => { running = false; tray.Visible = false; Application.Exit(); });
        tray.ContextMenu = menu;
        tray.DoubleClick += (s, e) => ShowWindow();
        // 최소화(_)는 기본 동작 유지 → 작업표시줄에 남음. 닫기(X)만 트레이로 보냄.
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); ShowInTaskbar = false; tray.ShowBalloonTip(1500, "SecureGate 자동전송", "트레이에서 계속 실행됩니다. 종료하려면 트레이 아이콘 우클릭 → 종료.", ToolTipIcon.Info); } };
    }
    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Activate(); BringToFront(); }

    // 자동시작(/tray): 첫 표시를 건너뛰고 트레이로만 뜸.
    // 생성자에서 Hide()/BeginInvoke 를 부르면 핸들 미생성으로 예외 → 프로세스 즉사하므로 여기서 처리.
    bool startHidden;
    protected override void SetVisibleCore(bool value) {
        if (startHidden) {
            startHidden = false;
            if (!IsHandleCreated) CreateHandle();   // 핸들만 생성(화면 표시 X)
            ShowInTaskbar = false;
            value = false;
        }
        base.SetVisibleCore(value);
    }

    // 파란 둥근 사각 위에 흰 카메라 — 코드로 그려 .ico 파일 없이 아이콘 생성
    static Icon MakeAppIcon() {
        using (var bmp = new Bitmap(32, 32)) {
            using (var g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var path = RoundRect(new Rectangle(1, 1, 30, 30), 7))
                using (var b = new SolidBrush(Color.FromArgb(37, 99, 235))) g.FillPath(b, path);
                using (var b = new SolidBrush(Color.White)) {
                    g.FillRectangle(b, 6, 12, 20, 13);   // 카메라 몸통
                    g.FillRectangle(b, 11, 8, 7, 4);     // 뷰파인더 돌출
                }
                using (var b = new SolidBrush(Color.FromArgb(37, 99, 235))) g.FillEllipse(b, 12, 14, 8, 8); // 렌즈 테
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 14, 16, 4, 4);                  // 렌즈 안
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
    static GraphicsPath RoundRect(Rectangle r, int rad) {
        var p = new GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 중복 실행 시 두 번째 인스턴스가 보낸 신호를 받아 창을 앞으로
    void StartShowListener() {
        if (_showEvent == null) return;
        var t = new Thread(() => {
            while (running) {
                try { if (_showEvent.WaitOne(1000)) { try { BeginInvoke((Action)(() => ShowWindow())); } catch { } } }
                catch { break; }
            }
        });
        t.IsBackground = true; t.Start();
    }
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
        string pn = (txtPin.Text ?? "").Trim();
        if (sb.Length != 5) { MessageBox.Show("사번은 5글자입니다.", "안내"); return; }
        bool digits = pn.Length > 0;
        foreach (char c in pn) if (c < '0' || c > '9') digits = false;
        if (pn.Length < 4 || pn.Length > 6 || !digits) {
            MessageBox.Show("PIN은 숫자 4~6자리입니다.\n\n처음 등록하는 사번이면 여기서 정한 PIN이 내 사번의 잠금이 되고,\n이미 등록된 사번이면 그때 정한 PIN을 입력해야 합니다.", "안내"); return;
        }
        btnEnroll.Enabled = false; SetStatus("발급 요청 중...");
        ThreadPool.QueueUserWorkItem(_ => {
            try {
                var data = new NameValueCollection(); data["sabeon"] = sb; data["pin"] = pn;
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
                try {
                    if (we.Response != null)
                        using (var sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8)) {
                            string body = sr.ReadToEnd();
                            try {   // {"ok":false,"error":"..."} 에서 사람이 읽을 메시지만 추출
                                var eo = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(body);
                                if (eo != null && eo.ContainsKey("error")) msg = Convert.ToString(eo["error"]);
                                else msg = body;
                            } catch { msg = body; }
                        }
                } catch { }
                SetStatus("발급 실패: " + msg);
                Log("발급 실패: " + msg);
                string m = msg;
                try { BeginInvoke((Action)(() => MessageBox.Show(m, "발급 실패"))); } catch { }
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
                File.Move(part, final); got.Add(final);
                lock (fedFiles) fedFiles.Add(final);      // 폴더 감시가 중복 투입하지 않도록
                Log("저장: " + Path.GetFileName(final));
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
            AutoSendWhenReady();
        } catch (Exception e) { Log("SecureGate 투입 실패: " + e.Message); }
    }
    // ── SecureGate [파일보내기] 자동 클릭 ───────────────────────────────
    // 좌표 클릭이 아니라 컨트롤 ID로 버튼을 찾아 BM_CLICK 을 보냄(해상도/창위치 무관).
    // 대용량 파일은 SecureGate 가 목록에 등록하는 데 시간이 걸리므로,
    // "목록 건수가 N초간 변화 없음 + 버튼 활성" 이 될 때까지 기다린 뒤 클릭한다.
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    const int  ID_SEND = 3006;             // [파일보내기] 버튼
    const int  ID_LIST = 3009;             // 전송 파일 목록(SysListView32)
    const uint BM_CLICK = 0x00F5;
    const uint LVM_GETITEMCOUNT = 0x1004;

    static string WinText(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }

    static IntPtr FindTransferWindow() {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            if (WinText(h).IndexOf("자료전송", StringComparison.Ordinal) < 0) return true;
            int pid; GetWindowThreadProcessId(h, out pid);
            try {
                if (Process.GetProcessById(pid).ProcessName.IndexOf("SecureGate", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
            } catch { return true; }
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    static void FindSendControls(IntPtr root, out IntPtr btn, out IntPtr list) {
        IntPtr b = IntPtr.Zero, lv = IntPtr.Zero;
        EnumChildWindows(root, delegate(IntPtr h, IntPtr l) {
            int id = GetDlgCtrlID(h);
            // ID + 텍스트 이중 검증 — 엉뚱한 버튼을 누르지 않도록
            if (id == ID_SEND && b == IntPtr.Zero && WinText(h) == "파일보내기") b = h;
            else if (id == ID_LIST && lv == IntPtr.Zero) lv = h;
            return true;
        }, IntPtr.Zero);
        btn = b; list = lv;
    }

    void AutoSendWhenReady() {
        if (!autoSend) return;
        ThreadPool.QueueUserWorkItem(_ => {
            try {
                DateTime deadline = DateTime.Now.AddSeconds(autoSendTimeoutSec);
                IntPtr win = IntPtr.Zero;
                while (DateTime.Now < deadline && win == IntPtr.Zero) {
                    win = FindTransferWindow();
                    if (win == IntPtr.Zero) Thread.Sleep(1000);
                }
                if (win == IntPtr.Zero) { Log("자동보내기: 자료전송 창을 찾지 못함 → 직접 눌러주세요"); return; }

                IntPtr btn, lv; FindSendControls(win, out btn, out lv);
                if (btn == IntPtr.Zero || lv == IntPtr.Zero) {
                    Log("자동보내기: 버튼/목록을 찾지 못함(프로그램 버전 변경?) → 직접 눌러주세요"); return;
                }

                int last = -1; DateTime stableSince = DateTime.Now;
                while (DateTime.Now < deadline) {
                    int cnt = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    if (cnt != last) {                       // 아직 등록 중(대용량이면 오래 걸림)
                        last = cnt; stableSince = DateTime.Now;
                        Log("자동보내기: 목록 " + cnt + "건 등록중...");
                    } else if (cnt > 0 && IsWindowEnabled(btn)
                               && (DateTime.Now - stableSince).TotalSeconds >= autoSendStableSec) {
                        SendMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Log("자동보내기: [파일보내기] 클릭 — " + cnt + "건");
                        // SecureGate 가 목록을 비우면 접수된 것 → 그때 완료 알림
                        bool accepted = false;
                        DateTime until = DateTime.Now.AddSeconds(120);
                        while (DateTime.Now < until) {
                            Thread.Sleep(1000);
                            int now = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                            if (now < cnt) { accepted = true; break; }
                        }
                        if (accepted) Notify("✅ 자료전송 완료", cnt + "건을 SecureGate 로 전송했습니다.");
                        else Notify("자료전송 요청함", cnt + "건 전송을 눌렀습니다. 자료전송 창을 확인하세요.");
                        return;
                    }
                    Thread.Sleep(500);
                }
                Log("자동보내기: 대기 시간 초과(" + autoSendTimeoutSec + "초) → 직접 눌러주세요");
            } catch (Exception e) { Log("자동보내기 오류: " + e.Message + " → 직접 눌러주세요"); }
        });
    }

    // ── 받는 폴더 직접 감시 ────────────────────────────────────────
    // 폰 업로드가 아니라 사용자가 직접 폴더에 옮겨넣은 파일도 SecureGate 에 투입한다.
    // · 앱 시작 시점에 이미 있던 파일은 '처리됨'으로 기준선을 잡아 재시작 때 재전송하지 않음
    // · 크기가 안정되고 잠금이 풀린 뒤에만 투입(복사 중인 대용량 파일 방지)
    void StartFolderWatch() {
        var t = new Thread(() => {
            try { if (Directory.Exists(dest)) foreach (var f in Directory.GetFiles(dest)) fedFiles.Add(f); }
            catch { }
            var sizes = new Dictionary<string, long>();
            while (running) {
                try {
                    if (watchFolder && Directory.Exists(dest)) {
                        var batch = new List<string>();
                        foreach (var f in Directory.GetFiles(dest)) {
                            if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                            lock (fedFiles) { if (fedFiles.Contains(f)) continue; }
                            long len;
                            try { len = new FileInfo(f).Length; } catch { continue; }
                            long prev;
                            if (!sizes.TryGetValue(f, out prev) || prev != len) { sizes[f] = len; continue; }
                            if (!IsFileReady(f)) continue;      // 아직 쓰는 중
                            batch.Add(f);
                        }
                        if (batch.Count > 0) {
                            lock (fedFiles) foreach (var f in batch) fedFiles.Add(f);
                            foreach (var f in batch) sizes.Remove(f);
                            Log("폴더에서 새 파일 " + batch.Count + "개 발견 → SecureGate 투입");
                            Feed(batch);
                        }
                    }
                } catch (Exception e) { Log("폴더 감시 오류: " + e.Message); }
                Thread.Sleep(3000);
            }
        });
        t.IsBackground = true; t.Start();
    }

    static bool IsFileReady(string p) {
        try { using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
        catch { return false; }
    }

    /// 윈도우 알림(트레이 풍선) — UI 스레드로 마샬링
    void Notify(string title, string text) {
        Log(title + " — " + text);
        try { BeginInvoke((Action)(() => {
            try { tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); } catch { }
        })); } catch { }
    }

    // ── 자동 업데이트(알림 후 확인) ──────────────────────────────────
    // 서버의 GUI 소스 sha256 을 주기적으로 확인 → 다르면 알림만 띄우고,
    // 사용자가 [지금 업데이트] 를 누르면 소스를 받아 로컬 컴파일 후 교체·재시작.
    // (완성된 exe 를 내려받지 않으므로 설치 때와 동일하게 보안SW 마찰이 적음)
    volatile bool updateBusy = false;
    string newSha = "", newVer = "";

    void StartUpdateChecker() {
        var t = new Thread(() => {
            Thread.Sleep(15000);                       // 시작 직후 1회
            while (running) {
                CheckUpdate(true);
                for (int i = 0; i < 360 && running; i++) Thread.Sleep(60000);   // 이후 6시간마다
            }
        });
        t.IsBackground = true; t.Start();
    }

    void CheckUpdate(bool silent) {
        ThreadPool.QueueUserWorkItem(_ => {
            try {
                string body;
                using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; body = wc.DownloadString(server + "/agent/version"); }
                var o = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                if (o == null || !o.ContainsKey("sha256")) return;
                string sha = Convert.ToString(o["sha256"]);
                string ver = o.ContainsKey("version") ? Convert.ToString(o["version"]) : "";
                if (string.IsNullOrEmpty(srcSha)) {     // 방금 설치 = 지금 소스가 기준
                    srcSha = sha; SaveConfig();
                    if (!silent) Log("최신 버전입니다 (v" + ver + ")");
                    return;
                }
                if (sha != srcSha) {
                    newSha = sha; newVer = ver;
                    Log("새 버전 발견: v" + ver + " — [지금 업데이트] 를 누르세요");
                    try { BeginInvoke((Action)(() => {
                        lblUpdate.Text = "🔔 새 버전 v" + ver + " 사용 가능";
                        lblUpdate.Visible = true; btnUpdate.Visible = true; btnUpdate.Enabled = true;
                    })); } catch { }
                    try { tray.ShowBalloonTip(4000, "SecureGate 자동전송",
                          "새 버전 v" + ver + " 이 있습니다. 앱을 열어 [지금 업데이트]를 누르세요.", ToolTipIcon.Info); } catch { }
                } else if (!silent) Log("최신 버전입니다 (v" + ver + ")");
            } catch (Exception e) { if (!silent) Log("업데이트 확인 실패: " + e.Message); }
        });
    }

    void ApplyUpdate() {
        if (updateBusy) return;
        updateBusy = true;
        btnUpdate.Enabled = false;
        SetStatus("업데이트 중... (소스 받아 컴파일)");
        ThreadPool.QueueUserWorkItem(_ => {
            string exe    = Application.ExecutablePath;
            string dir    = Path.GetDirectoryName(exe);
            string newCs  = Path.Combine(dir, "SecureGateSyncUI.new.cs");
            string newExe = Path.Combine(dir, "SecureGateSyncUI.new.exe");
            string oldExe = Path.Combine(dir, "SecureGateSyncUI.old.exe");
            try {
                string src;
                using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; src = wc.DownloadString(server + "/agent/source.cs"); }
                if (src.Length < 1000) throw new Exception("소스가 비정상적으로 짧음");
                File.WriteAllText(newCs, src, new UTF8Encoding(true));

                string win = Environment.GetEnvironmentVariable("WINDIR");
                string csc = Path.Combine(win, @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
                if (!File.Exists(csc)) csc = Path.Combine(win, @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
                if (!File.Exists(csc)) throw new Exception("csc.exe 를 찾을 수 없음");

                string ico = Path.Combine(dir, "app.ico");
                string args = "/nologo /target:winexe \"/out:" + newExe + "\""
                            + " /r:System.Windows.Forms.dll /r:System.Drawing.dll"
                            + " /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll"
                            + (File.Exists(ico) ? " \"/win32icon:" + ico + "\"" : "")
                            + " \"" + newCs + "\"";
                var psi = new ProcessStartInfo(csc, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using (var pc = Process.Start(psi)) pc.WaitForExit(180000);
                if (!File.Exists(newExe)) throw new Exception("컴파일 실패 — 기존 버전 유지");

                // 실행 중인 exe 는 덮어쓸 수 없지만 이름 변경은 가능
                if (File.Exists(oldExe)) { try { File.Delete(oldExe); } catch { } }
                File.Move(exe, oldExe);
                try { File.Move(newExe, exe); }
                catch { File.Move(oldExe, exe); throw; }      // 실패 시 롤백

                srcSha = newSha; SaveConfig();
                Log("업데이트 완료 (v" + newVer + ") — 재시작합니다");
                try { Process.Start(exe, "/updated"); } catch { }
                running = false;
                try { BeginInvoke((Action)(() => { tray.Visible = false; Application.Exit(); })); } catch { }
            } catch (Exception e) {
                Log("업데이트 실패: " + e.Message + " — 기존 버전으로 계속 실행합니다");
                SetStatus("업데이트 실패 — 기존 버전 유지");
                try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
                try { BeginInvoke((Action)(() => btnUpdate.Enabled = true)); } catch { }
            } finally {
                updateBusy = false;
                try { if (File.Exists(newCs)) File.Delete(newCs); } catch { }
            }
        });
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
