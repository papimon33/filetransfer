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
    int intervalMs = 3000;   // 서버 폴링 주기(10명 규모 트래픽 고려 3s)
    // 받는 폴더에 직접 넣은 파일도 자동 투입(항상 ON — 옵션 제거됨)
    readonly bool watchFolder = true;
    readonly HashSet<string> fedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // 다운로드 폴더 감시 → 새 다운로드마다 [자료전송]/[무시] 토스트(기본 ON)
    bool askDownloads = true;
    string downloadsDir = "";
    readonly List<Form> toasts = new List<Form>();
    // [파일보내기] 자동 클릭 (기본 OFF — 켠 사람만 사용)
    bool autoSend = false;
    int autoSendStableSec = 3;      // 목록 건수가 이 시간만큼 변화 없어야 클릭(대용량 등록 대기)
    int autoSendTimeoutSec = 900;   // 대용량 감안한 최대 대기

    TextBox txtSabeon, txtPin, txtLog;
    Button btnEnroll;
    Label lblStatus, lblUrl, lblUpdate;
    Button btnUpdate;
    PictureBox picQr;
    CheckBox chkAuto, chkSend, chkAsk;
    volatile NotifyIcon tray;
    Thread syncThread;
    volatile bool running = true;
    System.Windows.Forms.Timer beatTimer;
    volatile int uiHeartbeat;        // UI 스레드가 스스로 증가(워치독이 읽기만 함)
    // 단조 증가 시계 — Environment.TickCount 는 24.9일마다 음수로 뒤집혀 시간 계산이 깨진다.
    static readonly Stopwatch appClock = Stopwatch.StartNew();
    long uiBeatMs;

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

        // 로그온 자동시작(/tray)일 때는 탐색기(작업표시줄)가 준비된 뒤에 창을 만든다.
        // 창 생성(CreateWindowEx)은 셸에 알림을 보내므로, 로그온 직후 셸이 바쁘면
        // 창을 만드는 도중에 UI 스레드가 붙잡혀 앱이 통째로 멈춘다(실제 장애 원인).
        if (startTray) WaitForShellReady();

        Application.Run(new SyncUI(startTray));
        GC.KeepAlive(_mutex);
    }

    /// 셸(탐색기)이 '실제로 응답'할 때까지 기다린다.
    /// 창을 만들거나 트레이에 등록하는 동작은 셸에 동기 메시지를 보내므로,
    /// 탐색기가 막 시작해 초기화 중이면 그 호출에서 앱이 통째로 멈춘다.
    /// (실측: 탐색기 시작 30초 뒤에 앱이 뜨면서 창 생성 단계에서 정지)
    static void WaitForShellReady() {
        IntPtr tray = IntPtr.Zero;
        for (int i = 0; i < 240 && tray == IntPtr.Zero; i++) {      // 작업표시줄이 생길 때까지(최대 2분)
            tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) Thread.Sleep(500);
        }
        if (tray != IntPtr.Zero) {
            for (int i = 0; i < 120; i++) {                          // 응답할 때까지(최대 1분)
                IntPtr res;
                if (SendMessageTimeout(tray, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero,
                                       SMTO_ABORTIFHUNG, 1000, out res) != IntPtr.Zero) break;
                Thread.Sleep(500);
            }
        }
        Thread.Sleep(5000);                                          // 셸 확장 로딩까지 여유
    }

    public SyncUI(bool startTray) {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureGateSync");
        Directory.CreateDirectory(dir);
        cfgPath = Path.Combine(dir, "ui.config");
        logPath = Path.Combine(dir, "ui.log");
        LoadConfig();
        // 워치독을 '가장 먼저' 띄운다. UI 구성이나 창 생성 도중에 멈추는 경우가 실제로 있었는데,
        // 워치독이 그 뒤에 시작되면 아예 존재하지 않아 복구가 불가능했다.
        StartWatchdog();
        BuildUi();
        StartTrayThread();
        StartShowListener();
        StartUpdateChecker();
        StartFolderWatch();
        StartDownloadWatch();
        if (!string.IsNullOrEmpty(token)) {
            txtSabeon.Text = sabeon;
            SetEnrolledUi(true);            // 이미 등록됨 → 사번/PIN 잠그고 버튼 "재발급"
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
                    // autosend_stable/timeout 은 더 이상 config 에서 읽지 않음(컴파일 기본값 사용 → 업데이트로 개선 전파)
                    else if (k == "srcsha") srcSha = v;
                    else if (k == "askdownloads") askDownloads = (v == "1" || v.ToLower() == "true");
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
        sb.Append("srcsha=").Append(srcSha).Append("\r\n");
        sb.Append("askdownloads=").Append(askDownloads ? "true" : "false").Append("\r\n");
        try { File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
    }

    // ── UI ──
    Icon appIcon;
    void BuildUi() {
        Text = "SecureGate 사진 자동전송";
        try { appIcon = MakeAppIcon(); Icon = appIcon; } catch { }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(420, 562);
        Font = new Font("Malgun Gothic", 9F);

        var l1 = new Label { Text = "사번", Location = new Point(14, 18), AutoSize = true };
        txtSabeon = new TextBox { Location = new Point(48, 15), Size = new Size(90, 24), MaxLength = 5, CharacterCasing = CharacterCasing.Upper };
        var l2 = new Label { Text = "PIN", Location = new Point(148, 18), AutoSize = true };
        txtPin = new TextBox { Location = new Point(182, 15), Size = new Size(80, 24), MaxLength = 6,
                               UseSystemPasswordChar = true };
        btnEnroll = new Button { Text = "발급 / 등록", Location = new Point(272, 14), Size = new Size(120, 26) };
        btnEnroll.Click += (s, e) => OnEnrollButton();
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

        // 받는 폴더 직접 투입은 항상 켜짐(체크박스 제거) → watchFolder 는 상시 true

        chkAsk = new CheckBox { Text = "다운로드할 때마다 자료전송 여부 물어보기", Location = new Point(14, 384), AutoSize = true };
        chkAsk.Checked = askDownloads;
        chkAsk.CheckedChanged += (s, e) => { askDownloads = chkAsk.Checked; SaveConfig();
            Log(askDownloads ? "다운로드 감시 켬: " + downloadsDir : "다운로드 감시 끔"); };

        lblUpdate = new Label { Location = new Point(14, 412), Size = new Size(250, 22), ForeColor = Color.OrangeRed,
                                TextAlign = ContentAlignment.MiddleLeft, Visible = false };
        btnUpdate = new Button { Text = "지금 업데이트", Location = new Point(270, 408), Size = new Size(136, 26), Visible = false };
        btnUpdate.Click += (s, e) => ApplyUpdate();

        txtLog = new TextBox { Location = new Point(14, 440), Size = new Size(392, 104), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };

        Controls.AddRange(new Control[] { l1, txtSabeon, l2, txtPin, btnEnroll, lblPinHint, lblStatus,
                                          picQr, lblQrHint, lblUrl, chkAuto, chkSend, chkAsk,
                                          lblUpdate, btnUpdate, txtLog });

        // 트레이 아이콘은 '전용 스레드'에서 만든다 → StartTrayThread()
        // (Shell_NotifyIcon 은 셸에 동기 메시지를 보내므로, 로그온 직후처럼 셸이 바쁘면
        //  호출 스레드가 무기한 멈춘다. 메인 UI 스레드에서 만들면 앱 전체가 얼어붙음 — 실제 장애 원인)

        // UI 생존 하트비트(워치독이 읽음). 크로스스레드 호출 없이 UI 스레드가 스스로 갱신.
        beatTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        beatTimer.Tick += (s, e) => { uiHeartbeat++; Interlocked.Exchange(ref uiBeatMs, appClock.ElapsedMilliseconds); };
        beatTimer.Start();

        // 최소화(_)는 기본 동작 유지 → 작업표시줄에 남음. 닫기(X)만 트레이로 보냄.
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); ShowInTaskbar = false; OnTrayThread(() => ShowNotifyToast("SecureGate 자동전송", "트레이에서 계속 실행됩니다. 종료: 트레이 아이콘 우클릭 → 종료")); } };
    }

    // ── 트레이 아이콘: 전용 스레드 + 전용 메시지 루프 ──────────────────
    // 셸이 응답하지 않아 이 스레드가 막히더라도 메인 UI(창/동기화)는 정상 동작한다.
    Thread trayThread;
    volatile bool trayReady;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);

    volatile Form trayPump;      // 트레이 스레드 소유 히든 폼 — 토스트를 여기로 마샬링

    void StartTrayThread() {
        trayThread = new Thread(() => {
            try {
                // 로그온 직후엔 탐색기 작업표시줄이 아직 없을 수 있다. 준비될 때까지 대기(최대 90초).
                for (int i = 0; i < 180 && running; i++) {
                    if (FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero) break;
                    Thread.Sleep(500);
                }
                Thread.Sleep(1500);                      // 셸이 초기화를 끝낼 여유

                var pump = new PumpForm();               // 이 스레드의 메시지 루프 주인(화면 표시 없음)
                trayPump = pump;

                var ni = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, Text = "SecureGate 자동전송" };
                var menu = new ContextMenu();
                menu.MenuItems.Add("열기", (s, e) => RequestShowWindow());
                menu.MenuItems.Add("종료", (s, e) => RequestExit());
                ni.ContextMenu = menu;
                ni.DoubleClick += (s, e) => RequestShowWindow();
                tray = ni;
                try { ni.Visible = true; } catch { }
                trayReady = true;
                Application.Run(pump);                   // 이 스레드 전용 메시지 루프
            } catch (Exception e) { Log("트레이 스레드 오류: " + e.Message); }
            finally { trayPump = null; trayReady = false; }
        });
        trayThread.IsBackground = true;
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();
    }

    /// 토스트/알림은 트레이 스레드에서 그린다.
    /// 창 생성·표시는 셸 훅을 건드리므로, 메인 UI 스레드에서 하면 셸이 꼬였을 때 앱이 얼어붙는다.
    /// 트레이 스레드는 막혀도 워치독이 다시 만들면 되는 '버려도 되는' 스레드다.
    void OnTrayThread(Action a) {
        try { var pf = trayPump; if (pf != null && pf.IsHandleCreated) pf.BeginInvoke(a); } catch { }
    }

    // 트레이 스레드 → 메인 UI 로 요청(메인이 막혀 있어도 트레이 스레드는 안 막힘)
    void RequestShowWindow() {
        try { if (IsHandleCreated) BeginInvoke((Action)(() => ShowWindow())); } catch { }
    }
    void RequestExit() {
        exitRequested = true;
        running = false;
        try { if (tray != null) tray.Visible = false; } catch { }
        try { if (IsHandleCreated) BeginInvoke((Action)(() => Application.Exit())); } catch { }
        // 메인 UI 가 막혀 있으면 정상 종료가 안 된다. Environment.Exit 도 종료 절차를 타느라
        // 함께 막힐 수 있으므로, 최후에는 절대 막히지 않는 Kill 로 확실히 끝낸다.
        var t = new Thread(() => { Thread.Sleep(3000); HardKill(); });
        t.IsBackground = true; t.Start();
    }
    volatile bool exitRequested;
    static void HardKill() {
        try { Process.GetCurrentProcess().Kill(); } catch { }
        try { Environment.Exit(0); } catch { }
    }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    /// 창 복원. Activate()/BringToFront() 는 셸의 포그라운드 협상을 거치며,
    /// 트레이 메뉴가 열려 있는 등 셸이 모달 상태면 UI 스레드가 붙잡힐 수 있다.
    /// 실패해도 그만인 SetForegroundWindow 만 쓰고, 창 표시 자체는 로컬 연산으로 끝낸다.
    void ShowWindow() {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        try { SetForegroundWindow(Handle); } catch { }
    }

    // 자동시작(/tray): 첫 표시를 건너뛰고 트레이로만 뜸.
    // 생성자에서 Hide()/BeginInvoke 를 부르면 핸들 미생성으로 예외 → 프로세스 즉사하므로 여기서 처리.
    bool startHidden;
    protected override void SetVisibleCore(bool value) {
        // /tray 로 시작하면 첫 표시만 건너뛰고 트레이에 상주.
        // ShowInTaskbar 를 여기서 건드리면 핸들이 재생성돼 이후 창 복원이 깨지므로 손대지 않는다.
        if (startHidden && !IsHandleCreated) {
            startHidden = false;
            CreateHandle();               // 메시지 펌프용 핸들만 생성(화면 표시 X)
            base.SetVisibleCore(false);
            return;
        }
        startHidden = false;
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
            IntPtr hIcon = bmp.GetHicon();
            try { using (var tmp = Icon.FromHandle(hIcon)) return (Icon)tmp.Clone(); }
            finally { DestroyIcon(hIcon); }        // FromHandle 은 핸들을 소유하지 않음 → 직접 해제
        }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    static GraphicsPath RoundRect(Rectangle r, int rad) {
        var p = new GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ── UI 행(hang) 워치독 ─────────────────────────────────────────
    // UI 스레드에 하트비트를 보내고, 일정 시간 응답이 없으면 로그를 남기고 스스로 재시작한다.
    // (원인 미상의 블록이 또 생겨도 앱이 죽은 채 방치되지 않도록 하는 최후의 안전망)
    // 하트비트는 UI 스레드의 Timer 가 스스로 올린다. 워치독은 '읽기만' 하므로
    // BeginInvoke·락 등 어떤 크로스스레드 호출에도 의존하지 않는다(이전 워치독이 무력화된 이유).
    void StartWatchdog() {
        var t = new Thread(() => {
            int miss = 0, exitWait = 0;
            // 시스템 TickCount 는 24.9일마다 뒤집히므로 단조 증가하는 Stopwatch 로 시간을 잰다.
            long startMs = appClock.ElapsedMilliseconds;
            long lastMs = startMs;
            // running 이 false 여도 계속 돈다 — '종료 요청했는데 안 죽는' 상황까지 책임진다.
            while (true) {
                Thread.Sleep(15000);
                long nowMs = appClock.ElapsedMilliseconds;
                bool resumed = (nowMs - lastMs) > 60000;   // 절전/최대절전 복귀 → 오탐 방지
                lastMs = nowMs;

                if (exitRequested) {                        // 종료를 눌렀는데 프로세스가 안 죽는 경우
                    if (++exitWait >= 2) { Log("종료가 지연됨 — 강제 종료합니다"); HardKill(); }
                    continue;
                }
                if (!running) continue;
                if (resumed || updateBusy) { miss = 0; continue; }

                // UI 초기화(창 핸들 생성)가 끝났는지. 창을 만드는 도중 셸에 붙잡히면
                // 여기가 계속 false 인데, 예전엔 그럴 때 그냥 skip 해서 워치독이 영구히 무력화됐다.
                // → 기동 후 90초가 지나도 준비가 안 되면 그 자체를 '행'으로 판정한다.
                bool uiReady = (beatTimer != null && IsHandleCreated);
                if (!uiReady) {
                    if (nowMs - startMs < 90000) { miss = 0; continue; }   // 정상 기동 여유
                    miss++;
                    Log("경고: UI 초기화가 끝나지 않음 " + miss + "회 (창 생성 단계에서 멈춘 것으로 보임)");
                    if (miss >= 3) { Log("UI 행(hang) 감지 — 앱을 자동 재시작합니다"); RestartSelf(); }
                    continue;
                }

                // 트레이 스레드가 죽었으면(셸 이상 등) 아이콘이 사라진 상태 → 다시 만든다
                if (trayThread != null && !trayThread.IsAlive) {
                    Log("트레이 아이콘이 사라짐 — 다시 등록합니다");
                    trayReady = false;
                    try { StartTrayThread(); } catch { }
                }

                long idle = nowMs - Interlocked.Read(ref uiBeatMs);              // 마지막 하트비트 이후 경과(ms)
                if (idle < 12000) { miss = 0; continue; }
                miss++;
                Log("경고: UI 무응답 " + miss + "회 (" + (idle / 1000) + "초째)");
                if (miss >= 3) {                           // 약 45초+ 확정 행
                    Log("UI 행(hang) 감지 — 앱을 자동 재시작합니다");
                    RestartSelf();
                }
            }
        });
        t.IsBackground = true; t.Start();
    }

    void RestartSelf() {
        try {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c ping -n 4 127.0.0.1 >nul & start \"\" \"" + Application.ExecutablePath + "\" /tray");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            Process.Start(psi);            // 3초 뒤 새 인스턴스 기동(우리가 죽어 뮤텍스가 풀린 뒤)
        } catch { }
        // Environment.Exit 는 종료 절차(파이널라이저 등)를 타므로 UI 가 막힌 상태에서 함께 멈출 수 있다.
        // Kill 은 커널이 즉시 끝내므로 어떤 경우에도 막히지 않는다.
        HardKill();
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
        // COM(WScript.Shell) 은 셸을 거치므로 UI 스레드에서 부르면 멈출 수 있다 → 백그라운드에서 처리
        ThreadPool.QueueUserWorkItem(_ => SetAutostartCore(on));
    }
    void SetAutostartCore(bool on) {
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
    bool enrolledLocked = false;
    void OnEnrollButton() {
        if (enrolledLocked) { SetEnrolledUi(false); return; }   // "재발급" → 잠금 해제하고 다시 입력받기
        DoEnroll();
    }
    // 등록 완료 시 사번/PIN 잠그고 버튼을 "재발급"으로, 해제 시 원복
    void SetEnrolledUi(bool locked) {
        enrolledLocked = locked;
        txtSabeon.Enabled = !locked;
        txtPin.Enabled = !locked;
        if (locked) { txtPin.Text = ""; btnEnroll.Text = "재발급"; }
        else { btnEnroll.Text = "발급 / 등록"; txtSabeon.Focus(); }
    }
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
                using (var wc = new TimedWebClient(30000)) resp = wc.UploadValues(server + "/api/enroll", data);
                var js = new JavaScriptSerializer();
                var o = (Dictionary<string, object>)js.DeserializeObject(Encoding.UTF8.GetString(resp));
                if (o != null && Convert.ToBoolean(o["ok"])) {
                    token = Convert.ToString(o["token"]);
                    sabeon = sb;
                    bool existed = o.ContainsKey("existed") && Convert.ToBoolean(o["existed"]);
                    SaveConfig();
                    Log((existed ? "기존 토큰 등록" : "새 토큰 발급") + " (사번 " + sb + ")");
                    BeginInvoke((Action)(() => { LoadQr(); SetEnrolledUi(true); }));
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
                // 모달 MessageBox 는 트레이 모드(창 숨김)에서 보이지 않은 채 UI 스레드를 붙잡을 수 있다 → 토스트로 알림
                OnTrayThread(() => ShowNotifyToast("발급 실패", m));
            } catch (Exception e) { SetStatus("발급 실패: " + e.Message); Log("발급 실패: " + e.Message); }
            finally { BeginInvoke((Action)(() => btnEnroll.Enabled = true)); }
        });
    }

    // QR 은 반드시 백그라운드에서 받는다.
    // (UI 스레드에서 동기 다운로드하면 서버 콜드스타트/지연 시 앱 전체가 멈춤 — 실제 장애 원인이었음)
    void LoadQr() {
        if (string.IsNullOrEmpty(token)) return;
        string url = server + "/u/" + token + "/qr.png";
        lblUrl.Text = server + "/u/" + token;      // 텍스트는 즉시(UI 스레드에서 호출됨)
        ThreadPool.QueueUserWorkItem(_ => {
            try {
                byte[] b;
                using (var wc = new TimedWebClient(20000)) b = wc.DownloadData(url);
                Image img = Image.FromStream(new MemoryStream(b));
                for (int i = 0; i < 100 && !IsHandleCreated && running; i++) Thread.Sleep(100);
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)(() => {
                    var prev = picQr.Image; picQr.Image = img;
                    if (prev != null) try { prev.Dispose(); } catch { }   // 재발급 반복 시 GDI 누수 방지
                })); } catch { }
            } catch (Exception e) { Log("QR 로드 실패: " + e.Message); }
        });
    }

    // ── 동기화 루프 ──
    void StartSync() {
        if (syncThread != null && syncThread.IsAlive) return;
        syncThread = new Thread(SyncLoop); syncThread.IsBackground = true; syncThread.Start();
    }
    bool wasQuiet = false;
    int failStreak = 0;              // 연속 실패 횟수(지수 백오프용)
    string lastFailMsg = "";
    void SyncLoop() {
        while (running) {
            int wait = intervalMs;
            try {
                bool quiet = IsQuietHours();
                if (quiet != wasQuiet) {
                    Log(quiet ? "야간(21~08시) — 서버 폴링 중지(서버 부하/실행시간 절약)"
                              : "주간 — 서버 폴링 재개");
                    wasQuiet = quiet;
                }
                if (!string.IsNullOrEmpty(token) && !quiet) {
                    SyncOnce();
                    if (failStreak > 0) { Log("서버 연결 복구됨"); SetStatus("동기화 중 — 폰 업로드를 기다립니다."); }
                    failStreak = 0; lastFailMsg = "";
                }
            } catch (Exception e) {
                failStreak++;
                // 서버가 죽었을 때 3초마다 두드리며 로그를 도배하지 않도록:
                // 재시도 간격을 점점 늘리고(최대 5분), 같은 오류는 처음/10회마다만 기록.
                string msg = e.Message;
                if (msg != lastFailMsg || failStreak % 10 == 0) {
                    Log("동기화 오류(" + failStreak + "회): " + msg);
                    lastFailMsg = msg;
                }
                SetStatus("서버 연결 실패 — 재시도 중(" + failStreak + "회)");
                int mult = 1;
                for (int i = 1; i < failStreak && mult < 100; i++) mult *= 2;
                // 최대 60초 — 무료 플랜 콜드스타트(약 1분) 후 아침 첫 동기화가 오래 지연되지 않게.
                wait = Math.Min(intervalMs * mult, 60000);
            }
            Thread.Sleep(wait);
        }
    }
    // 야간(한국시간 21:00~08:00)엔 서버 폴링을 멈춘다. PC 시간대와 무관하게 UTC+9 로 판정.
    static bool IsQuietHours() {
        int h = DateTime.UtcNow.AddHours(9).Hour;
        return h >= 21 || h < 8;
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
            // UseShellExecute=true 는 ShellExecute(셸)를 타므로 셸이 바쁘면 지연될 수 있어 직접 실행을 먼저 시도.
            // 다만 SecureGate 가 권한 상승을 요구하는 경우엔 직접 실행이 실패하므로, 그때만 셸 방식으로 되돌린다.
            string args = "F " + paths.Count + " " + lp;
            try {
                var psi = new ProcessStartInfo(securegate, args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(securegate);
                Process.Start(psi);
            } catch (Exception ex1) {
                Log("직접 실행 실패 → 셸 실행으로 재시도: " + ex1.Message);
                var psi2 = new ProcessStartInfo(securegate, args);
                psi2.UseShellExecute = true;
                Process.Start(psi2);
            }
            Log("SecureGate 투입: " + paths.Count + "장");
            AutoSendWhenReady(paths);
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
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l,
                                                                      uint flags, uint timeout, out IntPtr result);
    const uint SMTO_ABORTIFHUNG = 0x0002;

    /// 다른 프로세스(SecureGate)로 보내는 동기 메시지는 반드시 타임아웃을 건다.
    /// 상대가 멈추면 SendMessage 는 영원히 돌아오지 않아 우리 스레드까지 묶인다.
    static int SendMsgTimed(IntPtr h, uint msg, int timeoutMs, int failValue) {
        IntPtr res;
        IntPtr ok = SendMessageTimeout(h, msg, IntPtr.Zero, IntPtr.Zero,
                                       SMTO_ABORTIFHUNG, (uint)timeoutMs, out res);
        return ok == IntPtr.Zero ? failValue : res.ToInt32();
    }
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

    int autoSendInFlight = 0;   // 자동보내기 동시 실행 방지(스레드 누적/중복 클릭 방지)
    void AutoSendWhenReady(List<string> fedPaths) {
        if (!autoSend) return;
        if (Interlocked.CompareExchange(ref autoSendInFlight, 1, 0) != 0) {
            Log("자동보내기: 이전 전송 대기 중 → 다음 폴링에서 함께 처리"); return;
        }
        string fileNames = NameList(fedPaths, 3);
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
                    int cnt = SendMsgTimed(lv, LVM_GETITEMCOUNT, 5000, -1);
                    if (cnt < 0) { Thread.Sleep(500); continue; }   // 응답 없음 → 다음 회차 재시도
                    if (cnt != last) {                       // 아직 등록 중(대용량이면 오래 걸림)
                        last = cnt; stableSince = DateTime.Now;
                        Log("자동보내기: 목록 " + cnt + "건 등록중...");
                    } else if (cnt > 0 && IsWindowEnabled(btn)
                               && (DateTime.Now - stableSince).TotalSeconds >= autoSendStableSec) {
                        SendMsgTimed(btn, BM_CLICK, 15000, -1);
                        Log("자동보내기: [파일보내기] 클릭 — " + cnt + "건");
                        // SecureGate 가 목록을 비우면 접수된 것 → 그때 완료 알림
                        bool accepted = false;
                        DateTime until = DateTime.Now.AddSeconds(120);
                        while (DateTime.Now < until) {
                            Thread.Sleep(1000);
                            int now = SendMsgTimed(lv, LVM_GETITEMCOUNT, 5000, cnt);
                            if (now < cnt) { accepted = true; break; }
                        }
                        if (accepted)
                            Notify("✅ 자료전송 완료 (" + cnt + "건)", fileNames);
                        else
                            Notify("자료전송 요청함 (" + cnt + "건)", fileNames + "\n자료전송 창을 확인하세요.");
                        return;
                    }
                    Thread.Sleep(500);
                }
                Log("자동보내기: 대기 시간 초과(" + autoSendTimeoutSec + "초) → 직접 눌러주세요");
            } catch (Exception e) { Log("자동보내기 오류: " + e.Message + " → 직접 눌러주세요"); }
            finally { Interlocked.Exchange(ref autoSendInFlight, 0); }
        });
    }

    // ── 받는 폴더 직접 감시 ────────────────────────────────────────
    // 폰 업로드가 아니라 사용자가 직접 폴더에 옮겨넣은 파일도 SecureGate 에 투입한다.
    // · 앱 시작 시점에 이미 있던 파일은 '처리됨'으로 기준선을 잡아 재시작 때 재전송하지 않음
    // · 크기가 안정되고 잠금이 풀린 뒤에만 투입(복사 중인 대용량 파일 방지)
    void StartFolderWatch() {
        var t = new Thread(() => {
            try { if (Directory.Exists(dest)) foreach (var f in Directory.GetFiles(dest)) lock (fedFiles) fedFiles.Add(f); }
            catch { }
            var sizes = new Dictionary<string, long>();
            while (running) {
                try {
                    // 오래 켜두면 기록이 무한정 쌓인다 → 폴더에 더 이상 없는 항목은 정리
                    lock (fedFiles) {
                        if (fedFiles.Count > 2000) {
                            var alive = new HashSet<string>(
                                Directory.Exists(dest) ? Directory.GetFiles(dest) : new string[0],
                                StringComparer.OrdinalIgnoreCase);
                            fedFiles.RemoveWhere(x => !alive.Contains(x));
                        }
                    }
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
                Thread.Sleep(1500);
            }
        });
        t.IsBackground = true; t.Start();
    }

    static bool IsFileReady(string p) {
        try { using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
        catch { return false; }
    }

    // ── 다운로드 폴더 감시 → 새 다운로드마다 [자료전송]/[무시] 토스트 ──
    string GetDownloadsDir() {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders")) {
                if (k != null) {
                    var v = k.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                    if (!string.IsNullOrEmpty(v)) return Environment.ExpandEnvironmentVariables(v);
                }
            }
        } catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    void StartDownloadWatch() {
        var t = new Thread(() => {
            downloadsDir = GetDownloadsDir();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (Directory.Exists(downloadsDir)) foreach (var f in Directory.GetFiles(downloadsDir)) seen.Add(f); }
            catch { }
            var sizes = new Dictionary<string, long>();
            while (running) {
                try {
                    if (askDownloads && Directory.Exists(downloadsDir)) {
                        var cur = Directory.GetFiles(downloadsDir);
                        if (seen.Count > 5000) {          // 무한 증가 방지: 지금 폴더에 있는 것만 남김
                            var alive = new HashSet<string>(cur, StringComparer.OrdinalIgnoreCase);
                            seen.RemoveWhere(x => !alive.Contains(x));
                            foreach (var k2 in new List<string>(sizes.Keys)) if (!alive.Contains(k2)) sizes.Remove(k2);
                        }
                        foreach (var f in cur) {
                            if (seen.Contains(f)) continue;
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            if (ext == ".crdownload" || ext == ".part" || ext == ".partial"
                                || ext == ".tmp" || ext == ".download") continue;   // 브라우저 임시파일
                            long len; try { len = new FileInfo(f).Length; } catch { continue; }
                            long prev;
                            if (!sizes.TryGetValue(f, out prev) || prev != len) { sizes[f] = len; continue; }
                            if (!IsFileReady(f)) continue;             // 아직 받는 중
                            seen.Add(f); sizes.Remove(f);
                            string path = f;
                            OnTrayThread(() => ShowTransferToast(path));
                        }
                    }
                } catch (Exception e) { Log("다운로드 감시 오류: " + e.Message); }
                Thread.Sleep(1500);
            }
        });
        t.IsBackground = true; t.Start();
    }

    void ShowTransferToast(string filePath) {
        var f = new ToastForm();
        f.Size = new Size(330, 96);
        f.BackColor = Color.FromArgb(37, 99, 235);
        var lbl1 = new Label { Text = "새 다운로드 — 자료전송할까요?", ForeColor = Color.White,
                               Font = new Font("Malgun Gothic", 8F), Location = new Point(12, 8), AutoSize = true };
        var lbl2 = new Label { Text = Path.GetFileName(filePath), ForeColor = Color.White,
                               Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold),
                               Location = new Point(12, 28), Size = new Size(306, 20), AutoEllipsis = true };
        var btnGo = new Button { Text = "자료전송", Location = new Point(158, 56), Size = new Size(90, 30),
                                 FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(37, 99, 235) };
        var btnNo = new Button { Text = "무시", Location = new Point(254, 56), Size = new Size(64, 30),
                                 FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White };
        btnNo.FlatAppearance.BorderColor = Color.White;
        var timer = new System.Windows.Forms.Timer { Interval = 10000 };     // 10초 후 자동 무시
        Action close = () => { try { timer.Stop(); timer.Dispose(); toasts.Remove(f); f.Close(); f.Dispose(); RepositionToasts(); } catch { } };
        btnGo.Click += (s, e) => { close(); TransferDownloaded(filePath); };
        btnNo.Click += (s, e) => close();
        timer.Tick += (s, e) => close();
        f.Controls.AddRange(new Control[] { lbl1, lbl2, btnGo, btnNo });
        toasts.Add(f);
        PositionToast(f);
        f.Show();
        timer.Start();
    }

    void PositionToast(Form f) {
        var wa = Screen.PrimaryScreen.WorkingArea;
        int idx = toasts.IndexOf(f); if (idx < 0) idx = toasts.Count - 1;
        f.Location = new Point(wa.Right - f.Width - 12, wa.Bottom - 12 - (f.Height + 8) * (idx + 1));
    }
    void RepositionToasts() {
        var wa = Screen.PrimaryScreen.WorkingArea;
        for (int i = 0; i < toasts.Count; i++)
            toasts[i].Location = new Point(wa.Right - toasts[i].Width - 12, wa.Bottom - 12 - (toasts[i].Height + 8) * (i + 1));
    }

    void TransferDownloaded(string filePath) {
        if (!File.Exists(filePath)) { Log("자료전송 취소: 파일이 사라짐 " + Path.GetFileName(filePath)); return; }
        lock (fedFiles) fedFiles.Add(filePath);
        Log("다운로드 자료전송: " + Path.GetFileName(filePath));
        ThreadPool.QueueUserWorkItem(_ => Feed(new List<string> { filePath }));
    }

    /// 알림(우하단 자체 토스트) — 어느 스레드에서 불러도 안전. 5초 후 사라짐.
    /// ※ NotifyIcon.ShowBalloonTip 은 셸(작업표시줄)에 타임아웃 없는 SendMessage 를 보내
    ///   셸이 바쁘면 UI 스레드가 무기한 블록됨(실제 행 2회의 원인) → 풍선 알림 전면 금지.
    void Notify(string title, string text) {
        Log(title + " — " + text);
        OnTrayThread(() => ShowNotifyToast(title, text));
    }

    // 버튼 없는 정보 토스트 — 클릭하거나 5초 지나면 닫힘. (반드시 UI 스레드에서 호출)
    void ShowNotifyToast(string title, string text) {
        try {
            var f = new ToastForm();
            f.Size = new Size(330, 84);
            f.BackColor = Color.FromArgb(31, 41, 55);
            var l1 = new Label { Text = title, ForeColor = Color.White,
                                 Font = new Font("Malgun Gothic", 9.5F, FontStyle.Bold),
                                 Location = new Point(12, 10), Size = new Size(306, 20), AutoEllipsis = true };
            var l2 = new Label { Text = (text ?? "").Replace("\n", "  "), ForeColor = Color.FromArgb(209, 213, 219),
                                 Font = new Font("Malgun Gothic", 8.5F),
                                 Location = new Point(12, 34), Size = new Size(306, 40), AutoEllipsis = true };
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            Action close = () => { try { timer.Stop(); timer.Dispose(); toasts.Remove(f); f.Close(); f.Dispose(); RepositionToasts(); } catch { } };
            timer.Tick += (s, e) => close();
            EventHandler clickClose = (s, e) => close();
            f.Click += clickClose; l1.Click += clickClose; l2.Click += clickClose;
            f.Controls.AddRange(new Control[] { l1, l2 });
            toasts.Add(f);
            PositionToast(f);
            f.Show();
            timer.Start();
        } catch { }
    }

    /// 알림에 넣을 파일명 목록 — 너무 길지 않게 앞 max개만, 나머지는 "외 N건"
    static string NameList(List<string> paths, int max) {
        var sb = new StringBuilder();
        for (int i = 0; i < paths.Count && i < max; i++) {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Path.GetFileName(paths[i]));
        }
        if (paths.Count > max) sb.Append(" 외 " + (paths.Count - max) + "건");
        return sb.ToString();
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
                using (var wc = new TimedWebClient(30000)) { wc.Encoding = Encoding.UTF8; body = wc.DownloadString(server + "/agent/version"); }
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
                    // 배너 + 풍선알림 모두 UI 스레드에서(크로스스레드 NotifyIcon 호출은 프로세스 행 유발)
                    try { BeginInvoke((Action)(() => {
                        lblUpdate.Text = "🔔 새 버전 v" + ver + " 사용 가능";
                        lblUpdate.Visible = true; btnUpdate.Visible = true; btnUpdate.Enabled = true;
                        OnTrayThread(() => ShowNotifyToast("SecureGate 자동전송", "새 버전 v" + ver + " — 앱을 열어 [지금 업데이트]를 누르세요"));
                    })); } catch { }
                } else if (!silent) Log("최신 버전입니다 (v" + ver + ")");
            } catch (Exception e) { if (!silent) Log("업데이트 확인 실패: " + e.Message); }
        });
    }

    static string Sha256Hex(byte[] data) {
        using (var sha = System.Security.Cryptography.SHA256.Create()) {
            byte[] h = sha.ComputeHash(data);
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
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
                // 바이트로 받아 그대로 저장 + 그 바이트의 해시를 기록 → 서버가 계산하는 해시와 정확히 일치
                // (알림 시점 해시가 아니라 '실제 받은 소스' 해시라, 받는 도중 서버가 또 배포돼도 재알림 없음)
                byte[] srcBytes;
                using (var wc = new TimedWebClient(120000)) srcBytes = wc.DownloadData(server + "/agent/source.cs");
                if (srcBytes.Length < 1000) throw new Exception("소스가 비정상적으로 짧음");
                string builtSha = Sha256Hex(srcBytes);
                File.WriteAllBytes(newCs, srcBytes);

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
                // 이전 시도의 잔여물이 남아 있으면 '컴파일 성공'으로 오판해 낡은 exe 를 설치하게 된다 → 먼저 제거
                try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
                var psi = new ProcessStartInfo(csc, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                int rc = -1; string cscOut = "";
                using (var pc = Process.Start(psi)) {
                    // stderr 는 별도 스레드에서 읽음 — 순차 ReadToEnd 는 파이프 버퍼가 차면
                    // csc 와 서로 기다리는 데드락이 됨. 타임아웃 시 csc 를 강제 종료(좀비 방지).
                    string so = "", se = "";
                    var tErr = new Thread(() => { try { se = pc.StandardError.ReadToEnd(); } catch { } });
                    tErr.IsBackground = true; tErr.Start();
                    try { so = pc.StandardOutput.ReadToEnd(); } catch { }
                    if (!pc.WaitForExit(180000)) { try { pc.Kill(); } catch { } }
                    tErr.Join(5000);
                    cscOut = so + se;
                    try { rc = pc.ExitCode; } catch { rc = -1; }
                }
                if (rc != 0 || !File.Exists(newExe)) {          // 종료코드까지 확인(존재만으로 판단 금지)
                    string tail = (cscOut ?? "").Trim();
                    if (tail.Length > 300) tail = tail.Substring(tail.Length - 300);
                    throw new Exception("컴파일 실패(rc=" + rc + ") — 기존 버전 유지 " + tail);
                }
                // 새로 만든 exe 가 방금 컴파일된 것인지 최종 확인
                if ((DateTime.Now - File.GetLastWriteTime(newExe)).TotalMinutes > 5)
                    throw new Exception("컴파일 산출물이 최신이 아님 — 기존 버전 유지");

                // 실행 중인 exe 는 덮어쓸 수 없지만 이름 변경은 가능
                if (File.Exists(oldExe)) { try { File.Delete(oldExe); } catch { } }
                File.Move(exe, oldExe);
                try { File.Move(newExe, exe); }
                catch { File.Move(oldExe, exe); throw; }      // 실패 시 롤백

                srcSha = builtSha; SaveConfig();   // 알림 시점(newSha)이 아니라 실제 컴파일한 소스 해시
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
    static readonly object _logLock = new object();
    void Log(string msg) {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
        // 잠금 대기에 상한을 둔다 — 다른 스레드가 물고 늘어져도 로깅이(특히 워치독이) 영구히 막히지 않도록.
        bool got = false;
        try {
            got = Monitor.TryEnter(_logLock, 2000);
            if (got) {
                try {   // 로그 파일이 무한정 커지지 않도록 1MB 넘으면 직전 것 하나만 남기고 회전
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > 1024 * 1024) {
                        string bak = logPath + ".1";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(logPath, bak);
                    }
                } catch { }
                File.AppendAllText(logPath, line + "\r\n", new UTF8Encoding(false));
            }
        } catch { } finally { if (got) Monitor.Exit(_logLock); }
        try { if (txtLog != null && txtLog.IsHandleCreated) txtLog.BeginInvoke((Action)(() => {
            if (txtLog.Lines.Length > 200) {          // 무한히 쌓이면 메모리·렌더링 부담 → 최근 것만 유지
                var keep = txtLog.Lines; var cut = new string[100];
                Array.Copy(keep, keep.Length - 100, cut, 0, 100);
                txtLog.Lines = cut;
            }
            txtLog.AppendText(line + "\r\n");
        })); } catch { }
    }
    void SetStatus(string s) {
        // 핸들 생성 전엔 아무것도 하지 않는다(다른 스레드에서 Text 직접 대입은 불법 크로스스레드 접근).
        try { if (lblStatus != null && lblStatus.IsHandleCreated) lblStatus.BeginInvoke((Action)(() => lblStatus.Text = s)); } catch { }
    }
}

// 트레이 스레드의 메시지 루프 주인 — 화면에 절대 나타나지 않는 폼.
// 토스트를 이 스레드로 마샬링하기 위한 핸들 제공용.
class PumpForm : Form {
    public PumpForm() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
    }
    protected override void SetVisibleCore(bool value) {
        if (!IsHandleCreated) CreateHandle();     // 핸들만 만들고 표시는 하지 않음
        base.SetVisibleCore(false);
    }
}

// 타임아웃이 걸린 WebClient — 기본 WebClient 는 상황에 따라 무한정 대기할 수 있어
// 네트워크 지연 시 스레드가 영구히 묶이는 것을 막는다.
class TimedWebClient : WebClient {
    readonly int ms;
    public TimedWebClient(int timeoutMs) { ms = timeoutMs; }
    protected override WebRequest GetWebRequest(Uri address) {
        WebRequest r = base.GetWebRequest(address);
        if (r != null) {
            r.Timeout = ms;
            var h = r as HttpWebRequest;
            if (h != null) { h.ReadWriteTimeout = ms; }
        }
        return r;
    }
}

// 다운로드 알림 토스트 — 포커스를 뺏지 않고(WS_EX_NOACTIVATE) 항상 위(WS_EX_TOPMOST)에 뜨는 작은 창
class ToastForm : Form {
    public ToastForm() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE — 입력 포커스 안 뺏음
            cp.ExStyle |= 0x00000008;   // WS_EX_TOPMOST
            return cp;
        }
    }
}
