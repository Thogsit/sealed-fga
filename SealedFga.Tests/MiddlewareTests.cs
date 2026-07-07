using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using SealedFga.Exceptions;
using SealedFga.Middleware;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>Tests the exception→HTTP-status mapping of <see cref="SealedFgaExceptionHandlerMiddleware" />.</summary>
public class MiddlewareTests {
    private static async Task<HttpContext> InvokeWithThrow(Exception toThrow) {
        var ctx = new DefaultHttpContext();
        var middleware = new SealedFgaExceptionHandlerMiddleware(_ => throw toThrow);
        await middleware.InvokeAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task Forbidden_exception_maps_to_403() {
        var ctx = await InvokeWithThrow(new FgaForbiddenException("user:1", "can_view", "secret:2"));
        ctx.Response.StatusCode.ShouldBe((int) HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Entity_not_found_exception_maps_to_404() {
        var ctx = await InvokeWithThrow(new FgaEntityNotFoundException(typeof(string), "2"));
        ctx.Response.StatusCode.ShouldBe((int) HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unhandled_exception_is_rethrown()
        => await Should.ThrowAsync<InvalidOperationException>(
            async () => await InvokeWithThrow(new InvalidOperationException("boom")));

    [Fact]
    public async Task Successful_pipeline_leaves_default_status() {
        var ctx = new DefaultHttpContext();
        var middleware = new SealedFgaExceptionHandlerMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx);
        ctx.Response.StatusCode.ShouldBe(200);
    }
}
