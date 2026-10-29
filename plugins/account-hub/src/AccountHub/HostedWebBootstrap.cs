using System.Text;
using Microsoft.AspNetCore.Http;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub;

/// <summary>Bounded text-response adapter. Overflow streams the original unchanged; no disk edits.</summary>
public sealed class HostedWebBootstrap(RequestDelegate next, IServerApplicationHost app, IServiceProvider? services = null)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var notificationsOwner=services?.GetKeyedService<Func<bool>>("Notifications.Owner.1")?.Invoke()==true;
        var path = context.Request.Path.Value;
        var prefix = new Uri(app.GetLocalApiUrl("127.0.0.1", "http", app.HttpPort)).AbsolutePath.TrimEnd('/');
        if (!HttpMethods.IsGet(context.Request.Method) || path is null || !(path.Equals(prefix + "/web/index.html", StringComparison.OrdinalIgnoreCase) || path.Equals(prefix + "/web/", StringComparison.OrdinalIgnoreCase) || path.Equals(prefix + "/web", StringComparison.OrdinalIgnoreCase))) { await next(context); return; }
        context.Request.Headers.Remove("Accept-Encoding");
        foreach (var header in new[] { "If-None-Match", "If-Modified-Since", "Range", "If-Range" }) context.Request.Headers.Remove(header);
        var original = context.Response.Body;
        using var buffer = new Capture(original, 2 * 1024 * 1024); context.Response.Body = buffer;
        try { await next(context); } finally { context.Response.Body = original; }
        if (buffer.Overflow) return;
        var bytes = buffer.Bytes();
        if (context.Response.StatusCode == 200 && context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true && string.IsNullOrEmpty(context.Response.Headers.ContentEncoding))
        {
            var html = Encoding.UTF8.GetString(bytes);
            var anchor = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (anchor >= 0)
            {
                var tags="";
                if(!html.Contains("data-account-hub-link-return",StringComparison.Ordinal)){
                    var resume=System.Net.WebUtility.HtmlEncode(context.Request.PathBase+prefix+"/AccountHub/assets/link-return.js");
                    tags+=$"<script defer data-account-hub-link-return src=\"{resume}\"></script>";
                }
                if(!notificationsOwner&&!html.Contains("data-account-hub-bootstrap",StringComparison.Ordinal)){
                    var src = System.Net.WebUtility.HtmlEncode(context.Request.PathBase + prefix + "/AccountHub/assets/notifications/bell.js");
                    var css = System.Net.WebUtility.HtmlEncode(context.Request.PathBase + prefix + "/AccountHub/assets/notifications/bell.css");
                    tags+=$"<link rel=\"stylesheet\" href=\"{css}\"><script defer data-account-hub-bootstrap src=\"{src}\"></script>";
                }
                if(tags.Length>0){html=html.Insert(anchor,tags);bytes=Encoding.UTF8.GetBytes(html);
                    context.Response.ContentLength=bytes.Length;context.Response.Headers.Remove("ETag");context.Response.Headers.Remove("Last-Modified");context.Response.Headers.Remove("Accept-Ranges");context.Response.Headers.CacheControl="no-store";
                }
            }
        }
        await original.WriteAsync(bytes, context.RequestAborted);
    }
    private sealed class Capture(Stream output, int limit) : Stream
    {
        private readonly MemoryStream buffer = new();
        public bool Overflow { get; private set; }
        public byte[] Bytes() => buffer.ToArray();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => buffer.Length; public override long Position { get => buffer.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] bytes, int offset, int count)
        {
            if (!Overflow && buffer.Length + count > limit) { Overflow = true; buffer.Position = 0; buffer.CopyTo(output); buffer.SetLength(0); }
            if (Overflow) output.Write(bytes, offset, count); else buffer.Write(bytes, offset, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            if (!Overflow && buffer.Length + bytes.Length > limit) { Overflow = true; buffer.Position = 0; await buffer.CopyToAsync(output, ct); buffer.SetLength(0); }
            if (Overflow) await output.WriteAsync(bytes, ct); else await buffer.WriteAsync(bytes, ct);
        }
        public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken ct) => WriteAsync(bytes.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { if (Overflow) output.Flush(); }
        public override Task FlushAsync(CancellationToken ct) => Overflow ? output.FlushAsync(ct) : Task.CompletedTask;
        protected override void Dispose(bool disposing) { if (disposing) buffer.Dispose(); base.Dispose(disposing); }
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
