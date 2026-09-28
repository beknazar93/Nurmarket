namespace NurMarketKassa.Services;

public enum CustomerDisplayWindowMode
{
    FullScreen,
    WorkingArea,
    Windowed,
}

public enum CustomerDisplayLayoutMode
{
    Cards,
    Table,
}

public enum CustomerDisplayColumnMode
{
    Auto,
    Two,
    Three,
    Four,
}

public enum CustomerDisplayTheme
{
    Light,
    Dark,
    System,
}

public enum CustomerDisplayAdvertisementPosition
{
    Right,
    Bottom,
    EmptyScreen,
}

/// <summary>
/// Serializable customer-display preferences. Only primitive values are stored so the
/// settings remain portable and do not retain Avalonia Screen/Brush instances.
/// </summary>
public sealed class CustomerDisplaySettings
{
    public bool IsEnabled { get; set; } = true;
    public bool KeepCustomerDisplayOnTop { get; set; }
    public string? SelectedScreenId { get; set; }
    public CustomerDisplayWindowMode WindowMode { get; set; } = CustomerDisplayWindowMode.WorkingArea;
    public CustomerDisplayLayoutMode LayoutMode { get; set; } = CustomerDisplayLayoutMode.Cards;
    public CustomerDisplayColumnMode CardColumns { get; set; } = CustomerDisplayColumnMode.Auto;
    public double CardSize { get; set; } = 1;
    public bool ShowProductImage { get; set; } = true;
    public bool ShowBarcode { get; set; } = true;
    public bool ShowItemDiscount { get; set; } = true;
    public bool ShowStock { get; set; }

    public bool ShowTableProduct { get; set; } = true;
    public bool ShowTableQuantity { get; set; } = true;
    public bool ShowTablePrice { get; set; } = true;
    public bool ShowTableDiscount { get; set; }
    public bool ShowTableTotal { get; set; } = true;
    public bool ShowTableBarcode { get; set; } = true;
    public List<string> TableColumnOrder { get; set; } =
        ["Product", "Quantity", "Price", "Discount", "Total", "Barcode"];

    public bool ShowCompanyLogo { get; set; }
    public bool ShowStoreName { get; set; } = true;
    public bool ShowReceiptNumber { get; set; } = true;
    public bool ShowCashier { get; set; }
    public bool ShowDateTime { get; set; } = true;
    public bool ShowItemCount { get; set; } = true;
    public bool ShowSubtotal { get; set; } = true;
    public bool ShowDiscount { get; set; } = true;
    public bool ShowTotal { get; set; } = true;
    public bool ShowPaymentMethod { get; set; }
    public bool ShowPaidAmount { get; set; }
    public bool ShowChange { get; set; }

    public string EmptyTitle { get; set; } = DefaultEmptyTitle;
    public string EmptyDescription { get; set; } = DefaultEmptyDescription;
    public bool ShowLogoInEmptyState { get; set; } = true;

    public bool AdvertisementEnabled { get; set; }
    public string AdvertisementTitle { get; set; } = "Специальное предложение";
    public string AdvertisementDescription { get; set; } = "";
    public string AdvertisementImagePath { get; set; } = "";
    public CustomerDisplayAdvertisementPosition AdvertisementPosition { get; set; } =
        CustomerDisplayAdvertisementPosition.Right;

    public CustomerDisplayTheme Theme { get; set; } = CustomerDisplayTheme.Light;
    public string AccentColor { get; set; } = "#FACC15";
    public string BackgroundColor { get; set; } = "#F8FAFC";
    public string BackgroundImagePath { get; set; } = "";
    public double BackgroundOverlayOpacity { get; set; } = 0.15;
    public double CornerRadius { get; set; } = 16;
    public double Scale { get; set; } = 1;

    public string SuccessText { get; set; } = DefaultSuccessText;
    public bool ShowChangeAfterPayment { get; set; } = true;
    public int ClearDelaySeconds { get; set; } = 5;

    // ---- Вид экрана покупателя (2026-09-28, «при смене вида кассы меняй и 2 экран») ----
    // Новые поля. У касс, настроенных раньше, их нет в user-settings.json — тогда действуют
    // значения ниже, и экран выглядит как раньше, только вслед за видом кассы.

    /// <summary>Значение <see cref="DisplayStyle"/> «как у кассы».</summary>
    public const string StyleAuto = "auto";

