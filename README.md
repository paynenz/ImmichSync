# ImmichSync

ImmichSync is a Windows tray app that periodically downloads original photos from one Immich album. It targets Immich v3.4.2.

## Set up on Windows 11

1. Extract `ImmichSync-win-x64.zip` into a folder you want to keep and run `ImmichSync.exe`. The x64 package includes the .NET Desktop Runtime. Keep the extracted files together.
2. In Immich, create an API key with `album.read`, `asset.read`, and `asset.download` permissions. Enter the key and your server address in ImmichSync. Use the final HTTPS address if your server supports it. Plain HTTP sends the key without transport encryption.
3. Click **Load albums**, select an album, and choose a download folder.
4. Set the interval in minutes and click **Save settings**. **Sync now** starts the first check immediately. The next check runs one interval after the previous check finishes.
5. **Start ImmichSync when I sign in to Windows** is selected by default during setup. Leave it selected to launch in the tray after you sign in. If you move the app, open it from the new location and save settings again to update its startup path.

Minimize or close the window to keep the schedule running in the tray. Double-click the tray icon to reopen the window, or right-click it for **Open ImmichSync**, **Sync now**, and **Quit**. Windows may place the icon in the tray overflow menu. Checks run while your Windows session is signed in and the computer is awake.

## Downloads and settings

- The app downloads original images from the selected album. Separate video files are skipped.
- Each download has an Immich asset ID appended to its filename. This allows multiple photos with the same original filename and lets the app recognize photos it already downloaded. Photos removed from the album stay in the destination folder.
- Settings are stored under `%LOCALAPPDATA%\ImmichSync`. Windows DPAPI encrypts the API key for your Windows user account. Enter the key again when setting up another computer.
- The activity log shows the last 600 entries for the current session.

See the [Immich v3.4.2 album endpoints](https://github.com/immich-app/immich/blob/v3.4.2/server/src/controllers/album.controller.ts), [album timeline endpoints](https://github.com/immich-app/immich/blob/v3.4.2/server/src/controllers/timeline.controller.ts), and [original download endpoint](https://github.com/immich-app/immich/blob/v3.4.2/server/src/controllers/asset-media.controller.ts).

## Build the portable folder

Run from the repository with the .NET 10 SDK or later.

```powershell
dotnet publish ImmichSync.csproj -c Release -r win-x64 --self-contained -o artifacts/ImmichSync-win-x64
```

For Windows 11 on Arm, replace both instances of `win-x64` with `win-arm64`.
