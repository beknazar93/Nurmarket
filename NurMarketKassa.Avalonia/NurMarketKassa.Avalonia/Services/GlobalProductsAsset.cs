using System;
using System.IO;
using Avalonia.Platform;
using NurMarketKassa.Services.GlobalProducts;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27: встроенная выгрузка справочника готовых наименований NurCRM (Assets\data\global-products.sqlite, 8 МБ) лежит в
/// ресурсах программы (все проекты — Windows, Linux, Android — подключают Assets\** сами, csproj не менялся). Infrastructure про
/// Avalonia не знает, поэтому поток отдаётся ему через <see cref="GlobalProductCatalog.EmbeddedProvider"/>.</summary>
internal static class GlobalProductsAsset
{
    private static readonly Uri AssetUri = new("avares://NurMarketKassa.Avalonia/Assets/data/global-products.sqlite");

    public static void Register() => GlobalProductCatalog.EmbeddedProvider = () =>
        AssetLoader.Exists(AssetUri) ? AssetLoader.Open(AssetUri) : null;
}
