# Microsoft Store package

Store identity: `Virtyzz.DayZ-MapCompanion`, publisher
`CN=19150DDA-3930-4E74-B112-0BB4B6A6461B`, publisher display name `Virtyzz`,
Store ID `9PDWXCKJWZFG`.

From the repository root, in PowerShell:

```powershell
.\installer\store\Build-StorePackage.ps1
```

Requires .NET 8 SDK, Windows SDK's x64 `makeappx.exe`, and an x64 Tesseract runtime
(default `C:\Program Files\Tesseract-OCR`; override with `-TesseractDirectory`).
`-MakeAppxPath` can point to a portable official Windows SDK tool. The script
also looks under `artifacts/store-tools/sdk/bin`.

Without `-Version`, both scripts use the project version plus `.0` (currently
`0.6.0.0`). Pass `-Version X.Y.Z.0` only to explicitly override it.

The script publishes a self-contained x64 .NET application, bundles local OCR
with checked-in `eng`/`rus` models, creates logos and the Store manifest, and
runs MakeAppx validation. The output is
`artifacts/store/DayZ-Map-Companion_<version>_x64.msix`.

The package is unsigned for Store upload: Microsoft signs it after certification.
It cannot be installed by ordinary double-click before signing. For local MSIX
tests, use a development certificate matching the Store Publisher and trust it
on the test machine only, or use a suitable isolated test environment. Do not
modify the production Publisher to work around signature verification.

To create a separately signed local test copy in PowerShell 7:

```powershell
.\installer\store\New-TestPackage.ps1
```

This generates a self-signed code-signing certificate valid for 30 days and
signs only the copy under `artifacts/store/local-test`. It does not install the
package or change Windows trust. The temporary private-key PFX is deleted after
signing. Each invocation creates a new certificate; import the new public `.cer`
into `Cert:\LocalMachine\TrustedPeople` from an administrator PowerShell before
installing the test copy. The unsigned Store upload package is unchanged.

## Separate distribution builds

`StoreBuild=true` enables the Windows SDK target and `MICROSOFT_STORE` compilation
symbol. Store builds use StartupTask instead of the registry Run key, and open
Microsoft Store for updates instead of offering a GitHub EXE installer.

Without this flag the existing GitHub/Inno Setup build remains unchanged in
distribution mode. Its target remains `net8.0-windows`.

## Before submitting for certification

- Test an installed package on a clean Windows 10 2004+ / Windows 11 x64 machine:
  tray, overlay, OCR, startup on/off, HTTP API and access to external game files.
- Microsoft Edge WebView2 Runtime must be available. This package includes the
  SDK loader, not a fixed WebView2 browser runtime. Test deployment on systems
  without an existing runtime and finalize the dependency delivery strategy.
- Audit the exact bundled OCR DLLs' license notices and corresponding-source
  obligations; see `THIRD-PARTY-NOTICES.md`.
- Confirm the public privacy/support contacts and verify the privacy text
  against the final Store build. The root policy currently also covers the
  GitHub channel.
- In certification notes explain `runFullTrust`: this is a WinForms desktop
  utility using local game files, screen capture, tray, hotkeys and loopback HTTP.
- Select only the Windows Desktop device family in Partner Center.

Neither MakeAppx validation nor a successful compilation replaces installation
testing or Microsoft's certification.
