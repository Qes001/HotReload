// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 界面与按键。Ctrl+F7 开关窗口（固定）。
    /// 属于"可丢弃"部分，payload 重载时整个重建。
    /// 布局：自绘标题栏 + 大块日志区 + 底部操作行；「设置」页里可以自己绑快捷键。
    /// </summary>
    internal class PayloadBehaviour : MonoBehaviour
    {
        const int WindowId = 0x48524C44;
        const float WindowWidth = 440f;
        const float WindowHeight = 512f;
        const float TitleHeight = 26f;
        const float Pad = 8f;

        static Font _font;

        Rect _rect = new Rect(90f, 60f, WindowWidth, WindowHeight);
        bool _visible;
        bool _collapsed;
        bool _dragging;
        bool _guiErrorReported;
        string _guidText = "";
        Vector2 _scroll;
        int _lastLogCount;

        // 0 = 主界面，1 = 快捷键设置
        int _page;
        bool _capturing;
        HotkeyAction _captureTarget;
        Vector2 _settingsScroll;

        // 刚录完键的那一帧不响应，免得同一个按键顺手把动作也触发了
        int _suppressFrame = -1;

        // 「设置」页点了接口开关，留到 Update 里启停（-1 无，0 关，1 开）
        int _apiToggle = -1;

        GUIStyle _windowStyle, _titleStyle, _labelStyle, _hintStyle, _boxStyle, _smallStyle, _buttonStyle, _fieldStyle,
            _toggleStyle;
        Texture2D _texWindow, _texBox, _texButton, _texButtonHover, _texButtonActive, _texField;

        void Awake()
        {
            // 刷新角色后要用协程等一帧，再让 MaterialEditor 恢复材质数据
            ContentHotReload.CoroutineStarter = StartCoroutine;
        }

        void Update()
        {
            // HTTP 接口的请求排在这里，回到主线程执行
            ApiServer.Pump();

            // 「设置」页点的接口开关，放到主线程的 Update 里启停，不在 OnGUI 里碰套接字
            if (_apiToggle >= 0)
            {
                bool apiOn = _apiToggle == 1;
                _apiToggle = -1;
                ApiServer.SetEnabled(apiOn);
            }

            // Sideloader 可能比本插件晚加载，这里持续重试桥接
            ContentHotReload.Tick();

            // 录键过程中连窗口开关也不响应，否则按到 Ctrl+F7 会把窗口关掉
            if (!_capturing && HotkeyConfig.Get(HotkeyAction.ToggleWindow).IsDown())
            {
                _visible = !_visible;
                _guiErrorReported = false;
                if (!_visible)
                {
                    _dragging = false;
                    _capturing = false;
                    InputBlock.Clear();
                }
            }

            InputBlock.Visible = _visible;
            InputBlock.WindowRect = _rect;
            InputBlock.Dragging = _dragging;

            if (!_visible) return;

            // 录键中、设置页里、正在文本框输入，或者刚录完的那一帧，都不响应快捷键
            if (_capturing) return;
            if (_page != 0) return;
            if (Time.frameCount <= _suppressFrame) return;
            if (GUIUtility.keyboardControl != 0) return;

            HandleHotkeys();
        }

        /// <summary>用户自己绑的快捷键（默认都没绑，所以默认不会有任何动作）。</summary>
        void HandleHotkeys()
        {
            if (HotkeyConfig.Get(HotkeyAction.Release).IsDown())
            {
                ContentHotReload.Release(_guidText);
                return;
            }
            if (HotkeyConfig.Get(HotkeyAction.Reload).IsDown())
            {
                ContentHotReload.Reload();
                return;
            }
            if (HotkeyConfig.Get(HotkeyAction.Rescan).IsDown())
            {
                ContentHotReload.Rescan();
                return;
            }
            if (HotkeyConfig.Get(HotkeyAction.ClearLog).IsDown())
            {
                ContentHotReload.ClearLog();
                _lastLogCount = 0;
                return;
            }
            if (HotkeyConfig.Get(HotkeyAction.ExportLog).IsDown())
            {
                ContentHotReload.ExportLog();
            }
        }

        void OnGUI()
        {
            if (!_visible) return;

            var ev = Event.current;

            // 录键：拿 IMGUI 的 KeyDown 事件，能带上 Ctrl/Alt/Shift 状态
            if (_capturing && ev != null && ev.type == EventType.KeyDown)
            {
                CaptureKey(ev);
                return;
            }

            // 用 IMGUI 事件里的真实鼠标位置喂给 InputBlock，
            // 否则 mousePosition 被冻结后就没法判断鼠标什么时候离开窗口
            if (ev != null && ev.type != EventType.Used)
                InputBlock.RawMouse = new Vector3(ev.mousePosition.x, Screen.height - ev.mousePosition.y, 0f);

            EnsureStyle();
            var oldFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            try
            {
                _rect = GUI.Window(WindowId, _rect, DrawWindow, "", _windowStyle);
                InputBlock.WindowRect = _rect;
            }
            catch (Exception e)
            {
                if (!_guiErrorReported)
                {
                    _guiErrorReported = true;
                    ContentHotReload.SayErr("界面异常: " + e);
                }
            }
            finally
            {
                GUI.skin.font = oldFont;
            }
        }

        void CaptureKey(Event ev)
        {
            ev.Use();

            if (ev.keyCode == KeyCode.Escape)
            {
                _capturing = false;
                return;
            }
            if (Hotkey.IsModifierKey(ev.keyCode)) return;   // 只按修饰键不算

            HotkeyConfig.Set(_captureTarget, new Hotkey(ev.keyCode, ev.control, ev.alt, ev.shift));
            _capturing = false;
            _suppressFrame = Time.frameCount + 1;

            // 别让刚按的键继续落在输入框/按钮上
            GUIUtility.keyboardControl = 0;
        }

        void DrawWindow(int id)
        {
            // ---------- 标题栏 ----------
            var titleRect = new Rect(1f, 1f, _rect.width - 2f, TitleHeight);
            GUI.Label(titleRect, "HotReload  热重载  <size=10><color=#9aa0a6>v1.3.0 by Qes001</color></size>", _titleStyle);

            if (GUI.Button(new Rect(_rect.width - 50f, 4f, 20f, 18f), _collapsed ? "+" : "-", _smallStyle))
            {
                _collapsed = !_collapsed;
                _rect.height = _collapsed ? TitleHeight + 2f : WindowHeight;
            }

            if (GUI.Button(new Rect(_rect.width - 27f, 4f, 20f, 18f), "x", _smallStyle))
            {
                _visible = false;
                _dragging = false;
                _capturing = false;
                InputBlock.Clear();
                return;
            }

            // 只允许拖标题栏，避免拖窗口时把模型一起拖了
            var e = Event.current;
            if (e.type == EventType.MouseDown && titleRect.Contains(e.mousePosition)) _dragging = true;
            else if (e.type == EventType.MouseUp) _dragging = false;
            InputBlock.Dragging = _dragging;

            if (!_collapsed)
            {
                GUI.DragWindow(titleRect);

                // ---------- 主体 ----------
                float bodyTop = TitleHeight + Pad;
                float bodyHeight = _rect.height - bodyTop - Pad;
                if (bodyHeight < 120f) bodyHeight = 120f;

                GUILayout.BeginArea(new Rect(Pad, bodyTop, _rect.width - Pad * 2f, bodyHeight));

                if (_page == 1)
                {
                    GUILayout.BeginVertical(_boxStyle, GUILayout.ExpandHeight(true));
                    _settingsScroll = GUILayout.BeginScrollView(_settingsScroll);
                    DrawSettings();
                    GUILayout.EndScrollView();
                    GUILayout.EndVertical();
                }
                else
                {
                    // 日志区
                    GUILayout.BeginVertical(_boxStyle, GUILayout.ExpandHeight(true));
                    _scroll = GUILayout.BeginScrollView(_scroll);
                    var logs = ContentHotReload.Logs;
                    for (int i = 0; i < logs.Count; i++)
                        GUILayout.Label(logs[i], _labelStyle);
                    GUILayout.EndScrollView();
                    GUILayout.EndVertical();

                    // 内容包
                    GUILayout.Space(4f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("内容包", _hintStyle, GUILayout.Width(56f));
                    _guidText = GUILayout.TextField(_guidText == null ? "" : _guidText, _fieldStyle,
                        GUILayout.Height(22f), GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    // 选项：默认只重载 unity3d
                    GUILayout.Space(2f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("选项", _hintStyle, GUILayout.Width(56f));
                    ContentHotReload.SetReloadLists(GUILayout.Toggle(ContentHotReload.ReloadLists,
                        " 表格/manifest", _toggleStyle, GUILayout.Height(20f)));
                    GUILayout.EndHorizontal();
                }

                // 操作
                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("操作", _hintStyle, GUILayout.Width(56f));
                GUILayout.FlexibleSpace();

                if (_page == 1)
                {
                    if (GUILayout.Button("返回", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                    {
                        _page = 0;
                        _capturing = false;
                    }
                }
                else
                {
                    if (GUILayout.Button("释放", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                        ContentHotReload.Release(_guidText);
                    if (GUILayout.Button("重载", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                        ContentHotReload.Reload();
                    if (GUILayout.Button("检索", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                        ContentHotReload.Rescan();
                    if (GUILayout.Button("清空", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                    {
                        ContentHotReload.ClearLog();
                        _lastLogCount = 0;
                    }
                    if (GUILayout.Button("导出", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                        ContentHotReload.ExportLog();
                    if (GUILayout.Button("设置", _buttonStyle, GUILayout.Width(56f), GUILayout.Height(26f)))
                    {
                        _page = 1;
                        _capturing = false;
                    }
                }

                GUILayout.EndHorizontal();

                GUILayout.EndArea();
            }

            // 日志自动滚到底
            if (_page == 0 && ContentHotReload.LogCount != _lastLogCount)
            {
                _lastLogCount = ContentHotReload.LogCount;
                _scroll.y = float.MaxValue;
            }
        }

        void DrawSettings()
        {
            GUILayout.Label("点「改键」后按想要的组合键（Esc 取消），「默认/清除」恢复初始状态。快捷键只在主界面、窗口打开时生效，本页内不响应。", _labelStyle);
            GUILayout.Space(4f);
            GUILayout.Label("开关窗口 / payload 重载 出厂就带默认键，其余默认不绑定，都可自行修改。", _labelStyle);
            GUILayout.Space(6f);

            var actions = HotkeyConfig.All;
            for (int i = 0; i < actions.Length; i++)
            {
                var action = actions[i];
                var hk = HotkeyConfig.Get(action);
                bool capturingThis = _capturing && _captureTarget == action;
                var def = HotkeyConfig.Default(action);

                GUILayout.BeginHorizontal();
                GUILayout.Label(HotkeyConfig.Name(action), _hintStyle, GUILayout.Width(84f));

                string text;
                if (capturingThis) text = "按下组合键…";
                else if (hk.IsSet) text = hk.Describe();
                else text = "未设置";

                if (!capturingThis && def.IsSet && hk.SameAs(def))
                    text += "（默认）";

                GUILayout.Label(text, _labelStyle, GUILayout.ExpandWidth(true));

                if (GUILayout.Button(capturingThis ? "取消" : "改键", _buttonStyle,
                    GUILayout.Width(50f), GUILayout.Height(22f)))
                {
                    if (capturingThis)
                    {
                        _capturing = false;
                    }
                    else
                    {
                        _capturing = true;
                        _captureTarget = action;
                    }
                }

                if (GUILayout.Button(def.IsSet ? "默认" : "清除", _buttonStyle,
                    GUILayout.Width(50f), GUILayout.Height(22f)))
                {
                    HotkeyConfig.Reset(action);
                    if (capturingThis) _capturing = false;
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10f);
            GUILayout.Label("HTTP 接口：给 Python 等外部脚本用，只监听 127.0.0.1，默认关闭。", _labelStyle);
            GUILayout.Space(2f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("接口开关", _hintStyle, GUILayout.Width(84f));

            bool apiOn = GUILayout.Toggle(ApiServer.Enabled, " 开启", _toggleStyle, GUILayout.Height(20f));
            if (apiOn != ApiServer.Enabled) _apiToggle = apiOn ? 1 : 0;

            string apiState;
            if (!ApiServer.Enabled) apiState = "已关闭";
            else if (ApiServer.IsRunning) apiState = "运行中 · 端口 " + ApiServer.Port;
            else apiState = "启动失败，端口可能被占用";

            GUILayout.Label(apiState, _labelStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("配置写在 BepInEx/hotreload/hotkeys.txt 与 api.txt，删掉即恢复初始状态。", _labelStyle);
        }

        void EnsureStyle()
        {
            if (_font == null)
            {
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei", "SimHei", "Meiryo", "Arial Unicode MS", "Arial" }, 13);
                }
                catch (Exception)
                {
                    _font = null;
                }
            }

            if (_windowStyle != null) return;

            var line = new Color(0.32f, 0.32f, 0.35f, 1f);

            _texWindow = MakeBox(new Color(0.14f, 0.14f, 0.15f, 0.98f), line);
            _texBox = MakeBox(new Color(0.06f, 0.06f, 0.07f, 1f), line);
            _texButton = MakeBox(new Color(0.21f, 0.21f, 0.23f, 1f), line);
            _texButtonHover = MakeBox(new Color(0.29f, 0.29f, 0.32f, 1f), line);
            _texButtonActive = MakeBox(new Color(0.37f, 0.37f, 0.41f, 1f), line);
            _texField = MakeBox(new Color(0.06f, 0.06f, 0.07f, 1f), line);

            _windowStyle = new GUIStyle(GUI.skin.box);
            _windowStyle.normal.background = _texWindow;
            _windowStyle.border = new RectOffset(1, 1, 1, 1);
            _windowStyle.padding = new RectOffset(0, 0, 0, 0);
            _windowStyle.margin = new RectOffset(0, 0, 0, 0);

            // 标题栏底色
            var titleBg = MakeBox(new Color(0.19f, 0.19f, 0.21f, 1f), line);
            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.normal.background = titleBg;
            _titleStyle.normal.textColor = new Color(0.92f, 0.92f, 0.94f, 1f);
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.richText = true;
            _titleStyle.border = new RectOffset(1, 1, 1, 1);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.normal.textColor = new Color(0.82f, 0.82f, 0.85f, 1f);
            _labelStyle.wordWrap = true;
            _labelStyle.fontSize = 12;

            _hintStyle = new GUIStyle(GUI.skin.label);
            _hintStyle.normal.textColor = new Color(0.70f, 0.70f, 0.74f, 1f);
            _hintStyle.alignment = TextAnchor.MiddleLeft;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = _texBox;
            _boxStyle.border = new RectOffset(1, 1, 1, 1);
            _boxStyle.padding = new RectOffset(6, 6, 6, 6);
            _boxStyle.margin = new RectOffset(0, 0, 0, 0);

            _smallStyle = new GUIStyle(GUI.skin.button);
            _smallStyle.normal.background = _texButton;
            _smallStyle.hover.background = _texButtonHover;
            _smallStyle.active.background = _texButtonActive;
            _smallStyle.normal.textColor = new Color(0.88f, 0.88f, 0.90f, 1f);
            _smallStyle.hover.textColor = Color.white;
            _smallStyle.active.textColor = Color.white;
            _smallStyle.alignment = TextAnchor.MiddleCenter;
            _smallStyle.fontSize = 12;
            _smallStyle.padding = new RectOffset(0, 0, 0, 0);
            _smallStyle.border = new RectOffset(1, 1, 1, 1);
            _smallStyle.margin = new RectOffset(0, 0, 0, 0);

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.normal.background = _texButton;
            _buttonStyle.hover.background = _texButtonHover;
            _buttonStyle.active.background = _texButtonActive;
            _buttonStyle.normal.textColor = new Color(0.88f, 0.88f, 0.90f, 1f);
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.textColor = Color.white;
            _buttonStyle.alignment = TextAnchor.MiddleCenter;
            _buttonStyle.fontSize = 12;
            _buttonStyle.border = new RectOffset(1, 1, 1, 1);

            _fieldStyle = new GUIStyle(GUI.skin.textField);
            _fieldStyle.normal.background = _texField;
            _fieldStyle.focused.background = _texField;
            _fieldStyle.normal.textColor = new Color(0.90f, 0.90f, 0.92f, 1f);
            _fieldStyle.focused.textColor = Color.white;
            _fieldStyle.padding = new RectOffset(6, 6, 4, 4);
            _fieldStyle.border = new RectOffset(1, 1, 1, 1);

            _toggleStyle = new GUIStyle(GUI.skin.toggle);
            _toggleStyle.normal.textColor = new Color(0.82f, 0.82f, 0.85f, 1f);
            _toggleStyle.onNormal.textColor = new Color(0.90f, 0.90f, 0.92f, 1f);
            _toggleStyle.hover.textColor = Color.white;
            _toggleStyle.onHover.textColor = Color.white;
            _toggleStyle.fontSize = 12;
        }

        static Texture2D MakeBox(Color fill, Color border)
        {
            var t = new Texture2D(3, 3, TextureFormat.RGBA32, false);
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                    t.SetPixel(x, y, (x == 0 || y == 0 || x == 2 || y == 2) ? border : fill);
            }
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.Apply();
            return t;
        }
    }
}
