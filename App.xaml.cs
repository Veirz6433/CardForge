using BulkImageGenerator.Services;
using System.Windows;

namespace BulkImageGenerator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ExcelService.RegisterEncodings();
            base.OnStartup(e);
        }
    }
}
