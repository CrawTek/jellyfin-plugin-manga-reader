using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MangaReader;

// Add a same-origin bridge to the served web shell; no Jellyfin web files are rewritten.
public sealed class ReaderStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<ReaderShellMiddleware>();
        next(app);
    };
}

public sealed class ReaderShellMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var webIndex = path.LastIndexOf("/web", StringComparison.OrdinalIgnoreCase);
        var suffix = webIndex >= 0 ? path[webIndex..] : "";
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !(suffix.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase) || suffix is "/web" or "/web/") ||
            Plugin.Instance?.Configuration.EnableInAppReader != true)
        { await next(context); return; }

        // Revalidate the shell and request identity encoding so the injected script is not cached away.
        var encoding = context.Request.Headers.AcceptEncoding;
        var etag = context.Request.Headers.IfNoneMatch;
        var modified = context.Request.Headers.IfModifiedSince;
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        var output = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            context.Response.Body = output;
            if (context.Response.StatusCode == 200 &&
                context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true &&
                !context.Response.Headers.ContainsKey("Content-Encoding"))
            {
                var html = Encoding.UTF8.GetString(buffer.ToArray());
                var basePath = context.Request.PathBase.Value + path[..webIndex];
                var source = HtmlEncoder.Default.Encode(basePath + "/MangaReader/assets/bridge.js?v=0.5.0");
                var script = "<script defer src=\"" + source + "\"></script>";
                var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                html = end >= 0 ? html.Insert(end, script) : html + script;
                var data = Encoding.UTF8.GetBytes(html);
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentLength = data.Length;
                await output.WriteAsync(data, context.RequestAborted);
            }
            else { buffer.Position = 0; await buffer.CopyToAsync(output, context.RequestAborted); }
        }
        finally
        {
            context.Response.Body = output;
            context.Request.Headers.AcceptEncoding = encoding;
            context.Request.Headers.IfNoneMatch = etag;
            context.Request.Headers.IfModifiedSince = modified;
        }
    }
}



