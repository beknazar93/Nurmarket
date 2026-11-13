using Avalonia.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using System;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-13 (сервер apl1311+sn1311, «Электроника»): в карточке товара — «Продаётся с серийными номерами» (track_serials) и «Гарантия, мес.» (warranty_months).
// Блок виден только в сфере «Электроника»; в других сферах и на старом сервере ничего не меняется (поля в запрос не попадают). Уходят только значения, которые владелец изменил.
public partial class ProductEditDialog
{
    private bool _serialsOrigTrack;
    private string _serialsOrigMonths = "";
    private bool _serialsChanged; // владелец трогал поля

    private void InitSerialsBlock(CatalogProductTileVm? existing)
    {
        if (!ElectronicsProfile.IsActive)
            return;
        SerialsBlock.IsVisible = true;
        TrackSerialsCheck.Content = Tr.T("Продаётся с серийными номерами / IMEI", "Сериялык номерлер / IMEI менен сатылат", "Sold with serial numbers / IMEI", "Seri numaraları / IMEI ile satılır", "Seriya raqamlari / IMEI bilan sotiladi");
        WarrantyMonthsLabel.Text = Tr.T("Гарантия, мес.", "Кепилдик, ай", "Warranty, months", "Garanti, ay", "Kafolat, oy");
        var id = existing?.Id;
        _serialsOrigTrack = ProductSerialRules.TrackSerials(id) ?? (existing is not null && ElectronicsProfile.NeedsSerial(existing.Category));
        _serialsOrigMonths = ProductSerialRules.WarrantyMonths(id) is > 0 and var m ? m.ToString(CultureInfo.InvariantCulture) : "";
        TrackSerialsCheck.IsChecked = _serialsOrigTrack;
        WarrantyMonthsBox.Text = _serialsOrigMonths;
        _serialsChanged = false;
        TrackSerialsCheck.IsCheckedChanged += (_, _) => _serialsChanged = true;
        WarrantyMonthsBox.TextChanged += (_, _) => _serialsChanged = true;
    }

    /// <summary>Значения из карточки с сервера (track_serials / warranty_months): подставляются, пока владелец сам их не менял.</summary>
    private void ApplySerialsFromDetail(JsonElement card)
    {
        if (!ElectronicsProfile.IsActive || _serialsChanged)
            return;
        ProductSerialRules.Remember(_existing?.Id, card);
        var track = ProductSerialRules.TrackSerials(_existing?.Id);
        var months = ProductSerialRules.WarrantyMonths(_existing?.Id);
        if (track is null && months is null)
            return;
        _serialsOrigTrack = track ?? _serialsOrigTrack;
        _serialsOrigMonths = months is > 0 and var mm ? mm.ToString(CultureInfo.InvariantCulture) : "";
        TrackSerialsCheck.IsChecked = _serialsOrigTrack;
        WarrantyMonthsBox.Text = _serialsOrigMonths;
        _serialsChanged = false; // подстановка из карточки — не правка владельца
    }

    private bool SerialsVisible => ElectronicsProfile.IsActive && SerialsBlock.IsVisible;

    /// <summary>track_serials в запрос: у нового товара — если отмечено; у существующего — только если владелец менял галочку и значение отличается.</summary>
    private bool? SerialsRequestTrack()
    {
        if (!SerialsVisible)
            return null;
        var now = TrackSerialsCheck.IsChecked == true;
        if (_existing is null)
            return now ? true : null;
        return _serialsChanged && now != _serialsOrigTrack ? now : null;
    }

    private bool SerialsRequestSendWarranty()
    {
        if (!SerialsVisible)
            return false;
        var text = (WarrantyMonthsBox.Text ?? "").Trim();
        return _existing is null ? text.Length > 0 : _serialsChanged && !string.Equals(text, _serialsOrigMonths, StringComparison.Ordinal);
    }

    /// <summary>Гарантия в месяцах: пусто или 0 — убрать (null), иначе 1…120.</summary>
    private int? SerialsRequestWarranty() =>
        int.TryParse((WarrantyMonthsBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var months) && months > 0
            ? Math.Clamp(months, 1, 120)
            : null;

    private static void RememberSerialsAfterSave(string productId, NurMarketKassa.Services.Api.ProductEditRequest request)
    {
        if (request.TrackSerials is null && !request.SendWarrantyMonths)
            return;
        ProductSerialRules.Set(productId, request.TrackSerials ?? ProductSerialRules.TrackSerials(productId) ?? false,
            request.SendWarrantyMonths ? request.WarrantyMonths ?? 0 : ProductSerialRules.WarrantyMonths(productId));
        ProductSerialRules.SaveIfDirty();
    }
}
