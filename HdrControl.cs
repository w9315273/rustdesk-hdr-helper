using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

static class HdrControl
{
    const uint ActivePaths = 2;
    const int InsufficientBuffer = 122;
    const uint GetAdvancedColor = 9, SetAdvancedColor = 10;
    const uint GetAdvancedColor2 = 15, SetHdr = 16;

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    struct ColorInfo { public Header Header; public uint Flags, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)]
    struct ColorInfo2 { public Header Header; public uint Flags, Encoding, Bits, Mode; }
    [StructLayout(LayoutKind.Sequential)]
    struct ColorSet { public Header Header; public uint Value; }
    [StructLayout(LayoutKind.Sequential)]
    struct Source { public Luid Adapter; public uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct Target
    {
        public Luid Adapter;
        public uint Id, Mode, Technology, Rotation, Scaling;
        public uint RefreshNumerator, RefreshDenominator, Scanline;
        public int Available;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DisplayPath { public Source Source; public Target Target; public uint Flags; }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray,
        ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    static extern int GetInfo(ref ColorInfo info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)]
    static extern int GetInfo2(ref ColorInfo2 info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo", ExactSpelling = true)]
    static extern int SetInfo(ref ColorSet info);

    struct State
    {
        public bool Modern, Supported, Enabled, Active;
    }

    internal static void CheckLayouts()
    {
        CheckSize(typeof(Luid), 8); CheckSize(typeof(Header), 20);
        CheckSize(typeof(ColorSet), 24); CheckSize(typeof(ColorInfo), 32);
        CheckSize(typeof(ColorInfo2), 36); CheckSize(typeof(Source), 20);
        CheckSize(typeof(Target), 48); CheckSize(typeof(DisplayPath), 72);
        if (Marshal.OffsetOf(typeof(DisplayPath), "Target").ToInt32() != 20)
            throw new Exception("DisplayConfig target offset mismatch.");
    }
    static void CheckSize(Type type, int expected)
    {
        if (Marshal.SizeOf(type) != expected) throw new Exception("Win32 layout mismatch: " + type.Name);
    }
    static void Check(int error, string operation)
    {
        if (error != 0) throw new Win32Exception(error, operation + ": " + new Win32Exception(error).Message);
    }
    static bool Unsupported(int error)
    {
        return error == 50 || error == 87 || error == 120;
    }
    static Header MakeHeader(Target target, uint type, Type packet)
    {
        return new Header { Type = type, Size = (uint)Marshal.SizeOf(packet), Adapter = target.Adapter, Id = target.Id };
    }

    static List<Target> GetTargets()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            uint pathCount, modeCount;
            Check(GetDisplayConfigBufferSizes(ActivePaths, out pathCount, out modeCount), "查询显示器数量失败");
            IntPtr paths = Marshal.AllocHGlobal(checked((int)Math.Max(pathCount, 1u) * 72));
            IntPtr modes = IntPtr.Zero;
            try
            {
                modes = Marshal.AllocHGlobal(checked((int)Math.Max(modeCount, 1u) * 64));
                int result = QueryDisplayConfig(ActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == InsufficientBuffer) continue;
                Check(result, "查询活动显示器失败");
                List<Target> targets = new List<Target>();
                HashSet<string> seen = new HashSet<string>();
                for (int i = 0; i < pathCount; i++)
                {
                    DisplayPath path = (DisplayPath)Marshal.PtrToStructure(IntPtr.Add(paths, i * 72), typeof(DisplayPath));
                    Target t = path.Target;
                    string key = t.Adapter.High + ":" + t.Adapter.Low + ":" + t.Id;
                    if (t.Available != 0 && seen.Add(key)) targets.Add(t);
                }
                return targets;
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes);
            }
        }
        throw new Exception("显示器配置正在变化, 请稍后重试. ");
    }

    static State Read(Target target)
    {
        ColorInfo2 modern = new ColorInfo2 { Header = MakeHeader(target, GetAdvancedColor2, typeof(ColorInfo2)) };
        int result = GetInfo2(ref modern);
        if (result == 0)
        {
            return new State { Modern = true, Supported = (modern.Flags & 16) != 0,
                Enabled = (modern.Flags & 32) != 0, Active = modern.Mode == 2 };
        }
        if (!Unsupported(result)) Check(result, "查询 HDR 状态失败");
        ColorInfo legacy = new ColorInfo { Header = MakeHeader(target, GetAdvancedColor, typeof(ColorInfo)) };
        Check(GetInfo(ref legacy), "查询旧版 HDR 状态失败");
        return new State { Modern = false, Supported = (legacy.Flags & 1) != 0,
            Enabled = (legacy.Flags & 2) != 0, Active = (legacy.Flags & 2) != 0 };
    }

    static Target GetHdrTarget(out State before)
    {
        CheckLayouts();
        List<Target> hdrTargets = new List<Target>();
        List<State> states = new List<State>();
        foreach (Target target in GetTargets())
        {
            State state = Read(target);
            if (state.Supported) { hdrTargets.Add(target); states.Add(state); }
        }
        if (hdrTargets.Count == 0) throw new Exception("没有找到支持 HDR 的活动显示器. ");
        if (hdrTargets.Count != 1) throw new Exception("检测到多个 HDR 显示器；当前版本仅支持一个. ");
        before = states[0];
        return hdrTargets[0];
    }

    internal static bool IsEnabled()
    {
        State state;
        GetHdrTarget(out state);
        return state.Enabled;
    }

    internal static void SetEnabled(bool enabled)
    {
        State before;
        Target selected = GetHdrTarget(out before);
        if (before.Enabled == enabled && before.Active == enabled) return;
        ColorSet request = new ColorSet {
            Header = MakeHeader(selected, before.Modern ? SetHdr : SetAdvancedColor, typeof(ColorSet)),
            Value = enabled ? 1u : 0u
        };
        Check(SetInfo(ref request), "设置 HDR 失败");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(200);
            State after = Read(selected);
            if (after.Enabled == enabled && after.Active == enabled) return;
        }
        throw new Exception("Windows 已接受 HDR 请求, 但尚未确认生效；请检查显示设置. ");
    }
}
