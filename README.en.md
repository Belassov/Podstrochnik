# Podstrochnik

On-the-fly translation of selected text: select text in any app and get the translation.
The same window has a chat, so you can reply to a person in their own language.

[Русская версия →](README.md)

![Podstrochnik](screenshot.png)

## Step-by-step guide (for beginners)

**Step 1. Download the app.** Open the [latest release](../../releases/latest) and download
`Podstrochnik.exe` from the "Assets" block. Keep it anywhere — it is a single file, there is no
installer and nothing is written into the system.

**Step 2. Get an OpenRouter key.** Translation goes through the OpenRouter service.

1. Open [openrouter.ai](https://openrouter.ai) and sign in (Google or GitHub works).
2. Go to [openrouter.ai/keys](https://openrouter.ai/keys) → **Create Key** → name it anything →
   **Create**.
3. Copy the key: it starts with `sk-or-`. It is shown only once, so save it right away.
4. Top up the balance with $5–10 (the **Credits** section). One translation costs about
   `$0.00002–0.00013`, so that money covers tens of thousands of translations.

**Step 3. Put the key into the app.**

1. Open the folder where `Podstrochnik.exe` is.
2. In Explorer click the address bar, erase the path, type `cmd` and press Enter — a console
   opens right in that folder.
3. Type `Podstrochnik.exe --setkey sk-or-your-key-here` and press Enter.
4. The key is saved into the settings, encrypted for your Windows account.

No command line? Do it by hand: Win+R → paste `%APPDATA%\Podstrochnik` → Enter → open
`config.ini` in Notepad → add the key after `api_key=` → save. (In this case the key sits in the
file as plain text.)

**Step 4. Run it.** Double-click `Podstrochnik.exe`. Windows may warn that the publisher cannot
be verified — click "More info" → "Run anyway": small apps without a paid certificate always
look like that. The window opens and an icon appears near the clock.

**Step 5. Use it.**

* Select text in any app and press `Ctrl+C` **twice quickly** — the window opens with the
  translation. The first press copies as usual, normal copying is never hijacked.
* `Ctrl+Alt+S` — translate from the screen: the screen freezes, drag a rectangle around the text.
  Works for text you cannot select (games, images, PDFs).
* The field at the bottom is the chat: write in Russian, the answer comes in the other person's
  language with the Russian version below.
* Esc, the close button and minimizing hide the window to the tray; the real exit is the tray
  menu → "Exit".

**Troubleshooting**

* "Нет ключа OpenRouter" — Step 3 was not done.
* The window does not open at all — the WebView2 component is missing (it usually ships with
  Windows 10/11). Install "WebView2 Runtime" from Microsoft and try again.
* "Ошибка: HTTP 401" — the key is wrong or was deleted.
* "Ошибка: HTTP 402" / "insufficient credits" — no funds left on OpenRouter.
* Anything else — open `%APPDATA%\Podstrochnik\log.txt`; the last lines usually explain it.

## Features

* **Translate the selection** — two quick `Ctrl+C` presses in any application.
* **Translate from the screen** — `Ctrl+Alt+S` with a region selection; the model reads the text
  from the image itself, there is no separate OCR step.
* **Translate typed text or the clipboard** — buttons in the window, `Enter` in the input field.
* **Chat** — the reply in the other person's language plus the Russian version below it.
* **Session context** — every new selection is added to the context, so names and terms stay
  consistent. Limit 50,000 tokens, a fill counter and a reset button.
* **Window** — resizable from any edge, the layout adapts.
* **Tray, start with Windows**, dark theme.

## Requirements

Windows 10/11, the WebView2 Runtime component (ships with Edge) and an
[OpenRouter](https://openrouter.ai/keys) key.

## Settings and log

* `%APPDATA%\Podstrochnik\config.ini` — key, model, context limit.
* `%APPDATA%\Podstrochnik\log.txt` — startup, requests, errors.
* Nothing except the text you translate is sent anywhere; settings and log stay on your machine.

## Model

By default `deepseek/deepseek-v4.1-flash` via OpenRouter without reasoning: fast and cheap, and
the model reads images itself.

## Build

You need the .NET SDK 8 (the official Microsoft one) and this command:

```
dotnet publish -c Release -r win-x64
```

The interface lives in `ui/` and is embedded into the exe; you can look at it without building by
opening `ui/preview.html` in a browser. For a build with an embedded key create
`EmbeddedKey.g.cs` (see `EmbeddedKey.cs`); such builds are never published.

## Changelog

[CHANGELOG.md](CHANGELOG.md)

## License

MIT.
