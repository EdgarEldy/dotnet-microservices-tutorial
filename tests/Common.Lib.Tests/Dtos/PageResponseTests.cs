using Common.Lib.Dtos;
using Microsoft.AspNetCore.Http;

namespace Common.Lib.Tests.Dtos;

public sealed class PageResponseTests
{
    private const string ProductsPath = "/api/v1/Catalog/Products";

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenItemsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new PageResponse<int>(null!, 1, 10, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenPageIsLessThanOne(int page)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageResponse<int>([], page, 10, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenPageSizeIsLessThanOne(int pageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageResponse<int>([], 1, pageSize, 0));
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentOutOfRangeException_WhenTotalCountIsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageResponse<int>([], 1, 10, -1));
    }

    [Fact]
    public void Constructor_ShouldExposeItemsAndMetadata_WhenArgumentsAreValid()
    {
        IReadOnlyList<string> items = ["a", "b"];

        var page = new PageResponse<string>(items, 2, 2, 5);

        Assert.Same(items, page.Items);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(5, page.TotalCount);
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(25, 10, 3)]
    [InlineData(30, 10, 3)]
    public void TotalPages_ShouldReturnCeilingOfTotalCountOverPageSize_WhenPageIsCreated(long totalCount, int pageSize, long expected)
    {
        var page = new PageResponse<int>([], 1, pageSize, totalCount);

        Assert.Equal(expected, page.TotalPages);
    }

    [Fact]
    public void WriteHeaders_ShouldThrowArgumentNullException_WhenHttpContextIsNull()
    {
        var page = new PageResponse<int>([], 1, 10, 0);

        Assert.Throws<ArgumentNullException>(() => page.WriteHeaders(null!));
    }

    [Fact]
    public void WriteHeaders_ShouldWriteTotalCount_WhenCalled()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([1, 2], 1, 2, 1234).WriteHeaders(context);

        Assert.Equal("1234", context.Response.Headers[PageResponse<int>.TotalCountHeader].ToString());
    }

    [Fact]
    public void WriteHeaders_ShouldWriteFirstNextLastOnly_WhenOnFirstOfSeveralPages()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([], 1, 10, 25).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?page=2&pageSize=10>; rel=\"next\", " +
            $"<{ProductsPath}?page=3&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldWriteAllFourRelations_WhenOnMiddlePage()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([], 2, 10, 25).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"prev\", " +
            $"<{ProductsPath}?page=3&pageSize=10>; rel=\"next\", " +
            $"<{ProductsPath}?page=3&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldWriteFirstPrevLastOnly_WhenOnLastPage()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([], 3, 10, 25).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?page=2&pageSize=10>; rel=\"prev\", " +
            $"<{ProductsPath}?page=3&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldWriteFirstAndLastPointingToPageOne_WhenResultIsEmpty()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([], 1, 10, 0).WriteHeaders(context);

        Assert.Equal("0", context.Response.Headers[PageResponse<int>.TotalCountHeader].ToString());
        Assert.Equal(
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldPointPrevToLastPageAndOmitNext_WhenPageIsBeyondLastPage()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([], 7, 10, 25).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?page=3&pageSize=10>; rel=\"prev\", " +
            $"<{ProductsPath}?page=3&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldPointPrevToPageOne_WhenResultIsEmptyAndPageIsBeyondFirst()
    {
        var context = CreateContext(ProductsPath);

        new PageResponse<int>([], 4, 10, 0).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"prev\", " +
            $"<{ProductsPath}?page=1&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldPreserveFiltersAndReplacePaging_WhenQueryStringHasOtherParameters()
    {
        var context = CreateContext(ProductsPath, "?categoryId=3&page=2&pageSize=5&tag=a&tag=b");

        new PageResponse<int>([], 2, 5, 15).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?categoryId=3&tag=a&tag=b&page=1&pageSize=5>; rel=\"first\", " +
            $"<{ProductsPath}?categoryId=3&tag=a&tag=b&page=1&pageSize=5>; rel=\"prev\", " +
            $"<{ProductsPath}?categoryId=3&tag=a&tag=b&page=3&pageSize=5>; rel=\"next\", " +
            $"<{ProductsPath}?categoryId=3&tag=a&tag=b&page=3&pageSize=5>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldReplacePagingParameters_WhenTheirCasingDiffers()
    {
        var context = CreateContext(ProductsPath, "?Page=2&PAGESIZE=99&categoryId=3");

        new PageResponse<int>([], 1, 10, 5).WriteHeaders(context);

        var link = LinkHeader(context);
        Assert.DoesNotContain("Page=2", link, StringComparison.Ordinal);
        Assert.DoesNotContain("99", link, StringComparison.Ordinal);
        Assert.Contains($"<{ProductsPath}?categoryId=3&page=1&pageSize=10>; rel=\"first\"", link, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteHeaders_ShouldKeepFilterValuesEncoded_WhenTheyContainReservedCharacters()
    {
        var context = CreateContext(ProductsPath, "?search=red%20%26%20blue");

        new PageResponse<int>([], 1, 10, 5).WriteHeaders(context);

        Assert.Equal(
            $"<{ProductsPath}?search=red%20%26%20blue&page=1&pageSize=10>; rel=\"first\", " +
            $"<{ProductsPath}?search=red%20%26%20blue&page=1&pageSize=10>; rel=\"last\"",
            LinkHeader(context));
    }

    [Fact]
    public void WriteHeaders_ShouldWriteRelativeLinksIncludingPathBase_WhenRequestHasPathBase()
    {
        var context = CreateContext(ProductsPath);
        context.Request.PathBase = "/catalog";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("catalog-api", 8080);

        new PageResponse<int>([], 1, 10, 5).WriteHeaders(context);

        var link = LinkHeader(context);
        Assert.StartsWith($"</catalog{ProductsPath}?page=1&pageSize=10>", link, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-api", link, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", link, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateContext(string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static string LinkHeader(HttpContext context) =>
        context.Response.Headers[PageResponse<int>.LinkHeader].ToString();
}
