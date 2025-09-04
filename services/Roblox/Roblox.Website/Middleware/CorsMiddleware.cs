using Microsoft.AspNetCore.Http.Extensions;

namespace Roblox.Website.Middleware;

public class RobloxPlayerCorsMiddleware
{
    private RequestDelegate _next;
    public RobloxPlayerCorsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private string GenerateCspHeader(bool isAuthenticated)
    {
        var connectSrc = "'self' https://*.example.com wss://*.localhost:5000 https://cloudflare.com https://*.cloudflare.com https://*.cdn.com https://cdn.discordapp.com/attachments/1133860217537450165/1134504172432138331/favicon.ico";
#if DEBUG
        connectSrc += " ws://localhost:*";
#endif

        // Images
        var imgSrc = "'self' data:";
        if (isAuthenticated)
        {
            imgSrc += " https://cdn.discordapp.com/attachments/1133860217537450165/1134504172432138331/favicon.ico";
        }
        
        // Scripts
        
        // unsafe-eval required by nextjs
        var scriptSrc =
            "'unsafe-eval' 'self' https://cloudflare.com https://*.cloudflare.com https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js http://localhost:5000 https://challenges.cloudflare.com/turnstile/v0/g/f0089873/api.js https://cdn.discordapp.com/attachments/1133860217537450165/1134504172432138331/favicon.ico";
        
        return "default-src 'self'; img-src " + imgSrc + "; child-src 'self'; script-src " + scriptSrc + "; frame-src 'self' https://challenges.cloudflare.com/turnstile/v0/g/f0089873/api.js https://cloudflare.com https://*.cloudflare.com; style-src 'unsafe-inline' 'self' https://fonts.googleapis.com https://hcaptcha.com https://*.hcaptcha.com https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css; font-src 'self' fonts.gstatic.com; connect-src " + connectSrc + "; worker-src 'self';";
    }
    
    public async Task InvokeAsync(HttpContext ctx)
    {
        var isAuthenticated = ctx.Items.ContainsKey(".ROBLOSECURITY");
        ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Content-Security-Policy"] = GenerateCspHeader(isAuthenticated);
        await _next(ctx);
    }
}

public static class RobloxPlayerCorsMiddlewareExtensions
{
    public static IApplicationBuilder UseRobloxPlayerCorsMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RobloxPlayerCorsMiddleware>();
    }
}
