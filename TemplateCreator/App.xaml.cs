using System;
using System.IO;
using System.Windows;

namespace TIAgentTemplateCreator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template_creator_crash.txt");
                    File.WriteAllText(path, "DispatcherUnhandledException:\r\n" + args.Exception?.ToString());
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template_creator_crash.txt");
                    var ex = args.ExceptionObject as Exception;
                    File.WriteAllText(path, "AppDomain.UnhandledException:\r\n" + ex?.ToString());
                }
                catch { }
            };

            base.OnStartup(e);
        }
    }
}
