using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (Д1, отчёт «Проверка сфер»): окно PIN при возврате чека — сервер ответил 403 pin_required. Окно — общий PasswordPromptDialog (его открывает LateAcceptPrompt через IUserPrompts)
/// с пояснением ReturnSaleDialog.ReturnPinInfoText(). Проверка на ru, ky, uz (и tr) при экране 1024×600: окно целиком на экране, пояснение показано и не обрезано, поле PIN не налезает на пояснение,
/// кнопки не налезают на поле, введённый PIN доходит до проверки и подтверждает окно, отмена закрывает его без подтверждения. Снимки: %TEMP%\nurmarket-tests\ui-actual\return-pin-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReturnPinPromptUiTests
{
    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Turkish }.Select(l => new object[] { l });

    private static string Code(AppLanguage l) => l switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.Turkish => "tr", _ => "ru" };

    private static PasswordPromptDialog Create(Func<string, bool> validator) => new(
        Tr.T("PIN владельца или администратора", "Ээсинин же администратордун PIN коду", "Owner or administrator PIN", "Sahip veya yönetici PIN'i", "Egasi yoki administrator PIN kodi"),
        ReturnSaleDialog.ReturnPinInfoText(), validator);

    private static Rect R(Control c, Visual root) => new(c.TranslatePoint(default, root)!.Value, c.Bounds.Size);

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task ReturnPinPrompt_FitsOn1024x600_FieldsDoNotOverlap(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var dialog = Create(_ => true);
        try
        {
            using var _ = UiShot.Render(dialog);
            UiShot.Settle(3);
            var message = UiDrive.Find<TextBlock>(dialog, "MessageText");
            var box = UiDrive.Find<SecretTextBox>(dialog, "PasswordBox");
            var cancel = UiDrive.Find<Button>(dialog, "CancelButton");
            var confirm = UiDrive.Find<Button>(dialog, "ConfirmButton");

            message.Text.Should().Be(ReturnSaleDialog.ReturnPinInfoText(), "пояснение — про PIN владельца/администратора, а не про «нет права»");
            message.Text.Should().Contain("PIN");
            dialog.Bounds.Width.Should().BeLessThanOrEqualTo(1024);
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(600, "окно PIN целиком на экране 1024×600");
            foreach (var c in new Control[] { message, box, cancel, confirm })
            {
                var r = R(c, dialog);
                r.Left.Should().BeGreaterThanOrEqualTo(-0.5, c.Name + " внутри окна слева");
                r.Top.Should().BeGreaterThanOrEqualTo(-0.5, c.Name + " внутри окна сверху");
                r.Right.Should().BeLessThanOrEqualTo(dialog.Bounds.Width + 0.5, c.Name + " внутри окна справа");
                r.Bottom.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 0.5, c.Name + " внутри окна снизу");
            }
            // пояснение показано целиком: его высота не меньше высоты, которую текст занимает при этой ширине (нет обрезки по высоте)
            message.DesiredSize.Height.Should().BeLessThanOrEqualTo(message.Bounds.Height + 1, "пояснение не обрезано по высоте");
            R(message, dialog).Bottom.Should().BeLessThanOrEqualTo(R(box, dialog).Top + 0.5, "поле PIN под пояснением, не поверх него");
            R(box, dialog).Intersects(R(cancel, dialog)).Should().BeFalse("«Отмена» не налезает на поле PIN");
            R(box, dialog).Intersects(R(confirm, dialog)).Should().BeFalse("«Подтвердить» не налезает на поле PIN");
            R(cancel, dialog).Intersects(R(confirm, dialog)).Should().BeFalse("кнопки не налезают друг на друга");
            box.Bounds.Width.Should().BeGreaterThan(200, "поле PIN не схлопнулось");
            box.Bounds.Height.Should().BeGreaterThanOrEqualTo(40);
            confirm.Bounds.Height.Should().BeGreaterThanOrEqualTo(36);

            using var shot = UiShot.Render(dialog);
            using var image = SkiaSharp.SKImage.FromBitmap(shot);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"return-pin-{Code(lang)}-1024x600.png"), data.ToArray());
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnPinPrompt_TypedPin_ReachesValidator_AndConfirms()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        string? entered = null;
        var dialog = Create(code => { entered = code; return !string.IsNullOrWhiteSpace(code); });
        try
        {
            using var _ = UiShot.Render(dialog);
            var box = UiDrive.Find<SecretTextBox>(dialog, "PasswordBox");
            UiDrive.Type(dialog, box, "4321");
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "ConfirmButton"));
            UiShot.Settle(4);
            entered.Should().Be("4321", "введённый PIN доходит до проверки");
            dialog.IsVisible.Should().BeFalse("принятый PIN закрывает окно");
        }
        finally
        {
            if (dialog.IsVisible)
                dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReturnPinPrompt_EmptyPin_IsRejected_WindowStays_Cancel_Closes()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var calls = 0;
        var dialog = Create(code => { calls++; return !string.IsNullOrWhiteSpace(code); });
        try
        {
            using var _ = UiShot.Render(dialog);
            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "ConfirmButton"));
            UiShot.Settle(3);
            calls.Should().Be(1);
            dialog.IsVisible.Should().BeTrue("пустой PIN не принимается — окно остаётся");
            UiDrive.Find<TextBlock>(dialog, "ErrorText").IsVisible.Should().BeTrue("под полем показана ошибка");

            UiDrive.Click(dialog, UiDrive.Find<Button>(dialog, "CancelButton"));
            UiShot.Settle(3);
            dialog.IsVisible.Should().BeFalse("«Отмена» закрывает окно (возврат не отправляется)");
        }
        finally
        {
            if (dialog.IsVisible)
                dialog.Close();
            UiShot.Pump();
        }
    }
}
