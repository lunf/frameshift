![Frame Shift preview](FrameShift/Assets/app-preview.png)

# Frame Shift

A lightweight Windows tray app that updates your desktop wallpaper with photos from the Unsplash API.

## 🚀 Get started

**▶️ Run a release:** open `FrameShift.exe` in `artifacts/win-x64-self-contained`. This build includes .NET.

**💻 Run from source:** install the .NET 8 SDK or newer, open a terminal in the project folder, and run:

```powershell
dotnet run --project FrameShift/FrameShift.csproj -c Release
```

Look for the **green ring** in the Windows tray, including the hidden icons (`^`). The app has no main window. Exit the running app before rebuilding or starting another copy.

## 🖼️ Choose your photos

### 📁 Local folder — no key needed

Right-click the ring and choose **Photo source → Local photo folder…**. Supports JPG, PNG, and BMP images in that folder, without changing the originals.

**Change topic…** filters filenames. Leave it empty to use all photos. Subfolders are not included.

### 🌄 Unsplash

Create `.env.local` in the project folder when running from source, or beside `FrameShift.exe` for a release:

```dotenv
UNSPLASH_ACCESS_KEY=your-access-key
```

Use `.env.local.example` as a template. Restart after changing the file. Keep your key private; `.env.local` is excluded from Git and is not copied into releases.

An environment variable with the same name overrides the file. **Settings → Configure API…** lets you enter a key for the current session only.

Choose **Photo source → Unsplash** to use it. Unsplash's [API guidelines](https://help.unsplash.com/en/articles/2511245-unsplash-api-guidelines) exclude wallpaper apps; this integration requires separate authorization.

## 🖱️ Tray controls

| Action | What it does |
|---|---|
| 🔄 Left-click the ring | Change wallpaper |
| ☰ Right-click the ring | Open the menu |
| 🔎 Change topic… | Choose a subject or local filename filter |
| 🖼️ Photo source | Choose a source; open photo and photographer links |
| 🔑 Settings → Configure API… | Enter a session key |
| ⏱️ Settings → Refresh interval | Choose manual, 15/30 minutes, 1/3/6 hours, or daily |
| 🪟 Settings → Start with Windows | Enable or disable launch at login |
| ⏻ Exit | Close the app |

- ⏱️ Changes every **hour** by default. Manual-only mode changes at launch and on click.
- 🟢 The ring animates while loading.
- 📶 Temporary network failures retry automatically. Failed requests keep your current wallpaper.

💾 Settings and the last **10 wallpapers** are stored in `%LOCALAPPDATA%\FrameShift`. All monitors currently share the same wallpaper.

## 📦 Build a release

```powershell
dotnet publish FrameShift/FrameShift.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64-self-contained
```

Keep the executable in a permanent folder before enabling **Start with Windows**. Enable it again if you move the app.

## ✅ Run checks

```powershell
dotnet run --project FrameShift.Tests/FrameShift.Tests.csproj -c Release
dotnet run --project FrameShift.Windows.Tests/FrameShift.Windows.Tests.csproj -c Release
```

Checks use fake network responses and do not change your wallpaper or startup registration. See [PRODUCT_PLAN.md](PRODUCT_PLAN.md) for the product plan.
