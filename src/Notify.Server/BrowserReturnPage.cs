
using System.Text.Encodings.Web;

namespace Notify;

internal static class BrowserReturnPage
{
    public static IResult Render(string title, string description) => Results.Content($$"""
        <!doctype html>
        <html lang="ru">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{HtmlEncoder.Default.Encode(title)}} · Notify</title>
        <style>
          :root { color-scheme: light; --canvas:#f7f8fa; --surface:#fff; --text:#111722; --muted:#505d70; --line:#e0e5ed; --accent:#245bd0; --on-accent:#fff; }
          :root[data-theme=dark] { color-scheme: dark; --canvas:#0a0c10; --surface:#10141b; --text:#f2f5fa; --muted:#aab7cb; --line:#293343; --accent:#82afff; --on-accent:#0b1a35; }
          @media(prefers-color-scheme:dark) {
            :root:not([data-theme]) { color-scheme: dark; --canvas:#0a0c10; --surface:#10141b; --text:#f2f5fa; --muted:#aab7cb; --line:#293343; --accent:#82afff; --on-accent:#0b1a35; }
          }
          *, *::before, *::after { box-sizing:border-box; }
          body { min-height:100dvh; display:grid; place-items:center; margin:0; padding:16px; background:var(--canvas); color:var(--text); font:1rem/1.55 system-ui,sans-serif; }
          main { width:min(100%,32rem); padding:clamp(18px,5vw,32px); background:var(--surface); border:1px solid var(--line); border-radius:14px; }
          .brand { display:flex; align-items:center; gap:10px; font-weight:750; }
          .mark { display:grid; place-items:center; width:36px; height:36px; border-radius:10px; background:var(--accent); color:var(--on-accent); }
          h1 { margin:20px 0 8px; font-size:clamp(1.5rem,5vw,2rem); line-height:1.2; letter-spacing:-.025em; }
          p { margin:0; color:var(--muted); }
        </style>
        <script src="/notify-appearance.js" defer></script>
        <main>
          <div class="brand"><span class="mark"><svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/></svg></span>Notify</div>
          <h1>{{HtmlEncoder.Default.Encode(title)}}</h1>
          <p>{{HtmlEncoder.Default.Encode(description)}}</p>
        </main>
        </html>
        """, "text/html; charset=utf-8");
}
