// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>可以自己绑键的动作（默认全部不绑定）。</summary>
    internal enum HotkeyAction
    {
        Release = 0,
        Reload,
        Rescan,
        ClearLog,
        ExportLog
    }

    /// <summary>一个按键组合：主键 + 修饰键。</summary>
    internal struct Hotkey
    {
        public KeyCode Key;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;

        public static readonly Hotkey None = new Hotkey();

        public Hotkey(KeyCode key, bool ctrl, bool alt, bool shift)
        {
            Key = key;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
        }

        public bool IsSet { get { return Key != KeyCode.None; } }

        /// <summary>这一帧是否刚按下（修饰键必须完全一致，避免 Ctrl+F5 被 F5 触发）。</summary>
        public bool IsDown()
        {
            if (Key == KeyCode.None) return false;
            if (!Input.GetKeyDown(Key)) return false;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            return ctrl == Ctrl && alt == Alt && shift == Shift;
        }

        public string Describe()
        {
            if (Key == KeyCode.None) return "未设置";

            var sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Alt) sb.Append("Alt+");
            if (Shift) sb.Append("Shift+");
            sb.Append(KeyName(Key));
            return sb.ToString();
        }

        public static string KeyName(KeyCode key)
        {
            var name = key.ToString();

            if (name.Length == 6 && name.StartsWith("Alpha", StringComparison.Ordinal))
                return name.Substring(5);
            if (name.StartsWith("Keypad", StringComparison.Ordinal))
                return "小键盘" + name.Substring(6);

            if (name == "Return") return "Enter";
            if (name == "Escape") return "Esc";
            if (name == "LeftControl" || name == "RightControl") return "Ctrl";
            if (name == "LeftShift" || name == "RightShift") return "Shift";
            if (name == "LeftAlt" || name == "RightAlt") return "Alt";
            if (name == "BackQuote") return "`";
            if (name == "Minus") return "-";
            if (name == "Equals") return "=";
            if (name == "LeftBracket") return "[";
            if (name == "RightBracket") return "]";
            if (name == "Semicolon") return ";";
            if (name == "Quote") return "'";
            if (name == "Comma") return ",";
            if (name == "Period") return ".";
            if (name == "Slash") return "/";
            if (name == "Backslash") return "\\";
            return name;
        }

        public static bool IsModifierKey(KeyCode key)
        {
            return key == KeyCode.LeftControl || key == KeyCode.RightControl
                || key == KeyCode.LeftShift || key == KeyCode.RightShift
                || key == KeyCode.LeftAlt || key == KeyCode.RightAlt
                || key == KeyCode.Menu || key == KeyCode.None;
        }

        public string Serialize()
        {
            if (Key == KeyCode.None) return "";

            var sb = new StringBuilder();
            if (Ctrl) sb.Append("ctrl+");
            if (Alt) sb.Append("alt+");
            if (Shift) sb.Append("shift+");
            sb.Append(Key.ToString());
            return sb.ToString();
        }

        public static Hotkey Parse(string text)
        {
            var result = new Hotkey();
            if (string.IsNullOrEmpty(text)) return result;

            var parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0) continue;

                if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    result.Ctrl = true;
                    continue;
                }
                if (part.Equals("alt", StringComparison.OrdinalIgnoreCase)) { result.Alt = true; continue; }
                if (part.Equals("shift", StringComparison.OrdinalIgnoreCase)) { result.Shift = true; continue; }

                try { result.Key = (KeyCode)Enum.Parse(typeof(KeyCode), part, true); }
                catch (Exception) { }
            }

            return result;
        }
    }

    /// <summary>
    /// 快捷键配置。只有窗口开关（Ctrl+F7）和 payload 重载（Ctrl+F10）是固定的，
    /// 其余动作默认不绑定，存在 BepInEx/hotreload/hotkeys.txt。
    /// </summary>
    internal static class HotkeyConfig
    {
        /// <summary>固定的两个：壳与本插件自己实现，不允许改。</summary>
        public static readonly Hotkey ToggleWindow = new Hotkey(KeyCode.F7, true, false, false);
        public const string ToggleWindowText = "Ctrl+F7";
        public const string PayloadReloadText = "Ctrl+F10";

        public static readonly HotkeyAction[] Editable =
        {
            HotkeyAction.Release,
            HotkeyAction.Reload,
            HotkeyAction.Rescan,
            HotkeyAction.ClearLog,
            HotkeyAction.ExportLog
        };

        static readonly Dictionary<HotkeyAction, Hotkey> _map = new Dictionary<HotkeyAction, Hotkey>();
        static bool _loaded;

        public static string Name(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.Release: return "释放";
                case HotkeyAction.Reload: return "重载";
                case HotkeyAction.Rescan: return "检索";
                case HotkeyAction.ClearLog: return "清空日志";
                case HotkeyAction.ExportLog: return "导出日志";
                default: return action.ToString();
            }
        }

        public static string KeyName(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.Release: return "release";
                case HotkeyAction.Reload: return "reload";
                case HotkeyAction.Rescan: return "rescan";
                case HotkeyAction.ClearLog: return "clearlog";
                case HotkeyAction.ExportLog: return "exportlog";
                default: return action.ToString().ToLowerInvariant();
            }
        }

        static string ConfigPath
        {
            get
            {
                try { return Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "hotreload"), "hotkeys.txt"); }
                catch (Exception) { return null; }
            }
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                var path = ConfigPath;
                if (path == null || !File.Exists(path)) return;

                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (value.Length == 0) continue;

                    for (int k = 0; k < Editable.Length; k++)
                    {
                        if (!KeyName(Editable[k]).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                        var hk = Hotkey.Parse(value);
                        if (hk.IsSet) _map[Editable[k]] = hk;
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static Hotkey Get(HotkeyAction action)
        {
            EnsureLoaded();

            Hotkey hk;
            return _map.TryGetValue(action, out hk) ? hk : Hotkey.None;
        }

        public static void Set(HotkeyAction action, Hotkey hotkey)
        {
            EnsureLoaded();

            if (hotkey.IsSet) _map[action] = hotkey;
            else _map.Remove(action);

            Save();
        }

        public static void Clear(HotkeyAction action)
        {
            Set(action, Hotkey.None);
        }

        static void Save()
        {
            try
            {
                var path = ConfigPath;
                if (path == null) return;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# HotReload 快捷键（留空 = 不绑定）");
                for (int i = 0; i < Editable.Length; i++)
                {
                    var hk = Get(Editable[i]);
                    sb.Append(KeyName(Editable[i])).Append('=').Append(hk.Serialize()).AppendLine();
                }

                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception)
            {
            }
        }
    }
}
