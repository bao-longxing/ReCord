using System;
using System.Windows.Forms;
using ReCord.Services;

namespace ReCord
{
    internal static class Program
    {
        /// <summary>配置全局异常处理并启动录相机主窗口。</summary>
        [STAThread]
        private static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, args) =>
            {
                AppLog.Error("未处理的界面异常：" + args.Exception);
                System.Windows.MessageBox.Show("ReCord 发生异常：\n" + args.Exception.Message,
                    "ReCord", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                AppLog.Error("未处理的进程异常：" + args.ExceptionObject);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new RecorderForm());
        }
    }
}
