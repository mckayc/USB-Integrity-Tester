using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsbIntegrityTester.App.Views;

public partial class ReportView : UserControl
{
    public ReportView()
    {
        InitializeComponent();
    }

    private void ExportReportPng_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"usb-integrity-report-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var size = new Size(ReportCard.ActualWidth, ReportCard.ActualHeight);
        ReportCard.Measure(size);
        ReportCard.Arrange(new Rect(size));

        var bitmap = new RenderTargetBitmap(
            (int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(ReportCard);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new FileStream(dialog.FileName, FileMode.Create);
        encoder.Save(stream);
    }
}
