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
        // 2026-11-19 (п. 2 владельца): рамку можно двигать мышкой/пальцем прямо на картинке; ползунки справа — запасной путь
        DragHintText.Text = Tr.T("Тяните рамку на картинке за углы, стороны или середину; ползунки справа — для точной подстройки.",
            "Сүрөттөгү алкакты бурчтарынан, жактарынан же ортосунан сүйрөңүз; оң жактагы жылдыргычтар — так тууралоо үчүн.",
            "Drag the frame on the picture by its corners, sides or middle; the sliders on the right are for fine tuning.",
            "Resimdeki çerçeveyi köşelerinden, kenarlarından veya ortasından sürükleyin; sağdaki kaydırıcılar ince ayar içindir.",
            "Rasmdagi ramkani burchaklari, tomonlari yoki o'rtasidan torting; o'ngdagi surgichlar — aniq sozlash uchun.");
        CropSurface.CropChanged += (l, t, r, b) =>
        {
            if (!_initialized)
                return;
            _initialized = false; // значения ползунков ставим без лишних перерисовок
            LeftSlider.Value = double.Round(l, 1); // 2026-11-19: double.Round — геометрия рамки, не деньги
            TopSlider.Value = double.Round(t, 1);
            RightSlider.Value = double.Round(r, 1);
            BottomSlider.Value = double.Round(b, 1);
            _initialized = true;
            UpdatePreview(syncSurface: false);
        };
    }

    public QrCropDialog(string imagePath, string bankName) : this()
    {
        _bankName = bankName;
        SubtitleText.Text = Tr.T($"{bankName}: уберите лишние поля по краям. Оставьте небольшую белую рамку, чтобы QR надёжно сканировался. Исходный файл изменён не будет.", $"{bankName}: четтериндеги ашыкча боштукту кесип салыңыз. QR ишенимдүү сканерленышы үчүн кичинекей ак алкак калтырыңыз. Баштапкы файл өзгөрбөйт.", $"{bankName}: trim the extra margins around the edges. Leave a small white border so the QR code scans reliably. The original file will not be changed.", $"{bankName}: kenarlardaki fazla boşlukları kırpın. QR kodunun sorunsuz okunması için küçük bir beyaz çerçeve bırakın. Orijinal dosya değiştirilmeyecek.", $"{bankName}: chetlardagi ortiqcha joylarni olib tashlang. QR ishonchli skanerlanishi uchun kichik oq hoshiya qoldiring. Asl fayl o'zgartirilmaydi.");

        try
        {
            _source = new Bitmap(imagePath);
            CropSurface.SetBitmap(_source);
            _initialized = true;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            ShowError(Tr.T("Не удалось открыть изображение: ", "Сүрөттү ачуу мүмкүн болгон жок: ", "Could not open the image: ", "Görsel açılamadı: ", "Rasmni ochib bo'lmadi: ") + ex.Message);
        }
    }

    private void CropSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initialized)
            UpdatePreview();
    }

    private void UpdatePreview(bool syncSurface = true)
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
        if (syncSurface)
            CropSurface.SetCrop(LeftSlider.Value, TopSlider.Value, RightSlider.Value, BottomSlider.Value);

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
        if (SizeText is not null) SizeText.Text = Tr.T($"Результат: {width} × {height} px", $"Натыйжа: {width} × {height} px", $"Result: {width} × {height} px", $"Sonuç: {width} × {height} px", $"Natija: {width} × {height} px");
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
            ShowError(Tr.T("Изображение не загружено.", "Сүрөт жүктөлгөн жок.", "No image loaded.", "Görsel yüklenmedi.", "Rasm yuklanmagan."));
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
            ShowError(Tr.T("Не удалось сохранить QR-код: ", "QR-кодду сактоо мүмкүн болгон жок: ", "Could not save the QR code: ", "QR kodu kaydedilemedi: ", "QR-kodni saqlab bo'lmadi: ") + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    public static string GetManagedQrDirectory() =>
        Path.Combine(
            NurMarketKassa.Services.AppPaths.Roaming,
            NurMarketKassa.Services.AppMode.DataFolderName,
            "BankQr");

    protected override void OnClosed(EventArgs e)
    {
        PreviewImage.Source = null;
        _preview?.Dispose();
        _source?.Dispose();
        base.OnClosed(e);
    }
}
