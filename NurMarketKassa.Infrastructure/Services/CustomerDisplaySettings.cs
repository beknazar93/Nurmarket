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

    public string EmptyTitle { get; set; } = "Добро пожаловать!";
    public string EmptyDescription { get; set; } = "Ваши покупки появятся на этом экране";
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

    public string SuccessText { get; set; } = "Спасибо за покупку!";
    public bool ShowChangeAfterPayment { get; set; } = true;
    public int ClearDelaySeconds { get; set; } = 5;

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
        EmptyTitle ??= "Добро пожаловать!";
        EmptyDescription ??= "";
        AdvertisementTitle ??= "";
        AdvertisementDescription ??= "";
        AdvertisementImagePath ??= "";
        AccentColor = NormalizeColor(AccentColor, "#FACC15");
        BackgroundColor = NormalizeColor(BackgroundColor, "#F8FAFC");
        TableColumnOrder ??= ["Product", "Quantity", "Price", "Discount", "Total", "Barcode"];
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
