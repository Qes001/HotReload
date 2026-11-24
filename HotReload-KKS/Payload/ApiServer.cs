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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 极简 HTTP 接口，只监听 127.0.0.1，方便用 Python 等外部脚本驱动释放 / 重载 / 检索。
    ///
    /// 为什么不用 HttpListener：它走 http.sys，非管理员运行时经常撞 URL ACL 绑不上；
    /// 这里直接用 TcpListener 手写一层 HTTP/1.1，够用且不需要任何权限。
    ///
    /// 所有动作都会被丢回主线程执行（Unity 对象不能跨线程碰），
    /// 请求线程阻塞等结果，超时返回 503。
    /// </summary>
    internal static class ApiServer
    {
        const int DefaultPort = 8712;
        const int MaxRequestBytes = 8192;
        const int ActionTimeoutMs = 30000;

        static readonly Queue<Job> _jobs = new Queue<Job>();
        static readonly object _lock = new object();

        static TcpListener _listener;
        static Thread _thread;
        static volatile bool _running;
        static bool _started;      // 已经初始化过，别重复绑端口
        static bool _enabled;      // 配置里的开关，默认关闭

        static string _token = "";

        public static int Port { get; private set; }
        public static bool IsRunning { get { return _running; } }
        public static bool Enabled { get { return _enabled; } }
        public static bool NeedsToken { get { return _token.Length > 0; } }

        sealed class Job
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Query;
            public int Status;
            public string Json;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        static string ConfigPath
        {
            get
            {
                try { return Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "hotreload"), "api.txt"); }
                catch (Exception) { return null; }
            }
        }

        // ---------------------------------------------------------------- 生命周期

        public static void Start()
        {
            if (_started) return;
            _started = true;

            bool enabled = false;
            int port = DefaultPort;
            string token = "";
            LoadConfig(ref enabled, ref port, ref token);

            _enabled = enabled;
            _token = token == null ? "" : token;
            Port = port;

            if (!_enabled)
            {
                ContentHotReload.Say("HTTP 接口未开启（窗口 →「设置」里可以打开）");
                return;
            }

            Open();
        }

        /// <summary>「设置」页里的开关：立刻启停，并写回 api.txt。</summary>
        public static void SetEnabled(bool on)
        {
            if (on == _enabled) return;

            _enabled = on;
            _started = true;
            SaveConfig();

            if (on) Open();
            else Close("HTTP 接口已关闭");
        }

        public static void Stop()
        {
            Close(null);
            _started = false;
        }

        static void Open()
        {
            if (_running) return;

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                _running = true;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "HotReload.Api";
                _thread.Start();

                ContentHotReload.Say("API 已启动: http://127.0.0.1:" + Port + "/"
                    + (NeedsToken ? "（需要 token）" : "（仅本机可访问）")
                    + " · GET /status 看接口列表");
            }
            catch (Exception e)
            {
                _running = false;
                try { if (_listener != null) _listener.Stop(); }
                catch (Exception) { }
                _listener = null;

                ContentHotReload.SayErr("API 启动失败: " + e.Message);
            }
        }

        static void Close(string message)
        {
            bool wasRunning = _running;
            _running = false;

            try { if (_listener != null) _listener.Stop(); }
            catch (Exception) { }

            _listener = null;
            _thread = null;

            // 让还卡在等待里的请求线程立刻返回
            lock (_lock)
            {
                while (_jobs.Count > 0) _jobs.Dequeue().Done.Set();
            }

            if (wasRunning && message != null) ContentHotReload.Say(message);
        }

        static void LoadConfig(ref bool enabled, ref int port, ref string token)
        {
            try
            {
                var path = ConfigPath;
                if (path == null) return;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(path))
                {
                    // 首次运行写一份带注释的默认配置，方便改端口 / 开 token / 整个关掉
                    var sb = new StringBuilder();
                    sb.AppendLine("# HotReload HTTP API（只监听 127.0.0.1）");
                    sb.AppendLine("# enabled=0 可整体关闭（默认）；port 改端口；token 非空时请求需带 token 参数");
                    sb.AppendLine("# 也可以在游戏窗口「设置」里点开关，改完会写回这里");
                    sb.AppendLine("enabled=0");
                    sb.AppendLine("port=8712");
                    sb.AppendLine("token=");
                    File.WriteAllText(path, sb.ToString());
                }
                else
                {
                    var lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (line.Length == 0 || line[0] == '#') continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        var key = line.Substring(0, eq).Trim();
                        var value = line.Substring(eq + 1).Trim();

                        if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                            enabled = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
                        else if (key.Equals("port", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsed;
                            if (int.TryParse(value, out parsed) && parsed > 0 && parsed < 65536) port = parsed;
                        }
                        else if (key.Equals("token", StringComparison.OrdinalIgnoreCase))
                            token = value;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把当前开关写回 api.txt（端口 / token 保持不变）。</summary>
        static void SaveConfig()
        {
            try
            {
                var path = ConfigPath;
                if (path == null) return;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# HotReload HTTP API（只监听 127.0.0.1）");
                sb.AppendLine("# enabled=0 可整体关闭（默认）；port 改端口；token 非空时请求需带 token 参数");
                sb.AppendLine("# 也可以在游戏窗口「设置」里点开关，改完会写回这里");
                sb.AppendLine("enabled=" + (_enabled ? "1" : "0"));
                sb.AppendLine("port=" + Port);
                sb.AppendLine("token=" + _token);
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception)
            {
            }
        }

        // ---------------------------------------------------------------- 网络线程

        static void Loop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { break; }
                if (client == null) continue;

                try { ThreadPool.QueueUserWorkItem(HandleClient, client); }
                catch (Exception) { try { client.Close(); } catch (Exception) { } }
            }
        }

        static void HandleClient(object state)
        {
            var client = state as TcpClient;
            if (client == null) return;

            try
            {
                client.ReceiveTimeout = 5000;

                var stream = client.GetStream();
                if (stream == null) return;

                Job job;
                if (!TryReadRequest(stream, out job))
                {
                    Write(stream, 400, ErrorJson("请求格式不正确"));
                    return;
                }

                if (NeedsToken && !TokenOk(job.Query))
                {
                    Write(stream, 401, ErrorJson("token 不正确（api.txt 里配置的 token）"));
                    return;
                }

                lock (_lock) _jobs.Enqueue(job);

                if (!job.Done.WaitOne(ActionTimeoutMs))
                {
                    Write(stream, 503, ErrorJson("主线程忙，操作超时"));
                    return;
                }

                Write(stream, job.Status == 0 ? 500 : job.Status,
                    job.Json == null ? ErrorJson("没有返回值") : job.Json);
            }
            catch (Exception)
            {
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        static bool TryReadRequest(Stream stream, out Job job)
        {
            job = null;

            var buffer = new byte[MaxRequestBytes];
            int total = 0;
            int headerEnd = -1;

            while (total < buffer.Length)
            {
                int read;
                try { read = stream.Read(buffer, total, buffer.Length - total); }
                catch (Exception) { return false; }

                if (read <= 0) break;
                total += read;

                var probe = Encoding.ASCII.GetString(buffer, 0, total);
                headerEnd = probe.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0) break;
            }

            if (headerEnd < 0) return false;

            var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            int lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd <= 0) return false;

            var parts = head.Substring(0, lineEnd).Split(' ');
            if (parts.Length < 2) return false;

            var target = parts[1];
            int q = target.IndexOf('?');

            job = new Job();
            job.Method = parts[0].ToUpperInvariant();
            job.Path = q >= 0 ? target.Substring(0, q) : target;
            job.Query = new Dictionary<string, string>();
            if (q >= 0) ParseQuery(target.Substring(q + 1), job.Query);
            return true;
        }

        static void ParseQuery(string text, Dictionary<string, string> into)
        {
            var pairs = text.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;

                int eq = pairs[i].IndexOf('=');
                var key = eq >= 0 ? pairs[i].Substring(0, eq) : pairs[i];
                var value = eq >= 0 ? pairs[i].Substring(eq + 1) : "";

                key = Decode(key);
                value = Decode(value);
                if (key.Length == 0) continue;

                into[key.ToLowerInvariant()] = value;
            }
        }

        static string Decode(string text)
        {
            try { return Uri.UnescapeDataString(text.Replace("+", " ")); }
            catch (Exception) { return text; }
        }

        static bool TokenOk(Dictionary<string, string> query)
        {
            string token;
            if (!query.TryGetValue("token", out token)) return false;
            return string.Equals(token, _token, StringComparison.Ordinal);
        }

        static void Write(Stream stream, int status, string json)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(json ?? "");
                var head = "HTTP/1.1 " + status + " " + StatusText(status) + "\r\n"
                    + "Content-Type: application/json; charset=utf-8\r\n"
                    + "Content-Length: " + body.Length + "\r\n"
                    + "Connection: close\r\n"
                    + "Access-Control-Allow-Origin: *\r\n"
                    + "\r\n";

                var headBytes = Encoding.ASCII.GetBytes(head);
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            catch (Exception)
            {
            }
        }

        static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }

        // ---------------------------------------------------------------- 主线程

        /// <summary>每帧由界面调用：把排队的请求在主线程执行掉。</summary>
        public static void Pump()
        {
            while (true)
            {
                Job job;
                lock (_lock)
                {
                    if (_jobs.Count == 0) return;
                    job = _jobs.Dequeue();
                }

                try { Dispatch(job); }
                catch (Exception e)
                {
                    job.Status = 500;
                    job.Json = ErrorJson(e.Message);
                }
                finally
                {
                    job.Done.Set();
                }
            }
        }

        static void Dispatch(Job job)
        {
            var path = job.Path ?? "/";
            if (path.Length > 1 && path[path.Length - 1] == '/') path = path.Substring(0, path.Length - 1);

            string guid;
            job.Query.TryGetValue("guid", out guid);

            switch (path.ToLowerInvariant())
            {
                case "/":
                case "/status":
                    Status(job);
                    return;

                case "/log":
                    Log(job);
                    return;

                case "/hotkeys":
                    Hotkeys(job);
                    return;

                case "/release":
                    RunAction(job, delegate { ContentHotReload.Release(guid); });
                    return;

                case "/reload":
                    RunAction(job, ContentHotReload.Reload);
                    return;

                case "/rescan":
                    RunAction(job, ContentHotReload.Rescan);
                    return;

                case "/clear":
                    RunAction(job, ClearLog);
                    return;

                case "/export":
                    RunAction(job, ContentHotReload.ExportLog);
                    return;

                case "/hotkey":
                    SetHotkey(job);
                    return;

                case "/options":
                    SetOption(job);
                    return;

                default:
                    job.Status = 404;
                    job.Json = ErrorJson("未知接口: " + path);
                    return;
            }
        }

        static void ClearLog()
        {
            ContentHotReload.ClearLog();
            ContentHotReload.Say("日志已清空");
        }

        // ---------------------------------------------------------------- 接口实现

        static void Status(Job job)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"ok\":true");
            sb.Append(",\"version\":").Append(Quote(BuildStamp.Value));
            sb.Append(",\"game\":").Append(Quote(ProductName()));
            sb.Append(",\"bridge\":").Append(SideloaderBridge.Available ? "true" : "false");
            sb.Append(",\"capabilities\":").Append(Quote(SideloaderBridge.Capabilities));
            sb.Append(",\"bridgeError\":").Append(Quote(SideloaderBridge.LastError));
            sb.Append(",\"reloadLists\":").Append(ContentHotReload.ReloadLists ? "true" : "false");
            sb.Append(",\"activeZips\":").Append(StringArray(ContentHotReload.ActiveZips));
            sb.Append(",\"logs\":").Append(ContentHotReload.LogCount);
            sb.Append(",\"api\":{\"enabled\":").Append(_enabled ? "true" : "false")
              .Append(",\"port\":").Append(Port)
              .Append(",\"token\":").Append(NeedsToken ? "true" : "false")
              .Append(",\"loopbackOnly\":true}");
            sb.Append(",\"endpoints\":[\"GET /status\",\"GET /log?limit=100\",\"GET /hotkeys\","
                + "\"POST /release?guid=a,b\",\"POST /reload\",\"POST /rescan\",\"POST /clear\","
                + "\"POST /export\",\"POST /hotkey?action=release&key=F5&ctrl=1\",\"POST /options?reloadlists=1\"]");
            sb.Append('}');

            job.Status = 200;
            job.Json = sb.ToString();
        }

        static void Log(Job job)
        {
            int limit = 100;
            string raw;
            if (job.Query.TryGetValue("limit", out raw))
            {
                int parsed;
                if (int.TryParse(raw, out parsed) && parsed > 0) limit = parsed;
            }

            var logs = ContentHotReload.Logs;
            int start = logs.Count - limit;
            if (start < 0) start = 0;

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"log\":[");
            for (int i = start; i < logs.Count; i++)
            {
                if (i > start) sb.Append(',');
                sb.Append(Quote(logs[i]));
            }
            sb.Append("],\"total\":").Append(logs.Count).Append('}');

            job.Status = 200;
            job.Json = sb.ToString();
        }

        static void Hotkeys(Job job)
        {
            var actions = HotkeyConfig.All;
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"hotkeys\":[");
            for (int i = 0; i < actions.Length; i++)
            {
                var action = actions[i];
                var hk = HotkeyConfig.Get(action);

                if (i > 0) sb.Append(',');
                sb.Append("{\"action\":").Append(Quote(HotkeyConfig.KeyName(action)))
                  .Append(",\"name\":").Append(Quote(HotkeyConfig.Name(action)))
                  .Append(",\"key\":").Append(Quote(hk.Describe()))
                  .Append(",\"bound\":").Append(hk.IsSet ? "true" : "false")
                  .Append('}');
            }
            sb.Append("]}");

            job.Status = 200;
            job.Json = sb.ToString();
        }

        static void RunAction(Job job, Action action)
        {
            int before = ContentHotReload.LogCount;
            action();

            var logs = ContentHotReload.Logs;
            int start = before;
            if (start > logs.Count) start = 0;      // 日志被截断过就从最老的开始

            bool ok = true;
            for (int i = start; i < logs.Count; i++)
            {
                if (logs[i].StartsWith("[x]", StringComparison.Ordinal)) { ok = false; break; }
            }

            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(ok ? "true" : "false");
            sb.Append(",\"activeZips\":").Append(StringArray(ContentHotReload.ActiveZips));
            sb.Append(",\"log\":[");
            for (int i = start; i < logs.Count; i++)
            {
                if (i > start) sb.Append(',');
                sb.Append(Quote(logs[i]));
            }
            sb.Append("]}");

            job.Status = 200;
            job.Json = sb.ToString();
        }

        static void SetHotkey(Job job)
        {
            string actionName, key;
            job.Query.TryGetValue("action", out actionName);
            job.Query.TryGetValue("key", out key);

            if (string.IsNullOrEmpty(actionName))
            {
                job.Status = 400;
                job.Json = ErrorJson("缺少 action 参数（window / payloadreload / release / reload / rescan / clearlog / exportlog）");
                return;
            }

            HotkeyAction action;
            if (!TryFindAction(actionName, out action))
            {
                job.Status = 400;
                job.Json = ErrorJson("未知 action: " + actionName);
                return;
            }

            if (string.IsNullOrEmpty(key))
            {
                HotkeyConfig.Reset(action);
            }
            else
            {
                KeyCode code;
                try { code = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
                catch (Exception)
                {
                    job.Status = 400;
                    job.Json = ErrorJson("未知按键: " + key);
                    return;
                }

                HotkeyConfig.Set(action, new Hotkey(code, Flag(job, "ctrl"), Flag(job, "alt"), Flag(job, "shift")));
            }

            var hk = HotkeyConfig.Get(action);
            job.Status = 200;
            job.Json = "{\"ok\":true,\"action\":" + Quote(HotkeyConfig.KeyName(action))
                + ",\"key\":" + Quote(hk.Describe()) + "}";
        }

        static void SetOption(Job job)
        {
            string raw;
            if (job.Query.TryGetValue("reloadlists", out raw))
                ContentHotReload.SetReloadLists(raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));

            job.Status = 200;
            job.Json = "{\"ok\":true,\"reloadLists\":" + (ContentHotReload.ReloadLists ? "true" : "false") + "}";
        }

        static bool TryFindAction(string name, out HotkeyAction action)
        {
            action = HotkeyAction.Release;

            var actions = HotkeyConfig.All;
            for (int i = 0; i < actions.Length; i++)
            {
                if (!HotkeyConfig.KeyName(actions[i]).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                action = actions[i];
                return true;
            }
            return false;
        }

        static bool Flag(Job job, string name)
        {
            string raw;
            if (!job.Query.TryGetValue(name, out raw)) return false;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- 工具

        static string ProductName()
        {
            try { return Application.productName; }
            catch (Exception) { return "unknown"; }
        }

        static string StringArray(string[] items)
        {
            var sb = new StringBuilder("[");
            if (items != null)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Quote(items[i]));
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        static string ErrorJson(string message)
        {
            return "{\"ok\":false,\"error\":" + Quote(message) + "}";
        }

        static string Quote(string text)
        {
            if (text == null) return "null";

            var sb = new StringBuilder("\"");
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
