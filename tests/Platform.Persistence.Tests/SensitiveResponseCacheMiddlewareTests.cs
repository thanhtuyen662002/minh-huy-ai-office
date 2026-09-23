using Microsoft.AspNetCore.Http;
using MinhHuy.AIOffice.Core.Api.Authorization;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class SensitiveResponseCacheMiddlewareTests
{
    [Theory]
    [InlineData("/api/data-sources")]
    [InlineData("/api/data-sources/")]
    [InlineData("/api/data-sources/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/data-sources/00000000-0000-0000-0000-000000000001/connection-test")]
    public async Task Data_source_paths_are_marked_no_store(string path)
    {
        var middleware = new SensitiveResponseCacheMiddleware(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.StartAsync();
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/api/data-sources-other")]
    public async Task Unrelated_paths_are_not_cache_fenced(string path)
    {
        var middleware = new SensitiveResponseCacheMiddleware(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.StartAsync();
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }
}