    /// <summary>Тексты по умолчанию. Их показываем на языке программы, а не как есть: раньше
    /// «Добро пожаловать!» по-русски было на экране и у кыргызской, и у английской кассы.</summary>
    public const string DefaultEmptyTitle = "Добро пожаловать!";
    public const string DefaultEmptyDescription = "Ваши покупки появятся на этом экране";
    public const string DefaultSuccessText = "Спасибо за покупку!";

    /// <summary>Вид экрана: <see cref="StyleAuto"/> — тот же, что у кассы (UserPreferences.MainLayoutMode),
    /// иначе id вида (standard / table / cards / minimal / pro / onec).</summary>
    public string DisplayStyle { get; set; } = StyleAuto;

    /// <summary>true — цвета берутся из темы кассы (в том числе своей темы из редактора тем),
    /// false — свои: <see cref="BackgroundColor"/>, <see cref="AccentColor"/>, <see cref="TextColor"/>.
    /// null — настройки до 2026-09-28, решает <see cref="Normalize"/>.</summary>
    public bool? UseThemeColors { get; set; }

    /// <summary>Свой цвет текста (только при своих цветах); пустой — подбирается к фону.</summary>
    public string TextColor { get; set; } = "";

    /// <summary>Надпись сверху экрана; пустая — название магазина из настроек.</summary>
    public string StoreTitle { get; set; } = "";

    /// <summary>Множитель размера итоговой суммы (0.6–2).</summary>
    public double TotalScale { get; set; } = 1;

    public bool ShowItemList { get; set; } = true;
    public bool ShowPaymentQr { get; set; } = true;

    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    public void Normalize()
    {
        CardSize = Math.Clamp(CardSize, 0.75, 1.5);
        Scale = Math.Clamp(Scale, 0.75, 1.5);
        CornerRadius = Math.Clamp(CornerRadius, 0, 40);
        BackgroundOverlayOpacity = Math.Clamp(BackgroundOverlayOpacity, 0, 0.9);
        ClearDelaySeconds = Math.Clamp(ClearDelaySeconds, 0, 60);
        EmptyTitle ??= DefaultEmptyTitle;
        EmptyDescription ??= "";
        SuccessText ??= DefaultSuccessText;
        AdvertisementTitle ??= "";
        AdvertisementDescription ??= "";
        AdvertisementImagePath ??= "";
        AccentColor = NormalizeColor(AccentColor, "#FACC15");
        BackgroundColor = NormalizeColor(BackgroundColor, "#F8FAFC");
        TableColumnOrder ??= ["Product", "Quantity", "Price", "Discount", "Total", "Barcode"];

        // 2026-09-28: вид и цвета экрана покупателя.
        DisplayStyle = string.IsNullOrWhiteSpace(DisplayStyle) ? StyleAuto : DisplayStyle.Trim();
        TextColor = NormalizeColor(TextColor, "");
        StoreTitle ??= "";
        TotalScale = TotalScale <= 0 ? 1 : Math.Clamp(TotalScale, 0.6, 2);
        // Кто раньше сам задал фон экрана покупателя, у того фон остаётся своим. Остальные
        // (фон по умолчанию) получают цвета темы кассы: акцент туда и так уже подставлялся
        // из темы (App.SyncCustomerDisplayAccent), теперь — и фон, панели и текст.
        UseThemeColors ??= string.Equals(BackgroundColor, "#F8FAFC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Поля, которые меняет редактор экрана покупателя (2026-09-28): вид, цвета, тексты,
    /// что показывать. Монитор, окно, реклама и колонки таблицы не трогаются.</summary>
    public void CopyAppearanceFrom(CustomerDisplaySettings source)
    {
        DisplayStyle = source.DisplayStyle;
        UseThemeColors = source.UseThemeColors;
        Theme = source.Theme;
        AccentColor = source.AccentColor;
        BackgroundColor = source.BackgroundColor;
        TextColor = source.TextColor;
        StoreTitle = source.StoreTitle;
        EmptyTitle = source.EmptyTitle;
        EmptyDescription = source.EmptyDescription;
        SuccessText = source.SuccessText;
        TotalScale = source.TotalScale;
        ShowItemList = source.ShowItemList;
        ShowProductImage = source.ShowProductImage;
        ShowPaymentQr = source.ShowPaymentQr;
        ShowDateTime = source.ShowDateTime;
    }

    public CustomerDisplaySettings Clone()
    {
        var clone = (CustomerDisplaySettings)MemberwiseClone();
        clone.TableColumnOrder = [.. TableColumnOrder];
        return clone;
    }

    private static string NormalizeColor(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith('#')
            ? value.Trim()
            : fallback;
}
