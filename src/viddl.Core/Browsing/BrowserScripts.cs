namespace Viddl.Core.Browsing;

/// <summary>Scripts injected into every page of the built-in browser before the page's own scripts run.</summary>
public static class BrowserScripts
{
    /// <summary>Hides the signs of an embedded browser that some sites use to refuse service.</summary>
    public const string HideAutomation =
        "try{Object.defineProperty(navigator,'webdriver',{get:()=>false});" +
        "if(window.chrome&&window.chrome.webview){delete window.chrome.webview;}}catch(e){}";

    /// <summary>
    /// Neutralises common abusive tricks without touching normal page behaviour, so video players keep working:
    /// known miner APIs, "leave this page?" traps, clipboard hijacking, back-button flooding,
    /// notification prompts, protocol-handler hijacks and pop-ups opened without a click.
    /// </summary>
    public const string Guard = """
        (() => {
          try {
            const active = () => !navigator.userActivation || navigator.userActivation.isActive;
            const inert = function () { return { start() {}, stop() {}, on() {}, isRunning() { return false; }, setThrottle() {} }; };
            const stubNs = { Anonymous: inert, User: inert, Token: inert, Miner: inert };
            for (const n of ['CoinHive', 'CoinImp', 'CryptoLoot', 'CRLT', 'deepMiner', 'WMP', 'Client_CoinImp', 'JSEMine']) {
              try { Object.defineProperty(window, n, { get: () => stubNs, set() {}, configurable: false }); } catch (e) {}
            }
            const addEL = EventTarget.prototype.addEventListener;
            EventTarget.prototype.addEventListener = function (type, fn, opts) {
              if (type === 'beforeunload' && this === window) return;
              return addEL.call(this, type, fn, opts);
            };
            try { Object.defineProperty(window, 'onbeforeunload', { get: () => null, set() {}, configurable: false }); } catch (e) {}
            if (navigator.clipboard) {
              for (const m of ['writeText', 'write']) {
                const orig = navigator.clipboard[m] && navigator.clipboard[m].bind(navigator.clipboard);
                if (orig) navigator.clipboard[m] = (...a) => active() ? orig(...a) : Promise.reject(new DOMException('Blocked', 'NotAllowedError'));
              }
            }
            const exec = Document.prototype.execCommand;
            Document.prototype.execCommand = function (cmd, ...rest) {
              if (/^(copy|cut)$/i.test(cmd) && !active()) return false;
              return exec.call(this, cmd, ...rest);
            };
            let silentPushes = 0;
            const push = History.prototype.pushState;
            History.prototype.pushState = function (...a) {
              if (!active() && ++silentPushes > 5) return;
              return push.apply(this, a);
            };
            if (window.Notification) {
              try { Notification.requestPermission = () => Promise.resolve('denied'); } catch (e) {}
            }
            if (navigator.registerProtocolHandler) navigator.registerProtocolHandler = () => {};
            const open = window.open;
            window.open = function (...a) { return active() ? open.apply(this, a) : null; };
          } catch (e) { /* never break the page */ }
        })();
        """;

    /// <summary>Start page shown before the user has visited anything.</summary>
    public const string StartPage = """
        <html><head><meta charset="utf-8"><meta name="color-scheme" content="light dark"><title>viddl</title>
        <style>
          body { font-family: "Segoe UI", sans-serif; max-width: 640px; margin: 60px auto; line-height: 1.5; padding: 0 16px;
                 color: #222; background: #fafafa; }
          .note { color: #777; }
          @media (prefers-color-scheme: dark) { body { color: #e6e6e6; background: #1c1c1c; } .note { color: #9a9a9a; } }
        </style></head>
        <body>
        <h2>viddl</h2>
        <ol>
          <li>Type a web address in the bar above and press Enter.</li>
          <li>Play the video. Its streams appear in the <b>Detected streams</b> list on the right.</li>
          <li>Select one (or several), adjust the name if you like, and click <b>Download selected</b>.</li>
        </ol>
        <p>Have a direct link (.m3u8, .mpd, .mp4) or a page supported by yt-dlp? Paste it and click <b>Download URL</b>.</p>
        <p class="note">Only download videos you have the right to save. DRM-protected streams are not supported.</p>
        </body></html>
        """;
}
