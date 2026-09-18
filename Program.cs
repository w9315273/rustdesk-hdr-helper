using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using (Mutex mutex = new Mutex(false, @"Local\rustdesk-hdr-helper"))
        {
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { MessageBox.Show("另一份 rustdesk-hdr-helper 正在运行. 请先停止旧脚本或程序. "); return; }
                if (args.Length != 0)
                {
                    if (args.Length == 1 && args[0] == "--self-test")
                    {
                        HdrControl.CheckLayouts();
                        MessageBox.Show("Win32 数据结构检查通过. 此检查不会切换 HDR. ", "rustdesk-hdr-helper");
                        return;
                    }
                    if (args.Length != 2 || args[0] != "--hdr" || (args[1] != "on" && args[1] != "off"))
                        throw new ArgumentException("用法: RustDeskHDRhelper.exe [--hdr on|off] 或 [--self-test]");
                    HdrControl.SetEnabled(args[1] == "on");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show(ex.Message, "rustdesk-hdr-helper"); }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Icon appIcon;
    readonly ToolStripMenuItem status;
    readonly ToolStripMenuItem hdrToggle;
    readonly object hdrLock = new object();
    bool manualOverride;
    readonly Control dispatcher = new Control();
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly Thread worker;
    readonly string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"ServiceProfiles\LocalService\AppData\Roaming\RustDesk\log\server\RustDesk_rCURRENT.log");
    bool exiting;

    public TrayApp()
    {
        HdrControl.CheckLayouts();
        using (FileStream check = OpenLog()) { }
        IntPtr unused = dispatcher.Handle;
        status = new ToolStripMenuItem("等待连接: 已连接时请重连一次") { Enabled = false };
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        hdrToggle = new ToolStripMenuItem("HDR") { CheckOnClick = false };
        hdrToggle.Click += delegate { ToggleHdr(); };
        menu.Items.Add(hdrToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { Shutdown(); });
        menu.Opening += delegate { RefreshHdrMenu(); };
        appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        tray = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, Text = "rustdesk-hdr-helper", ContextMenuStrip = menu, Visible = true };
        worker = new Thread(Watch) { IsBackground = true, Name = "rustdesk-hdr-helper log watcher" };
        worker.Start();
        tray.ShowBalloonTip(4000, "rustdesk-hdr-helper", "已开始监听. 若正在远控, 请重新连接一次. ", ToolTipIcon.Info);
    }

    FileStream OpenLog()
    {
        return new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    void Status(string text)
    {
        if (exiting) return;
        try { dispatcher.BeginInvoke((Action)delegate { if (!exiting) status.Text = text; }); }
        catch (InvalidOperationException) { }
    }

    void SetHdr(string value)
    {
        HdrControl.SetEnabled(value == "on");
    }

    void RefreshHdrMenu()
    {
        try
        {
            lock (hdrLock) { hdrToggle.Checked = HdrControl.IsEnabled(); }
            hdrToggle.Text = hdrToggle.Checked ? "HDR: 开启" : "HDR: 关闭";
            hdrToggle.Enabled = true;
        }
        catch (Exception ex)
        {
            hdrToggle.Checked = false;
            hdrToggle.Text = "HDR: 不可用";
            hdrToggle.Enabled = false;
            status.Text = ex.Message;
        }
    }

    void ToggleHdr()
    {
        try
        {
            lock (hdrLock)
            {
                HdrControl.SetEnabled(!HdrControl.IsEnabled());
                manualOverride = true;
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "rustdesk-hdr-helper"); }
        RefreshHdrMenu();
    }

    void Watch()
    {
        HashSet<string> active = new HashSet<string>();
        Regex connect = new Regex(@"update:Update\((\d+),\s*SupportedDecoding");
        Regex close = new Regex(@"#(\d+)\s+Connection closed:");
        bool disabled = false;
        bool restoreNeeded = false;
        DateTime? emptySince = null;
        DateTime retry = DateTime.MinValue;
        try
        {
            FileInfo initial = new FileInfo(log);
            long offset = initial.Length;
            DateTime creation = initial.CreationTimeUtc;
            string partial = "";
            byte[] buffer = new byte[65536];
            while (!stop.WaitOne(0))
            {
                try
                {
                    FileInfo file = new FileInfo(log);
                    if (file.CreationTimeUtc != creation || file.Length < offset)
                    {
                        offset = 0; partial = ""; creation = file.CreationTimeUtc;
                    }
                    using (FileStream stream = OpenLog())
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        int count;
                        while (!stop.WaitOne(0) && (count = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            offset += count;
                            partial += Encoding.ASCII.GetString(buffer, 0, count);
                            int newline;
                            while ((newline = partial.IndexOf('\n')) >= 0)
                            {
                                string line = partial.Substring(0, newline);
                                partial = partial.Substring(newline + 1);
                                Match m = connect.Match(line);
                                if (m.Success)
                                {
                                    if (active.Add(m.Groups[1].Value))
                                        lock (hdrLock) { manualOverride = false; disabled = false; }
                                    emptySince = null;
                                }
                                else
                                {
                                    m = close.Match(line);
                                    if (m.Success && active.Remove(m.Groups[1].Value) && active.Count == 0)
                                    {
                                        emptySince = DateTime.UtcNow;
                                        lock (hdrLock) { manualOverride = false; }
                                    }
                                }
                            }
                        }
                    }
                    if (DateTime.UtcNow >= retry)
                    {
                        try
                        {
                            lock (hdrLock)
                            {
                                if (stop.WaitOne(0)) break;
                                if (!manualOverride)
                                {
                                    if (active.Count > 0 && !disabled) { restoreNeeded = true; SetHdr("off"); disabled = true; }
                                    else if (active.Count == 0 && restoreNeeded && emptySince.HasValue &&
                                        (DateTime.UtcNow - emptySince.Value).TotalSeconds >= 5)
                                    { SetHdr("on"); disabled = false; restoreNeeded = false; emptySince = null; }
                                }
                                Status(manualOverride ? "HDR 已手动设置 · 等待下一次连接或断开" : active.Count > 0 ? "远控中 · HDR 已关闭" : disabled ? "连接断开 · 等待恢复 HDR" : "等待连接 · 暂无活动远控");
                            }
                        }
                        catch (Exception ex) { retry = DateTime.UtcNow.AddSeconds(5); Status(ex.Message + ", 5 秒后重试"); }
                    }
                }
                catch (Exception ex) { Status("日志读取失败, 正在重试: " + ex.Message); }
                if (stop.WaitOne(2000)) break;
            }
        }
        catch (Exception ex) { Status("监听停止, 请退出并重启: " + ex.Message); }
    }

    void Shutdown()
    {
        if (exiting) return;
        exiting = true;
        status.Text = "正在退出…";
        stop.Set();
        worker.Join();
        tray.Visible = false;
        tray.Dispose();
        if (appIcon != null) appIcon.Dispose();
        dispatcher.Dispose();
        stop.Dispose();
        ExitThread();
    }
}
