# SecureSync

A private, encrypted "drive" that lives on your own devices: Windows PCs and Android phones. Files you add are
encrypted and stored on each device, and every device on the same sync chain gets the same encrypted files over your
local network. There's no cloud, no account, no server and no password to remember. You unlock with Windows Hello on
a PC, and with your fingerprint, face or screen lock on a phone.

## Building it

Double-click the script for the platform you want. Each one sets up what it needs, without admin rights.

| Script | Output |
|---|---|
| **`Build Windows.cmd`** | `dist\windows\SecureSync-Setup-<version>.msi`: double-click to install. It adds a **Desktop icon** and a Start menu entry, installs for your Windows user only, and uninstalls from Settings › Apps. Also `dist\windows\portable\SecureSync.exe`, which runs without installing. |
| **`Build Android.cmd`** | `dist\android\SecureSync-<version>.apk`: copy it to the phone and open it to install (Android 11 or newer). |

What the scripts set up the first time:

- **Windows** needs only the .NET 10 SDK. If it's missing, the script puts a private copy in `.tools\`.
- **Android** puts a private .NET SDK with the Android workload in `.tools\dotnet` (about 1.5 GB, once). It uses
  Android Studio's SDK and Java if they're installed, and downloads what's missing otherwise. It also creates an
  app signing key in **`.keys\`**. **Back up `.keys\`:** Android only installs updates that are signed with the
  same key.

Add `-SkipTests` to either script to skip the test run.

## Getting started

**First device: create the chain**

1. Open SecureSync and choose **Create a new sync chain**.
2. On a PC, pick a folder for the encrypted files; a phone uses its own private storage. Give the device a name.
3. Confirm with Windows Hello, or your fingerprint/face/screen lock. SecureSync uses it to lock the chain key away on
   this device.
4. SecureSync shows your **sync chain key**: 52 characters starting with `SS`, like a Bitcoin private key.
   **Save it somewhere safe**, such as a password manager or on paper. It's the only way to add another device, or
   to recover your files if a device is lost.

**Every other device: join it**

Choose **Join an existing sync chain**, then enter the key. You can paste it, open a key file on a PC, or type it in;
typos are caught by a checksum. To see the key later on any device that's already set up, use **Help › Show sync
chain key** on a PC or **⋮ › Show sync chain key** on a phone. Both ask you to verify again first.

From then on, SecureSync opens locked. Unlock with Windows Hello or your fingerprint, and it starts syncing by itself:
devices on the same Wi-Fi find each other within seconds, with no button to press. A green ✓ **Synced with …**
shows when they're up to date.

> **Windows Firewall:** the first time you unlock on a PC, Windows asks whether SecureSync may use the network.
> Click **Allow**. If your Wi-Fi is set to *Public*, either allow Public networks in that prompt or switch the network
> to *Private* (Settings › Network & internet › Wi-Fi › your network).

## Using it

**On a PC**, the window works like File Explorer: a folder tree, a details list, menus and keyboard shortcuts.

| | |
|---|---|
| **Upload files / Upload folder** | Encrypts into the current folder. You can also drag files and folders in from File Explorer. If names clash you choose: replace, skip, or keep both. |
| **New folder** (Ctrl+Shift+N), **Rename** (F2) | Name things in place, like Explorer. |
| **Move** | Drag onto a folder, **Cut**/**Paste** (Ctrl+X / Ctrl+V), or **Move to…**. |
| **Delete** (Del) | Deletes on *every* device. Your original files elsewhere on disk are never touched. |
| **Open** (double-click) | Opens a private, temporary decrypted copy, deleted when you lock or close SecureSync. Edits aren't saved back; upload the edited file to keep them. |
| **Save copy** (Ctrl+S) | Writes normal, unencrypted copies wherever you choose. |
| **Lock** (Ctrl+L) | Stops syncing, forgets the key and deletes temporary decrypted copies. |

**On a phone:** tap a folder to open it and a file to open it in another app. **⋮** next to an item offers Share or
save a copy, Rename, Move and Delete. **+** uploads files or makes a folder. Pull down to sync now, and tap the status
strip at the top for sync activity.

Every change (new files, renames, moves, new folders, deletes) syncs to all your devices.

## Syncing

- **Any number of devices.** Whatever devices are on at the moment sync with each other straight away. If you add
  something on your laptop while the desktop is off, it goes to the phone right away and to the desktop as soon as the
  desktop is on again. The desktop can get it from the phone even if the laptop is off by then.
- **Changes made apart merge automatically.** When devices reconnect, everything made on any of them syncs. If the
  *same* item was changed on two devices, the most recent change wins everywhere. A file added to a folder that was
  deleted elsewhere is kept: it moves up to the nearest folder that still exists.
- **Progress:** a progress bar shows files left and bytes transferred, and each file shows *On this computer*,
  *Downloading 45%* or *Waiting*. **Sync activity** lists connected devices and anything that needs attention.
- **Errors fix themselves.** Every received file is verified before it's accepted. Damaged transfers, dropped
  connections, a full disk, or a device that doesn't have a file yet are retried automatically, from another device
  if one has it. **Retry now** retries immediately, and **Check and repair files** (PC) re-verifies every stored file
  and re-downloads any damaged ones.
- **Connect by IP address** is for networks that block automatic discovery (some guest, hotel or office Wi-Fi). You
  only need to do it once: the device remembers the address and reconnects by itself whenever syncing starts.

## Battery and background use

SecureSync does nothing on a schedule. There are no update checks, no polling, and no background service.

- **Work happens when something changes:** a file is added, a device appears, or a retry is due.
- **When idle and connected,** devices send one tiny discovery packet a minute and a 30-second keep-alive. Every 10
  minutes they compare a 32-byte fingerprint of their contents, so nothing goes unnoticed.
- **On Android, syncing runs only while the app is open.** It stops the moment you switch away. There's no foreground
  service, scheduled job, boot receiver or wake lock. After 5 minutes away, the phone asks for your fingerprint again.
  Anything unfinished simply continues the next time the app is open alongside another device.
- **On a PC,** SecureSync syncs while its window is open. Close or lock it to stop.

## How it's secured

- **The sync chain key is the encryption key:** 256 random bits, written like a Bitcoin private key. Because it's
  fully random, it can't be guessed and needs no password stretching. HKDF-SHA256 derives separate keys for file
  encryption, for proving membership in the chain, and for finding each other on the network.
- **Stored on each device behind the device's own sign-in:**
  - **On Windows,** a Windows Hello key (in the TPM where available) signs a fixed challenge after you verify. A
    wrapping key derived from that signature encrypts the chain key with AES-256-GCM, and DPAPI seals it again to
    your Windows account.
  - **On Android,** an HMAC key in the Android Keystore (secure hardware where available) does the same signing step.
    It's usable only right after BiometricPrompt: fingerprint, face, or the screen-lock PIN/pattern/password.
  - **Cloud backup:** SecureSync opts out of Android cloud backup and device transfer, and hides its screens from
    screenshots and the recent-apps view.
- **Files: AES-256-GCM,** each file with its own key, in authenticated 1 MiB chunks, so any change, truncation or
  reordering is detected. Names, folders and dates are encrypted too, padded to hide name lengths. On disk,
  everything is stored under random IDs.
- **Between devices:** discovery broadcasts carry only a tag derived from the key. Devices prove they hold the key with
  an authenticated key exchange (ephemeral ECDH P-256 bound to the chain key, like Noise `NNpsk0`) before exchanging
  anything. All traffic is AES-256-GCM.

There are no third-party crypto libraries: SecureSync uses .NET's built-in cryptography plus the platforms' own
Windows Hello and Android Keystore/BiometricPrompt APIs.

## Good to know

- **Keep your sync chain key secret and backed up.** Anyone with it can read your files. If a device's sign-in is reset
  (Windows Hello PIN reset, phone screen lock removed), enter the key there again. The lock screen offers
  **Enter the sync chain key instead**.
- **Same local network only.** There's no internet relay.
- **Deletes are final** and sync everywhere. There's no recycle bin yet.
- The installer, exe and APK aren't code-signed by a certificate authority. Windows SmartScreen may warn when they're
  *downloaded* (choose *More info › Run anyway*), and Android asks you to allow installing from your file manager.
- Network ports: UDP 47820 (discovery) and TCP 47821 (sync; another free port is used if busy).
- This is new software and hasn't had an independent security audit.

## For developers

```bash
dotnet test tests/SecureSync.Tests
```

`SecureSync.exe --self-test report.txt` checks, without a window or prompt, that encryption and key sealing work on a
PC, and whether Windows Hello is set up.

```
src/SecureSync.Core      shared: chain key, encryption format, vault, key sealing, LAN sync engine (no platform code)
src/SecureSync.App       Windows app (WPF): Explorer-style UI, Windows Hello, DPAPI
src/SecureSync.Android   Android app (.NET MAUI): phone UI, BiometricPrompt + Android Keystore
installer/               Windows installer (WiX, built via NuGet)
build/                   build-windows.ps1, build-android.ps1 (used by the .cmd files)
tests/SecureSync.Tests   unit + integration tests (offline merge, conflicts, two and three devices over real sockets)
```
