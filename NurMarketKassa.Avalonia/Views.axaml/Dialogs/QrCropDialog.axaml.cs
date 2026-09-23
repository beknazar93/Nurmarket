using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class QrCropDialog : Window
{
    private readonly string _bankName = "QR";
    private Bitmap? _source;
    private CroppedBitmap? _preview;
    private PixelRect _cropRect;
    private bool _initialized;

    public QrCropDialog()
    {
        InitializeComponent();
    }

    public QrCropDialog(string imagePath, string bankName) : this()
    {
        _bankName = bankName;
        SubtitleText.Text = $"{bankName}: уберите лишние поля по краям. Оставьте небольшую белую рамку, чтобы QR надёжно сканировался. Исходный файл изменён не будет.";

        try
        {
            _source = new Bitmap(imagePath);
            _initialized = true;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            ShowError("Не удалось открыть изображение: " + ex.Message);
        }
    }

    private void CropSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initialized)
            UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (!_initialized || _source is null
            || LeftSlider is null || RightSlider is null
            || TopSlider is null || BottomSlider is null
            || PreviewImage is null)
            return;

        var size = _source.PixelSize;
        int left = ToPixels(LeftSlider.Value, size.Width);
        int right = ToPixels(RightSlider.Value, size.Width);
        int top = ToPixels(TopSlider.Value, size.Height);
        int bottom = ToPixels(BottomSlider.Value, size.Height);

        int width = Math.Max(1, size.Width - left - right);
        int height = Math.Max(1, size.Height - top - bottom);
        _cropRect = new PixelRect(left, top, width, height);

        if (_preview is null)
        {
            _preview = new CroppedBitmap
            {
                Source = _source,
                SourceRect = _cropRect,
            };
            PreviewImage.Source = _preview;
        }
        else
        {
            // Keep the same instance while the Image control is rendering it.
            // Disposing/replacing CroppedBitmap on every slider tick can race with rendering.
            _preview.SourceRect = _cropRect;
        }

        if (LeftValueText is not null) LeftValueText.Text = $"{LeftSlider.Value:0}%";
        if (RightValueText is not null) RightValueText.Text = $"{RightSlider.Value:0}%";
        if (TopValueText is not null) TopValueText.Text = $"{TopSlider.Value:0}%";
        if (BottomValueText is not null) BottomValueText.Text = $"{BottomSlider.Value:0}%";
        if (SizeText is not null) SizeText.Text = $"Результат: {width} × {height} px";
        if (ErrorText is not null) ErrorText.IsVisible = false;
    }

    private static int ToPixels(double percent, int total) =>
        (int)Math.Round(total * percent / 100d);

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _initialized = false;
        LeftSlider.Value = 0;
        RightSlider.Value = 0;
        TopSlider.Value = 0;
        BottomSlider.Value = 0;
        _initialized = true;
        UpdatePreview();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close((string?)null);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_preview is null || _source is null)
        {
            ShowError("Изображение не загружено.");
            return;
        }

        try
        {
            string directory = GetManagedQrDirectory();
            Directory.CreateDirectory(directory);
            string safeBankName = string.Concat(_bankName.Select(ch =>
                Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string outputPath = Path.Combine(
                directory,
                $"{safeBankName}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.png");

            // CroppedBitmap is a lightweight view and cannot encode itself.
            // Render it at its native cropped size, then encode the result as PNG.
            using var rendered = new RenderTargetBitmap(
                new PixelSize(_cropRect.Width, _cropRect.Height),
                new Vector(96, 96));
            var outputImage = new Image
            {
                Source = _preview,
                Width = _cropRect.Width,
                Height = _cropRect.Height,
                Stretch = Stretch.Fill,
            };
            var outputSize = new Size(_cropRect.Width, _cropRect.Height);
            outputImage.Measure(outputSize);
            outputImage.Arrange(new Rect(outputSize));
            rendered.Render(outputImage);
            rendered.Save(outputPath);
            Close(outputPath);
        }
        catch (Exception ex)
        {
            ShowError("Не удалось сохранить QR-код: " + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    public static string GetManagedQrDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "BankQr");

    protected override void OnClosed(EventArgs e)
    {
        PreviewImage.Source = null;
        _preview?.Dispose();
        _source?.Dispose();
        base.OnClosed(e);
    }
}
