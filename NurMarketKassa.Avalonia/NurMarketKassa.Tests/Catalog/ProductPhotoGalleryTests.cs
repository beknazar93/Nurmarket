using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-20 (просмотр фото товара на весь экран): разбор списка фото карточки товара — images[].image_url + image_thumb_url.</summary>
public sealed class ProductPhotoGalleryTests
{
    private const string Api = "https://app.nurcrm.kg/";

    private static IReadOnlyList<ProductPhotoRef> Gallery(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ProductImageUrl.ExtractGallery(doc.RootElement, Api);
    }

    [Fact]
    public void ServerImages_ReadFullAndThumb_InServerOrder()
    {
        var g = Gallery("""{"images":[{"image_url":"/media/a.jpg","image_thumb_url":"/media/a_t.jpg"},{"image_url":"https://cdn.kg/b.png","image_thumb_url":null}]}""");
        g.Should().HaveCount(2);
        g[0].Should().Be(new ProductPhotoRef("https://app.nurcrm.kg/media/a.jpg", "https://app.nurcrm.kg/media/a_t.jpg"));
        g[1].Should().Be(new ProductPhotoRef("https://cdn.kg/b.png", null));
    }

    [Fact]
    public void Duplicates_AreDropped_AndLimitIsTwelve()
    {
        Gallery("""{"images":["https://x.kg/a.jpg","https://X.kg/a.jpg","https://x.kg/b.jpg"]}""").Should().HaveCount(2);
        var many = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"https://x.kg/{i}.jpg\""));
        Gallery("{\"images\":[" + many + "]}").Should().HaveCount(12);
    }

    [Fact]
    public void ThumbEqualToFull_IsNotAThumb_AndOnlyThumbBecomesMainPhoto()
    {
        Gallery("""{"images":[{"image_url":"https://x.kg/a.jpg","image_thumb_url":"https://x.kg/a.jpg"}]}""")[0].ThumbUrl.Should().BeNull();
        var onlyThumb = Gallery("""{"images":[{"image_thumb_url":"https://x.kg/t.jpg"}]}""");
        onlyThumb.Should().ContainSingle().Which.Url.Should().Be("https://x.kg/t.jpg");
    }

    [Fact]
    public void NoImagesArray_FallsBackToMainPhoto_AndEmptyWhenNone()
    {
        Gallery("""{"image_url":"https://x.kg/main.jpg"}""").Should().ContainSingle().Which.Url.Should().Be("https://x.kg/main.jpg");
        Gallery("""{"name":"Товар","images":[]}""").Should().BeEmpty();
        Gallery("""[1,2]""").Should().BeEmpty();
        Gallery("""{"images":[{"image_url":""},{"image_url":"  "}]}""").Should().BeEmpty();
    }
}
