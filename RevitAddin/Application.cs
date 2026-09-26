using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BimS.Revit2024
{
    public sealed class Application : IExternalApplication
    {
        private NamedPipeBridge bridge;
        private RevitReadHandler readHandler;

        public Result OnStartup(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel("BIM-S");
            var button = new PushButtonData("BimSButton", "BIM-S",
                Assembly.GetExecutingAssembly().Location, typeof(Command).FullName);
            button.AvailabilityClassName = typeof(Availability).FullName;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BimS.Revit2024.BIM-S.png")
                ?? throw new System.InvalidOperationException("Ресурс иконки BIM-S не найден в сборке."))
            {
                var icon = new BitmapImage();
                icon.BeginInit();
                icon.CacheOption = BitmapCacheOption.OnLoad;
                icon.StreamSource = stream;
                icon.EndInit();
                icon.Freeze();
                button.LargeImage = icon;
                button.Image = icon;
            }
            panel.AddItem(button);
            readHandler = new RevitReadHandler();
            try { bridge = new NamedPipeBridge(readHandler.RequestAsync); }
            catch { readHandler.Dispose(); throw; }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            bridge?.Dispose();
            readHandler?.Dispose();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            TaskDialog.Show("BIM-S", "BIM-S работает");
            return Result.Succeeded;
        }
    }

    public sealed class Availability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
