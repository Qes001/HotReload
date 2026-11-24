// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace HotReload.Boot
{
    /// <summary>
    /// 固定不动的壳，编译一次之后不再替换。
    /// 职责：撤掉旧补丁 -> 让旧 payload 自我清理 -> 从磁盘读字节加载新 payload -> 调用 OnLoad。
    /// 不引用 Payload 工程，两边只靠类型名 + 静态方法签名约定。
    /// </summary>
    [BepInPlugin(Guid, "HotReload Boot", "1.2.4")]
    public class Boot : BaseUnityPlugin
    {
        public const string Guid = "as-sin.hotreload.boot";

        /// <summary>payload 里的 Harmony 实例必须用同一个 ID，否则 UnpatchAll 撤不掉。</summary>
        public const string PayloadHarmonyId = "as-sin.hotreload.payload";

        const string PayloadTypeName = "HotReload.Payload.Entry";

        ConfigEntry<bool> _autoReload;

        // payload 重载键跟 payload 共用 hotkeys.txt（默认 Ctrl+F10，可在界面里改）
        KeyCode _reloadKeyCode = KeyCode.F10;
        bool _reloadCtrl = true;
        bool _reloadAlt;
        bool _reloadShift;
        DateTime _hotkeyStamp = DateTime.MinValue;

        string _payloadPath;
        DateTime _lastWriteUtc;

        Assembly _payloadAssembly;
        Action _onLoad;
        Action _onUnload;

        void Awake()
        {
            _autoReload = Config.Bind("General", "AutoReload", true,
                "payload dll 一有变化就自动重载");

            _payloadPath = Path.Combine(Path.Combine(Paths.BepInExRootPath, "hotreload"), "HotReload.Payload.dll");
            Logger.LogInfo("payload 路径: " + _payloadPath);

            Reload();
            RefreshReloadHotkey();
        }

        void Update()
        {
            RefreshReloadHotkey();

            if (ReloadHotkeyDown())
            {
                Reload();
                return;
            }

            if (!_autoReload.Value) return;

            try
            {
                if (!File.Exists(_payloadPath)) return;
                var write = File.GetLastWriteTimeUtc(_payloadPath);
                if (write == _lastWriteUtc) return;
                _lastWriteUtc = write;
                Reload();
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        /// 从 hotkeys.txt 读 payloadreload（payload 的界面会写这个文件）。
        /// 只在文件变化时才重新解析，避免每帧读盘。
        /// </summary>
        void RefreshReloadHotkey()
        {
            try
            {
                var path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "hotreload"), "hotkeys.txt");
                if (!File.Exists(path)) return;

                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _hotkeyStamp) return;
                _hotkeyStamp = stamp;

                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;

                    var name = lines[i].Substring(0, eq).Trim();
                    if (!name.Equals("payloadreload", StringComparison.OrdinalIgnoreCase)) continue;

                    ApplyReloadHotkey(lines[i].Substring(eq + 1).Trim());
                    return;
                }
            }
            catch (Exception)
            {
            }
        }

        void ApplyReloadHotkey(string value)
        {
            var key = KeyCode.None;
            bool ctrl = false, alt = false, shift = false;

            var parts = value.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0) continue;

                if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    ctrl = true;
                    continue;
                }
                if (part.Equals("alt", StringComparison.OrdinalIgnoreCase)) { alt = true; continue; }
                if (part.Equals("shift", StringComparison.OrdinalIgnoreCase)) { shift = true; continue; }

                try { key = (KeyCode)Enum.Parse(typeof(KeyCode), part, true); }
                catch (Exception) { }
            }

            if (key == KeyCode.None && !ctrl && !alt && !shift)
            {
                // 空值 = 回到默认
                key = KeyCode.F10;
                ctrl = true;
            }

            _reloadKeyCode = key;
            _reloadCtrl = ctrl;
            _reloadAlt = alt;
            _reloadShift = shift;
        }

        bool ReloadHotkeyDown()
        {
            if (_reloadKeyCode == KeyCode.None) return false;
            if (!Input.GetKeyDown(_reloadKeyCode)) return false;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            return ctrl == _reloadCtrl && alt == _reloadAlt && shift == _reloadShift;
        }

        void Reload()
        {
            UnloadPayload();

            try
            {
                if (!File.Exists(_payloadPath))
                {
                    Logger.LogWarning("payload 不存在: " + _payloadPath);
                    return;
                }

                var bytes = File.ReadAllBytes(_payloadPath);
                _lastWriteUtc = File.GetLastWriteTimeUtc(_payloadPath);

                _payloadAssembly = Assembly.Load(bytes);

                var entry = _payloadAssembly.GetType(PayloadTypeName, true);
                _onLoad = Bind(entry, "OnLoad");
                _onUnload = Bind(entry, "OnUnload");

                _onLoad();
                Logger.LogInfo("payload 已加载: " + _payloadAssembly.GetName().Version);
            }
            catch (Exception e)
            {
                Logger.LogError("payload 加载失败: " + e);
                _onLoad = null;
                _onUnload = null;
                _payloadAssembly = null;
            }
        }

        static Action Bind(Type entry, string methodName)
        {
            var mi = entry.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (mi == null)
                throw new MissingMethodException(entry.FullName + "." + methodName + "()");
            return (Action)Delegate.CreateDelegate(typeof(Action), mi);
        }

        void UnloadPayload()
        {
            try
            {
                new Harmony(PayloadHarmonyId).UnpatchSelf();
            }
            catch (Exception e)
            {
                Logger.LogError("撤销补丁失败: " + e);
            }

            try
            {
                if (_onUnload != null) _onUnload();
            }
            catch (Exception e)
            {
                Logger.LogError("payload 清理失败: " + e);
            }

            _onLoad = null;
            _onUnload = null;
            _payloadAssembly = null;
        }

        void OnDestroy()
        {
            UnloadPayload();
        }
    }
}
