using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace WinSysUtil
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--logger")
                {
                    Service1.RunAsLogger();
                }
                else
                {
                    Service1.LogToFile("Main: Starting Service mode");
                    ServiceBase[] ServicesToRun;
                    ServicesToRun = new ServiceBase[]
                    {
                        new Service1()
                    };
                    ServiceBase.Run(ServicesToRun);
                }
            }
            catch (Exception ex)
            {
                Service1.LogToFile($"Main Fatal Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
